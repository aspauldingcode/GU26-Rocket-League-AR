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
    show(pairEl, true);
    qrEl.innerHTML = "";
    qrEl.setAttribute("aria-label", "QR for room " + code);

    // Desktop Safari (esp. with fingerprinting protection) can blank canvas/SVG/GIF
    // QR renders. An HTML table of black/white cells always paints.
    var make = window.qrcode || (typeof qrcode !== "undefined" ? qrcode : null);
    if (typeof make === "function") {
      try {
        var qr = make(0, "M");
        qr.addData(url);
        qr.make();
        qrEl.innerHTML = qr.createTableTag(6, 4);
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
    remote.width = 240;
    remote.height = 240;
    remote.alt = "QR for room " + code;
    remote.referrerPolicy = "no-referrer";
    remote.src = "https://api.qrserver.com/v1/create-qr-code/?size=240x240&margin=10&data=" + data;
    remote.onerror = function () {
      remote.onerror = null;
      remote.src = "https://quickchart.io/qr?size=240&text=" + data;
    };
    qrEl.appendChild(remote);
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

  function attachStream(stream) {
    if (!videoEl) return;
    // Drop the QR the moment the phone's mono cast arrives.
    if (qrEl) qrEl.innerHTML = "";
    show(pairEl, false);
    show(stageEl, true);
    show(actionsEl, true);
    if (document.body) document.body.classList.add("live");
    setStatus("live — mono AR stream");

    videoEl.srcObject = stream;
    videoEl.playsInline = true;
    videoEl.muted = true;
    videoEl.autoplay = true;
    var play = videoEl.play();
    if (play && typeof play.catch === "function") play.catch(function () {});
  }

  function startViewer(Peer, code) {
    show(actionsEl, true);
    show(stageEl, false);
    setStatus("open Share on the phone, then look at this QR");

    var peer = null;
    var callRef = null;
    var timer = null;
    var stopped = false;
    var delay = RETRY_START_MS;
    var generation = 0;
    var emptyStream = new MediaStream();

    function clearTimer() {
      if (timer) {
        clearTimeout(timer);
        timer = null;
      }
    }

    // Soft retry: keep the same PeerJS peer, only re-dial the phone.
    // Full connect() was slow because it re-registered a new peer every attempt.
    function scheduleCall(reason) {
      if (stopped) return;
      clearVideo();
      if (document.body) document.body.classList.remove("live");
      show(pairEl, true);
      show(stageEl, false);
      setStatus(reason || "waiting for phone…");
      clearTimer();
      timer = setTimeout(function () {
        if (stopped) return;
        if (peer && !peer.destroyed && peer.open) dial(generation);
        else connect();
      }, delay);
      delay = nextRetryMs(delay);
    }

    function schedulePeer(reason) {
      if (stopped) return;
      setStatus(reason || "Reconnecting…");
      clearTimer();
      timer = setTimeout(connect, delay);
      delay = nextRetryMs(delay);
    }

    function dial(gen) {
      try { if (callRef) callRef.close(); } catch (e) {}
      callRef = null;
      callRef = peer.call(hostPeerId(code), emptyStream);
      if (!callRef) {
        scheduleCall("Could not reach the phone");
        return;
      }
      callRef.on("stream", function (stream) {
        if (gen !== generation || stopped) return;
        delay = RETRY_START_MS;
        attachStream(stream);
      });
      callRef.on("close", function () {
        if (gen === generation && !stopped) scheduleCall("Stream paused. Retrying…");
      });
      callRef.on("error", function () {
        if (gen === generation && !stopped) scheduleCall("Call error. Retrying…");
      });
    }

    function connect() {
      if (stopped) return;
      clearTimer();
      var gen = ++generation;
      destroyPeer(peer, callRef);
      peer = null;
      callRef = null;

      peer = new Peer(undefined, { debug: 0 });
      peer.on("open", function () {
        if (gen !== generation || stopped) return;
        delay = RETRY_START_MS;
        setStatus("scan this QR with Share view");
        dial(gen);
      });
      peer.on("disconnected", function () {
        if (gen !== generation || stopped || !peer || peer.destroyed) return;
        try { peer.reconnect(); }
        catch (e) { schedulePeer("Signaling dropped. Retrying…"); }
      });
      peer.on("error", function (err) {
        if (gen !== generation || stopped) return;
        var type = err && err.type;
        if (type === "peer-unavailable") {
          // Phone has not scanned yet: cheap re-dial, do not rebuild the peer.
          delay = RETRY_START_MS;
          scheduleCall("scan this QR with the phone");
          return;
        }
        schedulePeer("Retrying…");
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
        // Full reload mints a brand-new QR room.
        location.href = location.pathname + "?t=" + Date.now();
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

    // Every refresh gets a brand-new room/QR. Reusing ?room= left people waiting
    // forever on a dead PeerJS host from the previous page load.
    room = newRoomCode();
    history.replaceState({}, "", "?room=" + encodeURIComponent(room));

    // Draw the QR before PeerJS loads so Safari always has something to scan.
    showPairingQr(room);
    setStatus("open Share on the phone and look at this QR — stream starts automatically");

    loadPeerJs()
      .then(function (Peer) { startViewer(Peer, room); })
      .catch(function (err) {
        setStatus((err && err.message ? err.message : String(err)) + " — QR is ready, retry PeerJS");
      });
  }

  if (document.readyState === "loading") document.addEventListener("DOMContentLoaded", boot);
  else boot();
})();
