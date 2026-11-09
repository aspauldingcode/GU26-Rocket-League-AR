using UnityEngine;
using UnityEngine.XR.ARFoundation;

namespace CardboardXR.Demos.Ascii
{
    // When the device can mesh the room (LiDAR ARKit, some ARCore builds), covers those meshes with
    // the ASCII material so chairs and other non-planar furniture show up alongside AR planes.
    // Harmless no-op when the mesh subsystem never produces geometry. Logs [Ascii].
    public class AsciiMeshes : MonoBehaviour
    {
        static readonly int k_Force = Shader.PropertyToID("_ForceFurniture");
        static readonly int k_Normal = Shader.PropertyToID("_UseMeshNormal");
        static readonly int k_Volume = Shader.PropertyToID("_Volume");
        static readonly int k_Figure = Shader.PropertyToID("_Figure");
        static readonly int k_Floor = Shader.PropertyToID("_AsciiFloorY");

        Material m_Material;
        ARMeshManager m_Meshes;
        MeshFilter m_PrefabFilter;
        readonly MaterialPropertyBlock m_Block = new();

        public ARMeshManager MeshManager => m_Meshes;

        public void Configure(Material asciiMaterial, Transform origin)
        {
            m_Material = asciiMaterial;
            if (origin == null || asciiMaterial == null)
            {
                enabled = false;
                return;
            }

            m_Meshes = origin.GetComponent<ARMeshManager>();
            if (m_Meshes == null)
                m_Meshes = origin.gameObject.AddComponent<ARMeshManager>();
            m_Meshes.density = 0.5f;
            m_Meshes.normals = true;
            if (m_Meshes.meshPrefab == null)
                m_Meshes.meshPrefab = EnsurePrefabFilter();
            // Keep the mesh subsystem running for the whole demo so walking the room keeps
            // growing geometry. Renderers stay off until the player is inside the gate.
            m_Meshes.enabled = true;
            enabled = true;
            HideMeshes();
            Debug.Log("[Ascii] Room meshing running (drawn only inside the gate)");
        }

        bool m_Show;

        public void SetActive(bool on)
        {
            m_Show = on && m_Meshes != null;
            if (m_Meshes != null)
                m_Meshes.enabled = true;
            if (!m_Show)
                HideMeshes();
        }

        void HideMeshes()
        {
            if (m_Meshes == null || m_Meshes.meshes == null)
                return;
            foreach (var filter in m_Meshes.meshes)
            {
                if (filter == null)
                    continue;
                var renderer = filter.GetComponent<MeshRenderer>();
                if (renderer == null)
                    continue;
                renderer.enabled = false;
                AsciiPortalCulling.Remove(renderer);
            }
        }

        void LateUpdate()
        {
            if (!m_Show || m_Meshes == null || m_Material == null || m_Meshes.meshes == null)
                return;
            foreach (var filter in m_Meshes.meshes)
            {
                if (filter == null)
                    continue;
                var renderer = filter.GetComponent<MeshRenderer>();
                if (renderer == null)
                    continue;
                if (renderer.sharedMaterial != m_Material)
                    renderer.sharedMaterial = m_Material;
                var bounds = renderer.bounds;
                var floorY = Shader.GetGlobalFloat(k_Floor);
                var furniture = AsciiFurniture.MeshChunkIsFurniture(bounds.size, bounds.min.y, floorY);
                m_Block.Clear();
                // A chair or table chunk is marked so its sides stay in the scan. Color stays green.
                m_Block.SetFloat(k_Force, furniture ? 1f : 0f);
                m_Block.SetFloat(k_Normal, 1f);
                m_Block.SetFloat(k_Volume, 0f);
                m_Block.SetFloat(k_Figure, 0f);
                renderer.SetPropertyBlock(m_Block);
                if (!renderer.enabled)
                {
                    renderer.enabled = true;
                    AsciiPortalCulling.Add(renderer);
                }
            }
        }

        void OnDestroy()
        {
            m_Show = false;
            if (m_Meshes != null)
                m_Meshes.enabled = false;
            HideMeshes();
            if (m_PrefabFilter != null)
                Destroy(m_PrefabFilter.gameObject);
        }

        MeshFilter EnsurePrefabFilter()
        {
            if (m_PrefabFilter != null)
                return m_PrefabFilter;
            var go = new GameObject("Ascii Mesh Chunk");
            go.SetActive(false);
            DontDestroyOnLoad(go);
            m_PrefabFilter = go.AddComponent<MeshFilter>();
            var renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = m_Material;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            return m_PrefabFilter;
        }
    }
}
