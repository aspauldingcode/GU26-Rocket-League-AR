using System.Collections.Generic;
using UnityEngine;

namespace CardboardXR.Demos.Ascii
{
    // Spawns weeping angels around the head when the player steps into the ASCII world, and clears
    // them on the way back out. They start far away and only close the gap while unseen. Logs
    // [Ascii].
    public class AsciiAngels : MonoBehaviour
    {
        const int k_Count = 3;
        const float k_SpawnDistance = 9f;
        // Slow enough that looking away briefly isn't an instant jump into your face.
        const float k_MinSpeed = 0.28f;
        const float k_MaxSpeed = 0.45f;

        GameObject m_Prefab;
        Transform m_Head;
        Material m_Stone;
        readonly List<WeepingAngel> m_Angels = new();

        public void Configure(GameObject prefab, Transform head)
        {
            m_Prefab = prefab;
            m_Head = head;
            m_Stone = StoneMaterial(prefab);
        }

        // The angel stays stone. Never the ASCII glyph shader, which turns the statue into a
        // mirror of the room when you walk up to it.
        static Material StoneMaterial(GameObject prefab)
        {
            if (prefab == null)
                return null;
            foreach (var renderer in prefab.GetComponentsInChildren<Renderer>(true))
            {
                var material = renderer.sharedMaterial;
                if (material == null || material.shader == null)
                    continue;
                if (material.shader.name.IndexOf("Ascii", System.StringComparison.OrdinalIgnoreCase) >= 0)
                    continue;
                return material;
            }
            return null;
        }

        void ApplyStone(GameObject go)
        {
            if (m_Stone == null)
                return;
            foreach (var renderer in go.GetComponentsInChildren<Renderer>(true))
                renderer.sharedMaterial = m_Stone;
        }

        public void SetInside(bool inside)
        {
            if (inside)
                Spawn();
            else
                Clear();
        }

        void OnDestroy() => Clear();

        void Spawn()
        {
            Clear();
            if (m_Prefab == null || m_Head == null)
            {
                Debug.LogWarning("[Ascii] No angel prefab; weeping angels stay away");
                return;
            }

            var yaw = m_Head.eulerAngles.y;
            for (var i = 0; i < k_Count; i++)
            {
                var angle = (yaw + 40f + i * (360f / k_Count)) * Mathf.Deg2Rad;
                var offset = new Vector3(Mathf.Sin(angle), 0f, Mathf.Cos(angle)) * k_SpawnDistance;
                var position = m_Head.position + offset;
                position.y = m_Head.position.y - 1.55f;
                var go = Instantiate(m_Prefab, position, Quaternion.identity, transform);
                go.name = $"Weeping Angel {i + 1}";
                go.SetActive(true);
                ApplyStone(go);
                var angel = go.GetComponent<WeepingAngel>();
                if (angel == null)
                    angel = go.AddComponent<WeepingAngel>();
                var speed = Mathf.Lerp(k_MinSpeed, k_MaxSpeed, i / (float)(k_Count - 1));
                angel.Configure(m_Head, speed);
                m_Angels.Add(angel);
            }
            Debug.Log($"[Ascii] {m_Angels.Count} weeping angels at {k_SpawnDistance:F0} m");
        }

        void Clear()
        {
            foreach (var angel in m_Angels)
            {
                if (angel != null)
                    Destroy(angel.gameObject);
            }
            m_Angels.Clear();
        }
    }
}
