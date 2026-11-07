/* Local AR cast: the phone app publishes a mono UI view; this page only watches.
   Signaling uses PeerJS (IDs stay private in the room code). Media is peer to peer.
   The public GitHub Pages site never publishes a camera and never hosts a global stream. */
(function () {
  "use strict";

  var PEER_PREFIX = "gurlar-";
  var RETRY_START_MS = 1200;
  var RETRY_MAX_MS = 8000;
  var params = new URLSearchParams(location.search);
  var hash = new URLSearchParams(location.hash.replace(/^#/, ""));
  var room = (params.get("room") || hash.get("room") || "").trim().toLowerCase();
  var role = (params.get("role") || "view").trim().toLowerCase();
  var fromApp = params.get("unity") === "1";

  var statusEl = document.getElementById("status");
  var detailEl = document.getElementById("detail");
  var videoEl = document.getElementById("view");
  var stageEl = document.getElementById("stage");
  var roomEl = document.getElementById("room");
  var formEl = document.getElementById("join");
  var roomInput = document.getElementById("room-input");
  var actionsEl = document.getElementById("actions");
  var reconnectBtn = document.getElementById("reconnect");
  var leaveBtn = document.getElementById("leave");

  function setStatus(title, detail) {
    if (statusEl) statusEl.textContent = title || "";
    if (detailEl) detailEl.textContent = detail || "";
  }

  function showStage(on) {
    if (stageEl) stageEl.hidden = !on;
  }

  function showJoin(on) {
    if (formEl) formEl.hidden = !on;
  }

  function showActions(on) {
    if (actionsEl) actionsEl.hidden = !on;
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

  // Exported for node tests.
  window.__castLogic = { hostPeerId: hostPeerId, nextRetryMs: nextRetryMs, RETRY_START_MS: RETRY_START_MS, RETRY_MAX_MS: RETRY_MAX_MS };

  function destroyPeer(peer, call) {
    try {
      if (call) call.close();
    } catch (e) {}
    try {
      if (peer && !peer.destroyed) peer.destroy();
    } catch (e) {}
  }

  function clearVideo() {
    if (!videoEl) return;
    videoEl.srcObject = null;
  }

  // Never open the phone or laptop camera from this page.
  function blockCameraSync() {
    if (!navigator.mediaDevices || !navigator.mediaDevices.getUserMedia) return;
    navigator.mediaDevices.getUserMedia = function () {
      return Promise.reject(
        new Error("This page only watches the AR app. It does not use your camera.")
      );
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
      script.onerror = function () {
        reject(new Error("Could not load PeerJS"));
      };
      document.head.appendChild(script);
    });
  }

  function attachStream(stream) {
    if (!videoEl) return;
    videoEl.srcObject = stream;
    videoEl.playsInline = true;
    videoEl.muted = true;
    videoEl.play().catch(function () {});
    showStage(true);
    setStatus("Live from the AR app", "Peer connection is up. This is a private room, not a public broadcast.");
  }

  function startViewer(Peer, code) {
    showJoin(false);
    showActions(true);
    if (roomEl) roomEl.textContent = code;
    showStage(true);

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
      setStatus("Reconnecting…", reason || "Trying the AR app again.");
      clearTimer();
      timer = setTimeout(connect, delay);
      delay = nextRetryMs(delay);
    }

    function wireCall(gen) {
      callRef = peer.call(hostPeerId(code), new MediaStream());
      if (!callRef) {
        schedule("Could not call the app. Is Share view on?");
        return;
      }
      callRef.on("stream", function (stream) {
        if (gen !== generation || stopped) return;
        delay = RETRY_START_MS;
        attachStream(stream);
      });
      callRef.on("close", function () {
        if (gen === generation && !stopped) schedule("Stream paused. Retrying the same room.");
      });
      callRef.on("error", function (err) {
        if (gen === generation && !stopped)
          schedule(err && err.message ? err.message : "Call error. Retrying.");
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
      setStatus("Connecting…", "Room " + code);

      peer = new Peer(undefined, { debug: 1 });
      peer.on("open", function () {
        if (gen !== generation || stopped) return;
        setStatus("Calling the phone…", "Room " + code);
        wireCall(gen);
      });
      peer.on("disconnected", function () {
        if (gen !== generation || stopped || !peer || peer.destroyed) return;
        try {
          peer.reconnect();
        } catch (e) {
          schedule("Signaling dropped. Retrying.");
        }
      });
      peer.on("error", function (err) {
        if (gen !== generation || stopped) return;
        var type = err && err.type;
        if (type === "peer-unavailable") {
          schedule("Phone not sharing this room yet, or it made a new QR. Retrying.");
          return;
        }
        if (type === "network" || type === "disconnected" || type === "socket-error" || type === "server-error") {
          schedule(err.message || "Network hiccup. Retrying.");
          return;
        }
        schedule(err && err.message ? err.message : "Viewer error. Retrying.");
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
        peer = null;
        callRef = null;
        clearVideo();
        showActions(false);
        setStatus("Left the room", "Enter a room code again, or scan a fresh QR from the phone.");
        history.replaceState({}, "", location.pathname);
        showJoin(true);
      },
    };

    if (reconnectBtn) reconnectBtn.onclick = function () { window.__viewer.reconnect(); };
    if (leaveBtn) leaveBtn.onclick = function () { window.__viewer.leave(); };

    connect();
  }

  // Host mode runs inside the app WebView only. Frames arrive from Unity as JPEG data URLs.
  function startHost(Peer, code) {
    if (!fromApp) {
      setStatus(
        "Streaming starts in the AR app",
        "Open Share view from the phone UI. This website only watches; it will not publish a camera."
      );
      showJoin(false);
      return;
    }

    showJoin(false);
    showActions(false);
    if (roomEl) roomEl.textContent = code;
    setStatus("Ready to share", "Waiting for a viewer to open this room.");

    var canvas = document.createElement("canvas");
    canvas.width = 640;
    canvas.height = 360;
    var ctx = canvas.getContext("2d");
    ctx.fillStyle = "#102018";
    ctx.fillRect(0, 0, canvas.width, canvas.height);
    ctx.fillStyle = "#d7ffe6";
    ctx.font = "20px system-ui,sans-serif";
    ctx.fillText("Waiting for AR frames…", 24, 48);

    var stream = canvas.captureStream(12);
    var img = new Image();
    var peer = null;
    var calls = [];
    var stopped = false;
    var bindTimer = null;
    var delay = RETRY_START_MS;

    function closeCalls() {
      for (var i = 0; i < calls.length; i++) {
        try {
          calls[i].close();
        } catch (e) {}
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

    function bindPeer() {
      if (stopped) return;
      closeCalls();
      destroyPeer(peer, null);
      peer = null;

      peer = new Peer(hostPeerId(code), { debug: 1 });
      peer.on("open", function () {
        if (stopped) return;
        delay = RETRY_START_MS;
        setStatus("Sharing room " + code, "Viewers who scan the in-app QR can watch.");
      });
      peer.on("call", function (call) {
        if (stopped) {
          try { call.close(); } catch (e) {}
          return;
        }
        call.answer(stream);
        calls.push(call);
        setStatus("Viewer connected", "Sending the mono AR UI over WebRTC.");
        call.on("close", function () {
          calls = calls.filter(function (c) { return c !== call; });
          if (!stopped && calls.length === 0)
            setStatus("Viewer left", "Still sharing. Another viewer can join with the same QR.");
        });
      });
      peer.on("disconnected", function () {
        if (stopped || !peer || peer.destroyed) return;
        try {
          peer.reconnect();
        } catch (e) {
          scheduleBind("Host signaling dropped. Rebinding.");
        }
      });
      peer.on("error", function (err) {
        if (stopped) return;
        var type = err && err.type;
        // Stale PeerJS id after a quick stop/start: wait and take the same room id again.
        if (type === "unavailable-id") {
          scheduleBind("Old host id still lingering. Retrying the same room.");
          return;
        }
        if (type === "network" || type === "disconnected" || type === "socket-error" || type === "server-error") {
          scheduleBind(err.message || "Host network hiccup. Rebinding.");
          return;
        }
        setStatus("Host error", err && err.message ? err.message : String(err));
      });
    }

    function scheduleBind(reason) {
      if (stopped) return;
      setStatus("Rebinding host…", reason || "Retrying PeerJS host.");
      if (bindTimer) clearTimeout(bindTimer);
      bindTimer = setTimeout(function () {
        bindTimer = null;
        bindPeer();
        delay = nextRetryMs(delay);
      }, delay);
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
        setStatus("Missing room", "The AR app should open this page with a room code.");
        return;
      }
      loadPeerJs()
        .then(function (Peer) {
          startHost(Peer, room);
        })
        .catch(function (err) {
          setStatus("Could not start sharing", err.message || String(err));
        });
      return;
    }

    if (room) {
      loadPeerJs()
        .then(function (Peer) {
          startViewer(Peer, room);
        })
        .catch(function (err) {
          setStatus("Could not start viewer", err.message || String(err));
        });
      return;
    }

    showJoin(true);
    showStage(false);
    showActions(false);
    setStatus(
      "Watch a live AR demo",
      "Scan the QR from Share view in the app, or paste the room code. This page does not use your camera and does not show a public stream."
    );

    if (formEl) {
      formEl.addEventListener("submit", function (event) {
        event.preventDefault();
        var value = (roomInput && roomInput.value ? roomInput.value : "").trim().toLowerCase();
        if (!/^[a-z0-9]{4,12}$/.test(value)) {
          setStatus("Check the room code", "Use the short code under the QR in the app.");
          return;
        }
        location.search = "?room=" + encodeURIComponent(value);
      });
    }
  }

  if (document.readyState === "loading") {
    document.addEventListener("DOMContentLoaded", boot);
  } else {
    boot();
  }
})();
