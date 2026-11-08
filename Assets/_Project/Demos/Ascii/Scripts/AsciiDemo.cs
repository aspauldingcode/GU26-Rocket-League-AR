using CardboardXR.Stereo;
using CardboardXR.UI;
using TMPro;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

namespace CardboardXR.Demos.Ascii
{
    // Place a glyph gate on the floor. Outside: normal passthrough, and the doorway peeks at a
    // terminal rendering of the other side. Inside: passthrough off, the room is that same
    // screen-space terminal. Character size, density and brightness show distance. The room is
    // green; weeping angels are gray stone with a soft edge. The camera keeps running, so the
    // open doorway shows the real room and people in it are drawn as light-blue glyphs. Logs [Ascii].
    public class AsciiDemo : Demo
    {
        const float k_DipAlpha = 0.75f;
        const float k_DipSeconds = 0.12f;
        const float k_RiseSeconds = 0.45f;
        const float k_WaveSeconds = 1.1f;

        static readonly int k_CameraImageId = Shader.PropertyToID("_AsciiCameraImage");
        static readonly int k_CameraFrustumId = Shader.PropertyToID("_AsciiCameraFrustum");
        static readonly int k_CameraOnId = Shader.PropertyToID("_AsciiCameraOn");
        static readonly int k_WaveId = Shader.PropertyToID("_AsciiWave");

        [SerializeField] GameObject m_PortalPrefab;
        [SerializeField] Material m_AsciiMaterial;
        [SerializeField] GameObject m_AngelPrefab;

        CoreServices m_Core;
        AsciiWorld m_Portal;
        AsciiSurfaces m_Surfaces;
        AsciiMeshes m_Meshes;
        AsciiAngels m_Angels;
        AsciiMathQuiz m_Quiz;
        AsciiPeople m_People;
        ScreenFade m_Fade;
        Coroutine m_Crossing;
        bool m_Capturing;
        RectTransform m_Panel;
        RectTransform m_DeathPanel;
        TMP_Text m_Hint;
        M3Button m_MoveButton;
        AudioSource m_Audio;
        AudioClip m_Whoosh;
        ARPlaneManager m_Planes;
        PlaneDetectionMode? m_PreviousDetection;
        bool m_HadPassthrough;
        bool m_InsideWorld;
        bool m_Dead;

        public override void OnDemoEnter(CoreServices core)
        {
            m_Core = core;
            core.Placer.ObjectPrefab = m_PortalPrefab;
            core.Placer.Placed += OnPlaced;

            m_Surfaces = core.Boundary.gameObject.AddComponent<AsciiSurfaces>();
            var planes = core.Boundary.GetComponent<ARPlaneManager>();
            if (planes == null)
                planes = core.Boundary.GetComponentInChildren<ARPlaneManager>(true);
            if (planes == null)
                planes = Object.FindAnyObjectByType<ARPlaneManager>(FindObjectsInactive.Include);
            if (planes == null)
                Debug.LogWarning("[Ascii] No ARPlaneManager: room surfaces can't be remapped");
            else
                FindWalls(planes, core.Boundary);
            m_Surfaces.Configure(m_AsciiMaterial, planes);
            // Keep finding floors, tables, seats and walls after the lobby scan ends.
            core.Boundary.HoldDetection = true;
            core.Boundary.ScanRoom();

            m_Meshes = gameObject.AddComponent<AsciiMeshes>();
            m_Meshes.Configure(m_AsciiMaterial, core.Boundary != null ? core.Boundary.transform : null);
            // Meshing stays warm but overlays only draw while inside.
            m_Meshes.SetActive(false);

            m_Angels = gameObject.AddComponent<AsciiAngels>();
            m_Angels.Configure(m_AngelPrefab, core.Head, m_AsciiMaterial, m_Meshes.MeshManager);
            m_Angels.Caught += OnCaught;

            m_Quiz = gameObject.AddComponent<AsciiMathQuiz>();
            m_Quiz.Configure(planes, core.Head, m_Angels.SpeedUp, OnQuizComplete);
            m_Quiz.WallSelected += OnQuizWallSelected;

            m_People = gameObject.AddComponent<AsciiPeople>();
            m_People.Configure(core.Passthrough);
            m_Fade = Object.FindAnyObjectByType<ScreenFade>(FindObjectsInactive.Include);

            m_Audio = gameObject.AddComponent<AudioSource>();
            m_Audio.playOnAwake = false;
            m_Whoosh = Whoosh();
            BuildPanel();
            BuildDeathPanel();
            UpdateHint();
            Debug.Log("[Ascii] Ready: look at the floor and pinch to open the glyph gate");
        }

        public override void OnDemoExit(CoreServices core)
        {
            core.Placer.Placed -= OnPlaced;
            if (m_Portal != null)
                m_Portal.Crossed -= OnCrossed;
            if (m_Quiz != null)
            {
                m_Quiz.WallSelected -= OnQuizWallSelected;
                m_Quiz.Stop();
                Destroy(m_Quiz);
                m_Quiz = null;
            }
            StopCrossing();
            SetInsideWorld(false);
            SetCapture(false);
            if (m_People != null)
            {
                Destroy(m_People);
                m_People = null;
            }
            AsciiTerminal.Active = false;
            if (m_Surfaces != null)
            {
                m_Surfaces.SetLive(false);
                Destroy(m_Surfaces);
                m_Surfaces = null;
            }
            if (m_Meshes != null)
            {
                m_Meshes.SetActive(false);
                Destroy(m_Meshes);
                m_Meshes = null;
            }
            if (m_Angels != null)
            {
                m_Angels.Caught -= OnCaught;
                m_Angels.SetInside(false);
                Destroy(m_Angels);
                m_Angels = null;
            }
            core.Boundary.HoldDetection = false;
            core.Boundary.ShowMarks = true;
            if (m_Planes != null && m_PreviousDetection.HasValue)
                m_Planes.requestedDetectionMode = m_PreviousDetection.Value;
            m_Planes = null;
            m_PreviousDetection = null;
            Debug.Log("[Ascii] Leaving");
        }

        void FindWalls(ARPlaneManager planes, RoomBoundary boundary)
        {
            m_Planes = planes;
            m_PreviousDetection = planes.requestedDetectionMode;
            const PlaneDetectionMode wanted = PlaneDetectionMode.Horizontal | PlaneDetectionMode.Vertical;
            if ((planes.requestedDetectionMode & wanted) == wanted)
                return;
            planes.requestedDetectionMode |= wanted;
            boundary.ScanRoom();
            Debug.Log("[Ascii] Scanning for walls as well as floors and tables");
        }

        void OnPlaced(GameObject placed)
        {
            if (!placed.TryGetComponent<AsciiWorld>(out var portal))
                return;
            placed.transform.localPosition = Vector3.zero;
            m_Portal = portal;
            m_Portal.Crossed += OnCrossed;
            m_Core.Placer.ObjectPrefab = null;
            // The X marks are the placement preview. Once the gate is up they draw over the glyphs.
            m_Core.Boundary.ShowMarks = false;
            AsciiTerminal.Active = true;
            SetCapture(true);
            m_Surfaces?.SetLive(true);
            var distance = Vector3.Distance(m_Core.Head.position, placed.transform.position);
            Debug.Log($"[Ascii] Gate opened {distance:F1} m away");
            m_Audio.PlayOneShot(m_Whoosh, 0.4f);
            UpdateHint();
        }

        void OnCrossed(bool inside)
        {
            m_Audio.PlayOneShot(m_Whoosh, 0.7f);
            StopCrossing();
            m_Crossing = StartCoroutine(Cross(inside));
        }

        // Dip toward black, swap worlds while it's dark, then lift. Going in, the glyphs also
        // spread outward from the head instead of the whole room appearing at once.
        System.Collections.IEnumerator Cross(bool inside)
        {
            if (m_Fade != null)
                yield return m_Fade.FadeTo(k_DipAlpha, k_DipSeconds);
            Shader.SetGlobalFloat(k_WaveId, inside ? 1f : 0f);
            ApplyCrossing(inside);
            var dark = m_Fade != null ? m_Fade.Alpha : 0f;
            var seconds = inside ? Mathf.Max(k_RiseSeconds, k_WaveSeconds) : k_RiseSeconds;
            for (var t = 0f; t < seconds; t += Time.unscaledDeltaTime)
            {
                if (m_Fade != null)
                    m_Fade.Alpha = Mathf.Lerp(dark, 0f, Mathf.SmoothStep(0f, 1f, t / k_RiseSeconds));
                if (inside)
                    Shader.SetGlobalFloat(k_WaveId, 1f - Mathf.SmoothStep(0f, 1f, t / k_WaveSeconds));
                yield return null;
            }
            if (m_Fade != null)
                m_Fade.Alpha = 0f;
            Shader.SetGlobalFloat(k_WaveId, 0f);
            m_Crossing = null;
        }

        void StopCrossing()
        {
            if (m_Crossing != null)
                StopCoroutine(m_Crossing);
            m_Crossing = null;
            Shader.SetGlobalFloat(k_WaveId, 0f);
            if (m_Fade != null && m_Fade.Alpha > 0f && m_Fade.Alpha <= k_DipAlpha)
                m_Fade.Alpha = 0f;
        }

        void ApplyCrossing(bool inside)
        {
            SetInsideWorld(inside);
            m_Surfaces?.SetDrawn(inside);
            m_Meshes?.SetActive(inside);
            m_MoveButton.Interactable = !inside;
            if (inside)
                m_Quiz?.Begin();
            else
            {
                m_Quiz?.Stop();
                m_Angels?.SetInside(false);
            }
            UpdateHint();
        }

        void OnQuizWallSelected(ARPlane quizWall)
        {
            if (m_InsideWorld && !m_Dead && !m_Angels.HasAngel)
                m_Angels?.SetInside(true, OppositeWallSpawn(quizWall));
            UpdateHint();
        }

        Vector3 OppositeWallSpawn(ARPlane quizWall)
        {
            var center2D = quizWall.centerInPlaneSpace;
            var center = quizWall.transform.TransformPoint(new Vector3(center2D.x, 0f, center2D.y));
            var inward = Vector3.ProjectOnPlane(m_Core.Head.position - center, Vector3.up);
            if (inward.sqrMagnitude < 1e-4f)
                inward = Vector3.ProjectOnPlane(quizWall.transform.up, Vector3.up);
            inward.Normalize();

            ARPlane opposite = null;
            var farthest = 2f;
            foreach (var plane in m_Planes.trackables)
            {
                if (plane == null || plane == quizWall || plane.subsumedBy != null ||
                    plane.trackingState != TrackingState.Tracking || plane.alignment != PlaneAlignment.Vertical ||
                    Mathf.Abs(Vector3.Dot(plane.transform.up, inward)) < 0.7f)
                    continue;
                var planeCenter2D = plane.centerInPlaneSpace;
                var planeCenter = plane.transform.TransformPoint(new Vector3(planeCenter2D.x, 0f, planeCenter2D.y));
                var distance = Vector3.Dot(planeCenter - center, inward);
                if (distance > farthest)
                {
                    farthest = distance;
                    opposite = plane;
                }
            }

            var spawn = center + inward * 6f;
            if (opposite != null)
            {
                var oppositeCenter2D = opposite.centerInPlaneSpace;
                var oppositeCenter = opposite.transform.TransformPoint(new Vector3(oppositeCenter2D.x, 0f, oppositeCenter2D.y));
                spawn = oppositeCenter - inward * 0.5f;
            }
            spawn.y = m_Core.Head.position.y - 1.55f;
            return spawn;
        }

        void OnQuizComplete()
        {
            m_Portal?.UnlockReturnPortal();
            UpdateHint();
        }

        void OnCaught()
        {
            if (m_Dead)
                return;
            m_Dead = true;
            m_Quiz?.Stop();
            m_Angels?.SetInside(false);
            WorldUi.PlaceInFront(m_DeathPanel, m_Core.Head, 1.2f, 4f);
            m_DeathPanel.gameObject.SetActive(true);
            Debug.Log("[Ascii] You were caught by the weeping angel");
            UpdateHint();
        }

        // Inside: drop the passthrough screen and mono eyes so Cardboard stereo and the hand
        // skeleton return. Outside: restore whatever passthrough was on before the crossing.
        void SetInsideWorld(bool inside)
        {
            if (m_Core == null || inside == m_InsideWorld)
                return;
            m_InsideWorld = inside;
            m_People?.SetActive(inside);
            if (inside)
            {
                m_HadPassthrough = m_Core.Passthrough.IsOn;
                if (m_HadPassthrough)
                    m_Core.Passthrough.Choose(false);
                VirtualEnvironment.SetAllHidden(true);
                Debug.Log("[Ascii] Passthrough off inside the gate (stereo + hand model)");
                return;
            }
            if (m_HadPassthrough)
            {
                m_Core.Passthrough.Choose(true);
                m_HadPassthrough = false;
                Debug.Log("[Ascii] Passthrough restored outside the gate");
            }
            VirtualEnvironment.SetAllHidden(m_Core.Passthrough.IsOn);
        }

        // While the gate is up the camera keeps drawing even with the passthrough screen off, for
        // the doorway back and for people inside.
        void SetCapture(bool on)
        {
            if (m_Core == null || on == m_Capturing)
                return;
            m_Capturing = on;
            if (on)
                m_Core.Passthrough.RetainCapture();
            else
                m_Core.Passthrough.ReleaseCapture();
            if (!on)
                Shader.SetGlobalFloat(k_CameraOnId, 0f);
        }

        void Update()
        {
            if (!m_Capturing)
                return;
            var image = m_Core.Passthrough.CameraImage;
            Shader.SetGlobalFloat(k_CameraOnId, image != null ? 1f : 0f);
            if (image == null)
                return;
            Shader.SetGlobalTexture(k_CameraImageId, image);
            Shader.SetGlobalVector(k_CameraFrustumId, m_Core.Passthrough.ImageFrustum);
        }

        void Move()
        {
            if (m_Portal == null || m_InsideWorld || m_Dead)
                return;
            var portal = m_Portal;
            portal.Crossed -= OnCrossed;
            m_Portal = null;
            AsciiTerminal.Active = false;
            m_Core.Boundary.ShowMarks = true;
            StopCrossing();
            SetInsideWorld(false);
            SetCapture(false);
            m_Surfaces?.SetLive(false);
            m_Meshes?.SetActive(false);
            m_Angels?.SetInside(false);
            m_Audio.PlayOneShot(m_Whoosh, 0.3f);
            Debug.Log("[Ascii] Closing the gate to move it");
            portal.Close(() =>
            {
                if (m_Core == null)
                    return;
                m_Core.Placer.RemoveAll();
                m_Core.Placer.ObjectPrefab = m_PortalPrefab;
                Debug.Log("[Ascii] Gate taken down to move it");
                UpdateHint();
            });
            UpdateHint();
        }

        void UpdateHint()
        {
            if (m_Hint == null)
                return;
            if (m_Portal == null)
                m_Hint.text = "Look at the floor and pinch to raise the glyph gate.\nNo highlight? Look down at the floor for a moment.";
            else if (!m_Portal.IsInside)
                m_Hint.text = "Passthrough room outside. Green ASCII through the gate.\nWalk through to step inside. Unplug first if it's on a cable.";
            else if (m_Dead)
                m_Hint.text = "The angel caught you.";
            else if (m_InsideWorld && m_Quiz?.QuizWall == null)
                m_Hint.text = "Scanning the room for a quiz wall. Look around the room.";
            else if (m_Portal.ReturnUnlocked)
                m_Hint.text = "All answers correct. The return portal is open.";
            else
                m_Hint.text = "Answer every question before time runs out. Look at the angel to freeze it.";
        }

        void BuildPanel()
        {
            const float distance = 1.5f;
            m_Panel = WorldUi.Panel("Ascii Panel", transform, new Vector2(440f, 192f), distance);
            m_Hint = M3.Text("Hint", m_Panel, "", M3Theme.Type.BodyLarge, new Vector2(0f, 36f), new Vector2(392f, 72f));
            m_MoveButton = M3.Button("Move portal", m_Panel, "Move gate", M3Button.Variant.Tonal,
                new Vector2(0f, -44f), Move, "open_with");
            WorldUi.PlaceInFront(m_Panel, m_Core.Head, distance, 8f, -25f);
        }

        void BuildDeathPanel()
        {
            m_DeathPanel = WorldUi.Panel("Ascii Death Screen", transform, new Vector2(520f, 260f), 1.2f,
                M3Theme.SurfaceContainer);
            M3.Text("Caught", m_DeathPanel, "The angel caught you", M3Theme.Type.HeadlineSmall,
                new Vector2(0f, 54f), new Vector2(460f, 56f));
            M3.Text("Retry", m_DeathPanel, "The portal is lost", M3Theme.Type.BodyLarge,
                new Vector2(0f, 6f), new Vector2(440f, 36f));
            M3.Button("Return to lobby", m_DeathPanel, "Return to lobby", M3Button.Variant.Filled,
                new Vector2(0f, -66f), () => m_Core.Demos.ExitToLobby(), width: 240f);
            m_DeathPanel.gameObject.SetActive(false);
        }

        static AudioClip Whoosh()
        {
            const int rate = 44100;
            const float seconds = 0.55f;
            var samples = new float[(int)(rate * seconds)];
            var random = new System.Random(7);
            var low = 0f;
            for (var i = 0; i < samples.Length; i++)
            {
                var t = i / (float)samples.Length;
                var noise = (float)(random.NextDouble() * 2.0 - 1.0);
                var cutoff = Mathf.Lerp(0.03f, 0.3f, Mathf.Sin(t * Mathf.PI));
                low += (noise - low) * cutoff;
                var buzz = Mathf.Sign(Mathf.Sin(t * 40f * Mathf.PI)) * 0.08f;
                samples[i] = (low + buzz) * Mathf.Sin(t * Mathf.PI) * 1.5f;
            }
            var clip = AudioClip.Create("AsciiWhoosh", samples.Length, 1, rate, false);
            clip.SetData(samples, 0);
            return clip;
        }
    }
}
