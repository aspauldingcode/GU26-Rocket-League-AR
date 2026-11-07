using System.Collections;
using UnityEngine;

namespace CardboardXR.Stereo
{
    // Shares a separate off-screen mono render of the scene over PeerJS.
    // Invariant: never calls StereoRig.SetMono and never draws the cast camera to the phone
    // display — stereo (or whatever the user had) stays on-device; only GH Pages gets mono.
    public class WebRtcCastHost : MonoBehaviour
    {
        // The default frame, 16:9, for a page whose QR carried no aspect. Otherwise the height stays
        // CaptureHeight and the width follows the website's display (FrameSize).
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
        int m_Width = CaptureWidth;
        int m_Height = CaptureHeight;

        public bool IsCasting => m_Casting;
        public Vector2Int FrameSize => new(m_Width, m_Height);
        public float Aspect => m_Width / (float)m_Height;

        // CaptureHeight tall, as wide as the aspect asks, in multiples of 8 for the JPEG encoder.
        public static Vector2Int FrameSizeFor(float aspect)
        {
            aspect = float.IsNaN(aspect) ? WebRtcCast.DefaultAspect : Mathf.Clamp(aspect, WebRtcCast.MinAspect, WebRtcCast.MaxAspect);
            var width = Mathf.RoundToInt(CaptureHeight * aspect / 8f) * 8;
            return new Vector2Int(width, CaptureHeight);
        }
        public string Room => m_Room;
        public string ViewerUrl => string.IsNullOrEmpty(m_Room) ? "" : WebRtcCast.ViewerUrl(m_Room);

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
            var size = FrameSizeFor(aspect);
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
            m_NextFrame = Time.unscaledTime + k_FrameInterval;
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
