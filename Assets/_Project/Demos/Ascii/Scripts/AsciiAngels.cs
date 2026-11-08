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

        static readonly int k_Force = Shader.PropertyToID("_ForceFurniture");
        static readonly int k_Normal = Shader.PropertyToID("_UseMeshNormal");
        static readonly int k_Volume = Shader.PropertyToID("_Volume");
        static readonly int k_Figure = Shader.PropertyToID("_Figure");

        GameObject m_Prefab;
        Transform m_Head;
        Material m_Ascii;
        readonly List<WeepingAngel> m_Angels = new();
        readonly MaterialPropertyBlock m_Block = new();

        public void Configure(GameObject prefab, Transform head, Material ascii)
        {
            m_Prefab = prefab;
            m_Head = head;
            m_Ascii = ascii;
        }

        // The statue is shaded with its own normals. The terminal draws it as HDR white glyphs,
        // still sized and darkened by distance, and it casts a shadow on the green room.
        void ApplyAscii(GameObject go)
        {
            if (m_Ascii == null)
                return;
            m_Block.Clear();
            m_Block.SetFloat(k_Force, 0f);
            m_Block.SetFloat(k_Normal, 1f);
            m_Block.SetFloat(k_Volume, 0f);
            m_Block.SetFloat(k_Figure, 1f);
            foreach (var renderer in go.GetComponentsInChildren<Renderer>(true))
            {
                renderer.sharedMaterial = m_Ascii;
                renderer.SetPropertyBlock(m_Block);
            }
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
                ApplyAscii(go);
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
