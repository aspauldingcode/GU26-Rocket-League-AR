using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

namespace CardboardXR.Demos.Ascii
{
    // While the gate is up, tracks the floor height the terminal shader uses to paint raised
    // tops yellow. Inside the gate, covers each plane with that shade material. The glyphs
    // themselves are the screen-space terminal pass, not a texture on the plane. Logs [Ascii].
    public class AsciiSurfaces : MonoBehaviour
    {
        static readonly int k_Floor = Shader.PropertyToID("_AsciiFloorY");
        static readonly int k_Force = Shader.PropertyToID("_ForceFurniture");

        Material m_Material;
        ARPlaneManager m_Planes;
        readonly Dictionary<TrackableId, MeshFilter> m_Overlays = new();
        readonly Dictionary<TrackableId, MeshRenderer> m_Hidden = new();
        readonly HashSet<TrackableId> m_Seen = new();
        readonly List<TrackableId> m_Stale = new();
        readonly MaterialPropertyBlock m_Block = new();
        bool m_Draw;
        bool m_LoggedFurniture;

        public void Configure(Material asciiMaterial, ARPlaneManager planes)
        {
            m_Material = asciiMaterial;
            m_Planes = planes;
            enabled = false;
        }

        // On for the whole time the gate is up, so the floor height stays current for the peek too.
        public void SetLive(bool on)
        {
            var want = on && m_Planes != null && m_Material != null;
            if (!want)
            {
                m_Draw = false;
                ClearOverlays();
                Shader.SetGlobalFloat(k_Floor, -999f);
            }
            enabled = want;
        }

        // Plane overlays only inside the gate. Outside, passthrough stays clear.
        public void SetDrawn(bool on)
        {
            m_Draw = on && enabled;
            if (!m_Draw)
                ClearOverlays();
            else
                SyncOverlays();
        }

        void LateUpdate()
        {
            UpdateFloor();
            if (m_Draw)
                SyncOverlays();
        }

        void OnDestroy() => SetLive(false);

        void UpdateFloor()
        {
            float? floor = null;
            var obstacles = 0;
            foreach (var plane in m_Planes.trackables)
            {
                if (plane == null || plane.trackingState == TrackingState.None || plane.subsumedBy != null)
                    continue;
                var area = plane.size.x * plane.size.y;
                floor = AsciiFurniture.FloorY(floor, plane.alignment, plane.classification, plane.transform.position.y, area);
            }
            var floorY = floor ?? -999f;
            Shader.SetGlobalFloat(k_Floor, floorY);
            if (!m_Draw || m_LoggedFurniture || !floor.HasValue)
                return;
            foreach (var plane in m_Planes.trackables)
            {
                if (plane == null || plane.subsumedBy != null)
                    continue;
                if (AsciiFurniture.IsObstacle(plane.alignment, plane.classification, plane.transform.position.y, floorY, plane.size))
                    obstacles++;
            }
            m_LoggedFurniture = true;
            Debug.Log($"[Ascii] Floor at {floorY:F2} m, {obstacles} furniture surfaces in yellow");
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
                var painted = overlay.GetComponent<MeshRenderer>();
                if (painted != null)
                {
                    var floorY = Shader.GetGlobalFloat(k_Floor);
                    var obstacle = AsciiFurniture.IsObstacle(plane.alignment, plane.classification,
                        plane.transform.position.y, floorY, plane.size);
                    m_Block.Clear();
                    m_Block.SetFloat(k_Force, obstacle ? 1f : 0f);
                    painted.SetPropertyBlock(m_Block);
                }
                // The plane's own grid is transparent and draws over everything. Up close, with
                // passthrough off, that reads as glass. The ASCII overlay is the surface.
                var grid = plane.GetComponent<MeshRenderer>();
                if (grid != null && grid.gameObject != overlay.gameObject)
                {
                    if (grid.enabled)
                        grid.enabled = false;
                    m_Hidden[plane.trackableId] = grid;
                }
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
                RestoreGrid(id);
            }
        }

        void RestoreGrid(TrackableId id)
        {
            if (!m_Hidden.TryGetValue(id, out var grid))
                return;
            m_Hidden.Remove(id);
            if (grid != null)
                grid.enabled = true;
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
            foreach (var pair in m_Hidden)
            {
                if (pair.Value != null)
                    pair.Value.enabled = true;
            }
            m_Hidden.Clear();
        }
    }
}
