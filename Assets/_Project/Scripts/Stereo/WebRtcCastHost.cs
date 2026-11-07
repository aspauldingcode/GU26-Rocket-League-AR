using System.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace CardboardXR.Stereo
{
    // Captures the mono phone UI and feeds JPEG frames into a hidden WebView that runs webrtc.js as
    // the PeerJS host. Stereo stays off while casting so the website sees a readable UI, not the
    // lens-warped 3D view.
    public class WebRtcCastHost : MonoBehaviour
    {
        const int k_Width = 640;
        const int k_Height = 360;
        const int k_JpgQuality = 45;
        const float k_FrameInterval = 1f / 10f;

        [SerializeField] StereoRig m_Rig;

        WebRtcCastWebView m_WebView;
        RenderTexture m_Capture;
        Texture2D m_Readback;
        bool m_WasMono;
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
            m_WasMono = m_Rig != null && m_Rig.IsMono;
            if (m_Rig != null)
                m_Rig.SetMono(true);

            EnsureCapture();
            m_Casting = true;
            m_NextFrame = Time.unscaledTime + 1f;
            // Open the WKWebView on the next frame so SetMono's camera rebuild finishes first.
            StartCoroutine(OpenHostPage());
            Debug.Log($"[Cast] Sharing mono UI on room {m_Room}");
        }

        IEnumerator OpenHostPage()
        {
            yield return null;
            if (!m_Casting || string.IsNullOrEmpty(m_Room))
                yield break;
            m_WebView ??= gameObject.AddComponent<WebRtcCastWebView>();
            m_WebView.Open(WebRtcCast.HostUrl(m_Room));
            m_NextFrame = Time.unscaledTime + 0.75f;
        }

        // Drops a stale host and joins a fresh laptop room (or mints one).
        public void RestartCast(string room = null)
        {
            StopCast();
            StartCast(room);
        }

        // Same room, fresh host page (clears a wedged PeerJS peer without changing the QR).
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
            if (m_Rig != null && m_Rig.IsMono != m_WasMono)
                m_Rig.SetMono(m_WasMono);
            m_Room = null;
            Debug.Log("[Cast] Stopped sharing");
        }

        void OnDisable() => StopCast();

        void OnDestroy()
        {
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
            if (!m_Casting || m_WebView == null || !m_WebView.IsReady)
                return;
            if (Time.unscaledTime < m_NextFrame || m_Reading)
                return;
            m_NextFrame = Time.unscaledTime + k_FrameInterval;
            StartCoroutine(PushFrame());
        }

        IEnumerator PushFrame()
        {
            m_Reading = true;
            yield return new WaitForEndOfFrame();
            if (!m_Casting)
            {
                m_Reading = false;
                yield break;
            }

            // ReadPixels after end-of-frame is safer on iOS XR than AsyncGPUReadback off a
            // CaptureScreenshotIntoRenderTexture target (that path can native-crash under ARKit).
            try
            {
                ScreenCapture.CaptureScreenshotIntoRenderTexture(m_Capture);
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
            m_Capture = new RenderTexture(k_Width, k_Height, 0, RenderTextureFormat.ARGB32)
            {
                name = "Cast Capture",
                filterMode = FilterMode.Bilinear,
            };
            m_Capture.Create();
            m_Readback = new Texture2D(k_Width, k_Height, TextureFormat.RGBA32, false)
            {
                name = "Cast Readback",
            };
        }
    }
}
