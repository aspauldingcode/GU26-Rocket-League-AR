/* External display for the AR app's mono view.
   The page owns the room: it registers gurlar-view-{room} on PeerJS and shows a QR for it.
   Share on the phone scans the QR, opens this page hidden as ?role=host, and dials the page.
   Frames are JPEGs pushed by Unity, sent over a reliable data channel in chunks, newest wins.
   No media tracks: an empty-stream call carried no video, and canvas capture stalls in hidden
   WebViews, which is why the page never updated. */
(function () {
  "use strict";

  var PEER_PREFIX = "gurlar-";
  var RETRY_START_MS = 400;
  var RETRY_MAX_MS = 3000;
  var CHUNK_CHARS = 16000;
  // Drop a frame rather than queue it behind a slow link: a live view wants the newest.
  var MAX_BUFFERED_BYTES = 512 * 1024;
  var OPEN_TIMEOUT_MS = 8000;
  var STALL_MS = 4000;
  var LOST_MS = 15000;
  var JPEG_PREFIX = "data:image/jpeg;base64,";

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

  function log(text) {
    try {
      var bridge = window.webkit && window.webkit.messageHandlers && window.webkit.messageHandlers.castlog;
      if (bridge) bridge.postMessage(String(text));
    } catch (e) {}
    try { console.log("[cast] " + text); } catch (e) {}
  }

  var liveStatusEl = document.getElementById("live-status");

  function setStatus(text) {
    if (statusEl) statusEl.textContent = text || "";
    if (liveStatusEl) liveStatusEl.textContent = text || "";
    log(text);
  }

  function show(el, on) {
    if (el) el.style.display = on ? "" : "none";
  }

  function viewerPeerId(code) {
    return PEER_PREFIX + "view-" + code;
  }

  function nextRetryMs(current) {
    var n = Math.floor((current || RETRY_START_MS) * 1.6);
    if (n < RETRY_START_MS) n = RETRY_START_MS;
    if (n > RETRY_MAX_MS) n = RETRY_MAX_MS;
    return n;
  }

  function newRoomCode() {
    var alphabet = "abcdefghijklmnopqrstuvwxyz0123456789";
    var out = "";
    var bytes = new Uint8Array(8);
    (window.crypto || window.msCrypto).getRandomValues(bytes);
    for (var i = 0; i < bytes.length; i++) out += alphabet[bytes[i] % alphabet.length];
    return out;
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

  // "F<id>:<index>:<count>:<base64>"; base64 has no ':'.
  function chunkFrame(id, base64, size) {
    size = size || CHUNK_CHARS;
    var count = Math.max(1, Math.ceil(base64.length / size));
    var out = [];
    for (var i = 0; i < count; i++)
      out.push("F" + id + ":" + i + ":" + count + ":" + base64.substr(i * size, size));
    return out;
  }

  function FrameAssembler() {
    this.id = -1;
    this.count = 0;
    this.parts = null;
    this.got = 0;
    this.lastDone = -1;
  }

  // Returns the whole base64 frame once its last chunk arrives, else null.
  FrameAssembler.prototype.push = function (message) {
    if (typeof message !== "string" || message.charAt(0) !== "F") return null;
    var a = message.indexOf(":");
    var b = message.indexOf(":", a + 1);
    var c = message.indexOf(":", b + 1);
    if (a < 0 || b < 0 || c < 0) return null;
    var id = parseInt(message.substring(1, a), 10);
    var index = parseInt(message.substring(a + 1, b), 10);
    var count = parseInt(message.substring(b + 1, c), 10);
    if (!(count > 0) || index < 0 || index >= count || id <= this.lastDone) return null;
    if (id !== this.id) {
      if (id < this.id) return null;
      this.id = id;
      this.count = count;
      this.parts = new Array(count);
      this.got = 0;
    }
    if (this.parts[index] === undefined) {
      this.parts[index] = message.substring(c + 1);
      this.got++;
    }
    if (this.got < this.count) return null;
    var whole = this.parts.join("");
    this.lastDone = id;
    this.parts = null;
    this.id = -1;
    return whole;
  };

  window.__castLogic = {
    viewerPeerId: viewerPeerId,
    nextRetryMs: nextRetryMs,
    newRoomCode: newRoomCode,
    displayAspect: displayAspect,
    chunkFrame: chunkFrame,
    FrameAssembler: FrameAssembler,
    RETRY_START_MS: RETRY_START_MS,
    RETRY_MAX_MS: RETRY_MAX_MS,
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

  function destroyPeer(peer) {
    try { if (peer && !peer.destroyed) peer.destroy(); } catch (e) {}
  }

  function blockCameraSync() {
    if (!navigator.mediaDevices || !navigator.mediaDevices.getUserMedia) return;
    navigator.mediaDevices.getUserMedia = function () {
      return Promise.reject(new Error("This page only watches the AR app."));
    };
  }

  function loadPeerJs() {
    return new Promise(function (resolve, reject) {
      if (window.Peer) {
        resolve(window.Peer);
        return;
      }
      var urls = [
        "https://unpkg.com/peerjs@1.5.4/dist/peerjs.min.js",
        "https://cdn.jsdelivr.net/npm/peerjs@1.5.4/dist/peerjs.min.js",
      ];
      var i = 0;
      function tryNext() {
        if (i >= urls.length) {
          reject(new Error("Could not load PeerJS"));
          return;
        }
        var script = document.createElement("script");
        script.src = urls[i++];
        script.onload = function () {
          if (window.Peer) resolve(window.Peer);
          else tryNext();
        };
        script.onerror = tryNext;
        document.head.appendChild(script);
      }
      tryNext();
    });
  }

  // ---- Viewer: the laptop, an external display for the phone's mono view. ----

  function startViewer(Peer) {
    var aspect = displayAspect(window.screen && screen.width, window.screen && screen.height);
    var code = room;
    var peer = null;
    var conn = null;
    var timer = null;
    var stopped = false;
    var delay = RETRY_START_MS;
    var generation = 0;
    var assembler = new FrameAssembler();
    var decoding = false;
    var pending = null;
    var lastFrameAt = 0;
    var live = false;
    var ctx = viewEl && viewEl.getContext ? viewEl.getContext("2d") : null;
    var img = new Image();

    function waiting(reason) {
      live = false;
      if (document.body) document.body.classList.remove("live");
      show(stageEl, false);
      showPairingQr(code, aspect);
      setStatus(reason || "open Share on the phone and look at this QR — the view starts by itself");
    }

    function goLive() {
      if (live) return;
      live = true;
      if (qrEl) qrEl.innerHTML = "";
      show(pairEl, false);
      show(stageEl, true);
      if (document.body) document.body.classList.add("live");
      setStatus("live — mono AR view (click for full screen)");
    }

    function draw(base64) {
      if (decoding) {
        pending = base64;
        return;
      }
      decoding = true;
      img.onload = function () {
        if (ctx) {
          if (viewEl.width !== img.naturalWidth || viewEl.height !== img.naturalHeight) {
            viewEl.width = img.naturalWidth;
            viewEl.height = img.naturalHeight;
          }
          ctx.drawImage(img, 0, 0);
        }
        decoding = false;
        if (pending) {
          var next = pending;
          pending = null;
          draw(next);
        }
      };
      img.onerror = function () {
        decoding = false;
        pending = null;
      };
      img.src = JPEG_PREFIX + base64;
    }

    function onData(data) {
      var frame = assembler.push(data);
      if (!frame) return;
      lastFrameAt = Date.now();
      window.__castFrames = (window.__castFrames || 0) + 1;
      goLive();
      draw(frame);
    }

    function attach(c) {
      if (conn && conn !== c) {
        try { conn.close(); } catch (e) {}
      }
      conn = c;
      assembler = new FrameAssembler();
      lastFrameAt = 0;
      setStatus("phone connected — starting the view…");
      c.on("data", function (data) {
        if (conn === c) onData(data);
      });
      c.on("close", function () {
        if (conn !== c || stopped) return;
        conn = null;
        setStatus("phone disconnected — waiting for it to come back");
      });
      c.on("error", function () {
        if (conn !== c || stopped) return;
        conn = null;
      });
    }

    function schedulePeer(reason) {
      if (stopped) return;
      setStatus(reason || "reconnecting…");
      clearTimeout(timer);
      timer = setTimeout(connect, delay);
      delay = nextRetryMs(delay);
    }

    function newRoom() {
      code = newRoomCode();
      history.replaceState({}, "", "?room=" + encodeURIComponent(code));
      waiting();
    }

    function connect() {
      if (stopped) return;
      clearTimeout(timer);
      var gen = ++generation;
      destroyPeer(peer);
      peer = new Peer(viewerPeerId(code), { debug: 1 });
      peer.on("open", function () {
        if (gen !== generation || stopped) return;
        delay = RETRY_START_MS;
        if (!live) waiting();
      });
      peer.on("connection", function (c) {
        if (gen !== generation || stopped) {
          try { c.close(); } catch (e) {}
          return;
        }
        attach(c);
      });
      peer.on("disconnected", function () {
        if (gen !== generation || stopped || peer.destroyed) return;
        // Signalling only: an open data channel keeps streaming meanwhile.
        try { peer.reconnect(); } catch (e) { schedulePeer("signalling dropped, retrying…"); }
      });
      peer.on("error", function (err) {
        if (gen !== generation || stopped) return;
        var type = err && err.type;
        if (type === "unavailable-id") {
          newRoom();
          connect();
          return;
        }
        if (type === "peer-unavailable") return;
        schedulePeer("connection problem (" + (type || "error") + "), retrying…");
      });
    }

    setInterval(function () {
      if (!live || stopped) return;
      var quiet = Date.now() - lastFrameAt;
      if (quiet > LOST_MS && !conn) waiting("the phone stopped sharing — scan again to resume");
      else if (quiet > STALL_MS) setStatus("waiting for the phone…");
      else if (statusEl && statusEl.textContent.indexOf("live") !== 0) setStatus("live — mono AR view (click for full screen)");
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
      reconnect: function () {
        delay = RETRY_START_MS;
        connect();
      },
      leave: function () {
        stopped = true;
        clearTimeout(timer);
        destroyPeer(peer);
        location.href = location.pathname + "?t=" + Date.now();
      },
    };
    if (reconnectBtn) reconnectBtn.onclick = function () { window.__viewer.reconnect(); };
    if (leaveBtn) leaveBtn.onclick = function () { window.__viewer.leave(); };
    show(actionsEl, true);
    connect();
  }

  // ---- Host: hidden WebView inside the AR app. Unity pushes JPEG data URLs. ----

  function startHost(Peer, code) {
    if (!fromApp) {
      setStatus("open this from the AR app");
      show(pairEl, false);
      return;
    }
    show(pairEl, false);
    show(actionsEl, false);
    if (roomEl) roomEl.textContent = code;

    var peer = null;
    var conn = null;
    var stopped = false;
    var timer = null;
    var openGuard = null;
    var delay = RETRY_START_MS;
    var frameId = 0;
    var sent = 0;
    var dropped = 0;

    function clearTimers() {
      clearTimeout(timer);
      clearTimeout(openGuard);
      timer = openGuard = null;
    }

    function closeConn() {
      var c = conn;
      conn = null;
      try { if (c) c.close(); } catch (e) {}
    }

    function destroy() {
      stopped = true;
      clearTimers();
      closeConn();
      destroyPeer(peer);
      peer = null;
    }

    function scheduleDial(reason) {
      if (stopped) return;
      setStatus(reason);
      clearTimeout(timer);
      timer = setTimeout(dial, delay);
      delay = nextRetryMs(delay);
    }

    function scheduleBind(reason) {
      if (stopped) return;
      setStatus(reason);
      clearTimeout(timer);
      timer = setTimeout(bind, delay);
      delay = nextRetryMs(delay);
    }

    function dial() {
      if (stopped) return;
      if (!peer || peer.destroyed || !peer.open) {
        bind();
        return;
      }
      closeConn();
      var c = peer.connect(viewerPeerId(code), { serialization: "raw", reliable: true });
      if (!c) {
        scheduleDial("could not dial the website, retrying");
        return;
      }
      conn = c;
      clearTimeout(openGuard);
      openGuard = setTimeout(function () {
        if (conn === c && !c.open) {
          closeConn();
          scheduleDial("website did not answer, retrying");
        }
      }, OPEN_TIMEOUT_MS);
      c.on("open", function () {
        if (conn !== c) return;
        clearTimeout(openGuard);
        delay = RETRY_START_MS;
        setStatus("streaming to room " + code);
      });
      c.on("close", function () {
        if (conn !== c || stopped) return;
        conn = null;
        scheduleDial("website closed, redialing");
      });
      c.on("error", function () {
        if (conn !== c || stopped) return;
        conn = null;
        scheduleDial("link error, redialing");
      });
    }

    function bind() {
      if (stopped) return;
      clearTimers();
      closeConn();
      destroyPeer(peer);
      // A random id: nothing to collide with when Share restarts quickly.
      var p = new Peer(undefined, { debug: 1 });
      peer = p;
      setStatus("connecting to room " + code);
      p.on("open", function () {
        if (peer !== p || stopped) return;
        delay = RETRY_START_MS;
        dial();
      });
      p.on("disconnected", function () {
        if (peer !== p || stopped || p.destroyed) return;
        try { p.reconnect(); } catch (e) { scheduleBind("signalling dropped, rebinding"); }
      });
      p.on("error", function (err) {
        if (peer !== p || stopped) return;
        var type = err && err.type;
        // The website hasn't registered yet, or reloaded: keep dialing the same room.
        if (type === "peer-unavailable") {
          closeConn();
          scheduleDial("waiting for the website in room " + code);
          return;
        }
        scheduleBind("signalling error (" + (type || "error") + "), rebinding");
      });
    }

    window.__cast = {
      pushFrame: function (dataUrl) {
        var c = conn;
        if (stopped || !dataUrl || !c || !c.open) return;
        var channel = c.dataChannel;
        if ((channel && channel.bufferedAmount > MAX_BUFFERED_BYTES) || c.bufferSize > 0) {
          dropped++;
          return;
        }
        var base64 = dataUrl.indexOf(JPEG_PREFIX) === 0 ? dataUrl.substring(JPEG_PREFIX.length) : dataUrl;
        var chunks = chunkFrame(++frameId, base64);
        try {
          for (var i = 0; i < chunks.length; i++) c.send(chunks[i]);
          sent++;
        } catch (e) {
          scheduleDial("send failed, redialing");
        }
        if (frameId % 120 === 0) log("frames sent " + sent + ", dropped " + dropped);
      },
      setStatus: setStatus,
      destroy: destroy,
    };

    window.addEventListener("pagehide", destroy);
    bind();
  }

  function boot() {
    blockCameraSync();

    if (role === "host") {
      if (!room) {
        setStatus("missing room");
        return;
      }
      show(pairEl, false);
      loadPeerJs()
        .then(function (Peer) { startHost(Peer, room); })
        .catch(function (err) { setStatus(err.message || String(err)); });
      return;
    }

    // Every load gets a brand-new room, so a stale tab never shadows a live one.
    room = newRoomCode();
    history.replaceState({}, "", "?room=" + encodeURIComponent(room));
    var aspect = displayAspect(window.screen && screen.width, window.screen && screen.height);
    showPairingQr(room, aspect);
    setStatus("open Share on the phone and look at this QR — the view starts by itself");

    loadPeerJs()
      .then(function (Peer) { startViewer(Peer); })
      .catch(function (err) {
        setStatus((err && err.message ? err.message : String(err)) + " — reload to retry");
      });
  }

  if (document.readyState === "loading") document.addEventListener("DOMContentLoaded", boot);
  else boot();
})();
