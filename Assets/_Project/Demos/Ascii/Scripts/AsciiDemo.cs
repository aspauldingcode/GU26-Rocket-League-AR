using CardboardXR.UI;
using TMPro;
using UnityEngine;
using UnityEngine.XR.ARFoundation;

namespace CardboardXR.Demos.Ascii
{
    // Place a glyph gate on the floor, walk through into an ASCII remapping of your room's surfaces,
    // walk back to return to passthrough. Logs [Ascii].
    public class AsciiDemo : Demo
    {
        [SerializeField] GameObject m_PortalPrefab;
        [SerializeField] Material m_AsciiMaterial;

        CoreServices m_Core;
        AsciiWorld m_Portal;
        AsciiSurfaces m_Surfaces;
        RectTransform m_Panel;
        TMP_Text m_Hint;
        AudioSource m_Audio;
        AudioClip m_Whoosh;

        public override void OnDemoEnter(CoreServices core)
        {
            m_Core = core;
            core.Placer.ObjectPrefab = m_PortalPrefab;
            core.Placer.Placed += OnPlaced;

            m_Surfaces = core.Boundary.gameObject.AddComponent<AsciiSurfaces>();
            var planes = core.Boundary.GetComponent<ARPlaneManager>()
                ?? core.Boundary.GetComponentInChildren<ARPlaneManager>(true);
            var camera = Object.FindAnyObjectByType<ARCameraManager>();
            m_Surfaces.Configure(m_AsciiMaterial, planes, camera);
            m_Surfaces.SetActive(false);

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
            if (m_Surfaces != null)
            {
                m_Surfaces.SetActive(false);
                Destroy(m_Surfaces);
                m_Surfaces = null;
            }
            Debug.Log("[Ascii] Leaving");
        }

        void OnPlaced(GameObject placed)
        {
            if (!placed.TryGetComponent<AsciiWorld>(out var portal))
                return;
            placed.transform.localPosition = Vector3.zero;
            m_Portal = portal;
            m_Portal.Crossed += OnCrossed;
            m_Core.Placer.ObjectPrefab = null;
            var distance = Vector3.Distance(m_Core.Head.position, placed.transform.position);
            Debug.Log($"[Ascii] Gate opened {distance:F1} m away");
            m_Audio.PlayOneShot(m_Whoosh, 0.4f);
            UpdateHint();
        }

        void OnCrossed(bool inside)
        {
            m_Audio.PlayOneShot(m_Whoosh, 0.7f);
            m_Surfaces?.SetActive(inside);
            UpdateHint();
        }

        void Move()
        {
            if (m_Portal == null)
                return;
            var portal = m_Portal;
            portal.Crossed -= OnCrossed;
            m_Portal = null;
            m_Surfaces?.SetActive(false);
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
                m_Hint.text = "Walk through the gate into the ASCII dimension.\nUnplug the phone first if it's on a cable.";
            else
                m_Hint.text = "Your room's surfaces are ASCII. Walk back through to return.";
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
                // A touch of square-ish buzz for a terminal feel.
                var buzz = Mathf.Sign(Mathf.Sin(t * 40f * Mathf.PI)) * 0.08f;
                samples[i] = (low + buzz) * Mathf.Sin(t * Mathf.PI) * 1.5f;
            }
            var clip = AudioClip.Create("AsciiWhoosh", samples.Length, 1, rate, false);
            clip.SetData(samples, 0);
            return clip;
        }
    }
}
