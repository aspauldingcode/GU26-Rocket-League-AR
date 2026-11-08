/* External display for the AR app's mono view.
   The page owns the room and shows a QR for it. Share in the app scans the QR, opens this page
   hidden as ?role=host, and both sides meet on a WebSocket relay (MQTT over WSS): the phone
   publishes JPEG frames, this page draws them. No WebRTC — its data channel from inside the
   app's WebView never opened, so the page sat on "Connecting…" while nothing crossed. */
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

  // Connects to the first broker that answers; onConnect runs on every (re)connect, which is
  // when subscriptions and the host's hello must be (re)sent.
  function connectRelay(mqtt, clientId, onConnect, onMessage) {
    var state = { client: null, brokerIndex: 0, stopped: false };

    function attempt() {
      if (state.stopped) return;
      var url = BROKERS[state.brokerIndex % BROKERS.length];
      log("relay " + url);
      var client = mqtt.connect(url, {
        clientId: clientId + "-" + randomSuffix(),
        reconnectPeriod: 1500,
        connectTimeout: 10000,
        queueQoSZero: false,
        clean: true,
      });
      state.client = client;
      var connected = false;
      client.on("connect", function () {
        connected = true;
        onConnect(client);
      });
      client.on("message", function (topic, payload) {
        if (onMessage) onMessage(topic, payload);
      });
      client.on("error", function (err) {
        log("relay error " + (err && err.message ? err.message : err));
        if (!connected && !state.stopped) {
          // First broker never answered (blocked or down): fall over to the next one.
          state.brokerIndex++;
          try { client.end(true); } catch (e) {}
          attempt();
        }
      });
    }

    attempt();
    state.end = function () {
      state.stopped = true;
      try { if (state.client) state.client.end(true); } catch (e) {}
    };
    return state;
  }

  // ---- Viewer: the laptop, an external display for the phone's mono view. ----

  function startViewer(mqtt) {
    var aspect = displayAspect(window.screen && screen.width, window.screen && screen.height);
    var code = room;
    var relay = null;
    var stopped = false;
    var live = false;
    var phoneHere = false;
    var lastFrameAt = 0;
    var helloAt = 0;
    var currentToken = "";
    var ctx = viewEl && viewEl.getContext ? viewEl.getContext("2d") : null;
    var img = new Image();
    var lastUrl = null;
    var decoding = false;
    var pendingBlob = null;

    function waiting() {
      live = false;
      phoneHere = false;
      currentToken = "";
      helloAt = 0;
      if (document.body) document.body.classList.remove("live");
      show(stageEl, false);
      showPairingQr(code, aspect);
      setStatus("Scan QR");
    }

    function goLive() {
      if (live) return;
      live = true;
      if (qrEl) qrEl.innerHTML = "";
      show(pairEl, false);
      show(stageEl, true);
      if (document.body) document.body.classList.add("live");
      setStatus("Live");
    }

    function draw(blob) {
      if (decoding) {
        pendingBlob = blob;
        return;
      }
      decoding = true;
      var url = URL.createObjectURL(blob);
      img.onload = function () {
        if (ctx) {
          if (viewEl.width !== img.naturalWidth || viewEl.height !== img.naturalHeight) {
            viewEl.width = img.naturalWidth;
            viewEl.height = img.naturalHeight;
          }
          ctx.drawImage(img, 0, 0);
        }
        if (lastUrl) URL.revokeObjectURL(lastUrl);
        lastUrl = url;
        decoding = false;
        if (pendingBlob) {
          var next = pendingBlob;
          pendingBlob = null;
          draw(next);
        }
      };
      img.onerror = function () {
        URL.revokeObjectURL(url);
        decoding = false;
        pendingBlob = null;
      };
      img.src = url;
    }

    function onMessage(topic, payload) {
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
      if (topic !== frameTopic(code) || !payload || payload.length === 0) return;
      phoneHere = true;
      lastFrameAt = Date.now();
      window.__castFrames = (window.__castFrames || 0) + 1;
      goLive();
      draw(new Blob([payload], { type: "image/jpeg" }));
    }

    function connect() {
      if (relay) relay.end();
      relay = connectRelay(mqtt, "gurlar-view-" + code, function (client) {
        client.subscribe([frameTopic(code), helloTopic(code)], { qos: 0 }, function (err) {
          if (err) log("subscribe failed " + err.message);
        });
        if (!live) waiting();
      }, onMessage);
    }

    setInterval(function () {
      if (stopped) return;
      window.__castDiag = { frames: window.__castFrames || 0, live: live, phone: phoneHere };
      if (!live) return;
      // Silence since the last frame (or the hello, before the first frame): a phone that
      // died without saying goodbye eventually hands the page back to the QR.
      var quiet = Date.now() - Math.max(lastFrameAt, helloAt);
      if (quiet > 20000) {
        waiting();
        return;
      }
      if (statusEl) {
        if (quiet > STALL_MS && phoneHere) setStatus("Waiting…");
        else if (statusEl.textContent !== "Live") setStatus("Live");
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
        // "New QR": forget this tab's room so the reload mints a fresh one.
        try { sessionStorage.removeItem("gurlar-room"); } catch (e) {}
        location.href = location.pathname + "?t=" + Date.now();
      },
    };
    if (reconnectBtn) reconnectBtn.onclick = function () { window.__viewer.reconnect(); };
    if (leaveBtn) leaveBtn.onclick = function () { window.__viewer.leave(); };
    show(actionsEl, true);
    connect();
  }

  // ---- Host: hidden WebView inside the AR app. Unity pushes JPEG data URLs. ----

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
      publishHello(true);
      setStatus("streaming to room " + code);
      if (heartbeatTimer) clearInterval(heartbeatTimer);
      heartbeatTimer = setInterval(function () { publishHello(true); }, 3000);
    }

    function onHello(topic, payload) {
      var hello = parseHello(payload);
      if (!hello || hello.token === token) return;
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

    relay = connectRelay(mqtt, "gurlar-host", function (client) {
      client.subscribe(helloTopic(code), { qos: 0 });
      if (claimed) {
        publishHello(true);
        return;
      }
      // Give a retained hello from the phone already presenting time to arrive.
      if (claimTimer) clearTimeout(claimTimer);
      claimTimer = setTimeout(tryClaim, 1200);
    }, onHello);

    // While yielding, keep checking whether the holder has gone away.
    var yieldTimer = setInterval(function () {
      window.__castState = { claimed: claimed, yielding: yielding, other: otherToken, sent: sent, dropped: dropped };
      if (!stopped && !claimed) tryClaim();
    }, 1500);

    function destroy() {
      stopped = true;
      if (claimTimer) clearTimeout(claimTimer);
      if (heartbeatTimer) clearInterval(heartbeatTimer);
      if (yieldTimer) clearInterval(yieldTimer);
      try {
        if (claimed) publishHello(false);
      } catch (e) {}
      claimed = false;
      if (relay) relay.end();
    }

    window.__cast = {
      pushFrame: function (dataUrl) {
        var client = relay && relay.client;
        if (stopped || !claimed || !dataUrl || !client || !client.connected) {
          dropped++;
          return;
        }
        var comma = dataUrl.indexOf(",");
        var binary = atob(comma >= 0 ? dataUrl.substring(comma + 1) : dataUrl);
        var bytes = new Uint8Array(binary.length);
        for (var i = 0; i < binary.length; i++) bytes[i] = binary.charCodeAt(i);
        client.publish(frameTopic(code), bytes, { qos: 0 });
        sent++;
        if (sent % 120 === 0) log("frames sent " + sent + ", dropped " + dropped);
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
      loadMqtt()
        .then(function (mqtt) { startHost(mqtt, room); })
        .catch(function (err) { setStatus(err.message || String(err)); });
      return;
    }

    // The room survives a refresh of this tab (sessionStorage), so an accidental reload —
    // or a crash — reconnects to the phone still streaming instead of orphaning it behind a
    // new QR. A new tab, or "New QR", mints a fresh room.
    if (!room) {
      try { room = sessionStorage.getItem("gurlar-room") || ""; } catch (e) {}
    }
    if (!room) room = newRoomCode();
    try { sessionStorage.setItem("gurlar-room", room); } catch (e) {}
    history.replaceState({}, "", "?room=" + encodeURIComponent(room));
    var aspect = displayAspect(window.screen && screen.width, window.screen && screen.height);
    showPairingQr(room, aspect);
    setStatus("Scan QR");

    loadMqtt()
      .then(function (mqtt) { startViewer(mqtt); })
      .catch(function (err) {
        setStatus("Reload");
        log(err && err.message ? err.message : String(err));
      });
  }

  if (document.readyState === "loading") document.addEventListener("DOMContentLoaded", boot);
  else boot();
})();
