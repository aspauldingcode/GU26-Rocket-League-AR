using UnityEngine;
using UnityEngine.XR.ARSubsystems;

namespace CardboardXR.Demos.Ascii
{
    // What counts as something to trip over. Tables and seats from the platform, plus any
    // horizontal surface raised off the floor. Walls, the floor and the ceiling stay room-colored.
    public static class AsciiFurniture
    {
        public const float RaisedMeters = 0.15f;
        public const float MinFloorArea = 0.8f;

        public static bool IsObstacle(PlaneAlignment alignment, PlaneClassification classification,
            float height, float floorY, Vector2 size)
        {
            if (classification == PlaneClassification.Table || classification == PlaneClassification.Seat)
                return true;
            if (classification is PlaneClassification.Floor or PlaneClassification.Wall
                or PlaneClassification.Ceiling or PlaneClassification.Door or PlaneClassification.Window)
                return false;
            if (alignment != PlaneAlignment.HorizontalUp)
                return false;
            // Specks from a noisy scan are not something you walk into.
            if (size.x * size.y < 0.04f)
                return false;
            return height > floorY + RaisedMeters;
        }

        // Lowest large upward plane, or an explicit floor classification. Null when nothing qualifies.
        public static float? FloorY(float? current, PlaneAlignment alignment, PlaneClassification classification,
            float height, float area)
        {
            if (classification == PlaneClassification.Floor)
                return current.HasValue ? Mathf.Min(current.Value, height) : height;
            if (alignment != PlaneAlignment.HorizontalUp || area < MinFloorArea)
                return current;
            if (classification is PlaneClassification.Table or PlaneClassification.Seat)
                return current;
            return current.HasValue ? Mathf.Min(current.Value, height) : height;
        }
    }
}
