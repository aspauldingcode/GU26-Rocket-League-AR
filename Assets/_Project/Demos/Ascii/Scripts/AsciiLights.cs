using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

namespace CardboardXR.Demos.Ascii
{
    // While the gate is up, turns scanned ceilings and windows into a few light proxies and pushes
    // them to the terminal as shafts, cast shadows, and hall fog. Cheap on purpose: at most four
    // analytical cones, one primary shadow direction. Logs [Ascii].
    public class AsciiLights : MonoBehaviour
    {
        static readonly int k_CountId = Shader.PropertyToID("_AsciiLightCount");
        static readonly int k_FogId = Shader.PropertyToID("_AsciiFog");
        static readonly int k_PrimaryId = Shader.PropertyToID("_AsciiPrimaryLight");
        static readonly int[] k_PosIds =
        {
            Shader.PropertyToID("_AsciiLight0"),
            Shader.PropertyToID("_AsciiLight1"),
            Shader.PropertyToID("_AsciiLight2"),
            Shader.PropertyToID("_AsciiLight3"),
        };
        static readonly int[] k_DirIds =
        {
            Shader.PropertyToID("_AsciiLightDir0"),
            Shader.PropertyToID("_AsciiLightDir1"),
            Shader.PropertyToID("_AsciiLightDir2"),
            Shader.PropertyToID("_AsciiLightDir3"),
        };

        const float k_RefreshSeconds = 0.45f;

        ARPlaneManager m_Planes;
        Transform m_Head;
        readonly List<AsciiLight> m_Candidates = new(16);
        readonly List<AsciiLight> m_Lights = new(AsciiLightMap.MaxLights);
        readonly Vector4[] m_Pos = new Vector4[AsciiLightMap.MaxLights];
        readonly Vector4[] m_Dir = new Vector4[AsciiLightMap.MaxLights];
        float m_NextRefresh;
        int m_LoggedCeiling;
        int m_LoggedWindow;
        Vector4 m_Fog = AsciiLightMap.FogParams();

        public int Count => m_Lights.Count;

        public void Configure(ARPlaneManager planes, Transform head)
        {
            m_Planes = planes;
            m_Head = head;
            enabled = planes != null;
            if (!enabled)
                ClearGlobals();
            else
            {
                m_NextRefresh = 0f;
                Refresh();
            }
        }

        public void SetActive(bool on)
        {
            enabled = on && m_Planes != null;
            if (!enabled)
                ClearGlobals();
            else
            {
                m_NextRefresh = 0f;
                Refresh();
            }
        }

        void LateUpdate()
        {
            if (Time.unscaledTime < m_NextRefresh)
            {
                PushPrimary();
                return;
            }
            Refresh();
        }

        void OnDestroy() => ClearGlobals();

        void Refresh()
        {
            m_NextRefresh = Time.unscaledTime + k_RefreshSeconds;
            m_Candidates.Clear();
            float? floor = null;
            var floorPoint = m_Head != null ? m_Head.position : Vector3.zero;
            floorPoint.y = 0f;

            foreach (var plane in m_Planes.trackables)
            {
                if (plane == null || plane.trackingState == TrackingState.None || plane.subsumedBy != null)
                    continue;
                var area = plane.size.x * plane.size.y;
                floor = AsciiFurniture.FloorY(floor, plane.alignment, plane.classification,
                    plane.transform.position.y, area);
            }
            var floorY = floor ?? (m_Head != null ? m_Head.position.y - 1.5f : 0f);
            floorPoint.y = floorY;
            if (m_Head != null)
            {
                floorPoint.x = m_Head.position.x;
                floorPoint.z = m_Head.position.z;
            }

            foreach (var plane in m_Planes.trackables)
            {
                if (plane == null || plane.trackingState == TrackingState.None || plane.subsumedBy != null)
                    continue;
                var center = plane.transform.position;
                var normal = plane.transform.up;
                if (AsciiLightMap.TryCeiling(plane.alignment, plane.classification, center, normal,
                        plane.size, out var ceiling))
                {
                    m_Candidates.Add(ceiling);
                    // A long ceiling gets a couple more lamps so shafts read down the hall.
                    var area = plane.size.x * plane.size.y;
                    if (area > 6f)
                    {
                        var right = plane.transform.right;
                        var forward = plane.transform.forward;
                        var span = Mathf.Min(plane.size.x, plane.size.y) * 0.28f;
                        if (AsciiLightMap.TryCeiling(plane.alignment, plane.classification,
                                center + right * span, normal, plane.size * 0.5f, out var a))
                            m_Candidates.Add(a);
                        if (AsciiLightMap.TryCeiling(plane.alignment, plane.classification,
                                center - right * span + forward * (span * 0.5f), normal, plane.size * 0.5f,
                                out var b))
                            m_Candidates.Add(b);
                    }
                }
                if (AsciiLightMap.TryWindow(plane.alignment, plane.classification, center, normal,
                        plane.size, floorY, out var window))
                    m_Candidates.Add(window);
            }

            AsciiLightMap.Collect(m_Candidates, floorPoint, m_Lights);
            PushGlobals(floorPoint);
            LogIfChanged();
        }

        void PushGlobals(Vector3 floorPoint)
        {
            for (var i = 0; i < AsciiLightMap.MaxLights; i++)
            {
                if (i < m_Lights.Count)
                {
                    var light = m_Lights[i];
                    m_Pos[i] = new Vector4(light.Position.x, light.Position.y, light.Position.z, light.Power);
                    m_Dir[i] = new Vector4(light.Direction.x, light.Direction.y, light.Direction.z,
                        light.Kind == AsciiLightKind.Window ? 0.28f : 0.16f);
                }
                else
                {
                    m_Pos[i] = Vector4.zero;
                    m_Dir[i] = new Vector4(0f, -1f, 0f, 0.16f);
                }
                Shader.SetGlobalVector(k_PosIds[i], m_Pos[i]);
                Shader.SetGlobalVector(k_DirIds[i], m_Dir[i]);
            }
            Shader.SetGlobalFloat(k_CountId, m_Lights.Count);
            Shader.SetGlobalVector(k_FogId, m_Fog);
            PushPrimary(floorPoint);
        }

        void PushPrimary(Vector3? floorPoint = null)
        {
            var from = m_Head != null ? m_Head.position : floorPoint ?? Vector3.zero;
            if (floorPoint.HasValue)
                from = Vector3.Lerp(from, floorPoint.Value + Vector3.up * 1.2f, 0.35f);
            var dir = AsciiLightMap.PrimaryDirection(m_Lights, from);
            Shader.SetGlobalVector(k_PrimaryId, new Vector4(dir.x, dir.y, dir.z, 1f));
        }

        void LogIfChanged()
        {
            var ceilings = 0;
            var windows = 0;
            for (var i = 0; i < m_Lights.Count; i++)
            {
                if (m_Lights[i].Kind == AsciiLightKind.Ceiling)
                    ceilings++;
                else if (m_Lights[i].Kind == AsciiLightKind.Window)
                    windows++;
            }
            if (ceilings == m_LoggedCeiling && windows == m_LoggedWindow)
                return;
            m_LoggedCeiling = ceilings;
            m_LoggedWindow = windows;
            var fallback = m_Lights.Count > 0 && m_Lights[0].Kind == AsciiLightKind.Fallback ? 1 : 0;
            Debug.Log($"[Ascii] Lights: {ceilings} ceiling, {windows} window" +
                      (fallback > 0 ? ", 1 fallback overhead" : "") +
                      $" (fog {m_Fog.x:F1}–{m_Fog.y:F1} m)");
        }

        static void ClearGlobals()
        {
            Shader.SetGlobalFloat(k_CountId, 0f);
            Shader.SetGlobalVector(k_FogId, Vector4.zero);
            var fallback = new Vector3(0.25f, 0.9f, 0.3f).normalized;
            Shader.SetGlobalVector(k_PrimaryId, new Vector4(fallback.x, fallback.y, fallback.z, 1f));
            for (var i = 0; i < AsciiLightMap.MaxLights; i++)
            {
                Shader.SetGlobalVector(k_PosIds[i], Vector4.zero);
                Shader.SetGlobalVector(k_DirIds[i], new Vector4(0f, -1f, 0f, 0.16f));
            }
        }
    }
}
