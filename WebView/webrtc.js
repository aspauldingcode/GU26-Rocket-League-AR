/* External display for the AR app's mono view.
   The page owns the room and shows a QR for it. Share in the app scans the QR, opens this page
   hidden as ?role=host, and both sides meet on a WebSocket relay (MQTT over WSS). No WebRTC —
   its data channel from inside the app's WebView never opened, so the page sat on
   "Connecting…" while nothing crossed.

   Video: Unity hands the host page near-lossless JPEG source frames. The host encodes them to
   H.264 (WebCodecs, hardware VBR, real-time) at a size and bitrate it adapts to the relay's
   health, and sends JPEG instead whenever any watching page can't decode H.264. The viewer
   decodes, sharpens on the GPU (contrast-adaptive, so codec edges don't ring), and falls back
   step by step — WebGL → 2D canvas, H.264 → JPEG — rather than ever showing a blank stage.
   The stage only appears once a frame has actually been drawn, and a stall keeps the last
   picture up. */
(function () {
  "use strict";

  var BROKERS = ["wss://broker.emqx.io:8084/mqtt", "wss://test.mosquitto.org:8081/mqtt"];
  var TOPIC_PREFIX = "gurlar/v1/";
  var STALL_MS = 5000;

  var params = new URLSearchParams(location.search);
  var room = (params.get("room") || "").trim().toLowerCase();
  var role = (params.get("role") || "view").trim().toLowerCase();
  var fromApp = params.get("unity") === "1";

  var statusEl = document.getElementById("status");
  var viewEl = document.getElementById("view");
  var stageEl = document.getElementById("stage");
  var pairEl = document.getElementById("pair");
  var roomEl = document.getElementById("room");
  var qrEl = document.getElementById("qr");
  var actionsEl = document.getElementById("actions");
  var reconnectBtn = document.getElementById("reconnect");
  var leaveBtn = document.getElementById("leave");
  var liveStatusEl = document.getElementById("live-status");

  function log(text) {
    try {
      var bridge = window.webkit && window.webkit.messageHandlers && window.webkit.messageHandlers.castlog;
      if (bridge) bridge.postMessage(String(text));
    } catch (e) {}
    try { console.log("[cast] " + text); } catch (e) {}
  }

  function setStatus(text) {
    if (statusEl) statusEl.textContent = text || "";
    if (liveStatusEl) liveStatusEl.textContent = text || "";
    log(text);
  }

  function show(el, on) {
    if (el) el.style.display = on ? "" : "none";
  }

  function newRoomCode() {
    var alphabet = "abcdefghijklmnopqrstuvwxyz0123456789";
    var out = "";
    var bytes = new Uint8Array(8);
    (window.crypto || window.msCrypto).getRandomValues(bytes);
    for (var i = 0; i < bytes.length; i++) out += alphabet[bytes[i] % alphabet.length];
    return out;
  }

  function randomSuffix() {
    return Math.random().toString(36).slice(2, 8);
  }

  // Landscape aspect of the display this page will present on, which the phone renders at.
  function displayAspect(width, height) {
    var w = Math.max(width || 0, height || 0);
    var h = Math.min(width || 0, height || 0);
    if (!(w > 0 && h > 0)) return 16 / 9;
    var a = w / h;
    if (a < 1.2) a = 1.2;
    if (a > 2.4) a = 2.4;
    return a;
  }

  function pairUrl(code, aspect) {
    return location.origin + location.pathname + "?room=" + encodeURIComponent(code) +
      "&aspect=" + aspect.toFixed(3);
  }

  function frameTopic(code) { return TOPIC_PREFIX + code + "/f"; }
  function helloTopic(code) { return TOPIC_PREFIX + code + "/h"; }
  function videoTopic(code) { return TOPIC_PREFIX + code + "/v"; }
  function controlTopic(code) { return TOPIC_PREFIX + code + "/c"; }

  // H.264 in preference order. Level 3.1 covers the largest rung (1280x720 at 15 fps).
  var AVC_CODECS = ["avc1.64001F", "avc1.4D401F", "avc1.42E01F"];
  var VIEWER_STALE_MS = 8000;
  // Viewers that join or lose a packet ask for a keyframe, so the timed ones are only a backstop.
  // Each costs several frames' bits, and under VBR the frames around it go soft: frequent ones
  // make the picture pulse.
  var KEYFRAME_MS = 8000;

  function toBytes(payload) {
    if (!payload) return new Uint8Array(0);
    if (payload instanceof Uint8Array) return payload;
    if (payload instanceof ArrayBuffer) return new Uint8Array(payload);
    if (typeof payload === "string") return new TextEncoder().encode(payload);
    if (payload.buffer instanceof ArrayBuffer)
      return new Uint8Array(payload.buffer, payload.byteOffset || 0, payload.byteLength);
    return new Uint8Array(0);
  }

  function bytesToText(payload) {
    try { return new TextDecoder().decode(toBytes(payload)); } catch (e) { return ""; }
  }

  // Video packet: "GV", version 1, flags (bit 0 = keyframe), u16 sequence, u16 width,
  // u16 height, u32 timestamp in ms, codec string (length-prefixed), Annex-B access unit.
  function packVideo(seq, key, width, height, tsMs, codec, data) {
    var c = new TextEncoder().encode(codec);
    var out = new Uint8Array(15 + c.length + data.length);
    out[0] = 0x47; out[1] = 0x56; out[2] = 1; out[3] = key ? 1 : 0;
    out[4] = (seq >> 8) & 255; out[5] = seq & 255;
    out[6] = (width >> 8) & 255; out[7] = width & 255;
    out[8] = (height >> 8) & 255; out[9] = height & 255;
    out[10] = (tsMs >>> 24) & 255; out[11] = (tsMs >>> 16) & 255;
    out[12] = (tsMs >>> 8) & 255; out[13] = tsMs & 255;
    out[14] = c.length;
    out.set(c, 15);
    out.set(data, 15 + c.length);
    return out;
  }

  // The public broker delivers about 10 messages a second per topic and silently drops the
  // rest, whatever their size. Video therefore goes out at most PUBLISH_MS apart, each
  // message a bundle of every packet queued since the last: "GB", version 1, count, then
  // u32-length-prefixed video packets.
  var PUBLISH_MS = 125;
  var BUNDLE_MAX = 6;

  function packBundle(packets) {
    var total = 4;
    for (var i = 0; i < packets.length; i++) total += 4 + packets[i].length;
    var out = new Uint8Array(total);
    out[0] = 0x47; out[1] = 0x42; out[2] = 1; out[3] = packets.length;
    var o = 4;
    for (var p = 0; p < packets.length; p++) {
      var n = packets[p].length;
      out[o] = (n >>> 24) & 255; out[o + 1] = (n >>> 16) & 255; out[o + 2] = (n >>> 8) & 255; out[o + 3] = n & 255;
      out.set(packets[p], o + 4);
      o += 4 + n;
    }
    return out;
  }

  // A single video packet or a bundle, as a list of video packets.
  function unpackBundle(payload) {
    var b = toBytes(payload);
    if (b.length >= 2 && b[0] === 0x47 && b[1] === 0x56) return [b];
    if (b.length < 4 || b[0] !== 0x47 || b[1] !== 0x42 || b[2] !== 1) return [];
    var out = [];
    var o = 4;
    for (var i = 0; i < b[3] && o + 4 <= b.length; i++) {
      var n = ((b[o] << 24) >>> 0) + (b[o + 1] << 16) + (b[o + 2] << 8) + b[o + 3];
      o += 4;
      if (o + n > b.length) break;
      out.push(b.subarray(o, o + n));
      o += n;
    }
    return out;
  }

  function unpackVideo(payload) {
    var b = toBytes(payload);
    if (b.length < 16 || b[0] !== 0x47 || b[1] !== 0x56 || b[2] !== 1) return null;
    var codecLen = b[14];
    if (b.length <= 15 + codecLen) return null;
    return {
      key: (b[3] & 1) === 1,
      seq: (b[4] << 8) | b[5],
      width: (b[6] << 8) | b[7],
      height: (b[8] << 8) | b[9],
      ts: ((b[10] << 24) >>> 0) + (b[11] << 16) + (b[12] << 8) + b[13],
      codec: new TextDecoder().decode(b.subarray(15, 15 + codecLen)),
      data: b.subarray(15 + codecLen),
    };
  }

  // Viewer → host, every couple of seconds: "id|mode|codecs|wantKey|lost".
  function packControl(id, mode, codecs, wantKey, lost) {
    return [id, mode, codecs.join(","), wantKey ? 1 : 0, lost | 0].join("|");
  }

  function parseControl(payload) {
    var parts = bytesToText(payload).split("|");
    if (parts.length < 5 || !parts[0]) return null;
    return {
      id: parts[0],
      mode: parts[1] === "avc" ? "avc" : "jpeg",
      codecs: parts[2] ? parts[2].split(",") : [],
      wantKey: parts[3] === "1",
      lost: parseInt(parts[4], 10) || 0,
    };
  }

  // H.264 only when every page watching right now can decode a codec this encoder has; any
  // JPEG-only page — or none heard yet, like a page cached from before H.264 — gets JPEG.
  function chooseMode(viewers, now, encoderCodecs, avcBroken) {
    var fresh = [];
    for (var id in viewers) {
      if (Object.prototype.hasOwnProperty.call(viewers, id) && now - viewers[id].at < VIEWER_STALE_MS)
        fresh.push(viewers[id]);
    }
    if (avcBroken || !encoderCodecs.length || !fresh.length) return { mode: "jpeg", codec: "" };
    for (var f = 0; f < fresh.length; f++) if (fresh[f].mode !== "avc") return { mode: "jpeg", codec: "" };
    for (var c = 0; c < encoderCodecs.length; c++) {
      var all = true;
      for (var v = 0; v < fresh.length; v++) if (fresh[v].codecs.indexOf(encoderCodecs[c]) < 0) all = false;
      if (all) return { mode: "avc", codec: encoderCodecs[c] };
    }
    return { mode: "jpeg", codec: "" };
  }

  // Quality ladder, best first. VBR targets: the encoder spends bits where the picture
  // changes and coasts on still frames. Heights are the 720p source at 1, 3/4 and 1/2: the
  // picture is a grid of small glyphs, and an odd scale such as 0.8 resamples each glyph
  // differently, which shimmers and costs the encoder bits. Bitrate drops before size does.
  var LADDER = [
    { h: 720, kbps: 3200, fps: 15 },
    { h: 720, kbps: 2400, fps: 15 },
    { h: 720, kbps: 1700, fps: 15 },
    { h: 540, kbps: 1100, fps: 12 },
    { h: 360, kbps: 650, fps: 10 },
    { h: 360, kbps: 400, fps: 8 },
  ];
  var START_LEVEL = 2;

  // One health verdict per second. Two bad seconds in a row step down at once; climbing back
  // needs a healthy run that doubles after each fall, so a flaky link settles instead of
  // bouncing between rungs.
  function nextLevel(level, badStreak, goodStreak, falls) {
    var need = Math.min(64, 8 * Math.pow(2, Math.max(0, falls)));
    if (badStreak >= 2) return Math.min(LADDER.length - 1, level + 1);
    if (goodStreak >= need) return Math.max(0, level - 1);
    return level;
  }

  function secondIsBad(s) {
    return s.buffered > 98304 || s.flushes > 0 || s.lost > 1 || s.backlog > 2 ||
      (s.offered > 3 && s.skipped / s.offered > 0.34);
  }

  function secondIsGood(s) {
    return s.buffered < 16384 && s.flushes === 0 && s.lost === 0 && s.skipped === 0;
  }

  function evenSize(height, aspect) {
    var h = Math.max(144, Math.round(height / 2) * 2);
    var w = Math.max(16, Math.round(h * aspect / 16) * 16);
    return { w: w, h: h };
  }

  function startsWithStartCode(d) {
    return d.length >= 4 && d[0] === 0 && d[1] === 0 && (d[2] === 1 || (d[2] === 0 && d[3] === 1));
  }

  function hasSps(d) {
    for (var i = 0; i + 3 < d.length; i++) {
      if (d[i] === 0 && d[i + 1] === 0 && d[i + 2] === 1 && (d[i + 3] & 31) === 7) return true;
    }
    return false;
  }

  // Some encoders hand back 4-byte length-prefixed NAL units even when asked for Annex B.
  function toAnnexB(d) {
    if (startsWithStartCode(d)) return d;
    var parts = [];
    var total = 0;
    var i = 0;
    while (i + 4 <= d.length) {
      var n = ((d[i] << 24) >>> 0) + (d[i + 1] << 16) + (d[i + 2] << 8) + d[i + 3];
      i += 4;
      if (n <= 0 || i + n > d.length) return d;
      parts.push(d.subarray(i, i + n));
      total += 4 + n;
      i += n;
    }
    if (i !== d.length || !parts.length) return d;
    var out = new Uint8Array(total);
    var o = 0;
    for (var p = 0; p < parts.length; p++) {
      out[o + 3] = 1;
      out.set(parts[p], o + 4);
      o += 4 + parts[p].length;
    }
    return out;
  }

  // SPS/PPS from an avcC description, as Annex B, for keyframes that arrive without them.
  function avccToAnnexB(desc) {
    var d = toBytes(desc);
    if (d.length < 7) return null;
    var out = [];
    var i = 5;
    var sps = d[i++] & 31;
    for (var s = 0; s < sps && i + 2 <= d.length; s++) {
      var sl = (d[i] << 8) | d[i + 1];
      i += 2;
      out.push(d.subarray(i, i + sl));
      i += sl;
    }
    if (i >= d.length) return null;
    var pps = d[i++];
    for (var p = 0; p < pps && i + 2 <= d.length; p++) {
      var pl = (d[i] << 8) | d[i + 1];
      i += 2;
      out.push(d.subarray(i, i + pl));
      i += pl;
    }
    var total = 0;
    for (var a = 0; a < out.length; a++) total += 4 + out[a].length;
    var bytes = new Uint8Array(total);
    var o = 0;
    for (var b = 0; b < out.length; b++) {
      bytes[o + 3] = 1;
      bytes.set(out[b], o + 4);
      o += 4 + out[b].length;
    }
    return bytes;
  }

  function concatBytes(a, b) {
    var out = new Uint8Array(a.length + b.length);
    out.set(a, 0);
    out.set(b, a.length);
    return out;
  }

  // Sharpen only what the display enlarges, harder the more it does. At 1:1 the glyphs are
  // already crisp, and sharpening would only lift the codec's ringing around them.
  function sharpnessFor(frameHeight, displayHeight) {
    var scale = frameHeight > 0 ? displayHeight / frameHeight : 1;
    if (scale <= 1.05) return 0;
    return Math.min(0.9, 0.3 + 0.4 * (scale - 1));
  }

  // Presence on the hello topic, retained: "1:{token}:{ts}" while a phone holds the room
  // (republished as a heartbeat), "0:{token}:{ts}" when it lets go. The token keeps one
  // phone's goodbye from ending another phone's stream, and lets a second phone that
  // scanned the same QR yield instead of interleaving its frames into the first one's.
  var HELLO_STALE_MS = 6000;

  function parseHello(payload) {
    // Payloads arrive as string, Uint8Array, or ArrayBuffer depending on the browser
    // (WebKit gives ArrayBuffer, which has no .length — that once read every hello as empty).
    var text = "";
    try {
      if (typeof payload === "string") text = payload;
      else if (payload instanceof ArrayBuffer) text = new TextDecoder().decode(new Uint8Array(payload));
      else if (payload && payload.length) text = new TextDecoder().decode(payload);
    } catch (e) { return null; }
    if (!text) return { on: false, token: "", ts: 0 };
    var parts = text.split(":");
    return { on: parts[0] === "1", token: parts[1] || "", ts: parseInt(parts[2] || "0", 10) || 0 };
  }

  window.__castLogic = {
    newRoomCode: newRoomCode,
    displayAspect: displayAspect,
    frameTopic: frameTopic,
    helloTopic: helloTopic,
    videoTopic: videoTopic,
    controlTopic: controlTopic,
    packVideo: packVideo,
    unpackVideo: unpackVideo,
    packBundle: packBundle,
    unpackBundle: unpackBundle,
    packControl: packControl,
    parseControl: parseControl,
    chooseMode: chooseMode,
    nextLevel: nextLevel,
    secondIsBad: secondIsBad,
    secondIsGood: secondIsGood,
    evenSize: evenSize,
    toAnnexB: toAnnexB,
    hasSps: hasSps,
    avccToAnnexB: avccToAnnexB,
    sharpnessFor: sharpnessFor,
    LADDER: LADDER,
  };

  function showPairingQr(code, aspect) {
    if (roomEl) roomEl.textContent = code;
    if (!qrEl) return;
    var url = pairUrl(code, aspect);
    show(pairEl, true);
    qrEl.innerHTML = "";
    qrEl.setAttribute("aria-label", "QR for room " + code);

    // Desktop Safari (with fingerprinting protection) can blank canvas/SVG/GIF QR renders. An
    // HTML table of black and white cells always paints. Low error correction keeps the modules
    // big, which phone cameras read from further away.
    var make = window.qrcode || (typeof qrcode !== "undefined" ? qrcode : null);
    if (typeof make === "function") {
      try {
        var qr = make(0, "L");
        qr.addData(url);
        qr.make();
        var cell = Math.max(4, Math.floor(300 / (qr.getModuleCount() + 8)));
        qrEl.innerHTML = qr.createTableTag(cell, cell * 4);
        var table = qrEl.querySelector("table");
        if (table) {
          table.style.margin = "0 auto";
          table.setAttribute("role", "presentation");
          return;
        }
      } catch (e1) {}
    }

    var data = encodeURIComponent(url);
    var remote = document.createElement("img");
    remote.width = 300;
    remote.height = 300;
    remote.alt = "QR for room " + code;
    remote.referrerPolicy = "no-referrer";
    remote.src = "https://api.qrserver.com/v1/create-qr-code/?size=300x300&margin=16&data=" + data;
    remote.onerror = function () {
      remote.onerror = null;
      remote.src = "https://quickchart.io/qr?size=300&text=" + data;
    };
    qrEl.appendChild(remote);
  }

  function blockCameraSync() {
    if (!navigator.mediaDevices || !navigator.mediaDevices.getUserMedia) return;
    navigator.mediaDevices.getUserMedia = function () {
      return Promise.reject(new Error("This page only watches the AR app."));
    };
  }

  function loadMqtt() {
    return new Promise(function (resolve, reject) {
      if (window.mqtt) {
        resolve(window.mqtt);
        return;
      }
      var urls = ["mqtt.min.js?v=5.14.1", "https://unpkg.com/mqtt@5.14.1/dist/mqtt.min.js"];
      var i = 0;
      function tryNext() {
        if (i >= urls.length) {
          reject(new Error("Could not load the relay library"));
          return;
        }
        var script = document.createElement("script");
        script.src = urls[i++];
        script.onload = function () {
          if (window.mqtt) resolve(window.mqtt);
          else tryNext();
        };
        script.onerror = tryNext;
        document.head.appendChild(script);
      }
      tryNext();
    });
  }

  // A dropped network during page load must not strand the page on a dead "Reload" status:
  // retry the relay library itself, slowly, until it arrives.
  function loadMqttWithRetry() {
    var delayMs = 1000;
    function attemptLoad() {
      return loadMqtt().catch(function (err) {
        setStatus("Reconnecting…");
        log("relay library load failed: " + (err && err.message ? err.message : err) +
          "; retrying in " + delayMs + " ms");
        return new Promise(function (resolve) { setTimeout(resolve, delayMs); }).then(function () {
          delayMs = Math.min(5000, Math.round(delayMs * 1.6));
          return attemptLoad();
        });
      });
    }
    return attemptLoad();
  }

  // Connects to the first broker that answers; onConnect runs on every (re)connect, which is
  // when subscriptions and the host's hello must be (re)sent. Callers can ask the returned
  // state whether the relay is actually connected, and forceReconnect when it has gone
  // quiet instead of merely hoping MQTT's own reconnect timer notices a half-open socket.
  function connectRelay(mqtt, clientId, onConnect, onMessage) {
    var state = { client: null, brokerIndex: 0, stopped: false, connected: false, downSince: Date.now() };
    var attemptTimer = null;

    function clearAttemptTimer() {
      if (attemptTimer) clearTimeout(attemptTimer);
      attemptTimer = null;
    }

    function fail(client, reason) {
      if (state.stopped || state.client !== client || state.connected) return;
      log("relay " + reason + "; trying the next broker");
      state.brokerIndex++;
      state.client = null;
      try { client.end(true); } catch (e) {}
      attempt();
    }

    function markDown(client) {
      if (state.client !== client) return;
      if (state.connected) log("relay disconnected; reconnecting");
      state.connected = false;
      if (!state.downSince) state.downSince = Date.now();
    }

    function attempt() {
      if (state.stopped) return;
      clearAttemptTimer();
      var url = BROKERS[state.brokerIndex % BROKERS.length];
      log("relay " + url);
      var client = mqtt.connect(url, {
        clientId: clientId + "-" + randomSuffix(),
        reconnectPeriod: 1500,
        connectTimeout: 8000,
        queueQoSZero: false,
        clean: true,
      });
      state.client = client;
      state.connected = false;
      state.downSince = Date.now();
      attemptTimer = setTimeout(function () { fail(client, "connect timed out"); }, 10000);
      client.on("connect", function () {
        clearAttemptTimer();
        state.connected = true;
        state.downSince = 0;
        onConnect(client);
      });
      client.on("message", function (topic, payload) {
        if (onMessage) onMessage(topic, payload);
      });
      client.on("error", function (err) {
        log("relay error " + (err && err.message ? err.message : err));
        if (!state.connected) fail(client, "connect failed");
      });
      client.on("close", function () { markDown(client); });
      client.on("offline", function () { markDown(client); });
      client.on("reconnect", function () { log("relay reconnecting"); });
    }

    attempt();
    state.forceReconnect = function (reason) {
      if (state.stopped) return;
      log("relay force reconnect: " + reason);
      clearAttemptTimer();
      var old = state.client;
      state.client = null;
      state.connected = false;
      state.downSince = Date.now();
      // Same broker first: the other side of the pair is probably still on it, and
      // splitting the pair across brokers would strand them apart. fail() moves on
      // to the next broker only if this attempt doesn't get through.
      try { if (old) old.end(true); } catch (e) {}
      attempt();
    };
    state.end = function () {
      state.stopped = true;
      clearAttemptTimer();
      try { if (state.client) state.client.end(true); } catch (e) {}
      state.client = null;
      state.connected = false;
    };
    return state;
  }

  // ---- Viewer: the laptop, an external display for the phone's mono view. ----

  var SHARPEN_VS =
    "attribute vec2 p; varying vec2 uv;" +
    "void main() { uv = vec2(p.x * 0.5 + 0.5, 0.5 - p.y * 0.5); gl_Position = vec4(p, 0.0, 1.0); }";

  // Contrast-adaptive sharpening (after AMD CAS): strong on soft detail, backed off where an
  // edge is already high-contrast, so codec blocks and edges don't grow halos.
  var SHARPEN_FS = [
    "precision mediump float;",
    "uniform sampler2D t; uniform vec2 texel; uniform float sharp; varying vec2 uv;",
    "void main() {",
    "  vec3 c = texture2D(t, uv).rgb;",
    "  vec3 n = texture2D(t, uv - vec2(0.0, texel.y)).rgb;",
    "  vec3 s = texture2D(t, uv + vec2(0.0, texel.y)).rgb;",
    "  vec3 e = texture2D(t, uv + vec2(texel.x, 0.0)).rgb;",
    "  vec3 w = texture2D(t, uv - vec2(texel.x, 0.0)).rgb;",
    "  vec3 mn = min(c, min(min(n, s), min(e, w)));",
    "  vec3 mx = max(c, max(max(n, s), max(e, w)));",
    "  vec3 amp = sqrt(clamp(min(mn, 1.0 - mx) / max(mx, vec3(0.0001)), 0.0, 1.0));",
    "  vec3 wt = amp * (-0.2 * sharp);",
    "  gl_FragColor = vec4(clamp((c + (n + s + e + w) * wt) / (1.0 + 4.0 * wt), 0.0, 1.0), 1.0);",
    "}",
  ].join("\n");

  // Draws frames on the stage: sharpened through WebGL when it works, plainly on a 2D canvas
  // when it doesn't. Every failure lands on the 2D canvas, which always paints, and that canvas
  // is kept within a second of the live picture so a lost GL context never shows black.
  function makeRenderer(canvas2d, canvasGl) {
    var ctx = canvas2d && canvas2d.getContext ? canvas2d.getContext("2d") : null;
    var gl = null;
    var uTexel = null;
    var uSharp = null;
    var scratch = null;
    var scratchCtx = null;
    var viaScratch = {};
    var verified = {};
    var backupAt = 0;
    var glFrames = 0;

    function use2d(reason) {
      if (gl) log("renderer: 2D canvas (" + reason + ")");
      gl = null;
      show(canvasGl, false);
      show(canvas2d, true);
    }

    function compile(type, source) {
      var shader = gl.createShader(type);
      gl.shaderSource(shader, source);
      gl.compileShader(shader);
      if (!gl.getShaderParameter(shader, gl.COMPILE_STATUS)) throw new Error(gl.getShaderInfoLog(shader) || "shader");
      return shader;
    }

    function initGl() {
      if (!canvasGl || !canvasGl.getContext) return;
      var attrs = { alpha: false, antialias: false, depth: false, stencil: false, premultipliedAlpha: false };
      try { gl = canvasGl.getContext("webgl", attrs) || canvasGl.getContext("experimental-webgl", attrs); } catch (e) { gl = null; }
      if (!gl) return;
      try {
        var program = gl.createProgram();
        gl.attachShader(program, compile(gl.VERTEX_SHADER, SHARPEN_VS));
        gl.attachShader(program, compile(gl.FRAGMENT_SHADER, SHARPEN_FS));
        gl.linkProgram(program);
        if (!gl.getProgramParameter(program, gl.LINK_STATUS)) throw new Error(gl.getProgramInfoLog(program) || "link");
        gl.useProgram(program);
        gl.bindBuffer(gl.ARRAY_BUFFER, gl.createBuffer());
        gl.bufferData(gl.ARRAY_BUFFER, new Float32Array([-1, -1, 1, -1, -1, 1, 1, 1]), gl.STATIC_DRAW);
        var loc = gl.getAttribLocation(program, "p");
        gl.enableVertexAttribArray(loc);
        gl.vertexAttribPointer(loc, 2, gl.FLOAT, false, 0, 0);
        gl.bindTexture(gl.TEXTURE_2D, gl.createTexture());
        // Frames are not powers of two: WebGL 1 samples such a texture as black unless it has
        // no mipmaps and clamps at the edges.
        gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.LINEAR);
        gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.LINEAR);
        gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE);
        gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
        gl.uniform1i(gl.getUniformLocation(program, "t"), 0);
        uTexel = gl.getUniformLocation(program, "texel");
        uSharp = gl.getUniformLocation(program, "sharp");
      } catch (e) {
        log("WebGL sharpen unavailable: " + (e && e.message ? e.message : e));
        gl = null;
        return;
      }
      canvasGl.addEventListener("webglcontextlost", function (ev) {
        ev.preventDefault();
        use2d("WebGL context lost");
      }, false);
      show(canvas2d, false);
      show(canvasGl, true);
      log("renderer: WebGL sharpen");
    }

    function draw2d(source, w, h) {
      if (!ctx) return;
      if (canvas2d.width !== w || canvas2d.height !== h) {
        canvas2d.width = w;
        canvas2d.height = h;
      }
      ctx.imageSmoothingEnabled = true;
      ctx.imageSmoothingQuality = "high";
      ctx.drawImage(source, 0, 0, w, h);
    }

    function upload(source, w, h, kind) {
      if (!viaScratch[kind]) {
        try {
          gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA, gl.RGBA, gl.UNSIGNED_BYTE, source);
          return;
        } catch (e) {
          viaScratch[kind] = true;
          log("renderer: " + kind + " frames upload through a canvas");
        }
      }
      if (!scratch) {
        scratch = document.createElement("canvas");
        scratchCtx = scratch.getContext("2d");
      }
      if (scratch.width !== w || scratch.height !== h) {
        scratch.width = w;
        scratch.height = h;
      }
      scratchCtx.drawImage(source, 0, 0, w, h);
      gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA, gl.RGBA, gl.UNSIGNED_BYTE, scratch);
    }

    function drawGl(source, w, h, kind) {
      // Rendered at the display's own resolution, so the sharpen also does the upscale.
      var dpr = Math.min(window.devicePixelRatio || 1, 2);
      var boxW = Math.max(1, window.innerWidth * dpr);
      var boxH = Math.max(1, window.innerHeight * dpr);
      var scale = Math.min(boxW / w, boxH / h, 3840 / w);
      var outW = Math.max(1, Math.round(w * scale));
      var outH = Math.max(1, Math.round(h * scale));
      if (canvasGl.width !== outW || canvasGl.height !== outH) {
        canvasGl.width = outW;
        canvasGl.height = outH;
      }
      upload(source, w, h, kind);
      gl.viewport(0, 0, outW, outH);
      gl.uniform2f(uTexel, 1 / w, 1 / h);
      gl.uniform1f(uSharp, sharpnessFor(h, outH));
      gl.drawArrays(gl.TRIANGLE_STRIP, 0, 4);
      glFrames++;
    }

    function meanLuma(data) {
      var sum = 0;
      for (var i = 0; i + 2 < data.length; i += 4) sum += data[i] * 0.3 + data[i + 1] * 0.59 + data[i + 2] * 0.11;
      return sum / Math.max(1, data.length / 4);
    }

    // A GL path can "succeed" and still draw black (an upload some engine doesn't support).
    // Compare a few GL output pixels with the same frame drawn in 2D, once per frame kind.
    function glDrewBlack(w, h) {
      var px = new Uint8Array(4);
      var glSamples = [];
      var marks = [0.25, 0.5, 0.75];
      for (var a = 0; a < 3; a++) {
        for (var b = 0; b < 3; b++) {
          gl.readPixels(Math.floor(canvasGl.width * marks[a]), Math.floor(canvasGl.height * marks[b]), 1, 1,
            gl.RGBA, gl.UNSIGNED_BYTE, px);
          glSamples.push(px[0], px[1], px[2], 255);
        }
      }
      var src = [];
      for (var x = 0; x < 3; x++) {
        for (var y = 0; y < 3; y++) {
          var d = ctx.getImageData(Math.floor(w * marks[x]), Math.floor(h * marks[y]), 1, 1).data;
          src.push(d[0], d[1], d[2], 255);
        }
      }
      return meanLuma(src) > 18 && meanLuma(glSamples) < 4;
    }

    initGl();
    if (!gl) use2d("no WebGL");

    return {
      draw: function (source, w, h, kind) {
        if (!(w > 0 && h > 0)) return;
        if (gl && gl.isContextLost && gl.isContextLost()) use2d("WebGL context lost");
        if (gl) {
          try {
            drawGl(source, w, h, kind);
          } catch (e) {
            use2d(e && e.message ? e.message : "draw failed");
          }
        }
        var now = Date.now();
        if (!gl || !verified[kind] || now - backupAt > 1000) {
          draw2d(source, w, h);
          backupAt = now;
        }
        if (gl && !verified[kind] && glFrames >= 2) {
          var black = false;
          try { black = glDrewBlack(w, h); } catch (e) { black = false; }
          if (!black) verified[kind] = true;
          else if (!viaScratch[kind]) {
            viaScratch[kind] = true;
            log("renderer: " + kind + " frames drew black in WebGL; uploading through a canvas");
          } else {
            use2d(kind + " frames drew black in WebGL");
          }
        }
      },
    };
  }

  function startViewer(mqtt) {
    var aspect = displayAspect(window.screen && screen.width, window.screen && screen.height);
    var code = room;
    var relay = null;
    var stopped = false;
    var live = false;
    var drawn = false;
    var phoneHere = false;
    var lastFrameAt = 0;
    var helloAt = 0;
    var currentToken = "";
    var relayDownSince = 0;
    var renderer = makeRenderer(viewEl, document.getElementById("view-gl"));
    var decoding = false;
    var pendingBlob = null;

    var viewerId = randomSuffix() + randomSuffix();
    var decoderCodecs = [];
    var avcBroken = !(window.VideoDecoder && window.EncodedVideoChunk);
    var decoder = null;
    var decoderKey = "";
    var waitingKey = true;
    var wantKey = true;
    var expectSeq = -1;
    var lost = 0;
    var lostReported = 0;
    var decodeErrors = [];
    var drawFailures = 0;
    var firstVideoAt = 0;
    var lastVideoAt = 0;
    var lastDecodedAt = 0;
    var controlSoon = null;

    function waiting() {
      live = false;
      drawn = false;
      phoneHere = false;
      currentToken = "";
      helloAt = 0;
      resetDecoder();
      expectSeq = -1;
      firstVideoAt = 0;
      lastVideoAt = 0;
      if (document.body) document.body.classList.remove("live");
      show(stageEl, false);
      show(pairEl, true);
      showPairingQr(code, aspect);
      setStatus("Scan QR");
    }

    // The phone is here; the stage still waits for a drawn frame, so a slow first frame
    // shows the pairing panel with a status line, never an empty black stage.
    function goLive() {
      if (live) return;
      live = true;
      if (!drawn) setStatus("Phone connected — starting video…");
    }

    function markDrawn() {
      lastFrameAt = Date.now();
      if (drawn) return;
      drawn = true;
      if (qrEl) qrEl.innerHTML = "";
      show(pairEl, false);
      show(stageEl, true);
      if (document.body) document.body.classList.add("live");
      setStatus("Live");
    }

    function mode() {
      return avcBroken || !decoderCodecs.length ? "jpeg" : "avc";
    }

    function sendControl() {
      if (controlSoon) clearTimeout(controlSoon);
      controlSoon = null;
      var client = relay && relay.client;
      if (!client || !client.connected) return;
      var delta = lost - lostReported;
      lostReported = lost;
      try {
        client.publish(controlTopic(code), packControl(viewerId, mode(), decoderCodecs, wantKey, delta), { qos: 0 });
      } catch (e) {}
    }

    function requestKey() {
      if (wantKey && controlSoon) return;
      wantKey = true;
      if (!controlSoon) controlSoon = setTimeout(sendControl, 150);
    }

    function resetDecoder() {
      if (decoder) {
        try { decoder.close(); } catch (e) {}
      }
      decoder = null;
      decoderKey = "";
      waitingKey = true;
    }

    // Once H.264 has failed here, this page asks for JPEG for the rest of its life.
    function giveUpAvc(reason) {
      if (avcBroken) return;
      avcBroken = true;
      resetDecoder();
      log("H.264 off (" + reason + "); asking the phone for JPEG");
      sendControl();
    }

    function decodeFailed(err) {
      log("decode error: " + (err && err.message ? err.message : err));
      resetDecoder();
      requestKey();
      var now = Date.now();
      decodeErrors.push(now);
      decodeErrors = decodeErrors.filter(function (t) { return now - t < 20000; });
      if (decodeErrors.length >= 3) giveUpAvc("repeated decoder errors");
    }

    function configureDecoder(p, key) {
      if (decoderCodecs.indexOf(p.codec) < 0) {
        giveUpAvc("phone sent " + p.codec);
        return false;
      }
      resetDecoder();
      try {
        var d = new VideoDecoder({
          output: function (frame) {
            try {
              renderer.draw(frame, frame.displayWidth, frame.displayHeight, "video");
              drawFailures = 0;
              lastDecodedAt = Date.now();
              markDrawn();
            } catch (e) {
              if (++drawFailures >= 3) giveUpAvc("video frames would not draw");
            } finally {
              frame.close();
            }
          },
          error: function (e) { if (d === decoder) decodeFailed(e); },
        });
        d.configure({ codec: p.codec, codedWidth: p.width, codedHeight: p.height, optimizeForLatency: true });
        decoder = d;
        decoderKey = key;
        return true;
      } catch (e) {
        decodeFailed(e);
        return false;
      }
    }

    function onVideo(payload) {
      if (avcBroken || !decoderCodecs.length) return;
      var p = unpackVideo(payload);
      if (!p) return;
      phoneHere = true;
      lastVideoAt = Date.now();
      if (!firstVideoAt) firstVideoAt = lastVideoAt;
      goLive();
      if (expectSeq >= 0 && p.seq !== expectSeq) {
        waitingKey = true;
        if (!p.key) lost++;
        if (lost <= 5 || lost % 50 === 0) log("video gap: expected " + expectSeq + ", got " + p.seq + (p.key ? " (key)" : ""));
      }
      expectSeq = (p.seq + 1) & 0xffff;

      var key = p.codec + "/" + p.width + "x" + p.height;
      if (!decoder || decoder.state === "closed" || key !== decoderKey) {
        if (!p.key) {
          requestKey();
          return;
        }
        if (!configureDecoder(p, key)) return;
      }
      if (waitingKey && !p.key) {
        requestKey();
        return;
      }
      if (decoder.decodeQueueSize > 6) {
        resetDecoder();
        requestKey();
        return;
      }
      try {
        decoder.decode(new EncodedVideoChunk({ type: p.key ? "key" : "delta", timestamp: p.ts * 1000, data: p.data }));
        if (p.key) {
          waitingKey = false;
          wantKey = false;
        }
      } catch (e) {
        decodeFailed(e);
      }
    }

    function decodeImage(blob) {
      function withImg() {
        return new Promise(function (resolve, reject) {
          var url = URL.createObjectURL(blob);
          var im = new Image();
          im.onload = function () {
            resolve({ source: im, w: im.naturalWidth, h: im.naturalHeight, done: function () { URL.revokeObjectURL(url); } });
          };
          im.onerror = function () {
            URL.revokeObjectURL(url);
            reject(new Error("bad JPEG"));
          };
          im.src = url;
        });
      }
      if (typeof createImageBitmap !== "function") return withImg();
      return createImageBitmap(blob).then(function (bmp) {
        return { source: bmp, w: bmp.width, h: bmp.height, done: function () { if (bmp.close) bmp.close(); } };
      }, withImg);
    }

    function drawJpeg(blob) {
      if (decoding) {
        pendingBlob = blob;
        return;
      }
      decoding = true;
      decodeImage(blob).then(function (pic) {
        try {
          renderer.draw(pic.source, pic.w, pic.h, "image");
          markDrawn();
        } catch (e) {
          log("JPEG draw failed: " + (e && e.message ? e.message : e));
        }
        pic.done();
      }).catch(function () {}).then(function () {
        decoding = false;
        if (pendingBlob) {
          var next = pendingBlob;
          pendingBlob = null;
          drawJpeg(next);
        }
      });
    }

    function onMessage(topic, payload) {
      if (topic === videoTopic(code)) {
        var packets = unpackBundle(payload);
        for (var i = 0; i < packets.length; i++) onVideo(packets[i]);
        return;
      }
      if (topic === helloTopic(code)) {
        var hello = parseHello(payload);
        if (!hello) return;
        if (hello.on) {
          currentToken = hello.token;
          phoneHere = true;
          if (!helloAt) helloAt = Date.now();
          goLive();
        } else if (hello.token && hello.token !== currentToken) {
          // A previous phone's WebView finishing its close late: not this stream's goodbye.
          log("ignored stale goodbye");
        } else if (phoneHere) {
          // The phone holding the room said goodbye (Share stopped): back to the QR.
          waiting();
        }
        return;
      }
      if (topic !== frameTopic(code)) return;
      var bytes = toBytes(payload);
      if (!bytes.length) return;
      phoneHere = true;
      window.__castFrames = (window.__castFrames || 0) + 1;
      goLive();
      drawJpeg(new Blob([bytes], { type: "image/jpeg" }));
    }

    function connect() {
      if (relay) relay.end();
      relay = connectRelay(mqtt, "gurlar-view-" + code, function (client) {
        client.subscribe([frameTopic(code), videoTopic(code), helloTopic(code)], { qos: 0 }, function (err) {
          if (err) log("subscribe failed " + err.message);
        });
        if (!live) waiting();
        requestKey();
        sendControl();
      }, onMessage);
    }

    function probeDecoders() {
      if (avcBroken) return Promise.resolve([]);
      return Promise.all(AVC_CODECS.map(function (codec) {
        return VideoDecoder.isConfigSupported({ codec: codec, codedWidth: 1280, codedHeight: 720, optimizeForLatency: true })
          .then(function (r) { return r && r.supported ? codec : null; }, function () { return null; });
      })).then(function (list) { return list.filter(Boolean); }, function () { return []; });
    }

    probeDecoders().then(function (codecs) {
      decoderCodecs = codecs;
      log("H.264 decode: " + (codecs.length ? codecs.join(", ") : "unavailable, JPEG only"));
      sendControl();
    });

    setInterval(function () {
      if (stopped) return;
      sendControl();
    }, 2000);

    setInterval(function () {
      if (stopped) return;
      var now = Date.now();
      // H.264 arriving but no picture for six seconds: whatever is wrong, JPEG will show something.
      if (!avcBroken && lastVideoAt && now - lastVideoAt < 2000 &&
          now - Math.max(lastDecodedAt, firstVideoAt) > 6000) {
        giveUpAvc("no picture from H.264");
      }
      window.__castDiag = {
        frames: window.__castFrames || 0, live: live, drawn: drawn, phone: phoneHere,
        mode: mode(), codecs: decoderCodecs, lost: lost,
      };
      var relayConnected = !!(relay && relay.connected && relay.client && relay.client.connected);
      if (!relayConnected) {
        if (!relayDownSince) relayDownSince = Date.now();
        else if (relay && Date.now() - relayDownSince > 25000) {
          relayDownSince = Date.now();
          relay.forceReconnect("viewer relay offline");
        }
        if (live) setStatus("Waiting…");
        return;
      }
      relayDownSince = 0;
      if (!live) return;
      // Silence since the last frame (or the hello, before the first frame): a phone that
      // died without saying goodbye eventually hands the page back to the QR. Generous,
      // because a relay reconnect on a flaky network can take a while and the stream
      // should resume into the same pairing, not be declared dead early.
      var quiet = Date.now() - Math.max(lastFrameAt, helloAt);
      if (quiet > 45000) {
        waiting();
        return;
      }
      if (statusEl) {
        var text = !drawn ? "Phone connected — starting video…" :
          quiet > STALL_MS && phoneHere ? "Waiting…" : "Live";
        if (statusEl.textContent !== text) setStatus(text);
      }
    }, 1000);

    if (stageEl) {
      stageEl.addEventListener("click", function () {
        var el = stageEl;
        if (document.fullscreenElement || document.webkitFullscreenElement) {
          (document.exitFullscreen || document.webkitExitFullscreen).call(document);
        } else if (el.requestFullscreen) {
          el.requestFullscreen();
        } else if (el.webkitRequestFullscreen) {
          el.webkitRequestFullscreen();
        }
      });
    }

    window.__viewer = {
      reconnect: function () { connect(); },
      leave: function () {
        stopped = true;
        if (relay) relay.end();
        // Reloading mints a fresh room, like any new load.
        location.href = location.pathname + "?t=" + Date.now();
      },
    };
    if (reconnectBtn) reconnectBtn.onclick = function () { window.__viewer.reconnect(); };
    if (leaveBtn) leaveBtn.onclick = function () { window.__viewer.leave(); };
    show(actionsEl, true);
    connect();
  }

  // ---- Host: hidden WebView inside the AR app. Unity pushes source JPEG data URLs; this
  // page turns them into H.264 (or a relay-sized JPEG) for whoever is watching. ----

  function startHost(mqtt, code) {
    if (!fromApp) {
      setStatus("open this from the AR app");
      show(pairEl, false);
      return;
    }
    show(pairEl, false);
    show(actionsEl, false);
    if (roomEl) roomEl.textContent = code;

    var relay = null;
    var stopped = false;
    var sent = 0;
    var dropped = 0;

    // One phone holds the room at a time. A second phone that scanned the same QR yields
    // (its frames would otherwise interleave into the presenter's stream) and takes over
    // when the holder says goodbye or its heartbeat goes stale.
    var token = randomSuffix();
    var claimed = false;
    var claimTs = 0;
    var yielding = false;
    var otherToken = "";
    var otherHelloAt = 0;
    var heartbeatTimer = null;
    var claimTimer = null;
    var lastSelfHelloAt = 0;
    var relayDownSince = 0;
    // Latest-frame-only intake: one source frame being turned into stream output, and a newer
    // one replaces the waiting one instead of queueing behind it. JPEG output is the same,
    // one in flight. H.264 output can't drop frames freely (each depends on the last), so it
    // queues briefly; a queue that grows is flushed and restarted from a keyframe.
    var ingesting = false;
    var pendingDataUrl = null;
    var sendingFrame = false;
    var frameGeneration = 0;
    var lastFrameSentAt = 0;
    var lastIngestAt = 0;

    var viewers = {};
    var mode = { mode: "jpeg", codec: "" };
    var encoderCodecs = [];
    var encoderAccel = {};
    var avcBroken = !(window.VideoEncoder && window.VideoFrame);
    var encoder = null;
    var encoderSig = "";
    var encoderErrors = [];
    var pendingMeta = {};
    var spsPps = null;
    var seq = 0;
    var needKey = true;
    var awaitKeyOut = true;
    var lastKeyAt = 0;
    var keyRequestedAt = 0;
    var fedSinceOutput = 0;
    var lastOutputAt = 0;
    var frameFailures = 0;
    var sendQueue = [];
    var pumpTimer = null;
    var lastPublishAt = 0;
    var level = START_LEVEL;
    var badStreak = 0;
    var goodStreak = 0;
    var falls = 0;
    var lastFallAt = 0;
    var stats = newStats();
    var jpegQuality = 0.72;
    var canvas = document.createElement("canvas");
    var canvasCtx = canvas.getContext("2d");
    var t0 = Date.now();

    function newStats() {
      return { offered: 0, skipped: 0, flushes: 0, lost: 0, buffered: 0, backlog: 0, bytes: 0 };
    }

    function publishHello(on) {
      var client = relay && relay.client;
      if (!client || !client.connected) return;
      client.publish(helloTopic(code), (on ? "1:" : "0:") + token + ":" + Date.now(),
        { qos: 0, retain: true });
    }

    function tryClaim() {
      if (stopped || claimed) return;
      var otherFresh = otherToken && Date.now() - otherHelloAt < HELLO_STALE_MS;
      if (otherFresh) {
        if (!yielding) {
          yielding = true;
          setStatus("Another phone is sharing");
        }
        return;
      }
      yielding = false;
      claimed = true;
      claimTs = Date.now();
      lastSelfHelloAt = Date.now();
      publishHello(true);
      setStatus("streaming to room " + code);
      if (heartbeatTimer) clearInterval(heartbeatTimer);
      heartbeatTimer = setInterval(function () { publishHello(true); }, 3000);
    }

    function onHello(topic, payload) {
      var hello = parseHello(payload);
      if (!hello) return;
      if (hello.token === token) {
        // The broker echoes our retained heartbeat back to us. That round trip is the
        // host's end-to-end relay health check: a socket can look connected long after
        // it has stopped delivering anything.
        if (hello.on) lastSelfHelloAt = Date.now();
        return;
      }
      if (hello.on) {
        otherToken = hello.token;
        otherHelloAt = hello.ts || Date.now();
        // Both claimed in the same moment: the lower token keeps the room, the other steps
        // down. (Timestamps can't arbitrate: the heartbeat keeps refreshing them.)
        if (claimed && hello.token < token) {
          claimed = false;
          yielding = true;
          if (heartbeatTimer) clearInterval(heartbeatTimer);
          setStatus("Another phone is sharing");
        }
      } else if (otherToken === hello.token) {
        otherToken = "";
        otherHelloAt = 0;
        if (!claimed) tryClaim();
      }
    }

    function onControl(payload) {
      var ctl = parseControl(payload);
      if (!ctl) return;
      var isNew = !viewers[ctl.id];
      viewers[ctl.id] = { mode: ctl.mode, codecs: ctl.codecs, at: Date.now() };
      stats.lost += ctl.lost;
      if (isNew) log("viewer " + ctl.id + " watches " + (ctl.mode === "avc" ? "H.264 " + ctl.codecs.join(",") : "JPEG"));
      if (ctl.wantKey || isNew) requestKeyframe();
      updateMode();
    }

    function requestKeyframe() {
      var now = Date.now();
      if (now - keyRequestedAt < 400) return;
      keyRequestedAt = now;
      needKey = true;
    }

    function updateMode() {
      var now = Date.now();
      for (var id in viewers) {
        if (Object.prototype.hasOwnProperty.call(viewers, id) && now - viewers[id].at > 30000) delete viewers[id];
      }
      var next = chooseMode(viewers, now, encoderCodecs, avcBroken);
      if (next.mode === mode.mode && next.codec === mode.codec) return;
      log("stream → " + (next.mode === "avc" ? "H.264 " + next.codec + " VBR" : "JPEG"));
      mode = next;
      sendQueue.length = 0;
      needKey = true;
      awaitKeyOut = true;
      if (next.mode !== "avc") closeEncoder();
    }

    relay = connectRelay(mqtt, "gurlar-host", function (client) {
      // A rebuilt connection invalidates any frame still marked in flight from the old one.
      frameGeneration++;
      sendingFrame = false;
      pendingDataUrl = null;
      sendQueue.length = 0;
      needKey = true;
      awaitKeyOut = true;
      client.subscribe([helloTopic(code), controlTopic(code)], { qos: 0 });
      if (claimed) {
        lastSelfHelloAt = Date.now();
        publishHello(true);
        return;
      }
      // Give a retained hello from the phone already presenting time to arrive.
      if (claimTimer) clearTimeout(claimTimer);
      claimTimer = setTimeout(tryClaim, 1200);
    }, function (topic, payload) {
      if (topic === controlTopic(code)) onControl(payload);
      else onHello(topic, payload);
    });

    // While yielding, keep checking whether the holder has gone away. This also watches
    // the relay itself: if the socket drops and stays down, or our own heartbeat stops
    // coming back from the broker, rebuild the connection instead of dropping frames
    // into a dead client forever.
    var yieldTimer = setInterval(function () {
      var relayConnected = !!(relay && relay.connected && relay.client && relay.client.connected);
      window.__castState = {
        claimed: claimed, yielding: yielding, other: otherToken, sent: sent, dropped: dropped,
        connected: relayConnected, lastSelfHelloAt: lastSelfHelloAt, lastFrameSentAt: lastFrameSentAt,
        sendingFrame: sendingFrame, hasPendingFrame: !!pendingDataUrl,
        mode: mode.mode, codec: mode.codec, level: level, rung: LADDER[level], queued: sendQueue.length,
        encoders: encoderCodecs, viewers: Object.keys(viewers).length,
      };
      if (stopped) return;
      if (!relayConnected) {
        if (!relayDownSince) relayDownSince = Date.now();
        else if (relay && Date.now() - relayDownSince > 25000) {
          relayDownSince = Date.now();
          relay.forceReconnect("host relay offline");
        }
        return;
      }
      relayDownSince = 0;
      if (claimed && lastSelfHelloAt && Date.now() - lastSelfHelloAt > 30000) {
        // Give the rebuilt connection a fresh grace period before judging it again.
        lastSelfHelloAt = Date.now();
        relay.forceReconnect("host heartbeat stopped returning");
        return;
      }
      if (!claimed) tryClaim();
    }, 1500);

    function destroy() {
      stopped = true;
      frameGeneration++;
      sendingFrame = false;
      pendingDataUrl = null;
      if (claimTimer) clearTimeout(claimTimer);
      if (heartbeatTimer) clearInterval(heartbeatTimer);
      if (yieldTimer) clearInterval(yieldTimer);
      if (healthTimer) clearInterval(healthTimer);
      if (pumpTimer) clearTimeout(pumpTimer);
      sendQueue.length = 0;
      closeEncoder();
      try {
        if (claimed) publishHello(false);
      } catch (e) {}
      claimed = false;
      if (relay) relay.end();
    }

    // Best-effort look at the WebSocket's unsent bytes, where the MQTT browser client
    // exposes them. Above this, a new frame would only arrive late: drop it instead.
    function socketBufferedBytes(client) {
      try {
        var stream = client && client.stream;
        var socket = stream && (stream.socket || stream._socket || stream.ws || stream._ws);
        return socket && typeof socket.bufferedAmount === "number" ? socket.bufferedAmount : 0;
      } catch (e) { return 0; }
    }

    function liveClient() {
      var client = relay && relay.client;
      return !stopped && claimed && client && client.connected ? client : null;
    }

    function dataUrlBytes(dataUrl) {
      var comma = dataUrl.indexOf(",");
      var binary = atob(comma >= 0 ? dataUrl.substring(comma + 1) : dataUrl);
      var bytes = new Uint8Array(binary.length);
      for (var i = 0; i < binary.length; i++) bytes[i] = binary.charCodeAt(i);
      return bytes;
    }

    function sourceBlob(dataUrl) {
      if (typeof fetch === "function") {
        return fetch(dataUrl).then(function (r) { return r.blob(); }).catch(function () {
          return new Blob([dataUrlBytes(dataUrl)], { type: "image/jpeg" });
        });
      }
      return Promise.resolve(new Blob([dataUrlBytes(dataUrl)], { type: "image/jpeg" }));
    }

    function decodeSource(dataUrl) {
      return sourceBlob(dataUrl).then(function (blob) {
        function withImg() {
          return new Promise(function (resolve, reject) {
            var im = new Image();
            im.onload = function () {
              resolve({ source: im, w: im.naturalWidth, h: im.naturalHeight, blob: blob, done: function () {} });
            };
            im.onerror = function () { reject(new Error("bad source frame")); };
            im.src = dataUrl;
          });
        }
        if (typeof createImageBitmap !== "function") return withImg();
        return createImageBitmap(blob).then(function (bmp) {
          return { source: bmp, w: bmp.width, h: bmp.height, blob: blob, done: function () { if (bmp.close) bmp.close(); } };
        }, withImg);
      });
    }

    function drawToCanvas(pic, w, h) {
      if (canvas.width !== w || canvas.height !== h) {
        canvas.width = w;
        canvas.height = h;
      }
      canvasCtx.imageSmoothingEnabled = true;
      canvasCtx.imageSmoothingQuality = "high";
      canvasCtx.drawImage(pic.source, 0, 0, w, h);
    }

    // ---- H.264 ----

    function encoderConfig(codec, w, h, rung, accel) {
      return {
        codec: codec,
        width: w,
        height: h,
        bitrate: rung.kbps * 1000,
        framerate: rung.fps,
        bitrateMode: "variable",
        latencyMode: "realtime",
        hardwareAcceleration: accel,
        avc: { format: "annexb" },
      };
    }

    function probeEncoders() {
      if (avcBroken) return Promise.resolve();
      var accels = ["prefer-hardware", "no-preference"];
      function tryAccel(codec, i) {
        if (i >= accels.length) return Promise.resolve();
        return VideoEncoder.isConfigSupported(encoderConfig(codec, 1280, 720, LADDER[1], accels[i]))
          .then(function (r) {
            if (r && r.supported) {
              encoderCodecs.push(codec);
              encoderAccel[codec] = accels[i];
              return null;
            }
            return tryAccel(codec, i + 1);
          }, function () { return tryAccel(codec, i + 1); });
      }
      return AVC_CODECS.reduce(function (chain, codec) {
        return chain.then(function () { return tryAccel(codec, 0); });
      }, Promise.resolve()).then(function () {
        log("H.264 encode: " + (encoderCodecs.length ? encoderCodecs.join(", ") : "unavailable, JPEG only"));
        if (!encoderCodecs.length) avcBroken = true;
        updateMode();
      });
    }

    function closeEncoder() {
      if (encoder) {
        try { encoder.close(); } catch (e) {}
      }
      encoder = null;
      encoderSig = "";
      pendingMeta = {};
      spsPps = null;
      fedSinceOutput = 0;
    }

    // Three encoder failures in a minute and this page sends JPEG until it's reopened.
    function encoderFailed(reason) {
      log("H.264 encoder: " + reason);
      closeEncoder();
      needKey = true;
      awaitKeyOut = true;
      var now = Date.now();
      encoderErrors.push(now);
      encoderErrors = encoderErrors.filter(function (t) { return now - t < 60000; });
      if (encoderErrors.length >= 3) {
        avcBroken = true;
        updateMode();
      }
    }

    function ensureEncoder(w, h, rung) {
      var sig = mode.codec + "/" + w + "x" + h + "/" + rung.kbps + "/" + rung.fps;
      if (encoder && encoder.state === "configured" && sig === encoderSig) return true;
      try {
        if (!encoder || encoder.state === "closed") {
          var e = new VideoEncoder({
            output: function (chunk, meta) { if (e === encoder) onChunk(chunk, meta); },
            error: function (err) { if (e === encoder) encoderFailed(err && err.message ? err.message : String(err)); },
          });
          encoder = e;
          lastOutputAt = Date.now();
        }
        encoder.configure(encoderConfig(mode.codec, w, h, rung, encoderAccel[mode.codec] || "no-preference"));
        encoderSig = sig;
        spsPps = null;
        needKey = true;
        log("H.264 " + w + "x" + h + " VBR ~" + rung.kbps + " kbps @ " + rung.fps + " fps");
        return true;
      } catch (err) {
        encoderFailed("configure failed: " + (err && err.message ? err.message : err));
        return false;
      }
    }

    function encodeFrame(pic) {
      var rung = LADDER[level];
      var size = evenSize(Math.min(rung.h, pic.h), pic.w / pic.h);
      if (!ensureEncoder(size.w, size.h, rung)) return;
      drawToCanvas(pic, size.w, size.h);
      var ts = (Date.now() - t0) * 1000;
      var key = needKey || Date.now() - lastKeyAt > KEYFRAME_MS;
      var frame = null;
      try {
        frame = new VideoFrame(canvas, { timestamp: ts });
        pendingMeta[ts] = { w: size.w, h: size.h, codec: mode.codec };
        encoder.encode(frame, { keyFrame: key });
        frameFailures = 0;
      } catch (err) {
        delete pendingMeta[ts];
        if (++frameFailures >= 3) {
          frameFailures = 0;
          encoderFailed("frames rejected: " + (err && err.message ? err.message : err));
        }
        return;
      } finally {
        if (frame) frame.close();
      }
      if (key) {
        needKey = false;
        lastKeyAt = Date.now();
      }
      fedSinceOutput++;
    }

    function onChunk(chunk, meta) {
      lastOutputAt = Date.now();
      fedSinceOutput = 0;
      var info = pendingMeta[chunk.timestamp];
      delete pendingMeta[chunk.timestamp];
      if (!info || mode.mode !== "avc" || info.codec !== mode.codec) return;
      var data = new Uint8Array(chunk.byteLength);
      chunk.copyTo(data);
      data = toAnnexB(data);
      if (meta && meta.decoderConfig && meta.decoderConfig.description) {
        var ps = avccToAnnexB(meta.decoderConfig.description);
        if (ps && ps.length) spsPps = ps;
      }
      var key = chunk.type === "key";
      // A viewer can only start from a keyframe that carries its own SPS/PPS.
      if (key && !hasSps(data) && spsPps) data = concatBytes(spsPps, data);
      var packet = packVideo(seq, key, info.w, info.h, Math.floor(chunk.timestamp / 1000) >>> 0, info.codec, data);
      seq = (seq + 1) & 0xffff;
      stats.bytes += data.length;
      enqueue(packet, key);
    }

    function enqueue(packet, key) {
      if (sendQueue.length >= 8) {
        sendQueue.length = 0;
        stats.flushes++;
        needKey = true;
        awaitKeyOut = true;
      }
      // After a flush the viewer can't use deltas until the next keyframe; the skipped
      // sequence numbers tell it to wait for one.
      if (awaitKeyOut && !key) return;
      if (key) awaitKeyOut = false;
      sendQueue.push(packet);
      pump();
    }

    function pump() {
      if (pumpTimer || sendingFrame || !sendQueue.length) return;
      var client = liveClient();
      if (!client) {
        sendQueue.length = 0;
        needKey = true;
        awaitKeyOut = true;
        return;
      }
      var wait = PUBLISH_MS - (Date.now() - lastPublishAt);
      if (socketBufferedBytes(client) > 262144) wait = Math.max(wait, 40);
      if (wait > 0) {
        pumpTimer = setTimeout(function () { pumpTimer = null; pump(); }, wait);
        return;
      }
      var packet = packBundle(sendQueue.splice(0, BUNDLE_MAX));
      var generation = frameGeneration;
      sendingFrame = true;
      lastPublishAt = Date.now();
      try {
        client.publish(videoTopic(code), packet, { qos: 0 }, function (err) {
          if (generation !== frameGeneration) return;
          sendingFrame = false;
          if (err) {
            dropped++;
            needKey = true;
            awaitKeyOut = true;
          } else {
            sent++;
            lastFrameSentAt = Date.now();
            if (sent % 300 === 0) log("video packets sent " + sent + ", dropped " + dropped);
          }
          pump();
        });
      } catch (e) {
        sendingFrame = false;
        dropped++;
        needKey = true;
        awaitKeyOut = true;
      }
    }

    // ---- JPEG, for pages that can't decode H.264 ----

    function publishJpeg(bytes) {
      var client = liveClient();
      if (!client || socketBufferedBytes(client) > 196608) {
        dropped++;
        stats.skipped++;
        return;
      }
      var generation = frameGeneration;
      sendingFrame = true;
      try {
        client.publish(frameTopic(code), bytes, { qos: 0 }, function (err) {
          if (generation !== frameGeneration) return;
          sendingFrame = false;
          if (err) dropped++;
          else {
            sent++;
            lastFrameSentAt = Date.now();
            if (sent % 120 === 0) log("frames sent " + sent + ", dropped " + dropped);
          }
        });
      } catch (e) {
        sendingFrame = false;
        dropped++;
      }
    }

    function blobBytes(blob) {
      var read = blob.arrayBuffer ? blob.arrayBuffer() : new Response(blob).arrayBuffer();
      return read.then(function (buffer) { return new Uint8Array(buffer); });
    }

    // JPEG costs several times H.264's bytes for the same picture: capped at 540p and sized by
    // quality to stay inside what a public relay carries.
    function sendJpeg(pic) {
      var size = evenSize(Math.min(LADDER[level].h, 540, pic.h), pic.w / pic.h);
      var original = pic.blob;
      drawToCanvas(pic, size.w, size.h);
      sendingFrame = true;
      function forwardOriginal() {
        blobBytes(original).then(function (bytes) {
          sendingFrame = false;
          publishJpeg(bytes);
        }, function () { sendingFrame = false; dropped++; });
      }
      try {
        canvas.toBlob(function (blob) {
          if (!blob || !blob.size) {
            forwardOriginal();
            return;
          }
          if (blob.size > 56000 && jpegQuality > 0.4) jpegQuality -= 0.04;
          else if (blob.size < 28000 && jpegQuality < 0.82) jpegQuality += 0.02;
          stats.bytes += blob.size;
          blobBytes(blob).then(function (bytes) {
            sendingFrame = false;
            publishJpeg(bytes);
          }, forwardOriginal);
        }, "image/jpeg", jpegQuality);
      } catch (e) {
        forwardOriginal();
      }
    }

    // ---- Intake ----

    function busy() {
      var client = relay && relay.client;
      if (mode.mode === "avc") {
        return sendQueue.length > 4 || (encoder && encoder.encodeQueueSize > 1) ||
          socketBufferedBytes(client) > 131072;
      }
      return sendingFrame;
    }

    function ingest(dataUrl) {
      var rung = LADDER[level];
      // JPEG is one frame per message, so it stays under the broker's message cap.
      var fps = mode.mode === "avc" ? rung.fps : Math.min(rung.fps, 1000 / PUBLISH_MS);
      var now = Date.now();
      if (now - lastIngestAt < 1000 / fps - (mode.mode === "avc" ? 12 : 0)) return;
      if (busy()) {
        stats.skipped++;
        dropped++;
        return;
      }
      ingesting = true;
      lastIngestAt = now;
      decodeSource(dataUrl).then(function (pic) {
        try {
          if (mode.mode === "avc") encodeFrame(pic);
          else sendJpeg(pic);
        } catch (e) {
          log("frame failed: " + (e && e.message ? e.message : e));
        }
        pic.done();
      }).catch(function () { dropped++; }).then(function () {
        ingesting = false;
        var next = pendingDataUrl;
        pendingDataUrl = null;
        if (next && !stopped) ingest(next);
      });
    }

    // Once a second: judge the relay's health, move along the ladder, watch the encoder.
    var healthTimer = setInterval(function () {
      if (stopped || !claimed) return;
      var now = Date.now();
      updateMode();
      if (mode.mode === "avc" && encoder && fedSinceOutput >= 10 && now - lastOutputAt > 3000) {
        encoderFailed("stalled");
      }
      stats.buffered = socketBufferedBytes(relay && relay.client);
      stats.backlog = Math.max(encoder ? encoder.encodeQueueSize : 0, sendQueue.length > 5 ? 3 : 0);
      if (stats.offered > 0) {
        var bad = secondIsBad(stats);
        var good = !bad && secondIsGood(stats);
        badStreak = bad ? badStreak + 1 : 0;
        goodStreak = bad ? 0 : good ? goodStreak + 1 : goodStreak;
        var next = nextLevel(level, badStreak, goodStreak, falls);
        if (next !== level) {
          if (next > level) {
            falls++;
            lastFallAt = now;
          }
          level = next;
          badStreak = 0;
          goodStreak = 0;
          var rung = LADDER[level];
          log("quality → " + rung.h + "p ~" + rung.kbps + " kbps @ " + rung.fps + " fps (" +
            Math.round(stats.bytes / 1024) + " KB last second, buffered " +
            Math.round(stats.buffered / 1024) + " KB)");
        }
      }
      if (falls > 0 && now - lastFallAt > 120000) {
        falls--;
        lastFallAt = now;
      }
      stats = newStats();
    }, 1000);

    probeEncoders();

    window.__cast = {
      pushFrame: function (dataUrl) {
        if (stopped || !claimed || !dataUrl) {
          dropped++;
          return;
        }
        stats.offered++;
        if (ingesting) {
          // Keep only the newest waiting frame; the replaced one is dropped on purpose.
          if (pendingDataUrl) {
            dropped++;
            stats.skipped++;
          }
          pendingDataUrl = dataUrl;
          return;
        }
        ingest(dataUrl);
      },
      setStatus: setStatus,
      destroy: destroy,
    };

    window.addEventListener("pagehide", destroy);
  }

  function boot() {
    blockCameraSync();

    if (role === "host") {
      if (!room) {
        setStatus("missing room");
        return;
      }
      show(pairEl, false);
      loadMqttWithRetry()
        .then(function (mqtt) { startHost(mqtt, room); });
      return;
    }

    // Every load is a new, independent pairing: a fresh room, rendered as a QR at once.
    // Nothing is shared or carried over — any number of phone/display pairs can run at the
    // same time in the same room code space without ever seeing or blocking each other;
    // only whoever scans this tab's QR joins this tab.
    room = newRoomCode();
    history.replaceState({}, "", "?room=" + encodeURIComponent(room));
    var aspect = displayAspect(window.screen && screen.width, window.screen && screen.height);
    showPairingQr(room, aspect);
    setStatus("Scan QR");

    loadMqttWithRetry()
      .then(function (mqtt) { startViewer(mqtt); });
  }

  if (document.readyState === "loading") document.addEventListener("DOMContentLoaded", boot);
  else boot();
})();
