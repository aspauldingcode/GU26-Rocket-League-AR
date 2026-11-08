using System.Collections.Generic;
using UnityEngine.AI;
using UnityEngine.XR.ARFoundation;
using UnityEngine;

namespace CardboardXR.Demos.Ascii
{
    // Weeping angels the math game sets on the player. The first starts furthest; later questions
    // add more with slight lateral offsets. Each one's walk speed is a fraction of its own start
    // distance so farther statues cover ground faster, and the first (spawned earliest and furthest)
    // still reaches the player first if they fail. Looking slows them to a crawl; only read/grace
    // holds them still. Logs [Ascii].
    public class AsciiAngels : MonoBehaviour
    {
        const float k_BakeInterval = 8f;
        // How long an angel has to cover (startDistance − reach) while unseen. Lower = slightly faster.
        public const float k_ReachSeconds = 45f;
        public const float k_AngelReach = 0.55f;

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
        NavMeshData m_NavMeshData;
        NavMeshDataInstance m_NavMeshInstance;
        AsyncOperation m_NavMeshUpdate;
        float m_NextBake;
        bool m_Inside;
        bool m_Held;

        public event System.Action Caught;
        public bool HasAngel => m_Angels.Count > 0;
        public int Count => m_Angels.Count;

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
            m_Inside = inside;
            if (inside)
                return;
            Clear();
            ClearNavigation();
        }

        public void SetHeld(bool held)
        {
            m_Held = held;
            foreach (var angel in m_Angels)
                if (angel != null)
                    angel.Held = held;
        }

        public bool TryGetAngel(out Vector3 position)
        {
            // Prefer the nearest living angel for UI placement.
            var best = float.PositiveInfinity;
            position = default;
            var found = false;
            foreach (var angel in m_Angels)
            {
                if (angel == null)
                    continue;
                var d = Vector3.ProjectOnPlane(angel.transform.position - m_Head.position, Vector3.up).sqrMagnitude;
                if (d >= best)
                    continue;
                best = d;
                position = angel.transform.position;
                found = true;
            }
            return found;
        }

        public float NearestDistance()
        {
            if (!TryGetAngel(out var position) || m_Head == null)
                return float.PositiveInfinity;
            return Vector3.ProjectOnPlane(position - m_Head.position, Vector3.up).magnitude;
        }

        // 0 far away, 1 at striking distance: for threat UI / shader pulse.
        public float Threat()
        {
            var distance = NearestDistance();
            if (float.IsInfinity(distance))
                return 0f;
            return 1f - Mathf.Clamp01((distance - k_AngelReach) / 4f);
        }

        public void Despawn() => Clear();

        void OnDestroy() => Clear();

        // Ensure at least count angels exist. New ones spawn at spawnPosition with a small lateral
        // offset so they don't stack; speed is (distance − reach) / reachSeconds of THAT spawn.
        public void EnsureCount(int count, Vector3 spawnPosition, Vector3 facing)
        {
            if (m_Prefab == null || m_Head == null)
            {
                Debug.LogWarning("[Ascii] No angel prefab; weeping angels stay away");
                return;
            }
            count = Mathf.Max(0, count);
            facing = Vector3.ProjectOnPlane(facing, Vector3.up);
            if (facing.sqrMagnitude < 1e-4f)
                facing = Vector3.forward;
            facing.Normalize();
            var right = Vector3.Cross(Vector3.up, facing).normalized;

            while (m_Angels.Count < count)
            {
                var index = m_Angels.Count;
                // Subtle spread: first on the line of sight, later ones a little to the side and nearer.
                var lateral = index == 0 ? 0f : ((index % 2 == 0) ? 1f : -1f) * (0.35f + 0.2f * ((index - 1) / 2));
                var nearer = index == 0 ? 0f : 0.4f + 0.25f * (index - 1);
                var along = Vector3.ProjectOnPlane(spawnPosition - m_Head.position, Vector3.up);
                var distance = Mathf.Max(k_AngelReach + 0.5f, along.magnitude - nearer);
                var pos = m_Head.position + facing * distance + right * lateral;
                pos.y = m_Head.position.y - 1.55f;
                // Percentage of this angel's own start gap per second — not a shared constant speed.
                var speed = Mathf.Max(0.02f, (distance - k_AngelReach) / k_ReachSeconds);
                SpawnOne(pos, speed, distance);
            }
            if (m_NavMeshData != null)
                foreach (var angel in m_Angels)
                    angel.RefreshNavigation();
        }

        void SpawnOne(Vector3 spawnPosition, float speed, float startDistance)
        {
            var go = Instantiate(m_Prefab, spawnPosition, Quaternion.identity, transform);
            go.name = $"Weeping Angel {m_Angels.Count + 1}";
            go.SetActive(true);
            ApplyAscii(go);
            var angel = go.GetComponent<WeepingAngel>();
            if (angel == null)
                angel = go.AddComponent<WeepingAngel>();
            angel.Configure(m_Head, speed, startDistance);
            angel.Held = m_Held;
            angel.Caught += OnCaught;
            m_Angels.Add(angel);
            Debug.Log($"[Ascii] Weeping angel #{m_Angels.Count} at {startDistance:F1} m, {speed:F2} m/s " +
                      $"({100f / k_ReachSeconds:F1}% of start gap / s)");
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
