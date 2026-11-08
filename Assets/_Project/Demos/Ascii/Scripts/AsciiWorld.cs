using System;
using System.Collections;
using UnityEngine;

namespace CardboardXR.Demos.Ascii
{
    // Glyph gate on the floor. Outside: normal passthrough, depth-mask doorway peeks at a green
    // ASCII "other side". Inside: real AR planes remap via AsciiSurfaces, passthrough is off.
    // Crossing uses the portal World's local space (+z through the door), same as PortalWorld.
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

        public int mathProblemIndex = 0;
        public String[] mathProblems = new String[] {
            "2 + 2 = ?",
            "3 * 3 = ?",
            "5 - 2 = ?",
            "4 / 2 = ?"
        };

        public String CurrentMathProblem => mathProblems[mathProblemIndex];

        public void NextMathProblem()
        {
            mathProblemIndex = (mathProblemIndex + 1) % mathProblems.Length;
        }

        static readonly int k_BaseColor = Shader.PropertyToID("_BaseColor");

        [SerializeField] Material m_Glow;
        [SerializeField] Material m_Mask;
        [SerializeField] Material m_Ascii;

        Transform m_World;
        Transform m_Frame;
        GameObject m_Peek;
        GameObject m_OutsideMask;
        GameObject m_InsideMask;
        Vector3 m_LastHead;
        bool m_HasLastHead;
        MaterialPropertyBlock m_Block;
        Material m_FrameMaterial;
        Material m_WindowMaterial;
        Coroutine m_Animating;

        public bool IsInside { get; private set; }
        public bool ReturnUnlocked { get; private set; }
        public event Action<bool> Crossed;

        void Start()
        {
            m_Block = new MaterialPropertyBlock();
            BuildFrame();
            m_World = new GameObject("World").transform;
            m_World.SetParent(transform, false);
            m_World.localRotation = Quaternion.Euler(0f, 180f, 0f);
            BuildPeek();
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
            // World is yawed 180°, so +z runs through the door into the ASCII side — same space as
            // PortalWorld.Crossing. Do not use the rising Frame (non-uniform scale breaks this).
            var head = m_World.InverseTransformPoint(core.Head.position);
            if (m_HasLastHead)
            {
                var crossing = AsciiCrossing.Crossing(m_LastHead, head, DoorWidth / 2f, DoorHeight);
                if (crossing > 0 && !IsInside)
                    SetInside(true, announce: true);
                else if (crossing < 0 && IsInside && ReturnUnlocked)
                    SetInside(false, announce: true);
            }
            m_LastHead = head;
            m_HasLastHead = true;
            // The peek is a room-sized box behind the door. Walked around to the back, the player
            // stands inside it and its far walls fade to a black wall, so it only draws from the front.
            if (m_Peek != null)
                m_Peek.SetActive(!IsInside && head.z < 0f);
        }

        void SetInside(bool inside, bool announce)
        {
            IsInside = inside;
            if (m_Peek != null)
                m_Peek.SetActive(!inside);
            m_OutsideMask.SetActive(!inside);
            m_Frame.gameObject.SetActive(!inside || ReturnUnlocked);
            m_InsideMask.SetActive(inside && ReturnUnlocked);
            if (!announce)
                return;
            Debug.Log(inside ? "[Ascii] Stepped through into the ASCII dimension" : "[Ascii] Stepped back out into the room");
            Crossed?.Invoke(inside);
        }

        // Locks the way back again for another round. Only from outside, where the frame still shows.
        public void Relock()
        {
            if (!IsInside)
                ReturnUnlocked = false;
        }

        public void UnlockReturnPortal()
        {
            ReturnUnlocked = true;
            if (!IsInside)
                return;
            m_Frame.gameObject.SetActive(true);
            m_InsideMask.SetActive(true);
        }

        // A real casing: jambs, lintel, and sill. The imported frame mesh was a thin CAD profile
        // skinned with the ASCII shader, which read as a mirror up close. This stays on m_Glow.
        void BuildFrame()
        {
            m_Frame = transform.Find("Frame");
            if (m_Frame == null)
            {
                m_Frame = new GameObject("Frame").transform;
                m_Frame.SetParent(transform, false);
            }
            for (var i = m_Frame.childCount - 1; i >= 0; i--)
            {
                var child = m_Frame.GetChild(i).gameObject;
                child.SetActive(false);
                Destroy(child);
            }

            var wood = new Color(0.45f, 0.32f, 0.22f);
            var frameMat = OpaqueFrame();
            const float jamb = 0.11f;
            const float depth = 0.16f;
            var half = DoorWidth / 2f + jamb / 2f;
            Casing("Left Jamb", frameMat, new Vector3(-half, DoorHeight / 2f, 0f), new Vector3(jamb, DoorHeight, depth), wood);
            Casing("Right Jamb", frameMat, new Vector3(half, DoorHeight / 2f, 0f), new Vector3(jamb, DoorHeight, depth), wood);
            Casing("Lintel", frameMat, new Vector3(0f, DoorHeight + jamb / 2f, 0f),
                new Vector3(DoorWidth + jamb * 2f, jamb, depth), wood);
            Casing("Sill", frameMat, new Vector3(0f, 0.025f, 0f), new Vector3(DoorWidth + jamb * 2f, 0.05f, depth * 1.15f), wood * 0.85f);
            // A thinner stop set back in the opening, so the hole reads as a doorway.
            const float stop = 0.035f;
            var stopHalf = DoorWidth / 2f - stop / 2f;
            Casing("Left Stop", frameMat, new Vector3(-stopHalf, DoorHeight / 2f, depth * 0.22f),
                new Vector3(stop, DoorHeight - 0.05f, 0.04f), wood * 0.7f);
            Casing("Right Stop", frameMat, new Vector3(stopHalf, DoorHeight / 2f, depth * 0.22f),
                new Vector3(stop, DoorHeight - 0.05f, 0.04f), wood * 0.7f);
            Casing("Head Stop", frameMat, new Vector3(0f, DoorHeight - stop / 2f, depth * 0.22f),
                new Vector3(DoorWidth, stop, 0.04f), wood * 0.7f);
        }

        // The shared glow material is additive. A door frame needs a solid casing, not a glass sheet.
        Material OpaqueFrame()
        {
            if (m_Glow == null)
                return null;
            m_FrameMaterial = new Material(m_Glow) { name = "Door Frame" };
            m_FrameMaterial.SetFloat("_Surface", 0f);
            m_FrameMaterial.SetFloat("_Blend", 0f);
            m_FrameMaterial.SetFloat("_ZWrite", 1f);
            m_FrameMaterial.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.One);
            m_FrameMaterial.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.Zero);
            m_FrameMaterial.SetOverrideTag("RenderType", "Opaque");
            m_FrameMaterial.DisableKeyword("_SURFACE_TYPE_TRANSPARENT");
            m_FrameMaterial.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Geometry;
            return m_FrameMaterial;
        }

        void OnDestroy()
        {
            if (m_FrameMaterial != null)
                Destroy(m_FrameMaterial);
            if (m_WindowMaterial != null)
                Destroy(m_WindowMaterial);
        }

        void Casing(string name, Material material, Vector3 position, Vector3 size, Color color) =>
            Primitive(PrimitiveType.Cube, name, m_Frame, position, size, Quaternion.identity, material != null ? material : m_Glow, color);

        // Green ASCII slabs parented under the depth-masked World so the doorway peeks at phosphor
        // green — not the real room, and not camera-tinted glyphs. Hidden once you're inside.
        // The ASCII shader treats local +Y as the surface normal, so each slab is rotated to face in.
        void BuildPeek()
        {
            m_Peek = new GameObject("Peek");
            m_Peek.transform.SetParent(m_World, false);
            var peek = m_Peek.transform;
            const float half = 12f;
            const float wallH = 4f;
            const float depth = 14f;
            // Thin in local Y; rotate so +Y is the inward-facing normal, then stretch in local XZ.
            Slab("Floor", peek, new Vector3(0f, 0.002f, depth / 2f), new Vector3(half * 2f, 0.01f, depth),
                Quaternion.identity);
            Slab("Ceiling", peek, new Vector3(0f, wallH, depth / 2f), new Vector3(half * 2f, 0.01f, depth),
                Quaternion.Euler(180f, 0f, 0f));
            Slab("Left Wall", peek, new Vector3(-half, wallH / 2f, depth / 2f), new Vector3(wallH, 0.01f, depth),
                Quaternion.Euler(0f, 0f, -90f));
            Slab("Right Wall", peek, new Vector3(half, wallH / 2f, depth / 2f), new Vector3(wallH, 0.01f, depth),
                Quaternion.Euler(0f, 0f, 90f));
            Slab("Back Wall", peek, new Vector3(0f, wallH / 2f, depth), new Vector3(half * 2f, 0.01f, wallH),
                Quaternion.Euler(90f, 0f, 0f));
        }

        void BuildMasks()
        {
            m_OutsideMask = BuildOutsideMask();
            m_InsideMask = Quad("Inside Mask", m_World, new Vector3(0f, DoorHeight / 2f, 0f),
                new Vector2(DoorWidth, DoorHeight), Quaternion.identity, WindowMaterial()).gameObject;
        }

        // Passthrough is off inside, so the doorway can't just reveal it like PortalWorld's does.
        // It draws a key color before the room; the terminal pass puts the camera image there.
        Material WindowMaterial()
        {
            if (m_Ascii == null)
                return m_Mask;
            m_WindowMaterial = new Material(m_Ascii) { name = "Ascii Window" };
            m_WindowMaterial.SetFloat("_Window", 1f);
            m_WindowMaterial.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Geometry - 10;
            return m_WindowMaterial;
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

        Transform Slab(string name, Transform parent, Vector3 position, Vector3 size, Quaternion rotation) =>
            Primitive(PrimitiveType.Cube, name, parent, position, size, rotation, m_Ascii != null ? m_Ascii : m_Glow, null);

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
