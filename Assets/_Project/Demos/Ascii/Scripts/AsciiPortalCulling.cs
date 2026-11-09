using System.Collections.Generic;
using UnityEngine;

namespace CardboardXR.Demos.Ascii
{
    // What a camera can see of the ASCII room from outside the gate.
    public enum PortalView
    {
        // Not through the doorway at all: it's off-screen, shut, or seen from behind.
        None,
        // Only what lies in the frustum through the opening.
        Through,
        // Too close to the door plane to narrow the frustum safely: draw it all, the shader clips.
        All,
    }

    // Portal culling, as engines do it: outside the gate the room only shows through the doorway,
    // so each camera narrows its frustum to the eye and the opening's four edges and draws only
    // the room renderers inside that. A camera that can't see the doorway draws none of them and
    // skips the full-screen terminal pass, which would otherwise copy the screen and hand every
    // pixel back unchanged. Ordinary frustum culling still applies on top.
    public static class AsciiPortalCulling
    {
        // Nearer the door plane than this, the side planes are close to degenerate.
        public const float MinEyeDistance = 0.05f;

        static readonly HashSet<Renderer> s_Room = new();
        static readonly List<Renderer> s_Dead = new();
        static readonly HashSet<Camera> s_SkipTerminal = new();
        static readonly Plane[] s_Camera = new Plane[6];
        static readonly Plane[] s_Portal = new Plane[5];

        // Room renderers drawn through the doorway: plane overlays, furniture, meshed chunks.
        public static void Add(Renderer renderer)
        {
            if (renderer != null)
                s_Room.Add(renderer);
        }

        public static void Remove(Renderer renderer)
        {
            if (renderer == null || !s_Room.Remove(renderer))
                return;
            renderer.forceRenderingOff = false;
        }

        public static int Count => s_Room.Count;

        // Read by the terminal feature, after this camera's beginCameraRendering.
        public static bool SkipsTerminal(Camera camera) => camera != null && s_SkipTerminal.Contains(camera);

        // The opening as AsciiWorld sets it: the sill's middle, unit vectors across, up and
        // through it (into the room), half its width, and how far up it's open.
        public struct Door
        {
            public Vector3 center;
            public Vector3 right;
            public Vector3 up;
            public Vector3 normal;
            public float halfWidth;
            public float height;
        }

        // Pure, for testing. On Through, planes holds the four edge planes and the door plane,
        // all facing in, as GeometryUtility.TestPlanesAABB expects.
        public static PortalView View(Vector3 eye, Door door, Plane[] cameraPlanes, Plane[] planes)
        {
            if (door.height <= 1e-3f || door.halfWidth <= 1e-3f)
                return PortalView.None;
            var near = Vector3.Dot(eye - door.center, door.normal);
            // The room is only on the far side, and only seen from in front (AsciiDoorInside).
            if (near >= 0f)
                return PortalView.None;
            var across = door.right * door.halfWidth;
            var up = door.up * door.height;
            var bottomLeft = door.center - across;
            var bottomRight = door.center + across;
            var topLeft = bottomLeft + up;
            var topRight = bottomRight + up;
            if (cameraPlanes != null)
            {
                var opening = new Bounds(bottomLeft, Vector3.zero);
                opening.Encapsulate(bottomRight);
                opening.Encapsulate(topLeft);
                opening.Encapsulate(topRight);
                if (!GeometryUtility.TestPlanesAABB(cameraPlanes, opening))
                    return PortalView.None;
            }
            if (-near < MinEyeDistance)
                return PortalView.All;
            var middle = door.center + up * 0.5f;
            planes[0] = EdgePlane(eye, bottomLeft, bottomRight, middle);
            planes[1] = EdgePlane(eye, bottomRight, topRight, middle);
            planes[2] = EdgePlane(eye, topRight, topLeft, middle);
            planes[3] = EdgePlane(eye, topLeft, bottomLeft, middle);
            planes[4] = new Plane(door.normal, door.center);
            return PortalView.Through;
        }

        // The plane through the eye and one edge of the opening, facing the opening's middle.
        static Plane EdgePlane(Vector3 eye, Vector3 a, Vector3 b, Vector3 inside)
        {
            var plane = new Plane(eye, a, b);
            return plane.GetDistanceToPoint(inside) >= 0f ? plane : plane.flipped;
        }

        // Before each camera culls. Inside the gate, or for scene and preview cameras, everything.
        public static void Apply(Camera camera, bool portalOpen, Door door)
        {
            Prune();
            var game = camera != null && camera.cameraType == CameraType.Game;
            if (!portalOpen || !game)
            {
                s_SkipTerminal.Remove(camera);
                SetAll(false);
                return;
            }
            GeometryUtility.CalculateFrustumPlanes(camera.projectionMatrix * camera.worldToCameraMatrix, s_Camera);
            var view = View(camera.transform.position, door, s_Camera, s_Portal);
            if (view == PortalView.None)
                s_SkipTerminal.Add(camera);
            else
                s_SkipTerminal.Remove(camera);
            switch (view)
            {
                case PortalView.None:
                    SetAll(true);
                    break;
                case PortalView.All:
                    SetAll(false);
                    break;
                default:
                    foreach (var renderer in s_Room)
                        renderer.forceRenderingOff = !GeometryUtility.TestPlanesAABB(s_Portal, renderer.bounds);
                    break;
            }
        }

        // No gate: every renderer draws as its owner set it, and the terminal runs everywhere.
        public static void Reset()
        {
            Prune();
            SetAll(false);
            s_SkipTerminal.Clear();
        }

        static void SetAll(bool off)
        {
            foreach (var renderer in s_Room)
            {
                if (renderer.forceRenderingOff != off)
                    renderer.forceRenderingOff = off;
            }
        }

        static void Prune()
        {
            s_Dead.Clear();
            foreach (var renderer in s_Room)
            {
                if (renderer == null)
                    s_Dead.Add(renderer);
            }
            foreach (var renderer in s_Dead)
                s_Room.Remove(renderer);
        }
    }
}
