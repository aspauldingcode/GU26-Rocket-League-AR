using UnityEngine;

namespace CardboardXR.Demos.Ascii
{
    // Portal-plane crossing tests, same rule as PortalWorld.Crossing: only a step through the
    // doorway counts, not walking around the frame.
    public static class AsciiCrossing
    {
        public const float Margin = 0.1f;

        // +1 into the ASCII side (z becomes >= 0), -1 back out, 0 otherwise. Positions are in the
        // gate's world space: +z through the door, +y up from the floor, x across the opening.
        public static int Crossing(Vector3 before, Vector3 after, float halfWidth, float height)
        {
            var inward = before.z < 0f && after.z >= 0f;
            var outward = before.z >= 0f && after.z < 0f;
            if (!inward && !outward)
                return 0;
            var t = Mathf.Abs(after.z - before.z) > 1e-6f ? -before.z / (after.z - before.z) : 0f;
            var at = Vector3.Lerp(before, after, t);
            var inFrame = Mathf.Abs(at.x) <= halfWidth + Margin && at.y >= -Margin && at.y <= height + Margin;
            if (!inFrame)
                return 0;
            return inward ? 1 : -1;
        }
    }
}
