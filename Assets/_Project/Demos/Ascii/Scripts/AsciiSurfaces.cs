using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

namespace CardboardXR.Demos.Ascii
{
    // While the gate is up, tracks the floor so furniture prisms reach it. Inside the gate, covers
    // each plane with the green shade mask. Glyphs and depth brightness are the terminal pass,
    // not a texture on the plane. Logs [Ascii].
    public class AsciiSurfaces : MonoBehaviour
    {
        static readonly int k_Floor = Shader.PropertyToID("_AsciiFloorY");
        static readonly int k_Force = Shader.PropertyToID("_ForceFurniture");
        static readonly int k_Normal = Shader.PropertyToID("_UseMeshNormal");
        static readonly int k_Volume = Shader.PropertyToID("_Volume");
        static readonly int k_Figure = Shader.PropertyToID("_Figure");
        const float k_BackThickness = 0.08f;

        Material m_Material;
        ARPlaneManager m_Planes;
        readonly Dictionary<TrackableId, MeshFilter> m_Overlays = new();
        readonly Dictionary<TrackableId, Mesh> m_Prisms = new();
        readonly Dictionary<TrackableId, Vector4> m_PrismKey = new();
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
            Debug.Log($"[Ascii] Floor at {floorY:F2} m, {obstacles} furniture surfaces");
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
                var floorY = Shader.GetGlobalFloat(k_Floor);
                var obstacle = AsciiFurniture.IsObstacle(plane.alignment, plane.classification,
                    plane.transform.position.y, floorY, plane.size);
                if (obstacle)
                    FitPrism(plane, overlay, floorY);
                else if (overlay.sharedMesh != source.sharedMesh)
                {
                    ReleasePrism(plane.trackableId);
                    overlay.sharedMesh = source.sharedMesh;
                }
                var painted = overlay.GetComponent<MeshRenderer>();
                if (painted != null)
                {
                    m_Block.Clear();
                    m_Block.SetFloat(k_Force, obstacle ? 1f : 0f);
                    m_Block.SetFloat(k_Normal, obstacle ? 1f : 0f);
                    m_Block.SetFloat(k_Volume, 0f);
                    m_Block.SetFloat(k_Figure, 0f);
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
                ReleasePrism(id);
                RestoreGrid(id);
            }
        }

        // A detected top becomes a solid down to the floor (table sides, the body under a seat).
        // A short vertical plane becomes a thin slab, so a chair back has thickness.
        void FitPrism(ARPlane plane, MeshFilter overlay, float floorY)
        {
            var boundary = plane.boundary;
            if (boundary.Length < 3)
                return;
            var horizontal = plane.alignment == PlaneAlignment.HorizontalUp;
            var drop = horizontal ? AsciiFurniture.PrismDrop(plane.transform.position.y, floorY) : k_BackThickness;
            var key = new Vector4(plane.size.x, plane.size.y, drop, boundary.Length);
            if (m_PrismKey.TryGetValue(plane.trackableId, out var previous) && previous == key && overlay.sharedMesh != null)
                return;
            var points = new Vector2[boundary.Length];
            for (var i = 0; i < boundary.Length; i++)
                points[i] = boundary[i];
            var top = horizontal ? 0.01f : k_BackThickness * 0.5f;
            var bottom = horizontal ? -drop : -k_BackThickness * 0.5f;
            var mesh = AsciiFurniture.BuildPrism(points, top, bottom);
            if (mesh == null)
                return;
            ReleasePrism(plane.trackableId);
            m_Prisms[plane.trackableId] = mesh;
            m_PrismKey[plane.trackableId] = key;
            overlay.sharedMesh = mesh;
        }

        void ReleasePrism(TrackableId id)
        {
            if (m_Prisms.TryGetValue(id, out var mesh) && mesh != null)
                Destroy(mesh);
            m_Prisms.Remove(id);
            m_PrismKey.Remove(id);
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
            foreach (var pair in m_Prisms)
            {
                if (pair.Value != null)
                    Destroy(pair.Value);
            }
            m_Prisms.Clear();
            m_PrismKey.Clear();
            foreach (var pair in m_Hidden)
            {
                if (pair.Value != null)
                    pair.Value.enabled = true;
            }
            m_Hidden.Clear();
        }
    }
}
