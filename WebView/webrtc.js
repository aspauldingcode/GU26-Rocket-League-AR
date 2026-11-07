/* Phone hosts the mono AR UI; this page only watches.
   Open the site, scan the on-screen QR from Share view in the app. */
(function () {
  "use strict";

  var PEER_PREFIX = "gurlar-";
  // Keep retries snappy so a phone scan turns into video right away.
  var RETRY_START_MS = 400;
  var RETRY_MAX_MS = 3000;
  var params = new URLSearchParams(location.search);
  var hash = new URLSearchParams(location.hash.replace(/^#/, ""));
  var room = (params.get("room") || hash.get("room") || "").trim().toLowerCase();
  var role = (params.get("role") || "view").trim().toLowerCase();
  var fromApp = params.get("unity") === "1";

  var statusEl = document.getElementById("status");
  var videoEl = document.getElementById("view");
  var stageEl = document.getElementById("stage");
  var pairEl = document.getElementById("pair");
  var roomEl = document.getElementById("room");
  var qrEl = document.getElementById("qr");
  var actionsEl = document.getElementById("actions");
  var reconnectBtn = document.getElementById("reconnect");
  var leaveBtn = document.getElementById("leave");

  function setStatus(text) {
    if (statusEl) statusEl.textContent = text || "";
  }

  function show(el, on) {
    if (!el) return;
    el.style.display = on ? "" : "none";
  }

  function hostPeerId(code) {
    return PEER_PREFIX + "host-" + code;
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

  function pageUrlForRoom(code) {
    return location.origin + location.pathname + "?room=" + encodeURIComponent(code);
  }

  function showPairingQr(code) {
    if (roomEl) roomEl.textContent = code;
    if (!qrEl) return;
    var url = pageUrlForRoom(code);
    qrEl.alt = "QR for room " + code;
    show(pairEl, true);

    // Prefer local generator (no third-party image host). Fall back to public QR APIs.
    try {
      if (typeof qrcode === "function") {
        var qr = qrcode(0, "M");
        qr.addData(url);
        qr.make();
        qrEl.src = qr.createDataURL(6, 8);
        return;
      }
    } catch (e) {}

    var data = encodeURIComponent(url);
    qrEl.src = "https://api.qrserver.com/v1/create-qr-code/?size=240x240&margin=10&data=" + data;
    qrEl.onerror = function () {
      qrEl.onerror = null;
      qrEl.src = "https://quickchart.io/qr?size=240&text=" + data;
    };
  }

  window.__castLogic = {
    hostPeerId: hostPeerId,
    nextRetryMs: nextRetryMs,
    newRoomCode: newRoomCode,
    RETRY_START_MS: RETRY_START_MS,
    RETRY_MAX_MS: RETRY_MAX_MS,
  };

  function destroyPeer(peer, call) {
    try { if (call) call.close(); } catch (e) {}
    try { if (peer && !peer.destroyed) peer.destroy(); } catch (e) {}
  }

  function clearVideo() {
    if (videoEl) videoEl.srcObject = null;
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
      var script = document.createElement("script");
      script.src = "https://unpkg.com/peerjs@1.5.4/dist/peerjs.min.js";
      script.onload = function () {
        if (window.Peer) resolve(window.Peer);
        else reject(new Error("PeerJS failed to load"));
      };
      script.onerror = function () { reject(new Error("Could not load PeerJS")); };
      document.head.appendChild(script);
    });
  }

  function attachStream(stream) {
    if (!videoEl) return;
    videoEl.srcObject = stream;
    videoEl.playsInline = true;
    videoEl.muted = true;
    videoEl.play().catch(function () {});
    show(pairEl, false);
    show(stageEl, true);
    show(actionsEl, true);
    setStatus("streaming");
  }

  function startViewer(Peer, code) {
    show(actionsEl, true);
    show(stageEl, false);
    setStatus("scan this QR with Share view");

    var peer = null;
    var callRef = null;
    var timer = null;
    var stopped = false;
    var delay = RETRY_START_MS;
    var generation = 0;

    function clearTimer() {
      if (timer) {
        clearTimeout(timer);
        timer = null;
      }
    }

    function schedule(reason) {
      if (stopped) return;
      clearVideo();
      show(pairEl, true);
      show(stageEl, false);
      setStatus(reason || "Reconnecting…");
      clearTimer();
      timer = setTimeout(connect, delay);
      delay = nextRetryMs(delay);
    }

    function wireCall(gen) {
      callRef = peer.call(hostPeerId(code), new MediaStream());
      if (!callRef) {
        schedule("Could not reach the phone");
        return;
      }
      callRef.on("stream", function (stream) {
        if (gen !== generation || stopped) return;
        delay = RETRY_START_MS;
        attachStream(stream);
      });
      callRef.on("close", function () {
        if (gen === generation && !stopped) schedule("Stream paused. Retrying…");
      });
      callRef.on("error", function () {
        if (gen === generation && !stopped) schedule("Call error. Retrying…");
      });
    }

    function connect() {
      if (stopped) return;
      clearTimer();
      var gen = ++generation;
      destroyPeer(peer, callRef);
      peer = null;
      callRef = null;
      clearVideo();

      peer = new Peer(undefined, { debug: 1 });
      peer.on("open", function () {
        if (gen !== generation || stopped) return;
        setStatus("Waiting for the phone…");
        wireCall(gen);
      });
      peer.on("disconnected", function () {
        if (gen !== generation || stopped || !peer || peer.destroyed) return;
        try { peer.reconnect(); }
        catch (e) { schedule("Signaling dropped. Retrying…"); }
      });
      peer.on("error", function (err) {
        if (gen !== generation || stopped) return;
        var type = err && err.type;
        if (type === "peer-unavailable") {
          // Phone has not hosted yet: poll fast until Share view scans this QR.
          delay = RETRY_START_MS;
          schedule("scan this QR with the phone");
          return;
        }
        schedule("Retrying…");
      });
    }

    window.__viewer = {
      reconnect: function () {
        if (stopped) return;
        delay = RETRY_START_MS;
        connect();
      },
      leave: function () {
        stopped = true;
        clearTimer();
        destroyPeer(peer, callRef);
        location.search = "?room=" + encodeURIComponent(newRoomCode());
      },
    };

    if (reconnectBtn) reconnectBtn.onclick = function () { window.__viewer.reconnect(); };
    if (leaveBtn) leaveBtn.onclick = function () { window.__viewer.leave(); };
    connect();
  }

  function startHost(Peer, code) {
    if (!fromApp) {
      setStatus("Open this from the AR app");
      show(pairEl, false);
      return;
    }

    show(pairEl, false);
    show(actionsEl, false);
    if (roomEl) roomEl.textContent = code;
    setStatus("Sharing " + code);

    var canvas = document.createElement("canvas");
    canvas.width = 640;
    canvas.height = 360;
    var ctx = canvas.getContext("2d");
    ctx.fillStyle = "#102018";
    ctx.fillRect(0, 0, canvas.width, canvas.height);

    var stream = canvas.captureStream(12);
    var img = new Image();
    var peer = null;
    var calls = [];
    var stopped = false;
    var bindTimer = null;
    var delay = RETRY_START_MS;

    function closeCalls() {
      for (var i = 0; i < calls.length; i++) {
        try { calls[i].close(); } catch (e) {}
      }
      calls = [];
    }

    function destroy() {
      stopped = true;
      if (bindTimer) clearTimeout(bindTimer);
      bindTimer = null;
      closeCalls();
      destroyPeer(peer, null);
      peer = null;
    }

    function scheduleBind() {
      if (stopped) return;
      setStatus("Rebinding host…");
      if (bindTimer) clearTimeout(bindTimer);
      bindTimer = setTimeout(function () {
        bindTimer = null;
        bindPeer();
        delay = nextRetryMs(delay);
      }, delay);
    }

    function bindPeer() {
      if (stopped) return;
      closeCalls();
      destroyPeer(peer, null);
      peer = new Peer(hostPeerId(code), { debug: 1 });
      peer.on("open", function () {
        if (stopped) return;
        delay = RETRY_START_MS;
        setStatus("Sharing " + code);
      });
      peer.on("call", function (call) {
        if (stopped) {
          try { call.close(); } catch (e) {}
          return;
        }
        call.answer(stream);
        calls.push(call);
        setStatus("Viewer connected");
        call.on("close", function () {
          calls = calls.filter(function (c) { return c !== call; });
          if (!stopped && calls.length === 0) setStatus("Sharing " + code);
        });
      });
      peer.on("disconnected", function () {
        if (stopped || !peer || peer.destroyed) return;
        try { peer.reconnect(); } catch (e) { scheduleBind(); }
      });
      peer.on("error", function (err) {
        if (stopped) return;
        var type = err && err.type;
        if (type === "unavailable-id" || type === "network" || type === "disconnected" ||
            type === "socket-error" || type === "server-error") {
          scheduleBind();
          return;
        }
        setStatus(err && err.message ? err.message : "Host error");
      });
    }

    window.__cast = {
      pushFrame: function (dataUrl) {
        if (!dataUrl || stopped) return;
        img.onload = function () {
          ctx.drawImage(img, 0, 0, canvas.width, canvas.height);
        };
        img.src = dataUrl;
      },
      setStatus: setStatus,
      destroy: destroy,
    };

    window.addEventListener("pagehide", destroy);
    window.addEventListener("beforeunload", destroy);
    bindPeer();
  }

  function boot() {
    blockCameraSync();

    if (role === "host") {
      if (!room) {
        setStatus("Missing room");
        return;
      }
      show(pairEl, false);
      loadPeerJs()
        .then(function (Peer) { startHost(Peer, room); })
        .catch(function (err) { setStatus(err.message || String(err)); });
      return;
    }

    if (!room) {
      room = newRoomCode();
      history.replaceState({}, "", "?room=" + encodeURIComponent(room));
    }

    // Draw the QR before PeerJS loads so the page is never a blank box.
    showPairingQr(room);
    setStatus("scan this QR with Share view");

    loadPeerJs()
      .then(function (Peer) { startViewer(Peer, room); })
      .catch(function (err) {
        setStatus((err && err.message ? err.message : String(err)) + " (QR still works after refresh)");
      });
  }

  if (document.readyState === "loading") document.addEventListener("DOMContentLoaded", boot);
  else boot();
})();
