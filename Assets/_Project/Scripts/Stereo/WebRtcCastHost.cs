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

        public void StartCast()
        {
            if (m_Casting)
                return;

            m_Room = WebRtcCast.NewRoomCode();
            m_WasMono = m_Rig != null && m_Rig.IsMono;
            if (m_Rig != null)
                m_Rig.SetMono(true);

            EnsureCapture();
            m_WebView ??= gameObject.AddComponent<WebRtcCastWebView>();
            m_WebView.Open(WebRtcCast.HostUrl(m_Room));
            m_Casting = true;
            m_NextFrame = Time.unscaledTime + 0.5f;
            Debug.Log($"[Cast] Sharing mono UI on room {m_Room}");
        }

        // New room + QR: drops stale PeerJS host ids so viewers can re-pair cleanly.
        public void RestartCast()
        {
            StopCast();
            StartCast();
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

            ScreenCapture.CaptureScreenshotIntoRenderTexture(m_Capture);
            var request = AsyncGPUReadback.Request(m_Capture, 0, TextureFormat.RGBA32);
            while (!request.done)
                yield return null;

            if (request.hasError || !m_Casting)
            {
                m_Reading = false;
                yield break;
            }

            var data = request.GetData<Color32>();
            m_Readback.SetPixelData(data, 0);
            m_Readback.Apply(false);
            var jpg = m_Readback.EncodeToJPG(k_JpgQuality);
            var dataUrl = "data:image/jpeg;base64," + System.Convert.ToBase64String(jpg);
            m_WebView.PushFrame(dataUrl);
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
