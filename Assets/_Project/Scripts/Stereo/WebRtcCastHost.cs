using System.Collections;
using UnityEngine;

namespace CardboardXR.Stereo
{
    // Shares an off-screen mono view of the scene over PeerJS. The phone display is left alone
    // (stereo stays stereo); a second midpoint camera renders into a RenderTexture that is JPEG'd
    // into the hidden host WebView for GH Pages viewers.
    public class WebRtcCastHost : MonoBehaviour
    {
        const int k_Width = 640;
        const int k_Height = 360;
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

        public bool IsCasting => m_Casting;
        public string Room => m_Room;
        public string ViewerUrl => string.IsNullOrEmpty(m_Room) ? "" : WebRtcCast.ViewerUrl(m_Room);

        public void Configure(StereoRig rig) => m_Rig = rig;

        public void StartCast(string room = null)
        {
            if (m_Casting)
                return;

            m_Room = string.IsNullOrWhiteSpace(room) ? WebRtcCast.NewRoomCode() : room.Trim().ToLowerInvariant();
            EnsureCapture();
            EnsureCastCamera();
            m_Casting = true;
            m_NextFrame = Time.unscaledTime + 0.35f;
            StartCoroutine(OpenHostPage());
            Debug.Log($"[Cast] Sharing off-screen mono on room {m_Room} (phone display unchanged)");
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
                m_CastCamera.enabled = false;
            m_Room = null;
            Debug.Log("[Cast] Stopped sharing");
        }

        void OnDisable() => StopCast();

        void OnDestroy()
        {
            if (m_CastCamera != null)
            {
                Destroy(m_CastCamera.gameObject);
                m_CastCamera = null;
            }
            if (m_Capture != null)
            {
                m_Capture.Release();
                Destroy(m_Capture);
            }
            if (m_Readback != null)
                Destroy(m_Readback);
        }

        void LateUpdate()
        {
            if (!m_Casting || m_WebView == null || !m_WebView.IsReady || m_CastCamera == null)
                return;
            if (Time.unscaledTime < m_NextFrame || m_Reading)
                return;
            m_NextFrame = Time.unscaledTime + k_FrameInterval;
            StartCoroutine(PushFrame());
        }

        IEnumerator PushFrame()
        {
            m_Reading = true;
            // Let the cast camera finish this frame's RT before readback.
            yield return new WaitForEndOfFrame();
            if (!m_Casting || m_Capture == null)
            {
                m_Reading = false;
                yield break;
            }

            try
            {
                var prev = RenderTexture.active;
                RenderTexture.active = m_Capture;
                m_Readback.ReadPixels(new Rect(0, 0, k_Width, k_Height), 0, 0, false);
                m_Readback.Apply(false);
                RenderTexture.active = prev;

                if (!m_Casting)
                {
                    m_Reading = false;
                    yield break;
                }

                var jpg = m_Readback.EncodeToJPG(k_JpgQuality);
                var dataUrl = "data:image/jpeg;base64," + System.Convert.ToBase64String(jpg);
                m_WebView.PushFrame(dataUrl);
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
            m_Capture = new RenderTexture(k_Width, k_Height, 24, RenderTextureFormat.ARGB32)
            {
                name = "Cast Mono",
                filterMode = FilterMode.Bilinear,
                antiAliasing = 1,
            };
            m_Capture.Create();
            m_Readback = new Texture2D(k_Width, k_Height, TextureFormat.RGBA32, false)
            {
                name = "Cast Readback",
            };
        }

        void EnsureCastCamera()
        {
            if (m_CastCamera == null)
            {
                var go = new GameObject("Cast Mono Eye");
                m_CastCamera = go.AddComponent<Camera>();
            }

            m_CastCamera.targetTexture = m_Capture;
            m_CastCamera.enabled = true;
            if (m_Rig != null)
                m_Rig.ConfigureCastCamera(m_CastCamera);
            else
            {
                m_CastCamera.transform.SetParent(transform, false);
                m_CastCamera.stereoTargetEye = StereoTargetEyeMask.None;
                m_CastCamera.fieldOfView = 60f;
            }
        }
    }
}
