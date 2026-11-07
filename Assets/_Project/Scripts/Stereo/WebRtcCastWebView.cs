using System;
using System.Runtime.InteropServices;
using UnityEngine;

namespace CardboardXR.Stereo
{
    // Tiny hidden WebView that loads the GitHub Pages host page (webrtc.js) so PeerJS runs in a
    // real browser engine. Unity only pushes JPEG frames into it.
    public class WebRtcCastWebView : MonoBehaviour
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        AndroidJavaObject m_WebView;
#elif UNITY_IOS && !UNITY_EDITOR
        IntPtr m_View;
#endif
        bool m_Ready;

        public bool IsReady => m_Ready;

        public void Open(string url)
        {
            Close();
            m_Ready = false;
#if UNITY_ANDROID && !UNITY_EDITOR
            OpenAndroid(url);
#elif UNITY_IOS && !UNITY_EDITOR
            OpenIos(url);
#else
            Debug.LogWarning($"[Cast] WebView host only runs on device. Would open {url}");
            m_Ready = true;
#endif
        }

        public void PushFrame(string dataUrl)
        {
            if (string.IsNullOrEmpty(dataUrl))
                return;
            var escaped = dataUrl.Replace("\\", "\\\\").Replace("'", "\\'");
            Eval($"window.__cast&&window.__cast.pushFrame('{escaped}');");
        }

        public void Close()
        {
            m_Ready = false;
#if UNITY_ANDROID && !UNITY_EDITOR
            CloseAndroid();
#elif UNITY_IOS && !UNITY_EDITOR
            CloseIos();
#endif
        }

        void OnDestroy() => Close();

        void Eval(string js)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (m_WebView == null)
                return;
            using var activity = Activity();
            activity.Call("runOnUiThread", new AndroidJavaRunnable(() =>
            {
                m_WebView?.Call("evaluateJavascript", js, null);
            }));
#elif UNITY_IOS && !UNITY_EDITOR
            if (m_View != IntPtr.Zero)
                CardboardXR_CastEval(m_View, js);
#else
            _ = js;
#endif
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        void OpenAndroid(string url)
        {
            using var activity = Activity();
            activity.Call("runOnUiThread", new AndroidJavaRunnable(() =>
            {
                m_WebView = new AndroidJavaObject("android.webkit.WebView", activity);
                using (var settings = m_WebView.Call<AndroidJavaObject>("getSettings"))
                {
                    settings.Call("setJavaScriptEnabled", true);
                    settings.Call("setDomStorageEnabled", true);
                    settings.Call("setMediaPlaybackRequiresUserGesture", false);
                }

                using var layout = new AndroidJavaObject("android.widget.FrameLayout$LayoutParams", 2, 2);
                m_WebView.Call("setLayoutParams", layout);
                m_WebView.Call("setAlpha", 0f);
                m_WebView.Call("setWebViewClient", new PageClient(this));
                m_WebView.Call("setWebChromeClient", new AndroidJavaObject("android.webkit.WebChromeClient"));

                using var contentId = new AndroidJavaClass("android.R$id");
                var id = contentId.GetStatic<int>("content");
                using var content = activity.Call<AndroidJavaObject>("findViewById", id);
                content.Call("addView", m_WebView);
                m_WebView.Call("loadUrl", url);
            }));
        }

        void CloseAndroid()
        {
            if (m_WebView == null)
                return;
            using var activity = Activity();
            var view = m_WebView;
            m_WebView = null;
            activity.Call("runOnUiThread", new AndroidJavaRunnable(() =>
            {
                using var parent = view.Call<AndroidJavaObject>("getParent");
                parent?.Call("removeView", view);
                view.Call("stopLoading");
                view.Call("destroy");
                view.Dispose();
            }));
        }

        static AndroidJavaObject Activity()
        {
            using var unity = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
            return unity.GetStatic<AndroidJavaObject>("currentActivity");
        }

        sealed class PageClient : AndroidJavaProxy
        {
            readonly WebRtcCastWebView m_Owner;

            public PageClient(WebRtcCastWebView owner) : base("android.webkit.WebViewClient")
            {
                m_Owner = owner;
            }

            // ReSharper disable once InconsistentNaming
            public void onPageFinished(AndroidJavaObject view, string url)
            {
                m_Owner.m_Ready = true;
                Debug.Log($"[Cast] Host page ready: {url}");
            }
        }
#elif UNITY_IOS && !UNITY_EDITOR
        void OpenIos(string url)
        {
            m_View = CardboardXR_CastOpen(url);
            // Page load finishes asynchronously; treat create as ready enough to queue frames.
            m_Ready = m_View != IntPtr.Zero;
        }

        void CloseIos()
        {
            if (m_View == IntPtr.Zero)
                return;
            CardboardXR_CastClose(m_View);
            m_View = IntPtr.Zero;
        }

        [DllImport("__Internal")]
        static extern IntPtr CardboardXR_CastOpen(string url);

        [DllImport("__Internal")]
        static extern void CardboardXR_CastClose(IntPtr view);

        [DllImport("__Internal")]
        static extern void CardboardXR_CastEval(IntPtr view, string js);
#endif
    }
}
