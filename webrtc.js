/* Local AR cast: the phone app publishes a mono UI view; this page only watches.
   Signaling uses PeerJS (IDs stay private in the room code). Media is peer to peer.
   The public GitHub Pages site never publishes a camera and never hosts a global stream. */
(function () {
  "use strict";

  var PEER_PREFIX = "gurlar-";
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

  function hostPeerId(code) {
    return PEER_PREFIX + "host-" + code;
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
    if (roomEl) roomEl.textContent = code;
    setStatus("Connecting…", "Waiting for the AR app on this room.");
    showStage(true);

    var peer = new Peer(undefined, { debug: 1 });
    var callRef = null;

    peer.on("open", function () {
      setStatus("Calling the phone…", "Room " + code);
      // PeerJS needs a local stream argument even when we only want to receive.
      callRef = peer.call(hostPeerId(code), new MediaStream());
      if (!callRef) {
        setStatus("Could not call the app", "Check that Share view is on in the phone UI.");
        return;
      }
      callRef.on("stream", function (stream) {
        attachStream(stream);
      });
      callRef.on("close", function () {
        setStatus("Stream ended", "Start Share view again on the phone, then refresh.");
      });
      callRef.on("error", function (err) {
        setStatus("Call error", err && err.message ? err.message : String(err));
      });
    });

    peer.on("error", function (err) {
      var msg = err && err.type === "peer-unavailable"
        ? "The AR app is not sharing this room yet. Open Share view on the phone, then try again."
        : err && err.message
          ? err.message
          : String(err);
      setStatus("Waiting for the AR app", msg);
    });
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
    var peer = new Peer(hostPeerId(code), { debug: 1 });
    var img = new Image();

    window.__cast = {
      pushFrame: function (dataUrl) {
        if (!dataUrl) return;
        img.onload = function () {
          ctx.drawImage(img, 0, 0, canvas.width, canvas.height);
        };
        img.src = dataUrl;
      },
      setStatus: setStatus,
    };

    peer.on("open", function () {
      setStatus("Sharing room " + code, "Viewers who scan the in-app QR can watch.");
    });

    peer.on("call", function (call) {
      call.answer(stream);
      setStatus("Viewer connected", "Sending the mono AR UI over WebRTC.");
      call.on("close", function () {
        setStatus("Viewer left", "Still sharing. Another viewer can join with the same QR.");
      });
    });

    peer.on("error", function (err) {
      setStatus("Host error", err && err.message ? err.message : String(err));
    });
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

    // Viewer
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
