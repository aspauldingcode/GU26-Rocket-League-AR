using CardboardXR.Interaction;
using CardboardXR.UI;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using UnityEngine.XR.ARFoundation;

namespace CardboardXR.Stereo
{
    // Lets the user pick their viewer by scanning the QR code printed on it, like the Cardboard apps'
    // "switch viewer" flow, and remembers it between launches. A button at the top right, where
    // Cardboard apps put their settings gear, opens a full-screen camera view for viewer QR (phone
    // in hand). Share uses a world-space Material panel so palm/pinch XR stays immersed: scan the
    // laptop QR, and a valid room starts mono casting immediately with no confirm sheet.
    public class ViewerSetupController : MonoBehaviour
    {
        const string k_SavedViewerKey = "CardboardXR.ViewerUrl";
        const string k_ScanPrompt = "Point the camera at the QR code on your viewer.";
        // Shown on the world-space Share panel; URL must match WebRtcCast.PagesBaseUrl.
        const string k_CastScanPrompt =
            "Look at the QR code from\nhttps://aspauldingcode.github.io/GU26-Rocket-League-AR";
        const float k_RetryDelaySeconds = 2.5f;
        const float k_ToastSeconds = 3f;
        const float k_WorldCastDistance = 1.2f;
        // Slightly below eye level: in view while looking at the laptop, off the QR itself.
        const float k_WorldCastPitch = -10f;
        // The hint re-centers once the head turns this far away from it.
        const float k_WorldCastDeadZone = 30f;
        // No camera image for this long after the scan starts: say so on the hint.
        const float k_NoFeedSeconds = 5f;
        // A second Share press within this window is the same press delivered twice, not a
        // toggle-off (PinchRouter's k_ClickLockoutSeconds guards pinches the same way).
        const float k_SharePressSeconds = 0.5f;

        // A share scan began: anything else using the room (the lobby's room scan) steps aside.
        public static event System.Action ShareScanStarted;

        [SerializeField] StereoRig m_Rig;
        [SerializeField] ARSession m_Session;
        [SerializeField] ARCameraManager m_CameraManager;
        [SerializeField] ViewerQrScanner m_Scanner;
        [SerializeField] WebRtcCastHost m_Cast;

        GameObject m_SettingsButton;
        GameObject m_Menu;
        float m_MenuCloseTime;
        M3Button m_View;
        M3Button m_Share;
        M3Button m_ToolbarShare;
        M3Button m_ToolbarRestart;
        M3Button m_UseCardboard;
        bool m_ShownMono;
        bool m_ShareSelected;
        GameObject m_ScanPanel;
        RawImage m_Preview;
        AspectRatioFitter m_PreviewFitter;
        TMP_Text m_Status;
        TMP_Text m_Toast;
        GameObject m_Snackbar;
        float m_ToastHideTime;
        bool m_Resolving;
        float m_LastSharePress = float.NegativeInfinity;
        bool m_CastScanning;
        bool m_SessionWasEnabled;
        bool m_CameraWasEnabled;

        // World-space cast scan hint (gaze + pinch), not the phone 2D overlay. No camera preview: a
        // second picture of the room floating in view read as a stray window.
        RectTransform m_WorldCastPanel;
        TMP_Text m_WorldCastStatus;
        float m_WorldCastYaw;
        bool m_ToolbarWired;

        void Awake()
        {
            EnsureEventSystem();
            if (m_Cast == null)
            {
                m_Cast = GetComponent<WebRtcCastHost>();
                if (m_Cast == null)
                    m_Cast = gameObject.AddComponent<WebRtcCastHost>();
                m_Cast.Configure(m_Rig);
                m_Cast.Ended += () => ShowToast("Viewer page closed — stopped sharing");
            }
            BuildUi();
            BuildWorldCastUi();
            EnsureToolbarShare();
            EnsureToolbarRestart();
            ShowScanPanel(false);
            ShowWorldCastScan(false);
            LoadSavedViewer();
        }

        void OnEnable() => m_Scanner.CodeFound += OnCodeFound;

        void OnDisable() => m_Scanner.CodeFound -= OnCodeFound;

        void Update()
        {
            if (!m_ToolbarWired)
                EnsureToolbarShare();
            if (m_ToolbarRestart == null)
                EnsureToolbarRestart();

            if (m_ScanPanel.activeSelf && m_Scanner.Preview != null)
            {
                m_Preview.texture = m_Scanner.Preview;
                m_PreviewFitter.aspectRatio = m_Scanner.Preview.width / (float)m_Scanner.Preview.height;
            }

            if (m_CastScanning)
                UpdateWorldCastScan();

            if (M3Fade.IsShown(m_Snackbar) && Time.unscaledTime > m_ToastHideTime)
                M3Fade.Hide(m_Snackbar);
            if (M3Fade.IsShown(m_Menu) && Time.unscaledTime > m_MenuCloseTime)
                M3Fade.Hide(m_Menu);

            if (m_ShownMono != m_Rig.IsMono)
                UpdateViewLabel();
            var sharing = m_Cast != null && m_Cast.IsCasting;
            if (sharing != m_ShareSelected)
                UpdateShareLabel();
        }

        // Hand-tracked Material toolbar (Gaze Panel): same cast flow as the phone-screen Share menu.
        public void ShareScreen() => ChooseShare();

        void LoadSavedViewer()
        {
            var url = PlayerPrefs.GetString(k_SavedViewerKey, "");
            if (url.Length == 0)
                return;

            if (ViewerParamsCodec.TryDecodeUrl(url, out var viewer))
            {
                m_Rig.Viewer = viewer;
                Debug.Log($"[Viewer] Using saved viewer {viewer.vendor} {viewer.model}");
            }
            else
            {
                PlayerPrefs.DeleteKey(k_SavedViewerKey);
            }
        }

        // Phone-in-hand viewer QR only: full-screen 2D, stereo paused.
        void SetScanning(bool scanning)
        {
            if (scanning == m_ScanPanel.activeSelf)
                return;
            if (scanning)
            {
                m_SessionWasEnabled = m_Session.enabled;
                m_CameraWasEnabled = m_CameraManager.enabled;
            }

            ShowScanPanel(scanning);
            m_Session.enabled = scanning || m_SessionWasEnabled;
            m_CameraManager.enabled = scanning || m_CameraWasEnabled;
            m_Scanner.enabled = scanning;
        }

        void ToggleView()
        {
            M3Fade.Hide(m_Menu);
            m_Rig.ToggleMono();
            UpdateViewLabel();
        }

        void ToggleMenu()
        {
            M3Fade.SetShown(m_Menu, !M3Fade.IsShown(m_Menu));
            m_MenuCloseTime = Time.unscaledTime + k_MenuSeconds;
        }

        void ChooseOpenSettings()
        {
            M3Fade.Hide(m_Menu);
            SettingsPanelHub.Open();
        }

        void ChooseSetViewer()
        {
            M3Fade.Hide(m_Menu);
            StopWorldCastScan(false);
            SetScanning(true);
        }

        // A second Share press this soon after the last is the same press delivered twice,
        // not a toggle-off. Pure, for testing.
        public static bool IsShareRepeat(float lastPress, float now) => now - lastPress < k_SharePressSeconds;

        void ChooseShare()
        {
            // One press can be delivered twice — a button carrying both a serialized and a
            // runtime listener, or a pinch racing the viewer trigger — and Share toggles,
            // so the duplicate would undo the press in the same instant and Share would
            // look dead. A press this soon after the last one is that same press.
            var now = Time.unscaledTime;
            if (IsShareRepeat(m_LastSharePress, now))
                return;
            m_LastSharePress = now;
            Debug.Log($"[Cast] Share pressed (casting={m_Cast != null && m_Cast.IsCasting}, scanning={m_CastScanning})");
            M3Fade.Hide(m_Menu);
            SettingsPanelHub.Close();
            // Share is world-space only — never open the phone 2D Scan Panel / never SetScanning.
            if (m_ScanPanel != null && m_ScanPanel.activeSelf)
                ShowScanPanel(false);

            if (m_Cast != null && m_Cast.IsCasting)
            {
                m_Cast.StopCast();
                UpdateShareLabel();
                return;
            }

            if (m_CastScanning)
            {
                StopWorldCastScan(false);
                return;
            }

            StartWorldCastScan();
        }

        void StartWorldCastScan()
        {
            m_CastScanning = true;
            m_Resolving = false;
            ShareScanStarted?.Invoke();
            // The room scan's wave and lobby panel would cover the view while looking for the QR.
            var boundary = FindAnyObjectByType<RoomBoundary>();
            if (boundary != null && boundary.IsScanning)
            {
                boundary.FinishScan();
                Debug.Log("[Cast] Ended the room scan for Share");
            }

            // In 6DoF the AR session already runs and the scanner reads its images. Otherwise the
            // pinch sensor holds the camera and the scanner reads its frames. The scan never turns
            // the AR session or camera manager on or off: enabling the AR camera outside 6DoF
            // paints its (black) background over the stereo view, and fighting the pinch sensor
            // for the camera stopped both scanning and pinching.
            var arRunning = m_Session.enabled && m_CameraManager.enabled;
            m_Scanner.UseFeed = !arRunning;
            m_Scanner.enabled = true;
            // Phone display stays whatever StereoRig already is (usually stereo). Do not SetMono,
            // do not disable the rig, do not open 2D overlays for this flow.
            if (m_Rig != null && !m_Rig.enabled)
                m_Rig.enabled = true;
            if (m_WorldCastStatus != null)
                m_WorldCastStatus.text = k_CastScanPrompt;
            ShowWorldCastScan(true);
            Debug.Log($"[Cast] Share scan started from {(arRunning ? "the AR camera" : "the pinch camera")}; display untouched");
        }

        void UpdateWorldCastScan()
        {
            // Switched to 3DoF mid-scan: the AR session has stopped, and the pinch sensor takes over.
            if (!m_Scanner.UseFeed && !(m_Session.enabled && m_CameraManager.enabled))
                m_Scanner.UseFeed = true;
            if (m_WorldCastStatus != null && Time.unscaledTime - m_Scanner.LastImageTime > k_NoFeedSeconds)
                m_WorldCastStatus.text = "No camera feed";
            FollowHead(false);
        }

        // paired: hide at once, so nothing lingers in view once the website shows the stream.
        void StopWorldCastScan(bool paired)
        {
            if (!m_CastScanning && (m_WorldCastPanel == null || !m_WorldCastPanel.gameObject.activeSelf))
                return;
            m_CastScanning = false;
            m_Resolving = false;
            m_Scanner.enabled = false;
            m_Scanner.UseFeed = false;
            // The AR session and camera manager are left exactly as they were: this scan never
            // turned them on, so it must not turn them off under a running 6DoF demo either.
            if (m_WorldCastPanel == null)
                return;
            if (paired)
                m_WorldCastPanel.gameObject.SetActive(false);
            else
                M3Fade.Hide(m_WorldCastPanel.gameObject);
        }

        void ShowWorldCastScan(bool show)
        {
            if (m_WorldCastPanel == null)
                return;
            if (show)
                FollowHead(true);
            M3Fade.SetShown(m_WorldCastPanel.gameObject, show);
        }

        // World-locked below the gaze, re-centered only once the head turns well away, so it
        // neither swims with every head movement nor sits over the QR being looked at.
        void FollowHead(bool snap)
        {
            if (m_WorldCastPanel == null || m_Rig == null)
                return;
            var head = m_Rig.transform;
            var flat = Vector3.ProjectOnPlane(head.forward, Vector3.up);
            if (flat.sqrMagnitude < 1e-4f)
                return;
            var headYaw = Quaternion.LookRotation(flat.normalized).eulerAngles.y;
            if (snap)
                m_WorldCastYaw = headYaw;
            else if (Mathf.Abs(Mathf.DeltaAngle(m_WorldCastYaw, headYaw)) > k_WorldCastDeadZone)
                m_WorldCastYaw = Mathf.MoveTowardsAngle(m_WorldCastYaw, headYaw, 240f * Time.unscaledDeltaTime);
            else
                return;
            var direction = Quaternion.Euler(-k_WorldCastPitch, m_WorldCastYaw, 0f) * Vector3.forward;
            var position = head.position + direction * k_WorldCastDistance;
            m_WorldCastPanel.SetPositionAndRotation(position, Quaternion.LookRotation(direction, Vector3.up));
        }

        void UpdateViewLabel()
        {
            m_ShownMono = m_Rig.IsMono;
            if (m_View != null)
            {
                m_View.Text = m_ShownMono ? "Viewer view" : "Phone view";
                m_View.Icon = m_ShownMono ? "view_agenda" : "smartphone";
            }
        }

        void UpdateShareLabel()
        {
            var on = m_Cast != null && m_Cast.IsCasting;
            m_ShareSelected = on;
            if (m_Share != null)
            {
                m_Share.Text = on ? "Stop share" : "Share view";
                m_Share.Icon = on ? "cast_connected" : "cast";
            }
            if (m_ToolbarShare != null)
            {
                m_ToolbarShare.Selected = on;
                m_ToolbarShare.Text = on ? "Sharing" : "Share";
                m_ToolbarShare.Icon = on ? "cast_connected" : "cast";
            }
        }

        // Whether the button's serialized listeners already call ShareScreen on target.
        // StereoBuilder writes that call into the scene, and serialized calls DO fire in
        // builds — so also adding a runtime listener runs ChooseShare twice per press.
        // Share toggles, so the second run cancelled the first in the same instant: the
        // 3D Share looked dead while the 2D menu (runtime listener only) worked. Pure,
        // for testing.
        public static bool HasSerializedShare(Button button, UnityEngine.Object target)
        {
            if (button == null)
                return false;
            for (var i = 0; i < button.onClick.GetPersistentEventCount(); i++)
            {
                if (button.onClick.GetPersistentTarget(i) == target &&
                    button.onClick.GetPersistentMethodName(i) == nameof(ShareScreen))
                    return true;
            }
            return false;
        }

        // The 3D toolbar Share must act exactly like the phone-screen menu's Share: same
        // ChooseShare. The scene's button already carries a serialized call to ShareScreen
        // (see HasSerializedShare); wire a runtime listener only where it doesn't, so the
        // press lands exactly once either way. Retried from Update until it lands, since
        // the panel may not exist yet when Awake runs.
        void EnsureToolbarShare()
        {
            if (m_ToolbarWired)
                return;
            var panelObject = FindGazePanel();
            if (panelObject == null)
                return;
            var panel = (RectTransform)panelObject.transform;
            var existing = panel.Find("Share");
            if (existing != null)
            {
                m_ToolbarShare = existing.GetComponent<M3Button>();
                var button = m_ToolbarShare != null && m_ToolbarShare.Button != null
                    ? m_ToolbarShare.Button
                    : existing.GetComponent<Button>();
                if (button == null)
                    return;
                button.onClick.RemoveListener(ShareScreen);
                var serialized = HasSerializedShare(button, this);
                if (!serialized)
                    button.onClick.AddListener(ShareScreen);
                m_ToolbarWired = true;
                UpdateShareLabel();
                Debug.Log($"[Cast] 3D toolbar Share wired to the share flow " +
                          $"({(serialized ? "serialized call already on the button" : "runtime listener added")})");
                return;
            }

            var toolbar = panelObject.GetComponent<M3Toolbar>();
            if (toolbar == null)
                return;

            m_ToolbarShare = M3.ToolbarItem("Share", panel, "cast", "Share", Vector2.zero, 92f, ShareScreen);
            var apps = panel.Find("Apps") as RectTransform;
            var recenter = panel.Find("Recenter") as RectTransform;
            var scan = panel.Find("Scan") as RectTransform;
            var tracking = panel.Find("Tracking Mode") as RectTransform;
            var passthrough = panel.Find("Passthrough") as RectTransform;
            if (apps == null || recenter == null || tracking == null || passthrough == null)
                return;

            var shareRect = (RectTransform)m_ToolbarShare.transform;
            var items = scan != null
                ? new[] { apps, recenter, scan, shareRect, tracking, passthrough }
                : new[] { apps, recenter, shareRect, tracking, passthrough };
            var groups = scan != null
                ? new[] { 0, 1, 1, 1, 2, 2 }
                : new[] { 0, 1, 1, 2, 2 };

            var dividers = new Image[3];
            var d1 = panel.Find("Divider 1");
            var d2 = panel.Find("Divider 2");
            if (d1 != null) dividers[1] = d1.GetComponent<Image>();
            if (d2 != null) dividers[2] = d2.GetComponent<Image>();
            toolbar.SetItems(items, groups, dividers);
            m_ToolbarWired = true;
            UpdateShareLabel();
        }

        // Palm settings → Restart reloads Core. Added next to Share so demos don't each need a button.
        void EnsureToolbarRestart()
        {
            if (m_ToolbarRestart != null)
                return;
            var panelObject = FindGazePanel();
            if (panelObject == null)
                return;
            var panel = (RectTransform)panelObject.transform;
            var existing = panel.Find("Restart");
            if (existing != null)
            {
                m_ToolbarRestart = existing.GetComponent<M3Button>();
                var button = m_ToolbarRestart != null && m_ToolbarRestart.Button != null
                    ? m_ToolbarRestart.Button
                    : existing.GetComponent<Button>();
                if (button == null)
                    return;
                button.onClick.RemoveListener(RestartApp);
                button.onClick.AddListener(RestartApp);
                return;
            }

            var toolbar = panelObject.GetComponent<M3Toolbar>();
            if (toolbar == null)
                return;

            m_ToolbarRestart = M3.ToolbarItem("Restart", panel, "restart_alt", "Restart", Vector2.zero, 92f, RestartApp);
            var apps = panel.Find("Apps") as RectTransform;
            var recenter = panel.Find("Recenter") as RectTransform;
            var scan = panel.Find("Scan") as RectTransform;
            var share = panel.Find("Share") as RectTransform;
            var tracking = panel.Find("Tracking Mode") as RectTransform;
            var passthrough = panel.Find("Passthrough") as RectTransform;
            var restartRect = (RectTransform)m_ToolbarRestart.transform;
            if (apps == null || recenter == null || tracking == null || passthrough == null)
                return;

            RectTransform[] items;
            int[] groups;
            if (scan != null && share != null)
            {
                items = new[] { apps, recenter, scan, share, restartRect, tracking, passthrough };
                groups = new[] { 0, 1, 1, 1, 1, 2, 2 };
            }
            else if (share != null)
            {
                items = new[] { apps, recenter, share, restartRect, tracking, passthrough };
                groups = new[] { 0, 1, 1, 1, 2, 2 };
            }
            else if (scan != null)
            {
                items = new[] { apps, recenter, scan, restartRect, tracking, passthrough };
                groups = new[] { 0, 1, 1, 1, 2, 2 };
            }
            else
            {
                items = new[] { apps, recenter, restartRect, tracking, passthrough };
                groups = new[] { 0, 1, 1, 2, 2 };
            }

            var dividers = new Image[3];
            var d1 = panel.Find("Divider 1");
            var d2 = panel.Find("Divider 2");
            if (d1 != null) dividers[1] = d1.GetComponent<Image>();
            if (d2 != null) dividers[2] = d2.GetComponent<Image>();
            toolbar.SetItems(items, groups, dividers);
            Debug.Log("[Settings] Restart added to the palm settings toolbar");
        }

        void RestartApp()
        {
            Debug.Log("[Settings] Restarting the app from the palm settings toolbar");
            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (scene.IsValid() && scene.name == "Core" && scene.buildIndex >= 0)
                {
                    SceneManager.LoadScene(scene.buildIndex, LoadSceneMode.Single);
                    return;
                }
            }
            SceneManager.LoadScene(0, LoadSceneMode.Single);
        }

        static GameObject FindGazePanel()
        {
            var canvases = FindObjectsByType<Canvas>(FindObjectsInactive.Include);
            for (var i = 0; i < canvases.Length; i++)
            {
                if (canvases[i] != null && canvases[i].gameObject.name == "Gaze Panel")
                    return canvases[i].gameObject;
            }
            return null;
        }

        void ShowScanPanel(bool show)
        {
            m_ScanPanel.SetActive(show);
            m_SettingsButton.SetActive(!show);
            M3Fade.Hide(m_Menu);
            if (!show)
                m_CastScanning = false;
            m_Rig.enabled = !show;
            m_Status.text = k_ScanPrompt;
            if (m_UseCardboard != null)
                m_UseCardboard.gameObject.SetActive(show);
        }

        async void OnCodeFound(string text)
        {
            if (m_Resolving)
                return;

            m_Resolving = true;
            m_Scanner.enabled = false;
            Debug.Log($"[Viewer] Scanned: {text}");

            if (m_CastScanning)
            {
                if (WebRtcCast.TryParseCast(text, out var room, out var aspect))
                {
                    // Valid GH Pages room: off-screen mono at the page's aspect → Pages; phone stereo
                    // unchanged; the hint goes at once; no 2D UI.
                    var displayMono = m_Rig != null && m_Rig.IsMono;
                    StopWorldCastScan(true);
                    m_Cast.StartCast(room, aspect);
                    UpdateShareLabel();
                    if (m_Rig != null && m_Rig.IsMono != displayMono)
                        Debug.LogError($"[Cast] Display mode changed after StartCast (was mono={displayMono})");
                    Debug.Log($"[Cast] Auto-started room {room}; phone IsMono={m_Rig != null && m_Rig.IsMono}");
                    return;
                }

                if (m_WorldCastStatus != null)
                    m_WorldCastStatus.text = "That QR isn't the share page's. Look at the one on the website.";
                await Awaitable.WaitForSecondsAsync(k_RetryDelaySeconds);
                m_Resolving = false;
                if (m_CastScanning)
                {
                    if (m_WorldCastStatus != null)
                        m_WorldCastStatus.text = k_CastScanPrompt;
                    m_Scanner.enabled = true;
                }
                return;
            }

            m_Status.text = "Reading viewer...";
            var (status, url) = await ViewerUrlResolver.ResolveAsync(text);
            m_Resolving = false;
            if (!m_ScanPanel.activeSelf)
                return;

            if (status == ViewerUrlResolver.Status.Ok && ViewerParamsCodec.TryDecodeUrl(url, out var viewer))
            {
                PlayerPrefs.SetString(k_SavedViewerKey, url);
                PlayerPrefs.Save();
                m_Rig.Viewer = viewer;
                SetScanning(false);
                ShowToast($"Viewer: {viewer.vendor} {viewer.model}");
                Debug.Log($"[Viewer] Switched to {viewer.vendor} {viewer.model} from {url}");
                return;
            }

            m_Status.text = status == ViewerUrlResolver.Status.ConnectionError
                ? "Couldn't follow the code's link. Check the connection and try again."
                : "That doesn't look like a Cardboard viewer code. Trying again...";
            await Awaitable.WaitForSecondsAsync(k_RetryDelaySeconds);
            if (m_ScanPanel.activeSelf)
            {
                m_Status.text = k_ScanPrompt;
                m_Scanner.enabled = true;
            }
        }

        void UseCardboardV1()
        {
            PlayerPrefs.DeleteKey(k_SavedViewerKey);
            PlayerPrefs.Save();
            m_Rig.Viewer = ViewerParams.CardboardV1;
            SetScanning(false);
            ShowToast("Viewer: Cardboard v1");
        }

        void ShowToast(string message)
        {
            m_Toast.text = message;
            M3Fade.Show(m_Snackbar);
            m_ToastHideTime = Time.unscaledTime + k_ToastSeconds;
        }

        static void EnsureEventSystem()
        {
            if (EventSystem.current == null)
                new GameObject("EventSystem", typeof(EventSystem), typeof(InputSystemUIInputModule));
        }

        void BuildWorldCastUi()
        {
            // World-locked, not a child of the head: placed below the gaze by FollowHead.
            m_WorldCastPanel = M3.Canvas("Cast Scan", transform, new Vector2(480f, 132f), k_WorldCastDistance,
                M3Theme.SurfaceContainer, M3Theme.k_ShapePanel);
            m_WorldCastPanel.gameObject.AddComponent<GazeCanvas>();

            M3.Icon("Icon", m_WorldCastPanel, "qr_code_scanner", 28f, new Vector2(-206f, 30f), M3Theme.Primary);
            M3.Text("Title", m_WorldCastPanel, "Share screen", M3Theme.Type.TitleMedium, new Vector2(20f, 30f),
                new Vector2(400f, 28f), alignment: TextAlignmentOptions.Left);
            m_WorldCastStatus = M3.Text("Status", m_WorldCastPanel, k_CastScanPrompt, M3Theme.Type.BodyMedium,
                new Vector2(-50f, -26f), new Vector2(340f, 52f), M3Theme.OnSurfaceVariant, TextAlignmentOptions.Left);
            M3.Button("Cancel", m_WorldCastPanel, "Cancel", M3Button.Variant.Tonal, new Vector2(170f, -26f),
                () => StopWorldCastScan(false), null, 108f);
            m_WorldCastPanel.gameObject.SetActive(false);
        }

        void BuildUi()
        {
            var canvasObject = new GameObject("Viewer Setup UI", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            canvasObject.transform.SetParent(transform, false);
            var canvas = canvasObject.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 100;
            var scaler = canvasObject.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 1f;
            var root = (RectTransform)canvasObject.transform;

            var corner = Frame("Corner", root, new Vector2(1f, 1f), new Vector2(-12f, -12f), new Vector2(40f, 40f));
            m_SettingsButton = M3.IconButton("Settings", corner, "settings", M3Button.Variant.Tonal, Vector2.zero, ToggleMenu, 40f).gameObject;
            var menuFrame = Frame("Menu Frame", root, new Vector2(1f, 1f), new Vector2(-12f, -60f), new Vector2(200f, 248f));
            var menu = M3.Surface("Menu", menuFrame, Vector2.zero, new Vector2(200f, 248f), M3Theme.SurfaceContainer, M3Theme.k_ShapeSmall);
            M3.Button("Set viewer", menu.rectTransform, "Set viewer", M3Button.Variant.Text, new Vector2(0f, 90f), ChooseSetViewer,
                "qr_code_scanner", 184f);
            m_View = M3.Button("View", menu.rectTransform, "Phone view", M3Button.Variant.Text, new Vector2(0f, 30f), ToggleView,
                "smartphone", 184f);
            m_Share = M3.Button("Share", menu.rectTransform, "Share view", M3Button.Variant.Text, new Vector2(0f, -30f), ChooseShare,
                "cast", 184f);
            M3.Button("Open settings", menu.rectTransform, "Open settings", M3Button.Variant.Text, new Vector2(0f, -90f),
                ChooseOpenSettings, "settings", 184f);
            m_Menu = menuFrame.gameObject;
            m_Menu.AddComponent<M3Fade>();
            m_Menu.SetActive(false);
            UpdateViewLabel();
            UpdateShareLabel();

            // Viewer QR only (phone in hand). Share uses world-space Cast Scan.
            m_ScanPanel = new GameObject("Scan Panel", typeof(Image));
            m_ScanPanel.transform.SetParent(root, false);
            Stretch((RectTransform)m_ScanPanel.transform);
            m_ScanPanel.GetComponent<Image>().color = M3Theme.Surface;

            var previewObject = new GameObject("Camera Preview", typeof(RawImage), typeof(AspectRatioFitter));
            previewObject.transform.SetParent(m_ScanPanel.transform, false);
            Stretch((RectTransform)previewObject.transform);
            m_Preview = previewObject.GetComponent<RawImage>();
            m_Preview.raycastTarget = false;
            m_PreviewFitter = previewObject.GetComponent<AspectRatioFitter>();
            m_PreviewFitter.aspectMode = AspectRatioFitter.AspectMode.FitInParent;

            var scanRect = (RectTransform)m_ScanPanel.transform;
            var top = Frame("Top", scanRect, new Vector2(0.5f, 1f), new Vector2(0f, -16f), new Vector2(600f, 64f));
            var statusBar = M3.Surface("Status Bar", top, Vector2.zero, new Vector2(600f, 64f), M3Theme.SurfaceContainerHigh,
                M3Theme.k_ShapeLarge);
            m_Status = M3.Text("Status", statusBar.rectTransform, "", M3Theme.Type.BodyLarge, Vector2.zero, new Vector2(568f, 56f));
            var actions = Frame("Actions", scanRect, new Vector2(0.5f, 0f), new Vector2(0f, 16f), new Vector2(420f, 56f));
            M3.Button("Cancel", actions, "Cancel", M3Button.Variant.Tonal, new Vector2(-110f, 0f), () => SetScanning(false), "close", 180f);
            m_UseCardboard = M3.Button("Use Cardboard v1", actions, "Use Cardboard v1", M3Button.Variant.Filled,
                new Vector2(100f, 0f), UseCardboardV1, null, 210f);

            var bottom = Frame("Snackbar Frame", root, new Vector2(0.5f, 0f), new Vector2(0f, 72f), new Vector2(560f, 56f));
            var snackbar = M3.Surface("Snackbar", bottom, Vector2.zero, new Vector2(560f, 56f), M3Theme.InverseSurface, 4f);
            m_Toast = M3.Text("Message", snackbar.rectTransform, "", M3Theme.Type.BodyMedium, Vector2.zero, new Vector2(528f, 48f),
                M3Theme.InverseOnSurface, TextAlignmentOptions.Left);
            m_Snackbar = snackbar.gameObject;
            m_Snackbar.AddComponent<M3Fade>();
            m_Snackbar.SetActive(false);
        }

        const float k_MenuSeconds = 6f;
        const float k_DpOnScreen = 2.6f;

        static RectTransform Frame(string name, RectTransform parent, Vector2 anchor, Vector2 offsetDp, Vector2 sizeDp)
        {
            var frame = new GameObject(name, typeof(RectTransform));
            frame.transform.SetParent(parent, false);
            var rect = (RectTransform)frame.transform;
            rect.anchorMin = rect.anchorMax = rect.pivot = anchor;
            rect.sizeDelta = sizeDp;
            rect.localScale = Vector3.one * k_DpOnScreen;
            rect.anchoredPosition = offsetDp * k_DpOnScreen;
            return rect;
        }

        static void Stretch(RectTransform rect)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
        }
    }
}
