using System.Collections.Generic;
using CardboardXR.Stereo;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

namespace CardboardXR.Demos.Ascii
{
    // Covers every tracked AR plane (floor, walls, tables, ceiling) with the opaque ASCII material
    // while the glyph gate is up. Each overlay is a child of its plane sharing the plane's mesh, so
    // it follows the plane as ARCore refines it; the extrusion in the shader puts it in front of
    // the plane's own grid. Each glyph cell takes its color and density from the passthrough camera
    // image where the cell is in the room. Outside the gate only the far side of the gate's plane
    // is drawn, which the portal's depth masks then show only through the doorway. Logs [Ascii].
    public class AsciiSurfaces : MonoBehaviour
    {
        static readonly int k_CameraTex = Shader.PropertyToID("_AsciiCameraTex");
        static readonly int k_UseCamera = Shader.PropertyToID("_AsciiUseCamera");
        static readonly int k_WorldToCamera = Shader.PropertyToID("_AsciiWorldToCamera");
        static readonly int k_Frustum = Shader.PropertyToID("_AsciiFrustum");
        static readonly int k_ClipPlane = Shader.PropertyToID("_AsciiClipPlane");
        static readonly int k_Clip = Shader.PropertyToID("_AsciiClip");

        Material m_Material;
        ARPlaneManager m_Planes;
        PassthroughController m_Passthrough;
        AsciiWorld m_Gate;
        readonly Dictionary<TrackableId, MeshFilter> m_Overlays = new();
        readonly HashSet<TrackableId> m_Seen = new();
        readonly List<TrackableId> m_Stale = new();
        bool m_LoggedCamera;

        public void Configure(Material asciiMaterial, ARPlaneManager planes, PassthroughController passthrough)
        {
            m_Material = asciiMaterial;
            m_Planes = planes;
            m_Passthrough = passthrough;
            enabled = false;
        }

        // On with the gate that clips the view from outside; off with null.
        public void SetGate(AsciiWorld gate)
        {
            m_Gate = gate;
            var on = gate != null && m_Planes != null && m_Material != null;
            if (on == enabled)
                return;
            enabled = on;
            if (on)
            {
                SyncOverlays();
                Debug.Log($"[Ascii] ASCII surfaces on ({m_Overlays.Count} planes)");
            }
            else
            {
                ClearOverlays();
                Shader.SetGlobalFloat(k_UseCamera, 0f);
                Shader.SetGlobalFloat(k_Clip, 0f);
                Debug.Log("[Ascii] ASCII surfaces off");
            }
        }

        void LateUpdate()
        {
            SyncOverlays();
            UpdateCamera();
            UpdateClip();
        }

        void OnDestroy() => SetGate(null);

        void UpdateCamera()
        {
            var image = m_Passthrough != null ? m_Passthrough.CameraImage : null;
            if (image == null)
            {
                Shader.SetGlobalFloat(k_UseCamera, 0f);
                return;
            }
            Shader.SetGlobalTexture(k_CameraTex, image);
            Shader.SetGlobalMatrix(k_WorldToCamera, m_Passthrough.CameraTransform.worldToLocalMatrix);
            Shader.SetGlobalVector(k_Frustum, m_Passthrough.ImageFrustum);
            Shader.SetGlobalFloat(k_UseCamera, 1f);
            if (m_LoggedCamera)
                return;
            m_LoggedCamera = true;
            Debug.Log($"[Ascii] Coloring glyphs from the camera image ({image.width}x{image.height})");
        }

        // The ASCII side of the gate is its local -z (see AsciiWorld.ToGateSpace).
        void UpdateClip()
        {
            if (m_Gate == null || m_Gate.IsInside)
            {
                Shader.SetGlobalFloat(k_Clip, 0f);
                return;
            }
            var gate = m_Gate.transform;
            var normal = -gate.forward;
            Shader.SetGlobalVector(k_ClipPlane, new Vector4(normal.x, normal.y, normal.z, -Vector3.Dot(normal, gate.position)));
            Shader.SetGlobalFloat(k_Clip, 1f);
        }

        void SyncOverlays()
        {
            if (m_Planes == null)
                return;
            m_Seen.Clear();
            foreach (var plane in m_Planes.trackables)
            {
                if (plane == null || plane.trackingState == TrackingState.None || plane.subsumedBy != null)
                    continue;
                var source = plane.GetComponent<MeshFilter>();
                if (source == null || source.sharedMesh == null)
                    continue;
                m_Seen.Add(plane.trackableId);
                if (!m_Overlays.TryGetValue(plane.trackableId, out var overlay) || overlay == null)
                {
                    overlay = CreateOverlay(plane);
                    m_Overlays[plane.trackableId] = overlay;
                }
                if (overlay.sharedMesh != source.sharedMesh)
                    overlay.sharedMesh = source.sharedMesh;
            }
            m_Stale.Clear();
            foreach (var pair in m_Overlays)
            {
                if (!m_Seen.Contains(pair.Key))
                    m_Stale.Add(pair.Key);
            }
            foreach (var id in m_Stale)
            {
                if (m_Overlays.TryGetValue(id, out var overlay) && overlay != null)
                    Destroy(overlay.gameObject);
                m_Overlays.Remove(id);
            }
        }

        MeshFilter CreateOverlay(ARPlane plane)
        {
            var go = new GameObject("Ascii Overlay");
            go.transform.SetParent(plane.transform, false);
            var filter = go.AddComponent<MeshFilter>();
            var renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = m_Material;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            return filter;
        }

        void ClearOverlays()
        {
            foreach (var pair in m_Overlays)
            {
                if (pair.Value != null)
                    Destroy(pair.Value.gameObject);
            }
            m_Overlays.Clear();
        }
    }
}
