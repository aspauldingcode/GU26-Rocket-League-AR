using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.ARSubsystems;

namespace CardboardXR.Demos.Ascii
{
    public enum AsciiLightKind
    {
        Ceiling,
        Window,
        Fallback,
    }

    // One cheap light proxy for the terminal: a point that casts a shaft along Direction.
    public struct AsciiLight
    {
        public Vector3 Position;
        public Vector3 Direction;
        public float Power;
        public AsciiLightKind Kind;
    }

    // Maps scanned AR planes to a handful of ceiling lamps and window shafts. Pure so EditMode
    // tests can check the rules without ARFoundation running.
    public static class AsciiLightMap
    {
        public const int MaxLights = 4;
        public const float MinCeilingArea = 0.6f;
        public const float MinWindowArea = 0.35f;
        public const float FallbackHeight = 2.4f;

        // Fog: start fading room glyphs, finish into mist, mist density for empty hall tiles.
        public static Vector4 FogParams(float startMeters = 2.2f, float endMeters = 9f, float density = 0.7f,
            float mist = 0.55f) =>
            new(startMeters, endMeters, density, mist);

        public static float FogAmount(float eyeDepthMeters, Vector4 fog)
        {
            var start = fog.x;
            var end = Mathf.Max(fog.y, start + 0.01f);
            var t = Mathf.Clamp01((eyeDepthMeters - start) / (end - start));
            t = t * t * (3f - 2f * t);
            return t * fog.z;
        }

        // Ceiling panel or classified ceiling → lamp hanging just under the plane, shaft straight down.
        public static bool TryCeiling(PlaneAlignment alignment, PlaneClassification classification,
            Vector3 center, Vector3 normal, Vector2 size, out AsciiLight light)
        {
            light = default;
            var area = size.x * size.y;
            var ceiling = classification == PlaneClassification.Ceiling ||
                          alignment == PlaneAlignment.HorizontalDown;
            if (!ceiling || area < MinCeilingArea)
                return false;
            // Plane normals for HorizontalDown face the room; fall back to world down.
            var down = Vector3.Dot(normal, Vector3.down) > 0.35f ? normal.normalized : Vector3.down;
            light = new AsciiLight
            {
                Position = center + down * 0.04f,
                Direction = down,
                Power = Mathf.Clamp(0.55f + area * 0.08f, 0.55f, 1.15f),
                Kind = AsciiLightKind.Ceiling,
            };
            return true;
        }

        // Classified window, or a mid-height vertical opening, casts inward along -normal.
        public static bool TryWindow(PlaneAlignment alignment, PlaneClassification classification,
            Vector3 center, Vector3 normal, Vector2 size, float floorY, out AsciiLight light)
        {
            light = default;
            if (alignment != PlaneAlignment.Vertical)
                return false;
            var area = size.x * size.y;
            var classified = classification == PlaneClassification.Window;
            var opening = classification == PlaneClassification.None &&
                          size.y > 0.7f && size.y < 2.4f && size.x > 0.5f && size.x < 2.8f &&
                          center.y > floorY + 0.7f && center.y < floorY + 2.4f && area >= MinWindowArea;
            if (!classified && !opening)
                return false;
            if (classified && area < MinWindowArea * 0.5f)
                return false;
            var inward = (-normal).normalized;
            if (inward.sqrMagnitude < 0.01f)
                inward = Vector3.forward;
            light = new AsciiLight
            {
                Position = center + inward * 0.05f,
                Direction = inward,
                Power = classified ? 1f : 0.7f,
                Kind = AsciiLightKind.Window,
            };
            return true;
        }

        // One overhead fill when the scan has no lamps yet, so shafts/shadows still read.
        public static AsciiLight FallbackOver(Vector3 floorPoint, float height = FallbackHeight) =>
            new()
            {
                Position = floorPoint + Vector3.up * height,
                Direction = Vector3.down,
                Power = 0.75f,
                Kind = AsciiLightKind.Fallback,
            };

        // Direction from a sample point toward the strongest light (for cast shadows / form).
        public static Vector3 PrimaryDirection(IReadOnlyList<AsciiLight> lights, Vector3 from)
        {
            if (lights == null || lights.Count == 0)
                return new Vector3(0.25f, 0.9f, 0.3f).normalized;
            var best = 0;
            var bestPower = -1f;
            for (var i = 0; i < lights.Count; i++)
            {
                if (lights[i].Power > bestPower)
                {
                    bestPower = lights[i].Power;
                    best = i;
                }
            }
            var to = lights[best].Position - from;
            return to.sqrMagnitude > 1e-4f ? to.normalized : -lights[best].Direction;
        }

        // Prefer windows, then ceilings, then a single fallback. Caps at MaxLights.
        public static int Collect(IReadOnlyList<AsciiLight> candidates, Vector3 floorPoint,
            List<AsciiLight> into)
        {
            into.Clear();
            if (candidates != null)
            {
                AppendKind(candidates, AsciiLightKind.Window, into);
                AppendKind(candidates, AsciiLightKind.Ceiling, into);
            }
            if (into.Count == 0)
                into.Add(FallbackOver(floorPoint));
            if (into.Count > MaxLights)
                into.RemoveRange(MaxLights, into.Count - MaxLights);
            return into.Count;
        }

        static void AppendKind(IReadOnlyList<AsciiLight> candidates, AsciiLightKind kind, List<AsciiLight> into)
        {
            for (var i = 0; i < candidates.Count && into.Count < MaxLights; i++)
            {
                if (candidates[i].Kind == kind)
                    into.Add(candidates[i]);
            }
        }
    }
}
