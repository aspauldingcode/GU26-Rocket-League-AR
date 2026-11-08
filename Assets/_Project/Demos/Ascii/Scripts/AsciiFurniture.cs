using System.Collections.Generic;
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
            // Specks from a noisy scan are not something you walk into.
            if (size.x * size.y < 0.04f)
                return false;
            if (alignment == PlaneAlignment.HorizontalUp)
                return height > floorY + RaisedMeters;
            // A short vertical plane is a chair back, a table apron, or clutter. A tall one is a wall.
            if (alignment == PlaneAlignment.Vertical)
                return size.y < 1.35f && size.y > 0.15f && size.x < 2.2f && height < floorY + 1.6f;
            return false;
        }

        // How far a horizontal obstacle should extend below its top, down to the floor.
        public static float PrismDrop(float planeY, float floorY) => Mathf.Max(0.04f, planeY - floorY);

        // A mesh chunk that is not a floor slab and not a thin tall wall is furniture: the body of
        // a chair or table, or loose clutter. Walls and the floor stay part of the room.
        public static bool MeshChunkIsFurniture(Vector3 size, float minY, float floorY)
        {
            var thin = Mathf.Min(size.x, size.z);
            var wide = Mathf.Max(size.x, size.z);
            if (size.y > 1.5f && wide > 1.2f && thin < 0.45f)
                return false;
            // One mesh of the whole room, not a single chair or table.
            if (size.x > 2f && size.z > 2f && size.y > 1.5f)
                return false;
            if (size.y < 0.3f && minY < floorY + 0.25f && size.x * size.z > 0.8f)
                return false;
            if (minY > floorY + 2.2f)
                return false;
            return true;
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

        // A solid from the plane's outline. topY and bottomY are in plane space (local Y is the
        // normal). A table uses the top at the surface and the bottom on the floor. A chair back
        // uses a thin slab straddling the plane.
        public static Mesh BuildPrism(IReadOnlyList<Vector2> boundary, float topY, float bottomY)
        {
            var count = boundary.Count;
            if (count < 3)
                return null;
            var vertices = new Vector3[count * 2];
            for (var i = 0; i < count; i++)
            {
                var point = boundary[i];
                vertices[i] = new Vector3(point.x, topY, point.y);
                vertices[count + i] = new Vector3(point.x, bottomY, point.y);
            }
            var triangles = new List<int>((count - 2) * 6 + count * 12);
            for (var i = 1; i < count - 1; i++)
            {
                triangles.Add(0);
                triangles.Add(i);
                triangles.Add(i + 1);
                triangles.Add(count);
                triangles.Add(count + i + 1);
                triangles.Add(count + i);
            }
            for (var i = 0; i < count; i++)
            {
                var next = (i + 1) % count;
                var topA = i;
                var topB = next;
                var botA = count + i;
                var botB = count + next;
                triangles.Add(topA);
                triangles.Add(botA);
                triangles.Add(topB);
                triangles.Add(topB);
                triangles.Add(botA);
                triangles.Add(botB);
                triangles.Add(topA);
                triangles.Add(topB);
                triangles.Add(botA);
                triangles.Add(topB);
                triangles.Add(botB);
                triangles.Add(botA);
            }
            var mesh = new Mesh { name = "Ascii Prism" };
            mesh.SetVertices(vertices);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }
    }
}
