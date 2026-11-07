using System;
using System.Collections;
using UnityEngine;

namespace CardboardXR.Demos.Ascii
{
    // A glyph gate on the floor. Outside, a depth-mask box keeps the doorway as the only open
    // view; inside, a mask in the frame shows the room through the gate behind them. Real AR
    // planes are remapped by AsciiSurfaces while the gate is up. Crossing uses AsciiCrossing.
    // Logs [Ascii].
    public class AsciiWorld : MonoBehaviour
    {
        public const float DoorWidth = 1.0f;
        public const float DoorHeight = 2.1f;
        const float k_BoxHalfWidth = 24f;
        const float k_BoxHeight = 20f;
        const float k_BoxDepth = 29f;
        const float k_RiseSeconds = 1.5f;
        const float k_SinkSeconds = 1.2f;
        static readonly int k_BaseColor = Shader.PropertyToID("_BaseColor");

        [SerializeField] Material m_Glow;
        [SerializeField] Material m_Mask;

        Transform m_World;
        Transform m_Frame;
        GameObject m_OutsideMask;
        GameObject m_InsideMask;
        Vector3 m_LastHeadWorld;
        bool m_HasLastHead;
        MaterialPropertyBlock m_Block;
        Coroutine m_Animating;

        public bool IsInside { get; private set; }
        public event Action<bool> Crossed;

        void Start()
        {
            m_Block = new MaterialPropertyBlock();
            BuildFrame();
            m_World = new GameObject("World").transform;
            m_World.SetParent(transform, false);
            m_World.localRotation = Quaternion.Euler(0f, 180f, 0f);
            BuildMasks();
            SetInside(false, announce: false);
            m_Animating = StartCoroutine(Animate(k_RiseSeconds, BackOut, null));
        }

        public void Close(Action closed)
        {
            if (m_Animating != null)
                StopCoroutine(m_Animating);
            var from = m_Frame != null ? Mathf.Clamp01(m_Frame.localScale.y) : 1f;
            m_Animating = StartCoroutine(Animate(k_SinkSeconds, t => from * (1f - t * t * t), closed));
        }

        IEnumerator Animate(float seconds, Func<float, float> height, Action done)
        {
            var cover = Quad("Door Cover", m_World, Vector3.zero, Vector2.one, Quaternion.identity, m_Mask);
            for (var t = 0f; ; t += Time.deltaTime)
            {
                var progress = Mathf.Clamp01(t / seconds);
                var h = height(progress);
                m_Frame.localScale = new Vector3(1f, Mathf.Max(0.001f, h), 1f);
                var open = Mathf.Clamp01(h) * DoorHeight;
                var covered = DoorHeight - open;
                cover.localPosition = new Vector3(0f, open + covered / 2f, 0f);
                cover.localScale = new Vector3(DoorWidth, Mathf.Max(0.001f, covered), 1f);
                if (progress >= 1f)
                    break;
                yield return null;
            }
            if (height(1f) >= 1f)
                Destroy(cover.gameObject);
            m_Animating = null;
            done?.Invoke();
        }

        static float BackOut(float t)
        {
            const float overshoot = 1.35f;
            var u = t - 1f;
            return 1f + u * u * ((overshoot + 1f) * u + overshoot);
        }

        void Update()
        {
            var core = CoreServices.Instance;
            if (core == null || m_World == null)
                return;
            var headWorld = core.Head.position;
            if (m_HasLastHead)
            {
                var before = ToGateSpace(m_LastHeadWorld);
                var head = ToGateSpace(headWorld);
                if (Mathf.Abs(head.z) >= AsciiCrossing.PlaneClearance)
                {
                    var crossing = AsciiCrossing.Crossing(before, head, DoorWidth / 2f, DoorHeight);
                    if (crossing > 0 && !IsInside)
                        SetInside(true, announce: true);
                    else if (crossing < 0 && IsInside)
                        SetInside(false, announce: true);
                    m_LastHeadWorld = headWorld;
                }
            }
            else
            {
                m_LastHeadWorld = headWorld;
                m_HasLastHead = true;
            }
        }

        Vector3 ToGateSpace(Vector3 worldPosition)
        {
            var point = m_Frame.InverseTransformPoint(worldPosition);
            point.z = -point.z;
            return point;
        }

        void SetInside(bool inside, bool announce)
        {
            IsInside = inside;
            m_OutsideMask.SetActive(!inside);
            m_InsideMask.SetActive(inside);
            if (!announce)
                return;
            Debug.Log(inside ? "[Ascii] Stepped through into the ASCII dimension" : "[Ascii] Stepped back out into the room");
            Crossed?.Invoke(inside);
        }

        // The door frame model, baked into the prefab under "Frame" by AsciiSetup and fitted so its
        // opening is the doorway. It rises and sinks with the gate (Animate scales Frame).
        void BuildFrame()
        {
            m_Frame = transform.Find("Frame");
            if (m_Frame != null)
                return;
            m_Frame = new GameObject("Frame").transform;
            m_Frame.SetParent(transform, false);
        }

        // Depth masks only — real room meshes come from AsciiSurfaces while the gate is up.
        void BuildMasks()
        {
            m_OutsideMask = BuildOutsideMask();
            m_InsideMask = Quad("Inside Mask", m_World, new Vector3(0f, DoorHeight / 2f, 0f),
                new Vector2(DoorWidth, DoorHeight), Quaternion.identity, m_Mask).gameObject;
        }

        GameObject BuildOutsideMask()
        {
            var mask = new GameObject("Outside Mask").transform;
            mask.SetParent(m_World, false);
            const float bottom = -0.5f;
            var middleY = bottom + k_BoxHeight / 2f;
            var halfDoor = DoorWidth / 2f;
            Quad("Far", mask, new Vector3(0f, middleY, k_BoxDepth), new Vector2(2f * k_BoxHalfWidth, k_BoxHeight), Quaternion.identity, m_Mask);
            Quad("Left", mask, new Vector3(-k_BoxHalfWidth, middleY, k_BoxDepth / 2f), new Vector2(k_BoxDepth, k_BoxHeight),
                Quaternion.Euler(0f, 90f, 0f), m_Mask);
            Quad("Right", mask, new Vector3(k_BoxHalfWidth, middleY, k_BoxDepth / 2f), new Vector2(k_BoxDepth, k_BoxHeight),
                Quaternion.Euler(0f, 90f, 0f), m_Mask);
            Quad("Top", mask, new Vector3(0f, bottom + k_BoxHeight, k_BoxDepth / 2f), new Vector2(2f * k_BoxHalfWidth, k_BoxDepth),
                Quaternion.Euler(90f, 0f, 0f), m_Mask);
            Quad("Bottom", mask, new Vector3(0f, bottom, k_BoxDepth / 2f), new Vector2(2f * k_BoxHalfWidth, k_BoxDepth),
                Quaternion.Euler(90f, 0f, 0f), m_Mask);
            var sideWidth = k_BoxHalfWidth - halfDoor;
            Quad("Front Left", mask, new Vector3(-halfDoor - sideWidth / 2f, middleY, 0f), new Vector2(sideWidth, k_BoxHeight),
                Quaternion.identity, m_Mask);
            Quad("Front Right", mask, new Vector3(halfDoor + sideWidth / 2f, middleY, 0f), new Vector2(sideWidth, k_BoxHeight),
                Quaternion.identity, m_Mask);
            var aboveHeight = bottom + k_BoxHeight - DoorHeight;
            Quad("Above Door", mask, new Vector3(0f, DoorHeight + aboveHeight / 2f, 0f), new Vector2(DoorWidth, aboveHeight),
                Quaternion.identity, m_Mask);
            Quad("Below Door", mask, new Vector3(0f, bottom / 2f, 0f), new Vector2(DoorWidth, -bottom), Quaternion.identity, m_Mask);
            return mask.gameObject;
        }

        Transform Box(string name, Transform parent, Vector3 position, Vector3 size, Color color) =>
            Primitive(PrimitiveType.Cube, name, parent, position, size, Quaternion.identity, m_Glow, color);

        Transform Quad(string name, Transform parent, Vector3 position, Vector2 size, Quaternion rotation, Material material) =>
            Primitive(PrimitiveType.Quad, name, parent, position, new Vector3(size.x, size.y, 1f), rotation, material, null);

        Transform Primitive(PrimitiveType type, string name, Transform parent, Vector3 position, Vector3 scale,
            Quaternion rotation, Material material, Color? color)
        {
            var primitive = GameObject.CreatePrimitive(type);
            primitive.name = name;
            Destroy(primitive.GetComponent<Collider>());
            primitive.transform.SetParent(parent, false);
            primitive.transform.SetLocalPositionAndRotation(position, rotation);
            primitive.transform.localScale = scale;
            var renderer = primitive.GetComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            if (color.HasValue)
            {
                m_Block.SetColor(k_BaseColor, color.Value);
                renderer.SetPropertyBlock(m_Block);
            }
            return primitive.transform;
        }
    }
}
