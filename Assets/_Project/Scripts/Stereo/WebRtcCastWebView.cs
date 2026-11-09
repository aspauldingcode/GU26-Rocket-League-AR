using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using UnityEngine;

namespace CardboardXR.Stereo
{
    // Tiny invisible WebView that loads the GitHub Pages host page (webrtc.js) in a
    // real browser engine. Unity pushes source JPEG frames into it; the page encodes them to
    // H.264 with WebCodecs (falling back to JPEG) and relays them to the website.
    public class WebRtcCastWebView : MonoBehaviour
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        AndroidJavaObject m_WebView;
#elif UNITY_IOS && !UNITY_EDITOR
        IntPtr m_View;
#endif
        bool m_Ready;
        // What the page told the app (webrtc.js notifyApp), oldest first.
        readonly ConcurrentQueue<string> m_Events = new();

        public bool IsReady => m_Ready;

        // An event from the page, such as "ended:viewer-left", if one is waiting.
        public bool TryTakeEvent(out string pageEvent)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            PollAndroidEvents();
#elif UNITY_IOS && !UNITY_EDITOR
            if (m_View != IntPtr.Zero)
            {
                var native = CardboardXR_CastTakeEvent(m_View);
                if (!string.IsNullOrEmpty(native))
                    m_Events.Enqueue(native);
            }
#endif
            return m_Events.TryDequeue(out pageEvent);
        }

        public void Open(string url)
        {
            Close();
            m_Ready = false;
            while (m_Events.TryDequeue(out _)) { }
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
            // Base64 data URLs never need escaping; skip copying a 200 KB string twice a frame.
            var escaped = dataUrl.IndexOf('\\') < 0 && dataUrl.IndexOf('\'') < 0
                ? dataUrl
                : dataUrl.Replace("\\", "\\\\").Replace("'", "\\'");
            Eval("window.__cast&&window.__cast.pushFrame('" + escaped + "');");
        }

        public void Close()
        {
            m_Ready = false;
            // Let the page say goodbye on the relay before tearing the WebView down.
            Eval("try{window.__cast&&window.__cast.destroy&&window.__cast.destroy()}catch(e){}");
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

                // On screen but invisible: a page in a fully transparent or detached view can be
                // paused, which stops the stream to the website.
                using var layout = new AndroidJavaObject("android.widget.FrameLayout$LayoutParams", 1, 1);
                m_WebView.Call("setLayoutParams", layout);
                m_WebView.Call("setAlpha", 0.01f);
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

        // A Java object with JavaScript-callable methods needs annotations Unity can't add, so
        // Android asks the page for its events instead, a couple of times a second.
        const float k_EventPollSeconds = 0.5f;
        float m_NextEventPoll;
        EventsCallback m_EventsCallback;

        void PollAndroidEvents()
        {
            if (m_WebView == null || Time.unscaledTime < m_NextEventPoll)
                return;
            m_NextEventPoll = Time.unscaledTime + k_EventPollSeconds;
            m_EventsCallback ??= new EventsCallback(this);
            var callback = m_EventsCallback;
            using var activity = Activity();
            activity.Call("runOnUiThread", new AndroidJavaRunnable(() =>
            {
                m_WebView?.Call("evaluateJavascript", "window.__cast&&window.__cast.takeEvents?window.__cast.takeEvents():''", callback);
            }));
        }

        sealed class EventsCallback : AndroidJavaProxy
        {
            readonly WebRtcCastWebView m_Owner;

            public EventsCallback(WebRtcCastWebView owner) : base("android.webkit.ValueCallback")
            {
                m_Owner = owner;
            }

            // The result arrives as a JSON string literal: quoted, with \n between events.
            // ReSharper disable once InconsistentNaming
            public void onReceiveValue(string json)
            {
                if (string.IsNullOrEmpty(json) || json == "null" || json == "\"\"")
                    return;
                var text = json.Length >= 2 && json[0] == '"' ? json.Substring(1, json.Length - 2) : json;
                foreach (var line in text.Replace("\\n", "\n").Split('\n'))
                {
                    if (line.Length > 0)
                        m_Owner.m_Events.Enqueue(line.Replace("\\\"", "\"").Replace("\\\\", "\\"));
                }
            }
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

        [DllImport("__Internal")]
        static extern string CardboardXR_CastTakeEvent(IntPtr view);
#endif
    }
}
