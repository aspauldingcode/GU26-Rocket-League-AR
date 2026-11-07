using System;
using System.Collections;
using UnityEngine;

namespace CardboardXR.Demos.Ascii
{
    // A glyph gate on the floor — bevelled phosphor posts, scanline brackets and a lintel of floating
    // ASCII blocks — not the night-sky doorway. Outside, a depth-mask box shows the ASCII peek only
    // through the opening; inside, the back half wraps the player and a mask shows the room through
    // the gate behind them. Crossing uses AsciiCrossing. Logs [Ascii].
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
        [SerializeField] Material m_Ascii;
        [SerializeField] Material m_Mask;

        Transform m_World;
        Transform m_Frame;
        GameObject m_BackHalf;
        GameObject m_OutsideMask;
        GameObject m_InsideMask;
        Vector3 m_LastHead;
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
            BuildPeekWorld();
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
            var head = m_Frame.InverseTransformPoint(core.Head.position);
            head.z = -head.z;
            if (m_HasLastHead)
            {
                if (Mathf.Abs(head.z) >= AsciiCrossing.PlaneClearance)
                {
                    var crossing = AsciiCrossing.Crossing(m_LastHead, head, DoorWidth / 2f, DoorHeight);
                    if (crossing > 0 && !IsInside)
                        SetInside(true, announce: true);
                    else if (crossing < 0 && IsInside)
                        SetInside(false, announce: true);
                    m_LastHead = head;
                }
            }
            else
            {
                m_LastHead = head;
                m_HasLastHead = true;
            }
        }

        void SetInside(bool inside, bool announce)
        {
            IsInside = inside;
            m_BackHalf.SetActive(inside);
            m_OutsideMask.SetActive(!inside);
            m_InsideMask.SetActive(inside);
            if (!announce)
                return;
            Debug.Log(inside ? "[Ascii] Stepped through into the ASCII dimension" : "[Ascii] Stepped back out into the room");
            Crossed?.Invoke(inside);
        }

        // Bevelled posts, corner brackets, scanline ribs and a lintel of glyph blocks — a terminal
        // gate, not Portal's crystal doorway.
        void BuildFrame()
        {
            m_Frame = new GameObject("Frame").transform;
            m_Frame.SetParent(transform, false);
            var phosphor = new Color(0.2f, 1f, 0.45f);
            var dim = phosphor * 0.55f;
            const float post = 0.07f;
            const float depth = 0.11f;
            var half = DoorWidth / 2f + post / 2f;

            // Thick outer posts with an inner bevel strip.
            // Box("Left Post", m_Frame, new Vector3(-half, DoorHeight / 2f, 0f), new Vector3(post, DoorHeight, depth), phosphor);
            // Box("Right Post", m_Frame, new Vector3(half, DoorHeight / 2f, 0f), new Vector3(post, DoorHeight, depth), phosphor);
            // Box("Left Bevel", m_Frame, new Vector3(-half + post * 0.55f, DoorHeight / 2f, depth * 0.35f),
            //     new Vector3(post * 0.35f, DoorHeight * 0.96f, depth * 0.4f), dim);
            // Box("Right Bevel", m_Frame, new Vector3(half - post * 0.55f, DoorHeight / 2f, depth * 0.35f),
            //     new Vector3(post * 0.35f, DoorHeight * 0.96f, depth * 0.4f), dim);

            // Corner brackets at the four opening corners.
            // const float bracket = 0.14f;
            // foreach (var side in new[] { -1f, 1f })
            // {
            //     Box($"Bracket Low {side}", m_Frame, new Vector3(side * (DoorWidth / 2f - bracket / 2f), bracket / 2f, depth * 0.55f),
            //         new Vector3(bracket, 0.025f, 0.025f), phosphor);
            //     Box($"Bracket Low V {side}", m_Frame, new Vector3(side * (DoorWidth / 2f - 0.012f), bracket / 2f, depth * 0.55f),
            //         new Vector3(0.025f, bracket, 0.025f), phosphor);
            //     Box($"Bracket High {side}", m_Frame, new Vector3(side * (DoorWidth / 2f - bracket / 2f), DoorHeight - bracket / 2f, depth * 0.55f),
            //         new Vector3(bracket, 0.025f, 0.025f), phosphor);
            //     Box($"Bracket High V {side}", m_Frame, new Vector3(side * (DoorWidth / 2f - 0.012f), DoorHeight - bracket / 2f, depth * 0.55f),
            //         new Vector3(0.025f, bracket, 0.025f), phosphor);
            // }

            // // Horizontal scanline ribs on each post.
            // for (var i = 0; i < 7; i++)
            // {
            //     var y = 0.22f + i * (DoorHeight - 0.4f) / 6f;
            //     Box($"Scan L {i}", m_Frame, new Vector3(-half, y, depth * 0.55f), new Vector3(post * 1.2f, 0.012f, 0.02f), dim);
            //     Box($"Scan R {i}", m_Frame, new Vector3(half, y, depth * 0.55f), new Vector3(post * 1.2f, 0.012f, 0.02f), dim);
            // }

            // // Lintel: a row of glyph cubes spelling a dense ASCII band.
            // Box("Lintel Bar", m_Frame, new Vector3(0f, DoorHeight + post / 2f, 0f),
            //     new Vector3(DoorWidth + 2f * post, post, depth), phosphor);
            // var glyphs = AsciiGlyph.Ramp;
            // var count = glyphs.Length;
            // for (var i = 0; i < 11; i++)
            // {
            //     var t = (i + 0.5f) / 11f;
            //     var x = Mathf.Lerp(-DoorWidth / 2f + 0.06f, DoorWidth / 2f - 0.06f, t);
            //     var ch = glyphs[AsciiGlyph.BrightnessToIndex(t, count)];
            //     var cube = Box($"Glyph {ch}", m_Frame, new Vector3(x, DoorHeight + post + 0.05f, depth * 0.2f),
            //         new Vector3(0.07f, 0.07f, 0.07f), Color.Lerp(dim, phosphor, t));
            //     cube.localRotation = Quaternion.Euler(0f, i * 17f, 8f);
            // }

            // Box("Sill", m_Frame, new Vector3(0f, 0.008f, 0f), new Vector3(DoorWidth, 0.016f, depth * 0.8f), dim);
        }

        void BuildPeekWorld()
        {
            var front = new GameObject("Front Half").transform;
            front.SetParent(m_World, false);
            m_BackHalf = new GameObject("Back Half");
            m_BackHalf.transform.SetParent(m_World, false);
            var back = m_BackHalf.transform;

            // ASCII-shaded floor and wall slabs as the peek / wrap of the dimension.
            Floor("Floor Front", front, 0.4f, k_BoxDepth - 1f);
            Floor("Floor Back", back, -(k_BoxDepth - 1f), -0.4f);
            Wall("Wall L Front", front, -4f, 4f, 8f);
            Wall("Wall R Front", front, 4f, 4f, 8f);
            Wall("Wall L Back", back, -4f, -8f, -0.5f);
            Wall("Wall R Back", back, 4f, -8f, -0.5f);

            m_OutsideMask = BuildOutsideMask();
            m_InsideMask = Quad("Inside Mask", m_World, new Vector3(0f, DoorHeight / 2f, 0f),
                new Vector2(DoorWidth, DoorHeight), Quaternion.identity, m_Mask).gameObject;
        }

        void Floor(string name, Transform parent, float fromZ, float toZ)
        {
            var q = Quad(name, parent, new Vector3(0f, 0.01f, (fromZ + toZ) / 2f),
                new Vector2(12f, toZ - fromZ), Quaternion.Euler(90f, 0f, 0f), m_Ascii);
            q.localScale = new Vector3(12f, Mathf.Abs(toZ - fromZ), 1f);
        }

        void Wall(string name, Transform parent, float x, float fromZ, float toZ)
        {
            Quad(name, parent, new Vector3(x, DoorHeight / 2f, (fromZ + toZ) / 2f),
                new Vector2(Mathf.Abs(toZ - fromZ), DoorHeight), Quaternion.Euler(0f, 90f, 0f), m_Ascii);
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
