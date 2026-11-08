using System.Collections.Generic;
using UnityEngine.AI;
using UnityEngine.XR.ARFoundation;
using UnityEngine;

namespace CardboardXR.Demos.Ascii
{
    // Spawns weeping angels around the head when the player steps into the ASCII world, and clears
    // them on the way back out. They start far away and only close the gap while unseen. Logs
    // [Ascii].
    public class AsciiAngels : MonoBehaviour
    {
        const int k_Count = 1;
        const float k_SpawnDistance = 9f;
        const float k_Speed = 0.3f;
        const float k_BakeInterval = 8f;

        static readonly int k_Force = Shader.PropertyToID("_ForceFurniture");
        static readonly int k_Normal = Shader.PropertyToID("_UseMeshNormal");
        static readonly int k_Volume = Shader.PropertyToID("_Volume");
        static readonly int k_Figure = Shader.PropertyToID("_Figure");

        GameObject m_Prefab;
        Transform m_Head;
        Material m_Ascii;
        ARMeshManager m_Meshes;
        Material m_AngelMaterial;
        readonly List<WeepingAngel> m_Angels = new();
        readonly MaterialPropertyBlock m_Block = new();
        NavMeshData m_NavMeshData;
        NavMeshDataInstance m_NavMeshInstance;
        AsyncOperation m_NavMeshUpdate;
        float m_NextBake;
        bool m_Inside;

        public event System.Action Caught;
        public bool HasAngel => m_Angels.Count > 0;

        public void Configure(GameObject prefab, Transform head, Material ascii, ARMeshManager meshes = null)
        {
            m_Prefab = prefab;
            m_Head = head;
            m_Ascii = ascii;
            m_Meshes = meshes;
        }

        void Update()
        {
            if (m_NavMeshUpdate != null)
            {
                if (!m_NavMeshUpdate.isDone)
                    return;
                m_NavMeshUpdate = null;
                foreach (var angel in m_Angels)
                    if (angel != null)
                        angel.RefreshNavigation();
            }
            if (!m_Inside || m_Meshes == null || Time.unscaledTime < m_NextBake)
                return;
            var sources = new List<NavMeshBuildSource>();
            foreach (var mesh in m_Meshes.meshes)
            {
                if (mesh == null)
                    continue;
                var filter = mesh.GetComponent<MeshFilter>();
                if (filter == null || filter.sharedMesh == null)
                    continue;
                sources.Add(new NavMeshBuildSource
                {
                    shape = NavMeshBuildSourceShape.Mesh,
                    sourceObject = filter.sharedMesh,
                    transform = filter.transform.localToWorldMatrix,
                    area = 0,
                });
            }
            if (sources.Count == 0)
            {
                m_NextBake = Time.unscaledTime + 2f;
                return;
            }

            var settings = NavMesh.GetSettingsByID(0);
            var bounds = new Bounds(m_Head.position, new Vector3(30f, 8f, 30f));
            if (m_NavMeshData == null)
            {
                m_NavMeshData = new NavMeshData(settings.agentTypeID);
                m_NavMeshInstance = NavMesh.AddNavMeshData(m_NavMeshData);
            }
            m_NavMeshUpdate = NavMeshBuilder.UpdateNavMeshDataAsync(m_NavMeshData, settings, sources, bounds);
            m_NextBake = Time.unscaledTime + k_BakeInterval;
        }

        // A material instance, not a property block: the figure flag has to be on the material
        // or the statue stays stone-colored without glyphs. The glyphs are gray, with a soft halo.
        Material AngelMaterial()
        {
            if (m_AngelMaterial != null || m_Ascii == null)
                return m_AngelMaterial;
            m_AngelMaterial = new Material(m_Ascii) { name = "Ascii Angel" };
            m_AngelMaterial.SetFloat(k_Force, 0f);
            m_AngelMaterial.SetFloat(k_Normal, 1f);
            m_AngelMaterial.SetFloat(k_Volume, 0f);
            m_AngelMaterial.SetFloat(k_Figure, 1f);
            m_AngelMaterial.SetFloat("_GlyphCount", AsciiGlyph.Ramp.Length);
            return m_AngelMaterial;
        }

        void ApplyAscii(GameObject go)
        {
            var material = AngelMaterial();
            if (material == null)
                return;
            foreach (var renderer in go.GetComponentsInChildren<Renderer>(true))
            {
                renderer.sharedMaterial = material;
                renderer.SetPropertyBlock(null);
            }
        }

        public void SetInside(bool inside)
        {
            if (inside)
                SetInside(true, m_Head != null ? m_Head.position - m_Head.forward * k_SpawnDistance : Vector3.zero);
            else
            {
                m_Inside = false;
                Clear();
                ClearNavigation();
            }
        }

        public void SetInside(bool inside, Vector3 spawnPosition)
        {
            if (!inside)
            {
                SetInside(false);
                return;
            }
            m_Inside = true;
            Spawn(spawnPosition);
        }

        public void SpeedUp()
        {
            foreach (var angel in m_Angels)
                if (angel != null)
                    angel.SpeedUp();
        }

        void OnDestroy() => Clear();

        void Spawn(Vector3 spawnPosition)
        {
            Clear();
            if (m_Prefab == null || m_Head == null)
            {
                Debug.LogWarning("[Ascii] No angel prefab; weeping angels stay away");
                return;
            }

            for (var i = 0; i < k_Count; i++)
            {
                spawnPosition.y = m_Head.position.y - 1.55f;
                var go = Instantiate(m_Prefab, spawnPosition, Quaternion.identity, transform);
                go.name = "Weeping Angel";
                go.SetActive(true);
                ApplyAscii(go);
                var angel = go.GetComponent<WeepingAngel>();
                if (angel == null)
                    angel = go.AddComponent<WeepingAngel>();
                angel.Configure(m_Head, k_Speed);
                angel.Caught += OnCaught;
                m_Angels.Add(angel);
            }
            if (m_NavMeshData != null)
                foreach (var angel in m_Angels)
                    angel.RefreshNavigation();
            Debug.Log("[Ascii] One weeping angel spawned opposite the quiz wall");
        }

        void OnCaught() => Caught?.Invoke();

        void ClearNavigation()
        {
            if (m_NavMeshUpdate != null && !m_NavMeshUpdate.isDone)
            {
                m_NavMeshUpdate.completed += _ =>
                {
                    if (this != null)
                        RemoveNavigationData();
                };
                return;
            }
            RemoveNavigationData();
        }

        void RemoveNavigationData()
        {
            m_NavMeshUpdate = null;
            if (m_NavMeshInstance.valid)
                m_NavMeshInstance.Remove();
            if (m_NavMeshData != null)
                Destroy(m_NavMeshData);
            m_NavMeshData = null;
        }

        void Clear()
        {
            foreach (var angel in m_Angels)
            {
                if (angel != null)
                {
                    angel.Caught -= OnCaught;
                    Destroy(angel.gameObject);
                }
            }
            m_Angels.Clear();
            if (m_AngelMaterial == null)
                return;
            Destroy(m_AngelMaterial);
            m_AngelMaterial = null;
        }
    }
}
