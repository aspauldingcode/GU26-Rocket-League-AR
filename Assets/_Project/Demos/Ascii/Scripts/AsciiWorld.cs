using System;
using System.Collections;
using UnityEngine;

namespace CardboardXR.Demos.Ascii
{
    // Glyph gate on the floor, a true portal. The ASCII room (the live SLAM planes and meshes,
    // AsciiSurfaces and AsciiMeshes) is drawn the whole time the gate is up; outside, each eye only
    // sees it through the opening (_AsciiPortalClip with the doorway set here), so walking through
    // just stops clipping it — nothing loads or swaps. Inside, passthrough is off and the opening
    // shows the camera instead. Crossing uses the portal World's local space (+z through the door),
    // same as PortalWorld. Logs [Ascii].
    public class AsciiWorld : MonoBehaviour
    {
        public const float DoorWidth = 1.0f;
        public const float DoorHeight = 2.1f;
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
        static readonly int k_PortalClip = Shader.PropertyToID("_AsciiPortalClip");
        static readonly int k_DoorCenter = Shader.PropertyToID("_AsciiDoorCenter");
        static readonly int k_DoorRight = Shader.PropertyToID("_AsciiDoorRight");
        static readonly int k_DoorUp = Shader.PropertyToID("_AsciiDoorUp");
        static readonly int k_DoorNormal = Shader.PropertyToID("_AsciiDoorNormal");

        [SerializeField] Material m_Glow;
        [SerializeField] Material m_Mask;
        [SerializeField] Material m_Ascii;

        Transform m_World;
        Transform m_Frame;
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
            m_Frame.localScale = new Vector3(1f, 0.001f, 1f);
            BuildWindow();
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

        // After walking in: the frame stays where it was placed and sinks shut so the game can start.
        // The live camera window stays in the opening until the door finishes closing — the real room
        // is cut off by the seal, not by killing the feed the moment you cross.
        public void SealDoor(Action sealedDoor = null)
        {
            ReturnUnlocked = false;
            if (m_Frame == null)
            {
                if (m_InsideMask != null)
                    m_InsideMask.SetActive(false);
                sealedDoor?.Invoke();
                return;
            }
            m_Frame.gameObject.SetActive(true);
            // Magenta window: terminal puts the camera image here while the opening shrinks.
            if (m_InsideMask != null)
                m_InsideMask.SetActive(true);
            if (m_Animating != null)
                StopCoroutine(m_Animating);
            var from = Mathf.Clamp01(m_Frame.localScale.y);
            m_Animating = StartCoroutine(Animate(k_SinkSeconds, t => from * (1f - t * t * t), () =>
            {
                if (m_Frame != null)
                    m_Frame.gameObject.SetActive(false);
                if (m_InsideMask != null)
                    m_InsideMask.SetActive(false);
                sealedDoor?.Invoke();
            }));
        }

        // Questions done: rise the same frame back open at the original placement so walking out wins.
        public void UnlockReturnPortal()
        {
            ReturnUnlocked = true;
            if (!IsInside || m_Frame == null)
                return;
            m_Frame.gameObject.SetActive(true);
            m_Frame.localScale = new Vector3(1f, 0.001f, 1f);
            FitWindow(0f);
            if (m_InsideMask != null)
                m_InsideMask.SetActive(true);
            if (m_Animating != null)
                StopCoroutine(m_Animating);
            m_Animating = StartCoroutine(Animate(k_RiseSeconds, BackOut, null));
            Debug.Log("[Ascii] Return portal rising open");
        }

        // Rises out of or sinks into the floor. The opening grows and shrinks with it: outside, the
        // portal view is clipped to OpenHeight; inside, the camera window is fitted to it.
        IEnumerator Animate(float seconds, Func<float, float> height, Action done)
        {
            for (var t = 0f; ; t += Time.deltaTime)
            {
                var progress = Mathf.Clamp01(t / seconds);
                m_Frame.localScale = new Vector3(1f, Mathf.Max(0.001f, height(progress)), 1f);
                if (IsInside)
                    FitWindow(OpenHeight);
                if (progress >= 1f)
                    break;
                yield return null;
            }
            m_Animating = null;
            done?.Invoke();
        }

        // How far up the opening is open, in meters: the frame's rise, without its overshoot.
        float OpenHeight => m_Frame != null && m_Frame.gameObject.activeInHierarchy
            ? Mathf.Clamp01(m_Frame.localScale.y) * DoorHeight
            : 0f;

        // The doorway's camera window, open from the sill up to height.
        void FitWindow(float height)
        {
            if (m_InsideMask == null)
                return;
            var window = m_InsideMask.transform;
            window.localPosition = new Vector3(0f, height / 2f, 0f);
            window.localScale = new Vector3(DoorWidth, Mathf.Max(0.001f, height), 1f);
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
        }

        // After everything that moves the head or the frame this frame, so the portal the eyes
        // render is the door as it stands now.
        void LateUpdate()
        {
            if (m_World == null)
                return;
            Shader.SetGlobalFloat(k_PortalClip, IsInside ? 0f : 1f);
            var right = m_World.right;
            var up = m_World.up;
            Shader.SetGlobalVector(k_DoorCenter, m_World.position);
            Shader.SetGlobalVector(k_DoorRight, new Vector4(right.x, right.y, right.z, DoorWidth / 2f));
            Shader.SetGlobalVector(k_DoorUp, new Vector4(up.x, up.y, up.z, OpenHeight));
            Shader.SetGlobalVector(k_DoorNormal, m_World.forward);
        }

        void SetInside(bool inside, bool announce)
        {
            IsInside = inside;
            // The portal flips in the same frame as the crossing, before the eyes render.
            Shader.SetGlobalFloat(k_PortalClip, inside ? 0f : 1f);
            // Keep the frame up on enter so SealDoor can sink it; hide only after it seals or when
            // already locked and not unlocking. UnlockReturnPortal rises it again in place.
            if (!inside)
            {
                m_Frame.gameObject.SetActive(true);
                if (announce && m_Frame.localScale.y < 0.5f)
                    m_Frame.localScale = Vector3.one;
                m_InsideMask.SetActive(false);
            }
            else
            {
                // Live camera through the doorway until SealDoor finishes (or until unlock for escape).
                m_Frame.gameObject.SetActive(true);
                FitWindow(OpenHeight);
                m_InsideMask.SetActive(true);
            }
            if (!announce)
                return;
            Debug.Log(inside ? "[Ascii] Stepped through into the ASCII dimension" : "[Ascii] Stepped back out into the room");
            Crossed?.Invoke(inside);
        }

        // Locks the way back again for another round. Only from outside, where the frame still shows.
        public void Relock()
        {
            if (IsInside)
                return;
            ReturnUnlocked = false;
            if (m_InsideMask != null)
                m_InsideMask.SetActive(false);
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
            // No gate, no doorway: nothing of the ASCII room shows until the next one rises.
            Shader.SetGlobalFloat(k_PortalClip, 1f);
            Shader.SetGlobalVector(k_DoorUp, Vector4.zero);
            if (m_FrameMaterial != null)
                Destroy(m_FrameMaterial);
            if (m_WindowMaterial != null)
                Destroy(m_WindowMaterial);
        }

        void Casing(string name, Material material, Vector3 position, Vector3 size, Color color) =>
            Primitive(PrimitiveType.Cube, name, m_Frame, position, size, Quaternion.identity, material != null ? material : m_Glow, color);

        void BuildWindow()
        {
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
