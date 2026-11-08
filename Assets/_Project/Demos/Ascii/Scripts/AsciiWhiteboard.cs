using System.Collections.Generic;
using CardboardXR.Demos.Paint;
using CardboardXR.Hands;
using UnityEngine;
using UnityEngine.Rendering;

namespace CardboardXR.Demos.Ascii
{
    // Red scratch notes in the air for working out a question, borrowed from the Paint demo: pinch
    // and the stroke follows the point between thumb and index fingertip. One color, one size, no
    // tools. The ink draws in the transparent queue, after the terminal pass, so it stays plain red
    // ink over the glyphs rather than turning into them. Strokes stay in the room's space until
    // Clear, which the game calls after each answer. Logs [Ascii].
    public class AsciiWhiteboard : MonoBehaviour
    {
        const float k_Width = 0.006f;
        // Same guards as PaintDemo: a tip jumping this far in a frame is the tracker, not the hand.
        const float k_JumpDistance = 0.06f;
        const float k_MaxHandSpeed = 3f;
        static readonly Color k_Ink = new(1f, 0.16f, 0.14f);

        HandTracker m_Hands;
        Transform m_Head;
        Transform m_Root;
        Material m_Material;
        readonly List<PaintStroke> m_Strokes = new();
        PaintStroke m_Current;
        readonly OneEuroFilter m_Smoothing = new(2f, 1.5f);

        bool Tracking => m_Hands != null && m_Hands.isActiveAndEnabled && m_Hands.IsTracking;

        public void Configure(HandTracker hands, Transform head, Transform room)
        {
            m_Hands = hands;
            m_Head = head;
            m_Root = new GameObject("Ascii Notes").transform;
            m_Root.SetParent(room != null ? room : transform, false);
            var shader = Shader.Find("Universal Render Pipeline/Unlit");
            if (shader == null)
            {
                Debug.LogWarning("[Ascii] No URP Unlit shader in the build; notes are off");
                return;
            }
            m_Material = new Material(shader) { name = "Ascii Note Ink", renderQueue = (int)RenderQueue.Transparent + 100 };
            m_Material.SetColor("_BaseColor", k_Ink);
            m_Material.SetFloat("_Cull", (float)CullMode.Off);
        }

        // Starts a stroke at the fingertips: false without a tracked hand.
        public bool Begin()
        {
            if (m_Material == null || !Tracking)
                return false;
            m_Smoothing.Reset();
            StartStroke(Tip());
            return true;
        }

        public void End()
        {
            if (m_Current == null)
                return;
            var stroke = m_Current;
            m_Current = null;
            if (stroke.PointCount >= 2)
                return;
            m_Strokes.Remove(stroke);
            Destroy(stroke.gameObject);
        }

        public void Clear()
        {
            End();
            if (m_Strokes.Count > 0)
                Debug.Log($"[Ascii] Cleared {m_Strokes.Count} note strokes");
            foreach (var stroke in m_Strokes)
                if (stroke != null)
                    Destroy(stroke.gameObject);
            m_Strokes.Clear();
        }

        void Update()
        {
            if (m_Current == null)
                return;
            if (!Tracking)
            {
                End();
                return;
            }
            var tip = Tip();
            var jump = Vector3.Distance(tip, m_Current.LastPoint);
            if (jump > Mathf.Max(k_JumpDistance, k_MaxHandSpeed * Time.unscaledDeltaTime))
            {
                End();
                StartStroke(tip);
                return;
            }
            m_Current.Add(tip, TowardHead(tip));
            if (m_Current.IsFull)
                StartStroke(m_Current.LastPoint);
        }

        void StartStroke(Vector3 start)
        {
            var strokeObject = new GameObject($"Note {m_Strokes.Count + 1}", typeof(MeshFilter), typeof(MeshRenderer));
            strokeObject.transform.SetParent(m_Root, false);
            var stroke = strokeObject.AddComponent<PaintStroke>();
            stroke.Begin(BrushStyle.Ink, k_Width, k_Ink, m_Material);
            stroke.Add(start, TowardHead(start));
            m_Strokes.Add(stroke);
            m_Current = stroke;
        }

        Vector3 Tip() => m_Smoothing.Filter(m_Hands.PinchPoint, Time.unscaledTimeAsDouble);

        Vector3 TowardHead(Vector3 point)
        {
            var toward = m_Head.position - point;
            return toward.sqrMagnitude > 1e-6f ? toward.normalized : -m_Head.forward;
        }

        void OnDestroy()
        {
            if (m_Root != null)
                Destroy(m_Root.gameObject);
            if (m_Material != null)
                Destroy(m_Material);
        }
    }
}
