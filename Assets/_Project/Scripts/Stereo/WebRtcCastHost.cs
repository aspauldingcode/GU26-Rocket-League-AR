using System.Collections;
using UnityEngine;

namespace CardboardXR.Stereo
{
    // Shares a separate off-screen mono render of the scene over PeerJS.
    // Invariant: never calls StereoRig.SetMono and never draws the cast camera to the phone
    // display — stereo (or whatever the user had) stays on-device; only GH Pages gets mono.
    public class WebRtcCastHost : MonoBehaviour
    {
        // 16:9 for laptop / GH Pages presentation — not the phone's taller screen aspect.
        public const int CaptureWidth = 960;
        public const int CaptureHeight = 540;
        public const float CaptureAspect = CaptureWidth / (float)CaptureHeight;

        const int k_JpgQuality = 50;
        const float k_FrameInterval = 1f / 12f;

        [SerializeField] StereoRig m_Rig;

        WebRtcCastWebView m_WebView;
        RenderTexture m_Capture;
        Texture2D m_Readback;
        Camera m_CastCamera;
        bool m_Casting;
        string m_Room;
        float m_NextFrame;
        bool m_Reading;
        bool m_DisplayWasMono;

        public bool IsCasting => m_Casting;
        public string Room => m_Room;
        public string ViewerUrl => string.IsNullOrEmpty(m_Room) ? "" : WebRtcCast.ViewerUrl(m_Room);

        // True while casting if the phone display mode was left alone (expected always).
        public bool DisplayModeUnchanged =>
            !m_Casting || m_Rig == null || m_Rig.IsMono == m_DisplayWasMono;

        public void Configure(StereoRig rig) => m_Rig = rig;

        public void StartCast(string room = null)
        {
            if (m_Casting)
                return;

            m_Room = string.IsNullOrWhiteSpace(room) ? WebRtcCast.NewRoomCode() : room.Trim().ToLowerInvariant();
            m_DisplayWasMono = m_Rig != null && m_Rig.IsMono;

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
            m_NextFrame = Time.unscaledTime + 0.35f;
            StartCoroutine(OpenHostPage());
            Debug.Log($"[Cast] Off-screen mono → Pages room {m_Room}; phone IsMono={m_DisplayWasMono} (unchanged)");
        }

        IEnumerator OpenHostPage()
        {
            yield return null;
            if (!m_Casting || string.IsNullOrEmpty(m_Room))
                yield break;
            m_WebView ??= gameObject.AddComponent<WebRtcCastWebView>();
            m_WebView.Open(WebRtcCast.HostUrl(m_Room));
            m_NextFrame = Time.unscaledTime + 0.25f;
        }

        public void RestartCast(string room = null)
        {
            StopCast();
            StartCast(room);
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

        void LateUpdate()
        {
            if (!m_Casting)
                return;

            // Continuous guard: never let the cast camera lose its RT (would paint the phone).
            if (m_CastCamera != null && m_CastCamera.enabled)
            {
                if (m_CastCamera.targetTexture != m_Capture)
                {
                    Debug.LogError("[Cast] Cast camera lost its RenderTexture — disabling to protect stereo display.");
                    m_CastCamera.enabled = false;
                    m_CastCamera.targetTexture = m_Capture;
                }
                else if (Mathf.Abs(m_CastCamera.aspect - CaptureAspect) > 0.01f)
                    ApplyWebsiteAspect(m_CastCamera);
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
            m_NextFrame = Time.unscaledTime + k_FrameInterval;
            StartCoroutine(PushFrame());
        }

        void OnDisable() => StopCast();

        void OnDestroy()
        {
            DestroyCastCamera();
            if (m_Capture != null)
            {
                m_Capture.Release();
                Destroy(m_Capture);
                m_Capture = null;
            }
            if (m_Readback != null)
            {
                Destroy(m_Readback);
                m_Readback = null;
            }
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
                m_Readback.ReadPixels(new Rect(0, 0, CaptureWidth, CaptureHeight), 0, 0, false);
                m_Readback.Apply(false);
                RenderTexture.active = prev;

                if (!m_Casting)
                {
                    m_Reading = false;
                    yield break;
                }

                var jpg = m_Readback.EncodeToJPG(k_JpgQuality);
                m_WebView.PushFrame("data:image/jpeg;base64," + System.Convert.ToBase64String(jpg));
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[Cast] Frame capture failed: {e.Message}");
            }
            m_Reading = false;
        }

        void EnsureCapture()
        {
            if (m_Capture != null)
                return;
            m_Capture = new RenderTexture(CaptureWidth, CaptureHeight, 24, RenderTextureFormat.ARGB32)
            {
                name = "Cast Mono RT",
                filterMode = FilterMode.Bilinear,
                antiAliasing = 1,
            };
            m_Capture.Create();
            m_Readback = new Texture2D(CaptureWidth, CaptureHeight, TextureFormat.RGBA32, false)
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
            // Lock frustum to the website 16:9 frame (Unity defaults to Screen aspect otherwise).
            ApplyWebsiteAspect(m_CastCamera);
        }

        static void ApplyWebsiteAspect(Camera cam)
        {
            if (cam == null)
                return;
            // Setting aspect disables screen-driven sizing; do not call ResetAspect().
            cam.aspect = CaptureAspect;
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
