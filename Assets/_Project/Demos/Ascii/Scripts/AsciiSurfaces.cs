using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

namespace CardboardXR.Demos.Ascii
{
    // Overlays every tracked AR plane with the ASCII surface material while the player is inside
    // the glyph gate. Camera frames, when available, feed luminance into the shader so the glyphs
    // remap the physical picture. Parent under Core's Boundary so planes stay in the room; destroy
    // overlays on exit. Logs [Ascii].
    public class AsciiSurfaces : MonoBehaviour
    {
        static readonly int k_CameraTex = Shader.PropertyToID("_AsciiCameraTex");
        static readonly int k_UseCamera = Shader.PropertyToID("_UseCamera");

        Material m_Material;
        ARPlaneManager m_Planes;
        ARCameraManager m_Camera;
        readonly Dictionary<TrackableId, GameObject> m_Overlays = new();
        bool m_Listening;

        public void Configure(Material asciiMaterial, ARPlaneManager planes, ARCameraManager camera)
        {
            m_Material = asciiMaterial;
            m_Planes = planes;
            m_Camera = camera;
        }

        public void SetActive(bool on)
        {
            if (on)
            {
                if (m_Planes == null || m_Material == null)
                    return;
                if (!m_Listening && m_Camera != null)
                {
                    m_Camera.frameReceived += OnFrame;
                    m_Listening = true;
                }
                SyncOverlays();
                Debug.Log($"[Ascii] ASCII surfaces on ({m_Overlays.Count} planes)");
            }
            else
            {
                if (m_Listening && m_Camera != null)
                {
                    m_Camera.frameReceived -= OnFrame;
                    m_Listening = false;
                }
                ClearOverlays();
                if (m_Material != null)
                    m_Material.SetFloat(k_UseCamera, 0f);
                Debug.Log("[Ascii] ASCII surfaces off");
            }
            enabled = on;
        }

        void LateUpdate()
        {
            if (enabled)
                SyncOverlays();
        }

        void OnDestroy()
        {
            SetActive(false);
        }

        void OnFrame(ARCameraFrameEventArgs args)
        {
            if (m_Material == null || args.textures == null || args.textures.Count == 0)
                return;
            var tex = args.textures[0];
            if (tex == null)
                return;
            m_Material.SetTexture(k_CameraTex, tex);
            m_Material.SetFloat(k_UseCamera, 1f);
        }

        void SyncOverlays()
        {
            if (m_Planes == null)
                return;
            var seen = new HashSet<TrackableId>();
            foreach (var plane in m_Planes.trackables)
            {
                if (plane == null || plane.trackingState == UnityEngine.XR.ARSubsystems.TrackingState.None)
                    continue;
                seen.Add(plane.trackableId);
                if (!m_Overlays.TryGetValue(plane.trackableId, out var overlay) || overlay == null)
                {
                    overlay = CreateOverlay(plane);
                    m_Overlays[plane.trackableId] = overlay;
                }
                UpdateOverlay(overlay, plane);
            }
            // Drop overlays for planes AR forgot.
            var stale = new List<TrackableId>();
            foreach (var pair in m_Overlays)
            {
                if (!seen.Contains(pair.Key))
                    stale.Add(pair.Key);
            }
            foreach (var id in stale)
            {
                if (m_Overlays.TryGetValue(id, out var go) && go != null)
                    Destroy(go);
                m_Overlays.Remove(id);
            }
        }

        GameObject CreateOverlay(ARPlane plane)
        {
            var go = new GameObject("Ascii Overlay");
            go.transform.SetParent(transform, false);
            go.AddComponent<MeshFilter>();
            var renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = m_Material;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            return go;
        }

        static void UpdateOverlay(GameObject overlay, ARPlane plane)
        {
            overlay.transform.SetPositionAndRotation(plane.transform.position, plane.transform.rotation);
            var source = plane.GetComponent<MeshFilter>();
            var dest = overlay.GetComponent<MeshFilter>();
            if (source != null && source.sharedMesh != null)
                dest.sharedMesh = source.sharedMesh;
        }

        void ClearOverlays()
        {
            foreach (var pair in m_Overlays)
            {
                if (pair.Value != null)
                    Destroy(pair.Value);
            }
            m_Overlays.Clear();
        }
    }
}
