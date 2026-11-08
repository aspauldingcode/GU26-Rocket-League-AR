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
    // screen-space terminal. Raised surfaces (tables, seats) are yellow. Logs [Ascii].
    public class AsciiDemo : Demo
    {
        [SerializeField] GameObject m_PortalPrefab;
        [SerializeField] Material m_AsciiMaterial;
        [SerializeField] GameObject m_AngelPrefab;

        CoreServices m_Core;
        AsciiWorld m_Portal;
        AsciiSurfaces m_Surfaces;
        AsciiMeshes m_Meshes;
        AsciiAngels m_Angels;
        RectTransform m_Panel;
        TMP_Text m_Hint;
        AudioSource m_Audio;
        AudioClip m_Whoosh;
        ARPlaneManager m_Planes;
        PlaneDetectionMode? m_PreviousDetection;
        bool m_HadPassthrough;
        bool m_InsideWorld;

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
            m_Angels.Configure(m_AngelPrefab, core.Head);

            m_Audio = gameObject.AddComponent<AudioSource>();
            m_Audio.playOnAwake = false;
            m_Whoosh = Whoosh();
            BuildPanel();
            UpdateHint();
            Debug.Log("[Ascii] Ready: look at the floor and pinch to open the glyph gate");
        }

        public override void OnDemoExit(CoreServices core)
        {
            core.Placer.Placed -= OnPlaced;
            if (m_Portal != null)
                m_Portal.Crossed -= OnCrossed;
            SetInsideWorld(false);
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
                m_Angels.SetInside(false);
                Destroy(m_Angels);
                m_Angels = null;
            }
            core.Boundary.HoldDetection = false;
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
            AsciiTerminal.Active = true;
            m_Surfaces?.SetLive(true);
            var distance = Vector3.Distance(m_Core.Head.position, placed.transform.position);
            Debug.Log($"[Ascii] Gate opened {distance:F1} m away");
            m_Audio.PlayOneShot(m_Whoosh, 0.4f);
            UpdateHint();
        }

        void OnCrossed(bool inside)
        {
            m_Audio.PlayOneShot(m_Whoosh, 0.7f);
            SetInsideWorld(inside);
            m_Surfaces?.SetDrawn(inside);
            m_Meshes?.SetActive(inside);
            m_Angels?.SetInside(inside);
            UpdateHint();
        }

        // Inside: drop the passthrough screen and mono eyes so Cardboard stereo and the hand
        // skeleton return. Outside: restore whatever passthrough was on before the crossing.
        void SetInsideWorld(bool inside)
        {
            if (m_Core == null || inside == m_InsideWorld)
                return;
            m_InsideWorld = inside;
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
        }

        void Move()
        {
            if (m_Portal == null)
                return;
            var portal = m_Portal;
            portal.Crossed -= OnCrossed;
            m_Portal = null;
            AsciiTerminal.Active = false;
            SetInsideWorld(false);
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
            else
                m_Hint.text = "Don't blink. The angels only move when you look away.\nWalk back through the gate to leave.";
        }

        void BuildPanel()
        {
            const float distance = 1.5f;
            m_Panel = WorldUi.Panel("Ascii Panel", transform, new Vector2(440f, 192f), distance);
            m_Hint = M3.Text("Hint", m_Panel, "", M3Theme.Type.BodyLarge, new Vector2(0f, 36f), new Vector2(392f, 72f));
            M3.Button("Move portal", m_Panel, "Move gate", M3Button.Variant.Tonal, new Vector2(0f, -44f), Move, "open_with");
            WorldUi.PlaceInFront(m_Panel, m_Core.Head, distance, 8f, -25f);
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
