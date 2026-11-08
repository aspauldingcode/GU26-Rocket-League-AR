using System.Collections;
using UnityEngine;

namespace CardboardXR.Stereo
{
    // Shares a separate off-screen mono render of the scene over a WebSocket relay.
    // Invariant: never calls StereoRig.SetMono and never draws the cast camera to the phone
    // display — stereo (or whatever the user had) stays on-device; only GH Pages gets mono.
    public class WebRtcCastHost : MonoBehaviour
    {
        // The starting frame, 16:9, for a page whose QR carried no aspect. The stream adapts
        // around this size (ProfileHeight): a public relay cannot carry desktop-class frames,
        // so stability wins over pixels when the encoded frames get heavy.
        public const int CaptureWidth = 640;
        public const int CaptureHeight = 360;
        public const float CaptureAspect = CaptureWidth / (float)CaptureHeight;

        // Adaptive stream profiles, best to lightest. Level 2 is the start: small enough for
        // a public MQTT broker, large enough to read on the website's full-screen display.
        public const int k_ProfileCount = 5;
        public const int k_StartProfile = 2;
        const int k_HeavyFrameBytes = 44_000;
        const int k_LightFrameBytes = 16_000;
        const int k_LightFramesToImprove = 60;
        // How long the hidden host page gets to finish loading before Unity reopens it.
        const float k_HostRetrySeconds = 8f;

        [SerializeField] StereoRig m_Rig;

        WebRtcCastWebView m_WebView;
        RenderTexture m_Capture;
        Texture2D m_Readback;
        Camera m_CastCamera;
        bool m_Casting;
        string m_Room;
        float m_NextFrame;
        float m_HostRetryAt;
        NetworkReachability m_LastReachability;
        bool m_Reading;
        bool m_DisplayWasMono;
        int m_Width = CaptureWidth;
        int m_Height = CaptureHeight;
        float m_RequestedAspect = CaptureAspect;
        int m_ProfileLevel = k_StartProfile;
        int m_JpgQuality = 45;
        float m_FrameInterval = 1f / 8f;
        int m_LightFrameStreak;
        float m_LastProfileChange;

        public bool IsCasting => m_Casting;
        public Vector2Int FrameSize => new(m_Width, m_Height);
        public float Aspect => m_Width / (float)m_Height;

        // ProfileHeight tall, as wide as the aspect asks, in multiples of 8 for the JPEG encoder.
        public static Vector2Int FrameSizeFor(float aspect) => FrameSizeFor(aspect, k_StartProfile);

        public static Vector2Int FrameSizeFor(float aspect, int profileLevel)
        {
            aspect = float.IsNaN(aspect) ? WebRtcCast.DefaultAspect : Mathf.Clamp(aspect, WebRtcCast.MinAspect, WebRtcCast.MaxAspect);
            var height = ProfileHeight(profileLevel);
            var width = Mathf.RoundToInt(height * aspect / 8f) * 8;
            return new Vector2Int(width, height);
        }

        public static int ProfileHeight(int level) => Mathf.Clamp(level, 0, k_ProfileCount - 1) switch
        {
            0 => 540,
            1 => 450,
            2 => 360,
            3 => 288,
            _ => 216,
        };

        public static int ProfileQuality(int level) => Mathf.Clamp(level, 0, k_ProfileCount - 1) switch
        {
            0 => 55,
            1 => 50,
            2 => 45,
            3 => 40,
            _ => 35,
        };

        public static float ProfileInterval(int level) => Mathf.Clamp(level, 0, k_ProfileCount - 1) switch
        {
            0 => 1f / 10f,
            1 => 1f / 9f,
            2 => 1f / 8f,
            3 => 1f / 6f,
            _ => 1f / 5f,
        };

        // One encoded frame's size votes on the profile: a heavy frame steps down at once
        // (after the caller's cooldown), a long run of light frames earns one step back up.
        // Pure, for testing.
        public static int NextProfileLevel(int currentLevel, int jpgBytes, int lightFrameStreak)
        {
            currentLevel = Mathf.Clamp(currentLevel, 0, k_ProfileCount - 1);
            if (jpgBytes > k_HeavyFrameBytes)
                return Mathf.Min(k_ProfileCount - 1, currentLevel + 1);
            if (jpgBytes < k_LightFrameBytes && lightFrameStreak >= k_LightFramesToImprove)
                return Mathf.Max(0, currentLevel - 1);
            return currentLevel;
        }
        public string Room => m_Room;
        public string ViewerUrl => string.IsNullOrEmpty(m_Room) ? "" : WebRtcCast.ViewerUrl(m_Room);

        // The hidden page gets this long to report ready before Unity reopens it. Pure, for testing.
        public static bool ShouldRetryHostPage(bool casting, bool webViewReady, float now, float retryAt) =>
            casting && !webViewReady && now >= retryAt;

        // A network that was gone and is back: the page's relay may have died with it, so
        // reopen the host page rather than streaming into it hopefully. Pure, for testing.
        public static bool NetworkReturned(NetworkReachability previous, NetworkReachability current) =>
            previous == NetworkReachability.NotReachable && current != NetworkReachability.NotReachable;

        // True while casting if the phone display mode was left alone (expected always).
        public bool DisplayModeUnchanged =>
            !m_Casting || m_Rig == null || m_Rig.IsMono == m_DisplayWasMono;

        public void Configure(StereoRig rig) => m_Rig = rig;

        public void StartCast(string room = null, float aspect = WebRtcCast.DefaultAspect)
        {
            if (m_Casting)
                return;

            m_Room = string.IsNullOrWhiteSpace(room) ? WebRtcCast.NewRoomCode() : room.Trim().ToLowerInvariant();
            m_DisplayWasMono = m_Rig != null && m_Rig.IsMono;
            m_RequestedAspect = float.IsNaN(aspect) ? WebRtcCast.DefaultAspect : Mathf.Clamp(aspect, WebRtcCast.MinAspect, WebRtcCast.MaxAspect);
            m_ProfileLevel = k_StartProfile;
            m_JpgQuality = ProfileQuality(m_ProfileLevel);
            m_FrameInterval = ProfileInterval(m_ProfileLevel);
            m_LightFrameStreak = 0;
            m_LastProfileChange = Time.unscaledTime;
            var size = FrameSizeFor(m_RequestedAspect, m_ProfileLevel);
            if (m_Capture != null && (m_Capture.width != size.x || m_Capture.height != size.y))
                ReleaseCapture();
            m_Width = size.x;
            m_Height = size.y;

            EnsureCapture();
            EnsureCastCamera();
            // Hard invariant: cast camera must only ever draw into the RT, never the backbuffer.
            if (m_CastCamera.targetTexture != m_Capture)
            {
                Debug.LogError("[Cast] Refusing to enable cast camera without off-screen target — phone display would change.");
                DestroyCastCamera();
                m_Room = null;
                return;
            }

            m_CastCamera.enabled = true;
            m_Casting = true;
            m_LastReachability = Application.internetReachability;
            m_HostRetryAt = Time.unscaledTime + k_HostRetrySeconds;
            m_NextFrame = Time.unscaledTime + 0.35f;
            StartCoroutine(OpenHostPage());
            Debug.Log($"[Cast] Off-screen mono {m_Width}x{m_Height} → Pages room {m_Room}; phone IsMono={m_DisplayWasMono} (unchanged)");
        }

        IEnumerator OpenHostPage()
        {
            yield return null;
            if (!m_Casting || string.IsNullOrEmpty(m_Room))
                yield break;
            m_WebView ??= gameObject.AddComponent<WebRtcCastWebView>();
            m_WebView.Open(WebRtcCast.HostUrl(m_Room));
            m_HostRetryAt = Time.unscaledTime + k_HostRetrySeconds;
            m_NextFrame = Time.unscaledTime + 0.25f;
        }

        public void RestartCast(string room = null)
        {
            var aspect = Aspect;
            StopCast();
            StartCast(room, aspect);
        }

        public void SoftReconnectHost()
        {
            if (!m_Casting || string.IsNullOrEmpty(m_Room) || m_WebView == null)
                return;
            m_WebView.Open(WebRtcCast.HostUrl(m_Room));
            m_NextFrame = Time.unscaledTime + 0.5f;
            Debug.Log($"[Cast] Soft-reconnected host for room {m_Room}");
        }

        public void StopCast()
        {
            if (!m_Casting)
                return;

            m_Casting = false;
            m_WebView?.Close();
            if (m_CastCamera != null)
            {
                m_CastCamera.enabled = false;
                m_CastCamera.targetTexture = null;
            }

            if (m_Rig != null && m_Rig.IsMono != m_DisplayWasMono)
            {
                Debug.LogError(
                    $"[Cast] Phone display mode changed during cast (was mono={m_DisplayWasMono}, now {m_Rig.IsMono}). Restoring.");
                m_Rig.SetMono(m_DisplayWasMono);
            }

            m_Room = null;
            Debug.Log("[Cast] Stopped sharing");
        }

        // Keeps the hidden host page alive across a flaky connection: if the network went
        // away and came back, reopen the page; if it never finished loading, retry it.
        // Once the page is up, its own relay watchdog (webrtc.js) owns reconnection.
        void WatchHostPage()
        {
            var reachability = Application.internetReachability;
            if (NetworkReturned(m_LastReachability, reachability))
            {
                m_LastReachability = reachability;
                Debug.Log("[Cast] Network is back; reopening the host page");
                SoftReconnectHost();
                return;
            }
            m_LastReachability = reachability;

            var ready = m_WebView != null && m_WebView.IsReady;
            if (!ShouldRetryHostPage(m_Casting, ready, Time.unscaledTime, m_HostRetryAt))
                return;
            m_HostRetryAt = Time.unscaledTime + k_HostRetrySeconds;
            Debug.LogWarning("[Cast] Host page did not become ready; reopening it");
            if (m_WebView == null)
                StartCoroutine(OpenHostPage());
            else
                SoftReconnectHost();
        }

        void LateUpdate()
        {
            if (!m_Casting)
                return;

            WatchHostPage();

            // Continuous guard: never let the cast camera lose its RT (would paint the phone).
            if (m_CastCamera != null && m_CastCamera.enabled)
            {
                if (m_CastCamera.targetTexture != m_Capture)
                {
                    Debug.LogError("[Cast] Cast camera lost its RenderTexture — disabling to protect stereo display.");
                    m_CastCamera.enabled = false;
                    m_CastCamera.targetTexture = m_Capture;
                }
                else if (Mathf.Abs(m_CastCamera.aspect - Aspect) > 0.01f)
                    m_CastCamera.aspect = Aspect;
            }

            if (m_Rig != null && m_Rig.IsMono != m_DisplayWasMono)
            {
                Debug.LogError("[Cast] StereoRig.IsMono changed while casting — restoring phone display.");
                m_Rig.SetMono(m_DisplayWasMono);
            }

            if (m_WebView == null || !m_WebView.IsReady || m_CastCamera == null || !m_CastCamera.enabled)
                return;
            if (Time.unscaledTime < m_NextFrame || m_Reading)
                return;
            m_NextFrame = Time.unscaledTime + m_FrameInterval;
            StartCoroutine(PushFrame());
        }

        void OnDisable() => StopCast();

        void OnDestroy()
        {
            DestroyCastCamera();
            ReleaseCapture();
        }

        void ReleaseCapture()
        {
            if (m_CastCamera != null)
                m_CastCamera.targetTexture = null;
            if (m_Capture != null)
            {
                m_Capture.Release();
                DestroyAsset(m_Capture);
                m_Capture = null;
            }
            if (m_Readback != null)
            {
                DestroyAsset(m_Readback);
                m_Readback = null;
            }
        }

        static void DestroyAsset(Object target)
        {
            if (Application.isPlaying)
                Destroy(target);
            else
                DestroyImmediate(target);
        }

        IEnumerator PushFrame()
        {
            m_Reading = true;
            yield return new WaitForEndOfFrame();
            if (!m_Casting || m_Capture == null || m_CastCamera == null || m_CastCamera.targetTexture != m_Capture)
            {
                m_Reading = false;
                yield break;
            }

            try
            {
                var prev = RenderTexture.active;
                RenderTexture.active = m_Capture;
                m_Readback.ReadPixels(new Rect(0, 0, m_Width, m_Height), 0, 0, false);
                m_Readback.Apply(false);
                RenderTexture.active = prev;

                if (!m_Casting)
                {
                    m_Reading = false;
                    yield break;
                }

                var jpg = m_Readback.EncodeToJPG(m_JpgQuality);
                m_WebView.PushFrame("data:image/jpeg;base64," + System.Convert.ToBase64String(jpg));
                AdaptProfile(jpg.Length);
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[Cast] Frame capture failed: {e.Message}");
            }
            m_Reading = false;
        }

        // Keeps the encoded stream inside what a public relay will carry for minutes, not
        // seconds: heavy frames step the capture down, light streaks earn quality back.
        void AdaptProfile(int jpgBytes)
        {
            if (jpgBytes < k_LightFrameBytes)
                m_LightFrameStreak++;
            else
                m_LightFrameStreak = 0;

            var now = Time.unscaledTime;
            var next = NextProfileLevel(m_ProfileLevel, jpgBytes, m_LightFrameStreak);
            if (next != m_ProfileLevel)
            {
                if (now - m_LastProfileChange < 1.5f)
                    return;
                SetProfileLevel(next);
                return;
            }

            // Same size, still heavy: trade JPEG quality before touching the frame again.
            if (jpgBytes > 30_000 && m_JpgQuality > 32 && now - m_LastProfileChange >= 1.5f)
            {
                m_JpgQuality -= 3;
                m_LastProfileChange = now;
                Debug.Log($"[Cast] Stream quality → JPEG q{m_JpgQuality} ({jpgBytes / 1000} KB frames)");
            }
        }

        void SetProfileLevel(int level)
        {
            level = Mathf.Clamp(level, 0, k_ProfileCount - 1);
            if (level == m_ProfileLevel && m_Capture != null)
                return;
            m_ProfileLevel = level;
            m_JpgQuality = ProfileQuality(level);
            m_FrameInterval = ProfileInterval(level);
            m_LightFrameStreak = 0;
            m_LastProfileChange = Time.unscaledTime;

            var size = FrameSizeFor(m_RequestedAspect, level);
            if (size.x == m_Width && size.y == m_Height && m_Capture != null)
                return;
            m_Width = size.x;
            m_Height = size.y;
            if (m_CastCamera != null)
                m_CastCamera.targetTexture = null;
            ReleaseCapture();
            EnsureCapture();
            if (m_CastCamera != null)
            {
                m_CastCamera.targetTexture = m_Capture;
                m_CastCamera.aspect = Aspect;
                m_CastCamera.enabled = m_Casting;
            }
            Debug.Log($"[Cast] Stream profile → {m_Width}x{m_Height}, JPEG q{m_JpgQuality}, {1f / m_FrameInterval:F0} fps");
        }

        void EnsureCapture()
        {
            if (m_Capture != null)
                return;
            m_Capture = new RenderTexture(m_Width, m_Height, 24, RenderTextureFormat.ARGB32)
            {
                name = "Cast Mono RT",
                filterMode = FilterMode.Bilinear,
                antiAliasing = 1,
            };
            m_Capture.Create();
            m_Readback = new Texture2D(m_Width, m_Height, TextureFormat.RGBA32, false)
            {
                name = "Cast Readback",
            };
        }

        void EnsureCastCamera()
        {
            if (m_CastCamera == null)
            {
                var go = new GameObject("Cast Mono Eye");
                // Hide from hierarchy noise; never add AudioListener.
                go.hideFlags = HideFlags.DontSave;
                m_CastCamera = go.AddComponent<Camera>();
            }

            m_CastCamera.targetTexture = m_Capture;
            m_CastCamera.enabled = false; // enabled only after StartCast checks the RT
            if (m_Rig != null)
                m_Rig.ConfigureCastCamera(m_CastCamera);
            else
            {
                m_CastCamera.transform.SetParent(transform, false);
                m_CastCamera.stereoTargetEye = StereoTargetEyeMask.None;
                m_CastCamera.fieldOfView = 60f;
            }
            // Re-assert after Configure — never allow a screen-bound cast camera.
            m_CastCamera.targetTexture = m_Capture;
            // Lock the frustum to the website's frame; Unity otherwise uses the phone screen's aspect.
            // Setting aspect stops screen-driven sizing; ResetAspect() would undo it.
            m_CastCamera.aspect = Aspect;
        }

        void DestroyCastCamera()
        {
            if (m_CastCamera == null)
                return;
            m_CastCamera.targetTexture = null;
            m_CastCamera.enabled = false;
            Destroy(m_CastCamera.gameObject);
            m_CastCamera = null;
        }
    }
}
