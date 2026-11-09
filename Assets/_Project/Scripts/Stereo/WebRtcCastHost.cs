using System;
using System.Collections;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace CardboardXR.Stereo
{
    // Shares a separate off-screen mono render of the scene over a WebSocket relay.
    // Invariant: never calls StereoRig.SetMono and never draws the cast camera to the phone
    // display — stereo (or whatever the user had) stays on-device; only GH Pages gets mono.
    public class WebRtcCastHost : MonoBehaviour
    {
        // The source frame, 16:9, for a page whose QR carried no aspect. Unity hands the host page
        // a near-lossless JPEG at this size; the page encodes it to H.264 (or a lighter JPEG) and
        // owns the network-side adaptation. Unity only steps the source down when the phone's own
        // frame rate suffers.
        public const int CaptureWidth = 1280;
        public const int CaptureHeight = 720;
        public const float CaptureAspect = CaptureWidth / (float)CaptureHeight;

        public const int k_SourceLevelCount = 3;
        const int k_SourceJpgQuality = 88;
        const float k_SourceInterval = 1f / 15f;
        const int k_SlowFramesToStepDown = 30;
        const int k_FastFramesToStepUp = 150;
        // How long the hidden host page gets to finish loading before Unity reopens it.
        const float k_HostRetrySeconds = 8f;

        [SerializeField] StereoRig m_Rig;

        PassthroughController m_Passthrough;
        WebRtcCastWebView m_WebView;
        RenderTexture m_Capture;
        Texture2D m_Readback;
        Camera m_CastCamera;
        // Where Configure put the cast camera under the rig: between the eyes.
        Vector3 m_EyeLocalPosition;
        // The cast camera is looking through the camera image's frustum, not its own field of view.
        bool m_ImageProjection;
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
        int m_SourceLevel;
        int m_SlowStreak;
        int m_FastStreak;
        float m_FrameSeconds;
        byte[] m_Pixels;

        public bool IsCasting => m_Casting;
        public Vector2Int FrameSize => new(m_Width, m_Height);
        public float Aspect => m_Width / (float)m_Height;

        // SourceHeight tall, as wide as the aspect asks, in multiples of 16 (H.264 macroblocks).
        public static Vector2Int FrameSizeFor(float aspect) => FrameSizeFor(aspect, 0);

        public static Vector2Int FrameSizeFor(float aspect, int sourceLevel)
        {
            aspect = float.IsNaN(aspect) ? WebRtcCast.DefaultAspect : Mathf.Clamp(aspect, WebRtcCast.MinAspect, WebRtcCast.MaxAspect);
            var height = SourceHeight(sourceLevel);
            var width = Mathf.RoundToInt(height * aspect / 16f) * 16;
            return new Vector2Int(width, height);
        }

        // 1, 3/4 and 1/2 of 720: the picture is a grid of small glyphs, and an odd scale resamples
        // each one differently, which shimmers once encoded.
        public static int SourceHeight(int level) => Mathf.Clamp(level, 0, k_SourceLevelCount - 1) switch
        {
            0 => 720,
            1 => 540,
            _ => 360,
        };

        // The phone's own frame rate votes on the source size: a sustained run of slow frames
        // steps down (the extra camera pass is what costs), a long healthy run steps back up.
        // Pure, for testing.
        public static int NextSourceLevel(int level, int slowStreak, int fastStreak)
        {
            level = Mathf.Clamp(level, 0, k_SourceLevelCount - 1);
            if (slowStreak >= k_SlowFramesToStepDown)
                return Mathf.Min(k_SourceLevelCount - 1, level + 1);
            if (fastStreak >= k_FastFramesToStepUp)
                return Mathf.Max(0, level - 1);
            return level;
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
            m_SourceLevel = 0;
            m_SlowStreak = 0;
            m_FastStreak = 0;
            m_FrameSeconds = 0f;
            var size = FrameSizeFor(m_RequestedAspect, m_SourceLevel);
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

            var dt = Time.unscaledDeltaTime;
            m_FrameSeconds = m_FrameSeconds <= 0f ? dt : Mathf.Lerp(m_FrameSeconds, dt, 0.1f);

            if (m_WebView == null || !m_WebView.IsReady || m_CastCamera == null || !m_CastCamera.enabled)
                return;
            if (Time.unscaledTime < m_NextFrame || m_Reading)
                return;
            m_NextFrame = Time.unscaledTime + k_SourceInterval;
            StartCoroutine(PushFrame());
        }

        void OnEnable() => RenderPipelineManager.beginCameraRendering += PlaceCastCamera;

        void OnDisable()
        {
            RenderPipelineManager.beginCameraRendering -= PlaceCastCamera;
            StopCast();
        }

        // While passthrough is on, the camera image is pinned to the pose it was taken from, and
        // in a viewer the eyes follow the faster fused head pose, so it trails around the view.
        // That's right in the headset, but on a flat screen it's the camera picture swimming in
        // the frame. The cast sees from the AR camera instead, through the image's own frustum
        // cropped to the page's shape, so the picture fills the frame edge to edge and holds still.
        // Set as the cast camera renders, to match this frame's pose and image.
        void PlaceCastCamera(ScriptableRenderContext context, Camera camera)
        {
            if (camera != m_CastCamera || m_CastCamera == null)
                return;
            if (m_Passthrough == null)
                m_Passthrough = FindAnyObjectByType<PassthroughController>();
            var camTransform = m_CastCamera.transform;
            var frustum = m_Passthrough != null ? m_Passthrough.ImageFrustum : Vector4.zero;
            if (m_Passthrough != null && m_Passthrough.IsOn && m_Passthrough.CameraImage != null &&
                m_Passthrough.CameraTransform != null && frustum.z > 0f && frustum.w > 0f)
            {
                var ar = m_Passthrough.CameraTransform;
                camTransform.SetPositionAndRotation(ar.position, ar.rotation);
                m_CastCamera.projectionMatrix = CoverProjection(frustum, Aspect, m_CastCamera.nearClipPlane, m_CastCamera.farClipPlane);
                m_ImageProjection = true;
            }
            else
            {
                camTransform.SetLocalPositionAndRotation(m_EyeLocalPosition, Quaternion.identity);
                if (m_ImageProjection)
                {
                    m_CastCamera.ResetProjectionMatrix();
                    m_CastCamera.aspect = Aspect;
                    m_ImageProjection = false;
                }
            }
        }

        // A little inside the image, so its edge texels and the screen's border never show.
        const float k_CoverInset = 0.985f;

        // The largest rectangle of the given aspect inside the camera image's frustum (center xy,
        // size zw at unit distance), centered on it, as an off-axis projection. Pure, for testing.
        public static Matrix4x4 CoverProjection(Vector4 frustum, float aspect, float near, float far)
        {
            var width = frustum.z;
            var height = frustum.w;
            if (width / height > aspect)
                width = height * aspect;
            else
                height = width / aspect;
            width *= k_CoverInset;
            height *= k_CoverInset;
            return Matrix4x4.Frustum(
                (frustum.x - width / 2f) * near, (frustum.x + width / 2f) * near,
                (frustum.y - height / 2f) * near, (frustum.y + height / 2f) * near, near, far);
        }

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

            Task<string> encode;
            try
            {
                var prev = RenderTexture.active;
                RenderTexture.active = m_Capture;
                m_Readback.ReadPixels(new Rect(0, 0, m_Width, m_Height), 0, 0, false);
                RenderTexture.active = prev;

                // ReadPixels fills the CPU copy, which is all the encoder needs: no Apply upload.
                var raw = m_Readback.GetRawTextureData<byte>();
                if (m_Pixels == null || m_Pixels.Length != raw.Length)
                    m_Pixels = new byte[raw.Length];
                raw.CopyTo(m_Pixels);

                // JPEG at 720p costs the main thread a frame; the encoder is thread-safe.
                var pixels = m_Pixels;
                var width = (uint)m_Width;
                var height = (uint)m_Height;
                encode = Task.Run(() =>
                {
                    var jpg = ImageConversion.EncodeArrayToJPG(pixels, GraphicsFormat.R8G8B8A8_UNorm, width, height, 0, k_SourceJpgQuality);
                    return jpg == null || jpg.Length == 0 ? null : "data:image/jpeg;base64," + Convert.ToBase64String(jpg);
                });
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Cast] Frame capture failed: {e.Message}");
                m_Reading = false;
                yield break;
            }

            while (!encode.IsCompleted)
                yield return null;

            if (encode.IsFaulted)
                Debug.LogWarning($"[Cast] Frame encode failed: {encode.Exception?.GetBaseException().Message}");
            else if (m_Casting && m_WebView != null && !string.IsNullOrEmpty(encode.Result))
                m_WebView.PushFrame(encode.Result);
            AdaptSource();
            m_Reading = false;
        }

        // The page adapts the stream to the network; this only protects the phone's own frame
        // rate, since the cast camera is a second full render of the scene.
        void AdaptSource()
        {
            var budget = 1f / (Application.targetFrameRate > 0 ? Application.targetFrameRate : 60);
            m_SlowStreak = m_FrameSeconds > budget * 1.3f ? m_SlowStreak + 1 : 0;
            m_FastStreak = m_FrameSeconds < budget * 1.1f ? m_FastStreak + 1 : 0;
            var next = NextSourceLevel(m_SourceLevel, m_SlowStreak, m_FastStreak);
            if (next != m_SourceLevel)
                SetSourceLevel(next);
        }

        void SetSourceLevel(int level)
        {
            level = Mathf.Clamp(level, 0, k_SourceLevelCount - 1);
            if (level == m_SourceLevel && m_Capture != null)
                return;
            m_SourceLevel = level;
            m_SlowStreak = 0;
            m_FastStreak = 0;

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
            Debug.Log($"[Cast] Source → {m_Width}x{m_Height} (phone frame {m_FrameSeconds * 1000f:F0} ms)");
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
            m_EyeLocalPosition = m_CastCamera.transform.localPosition;
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
