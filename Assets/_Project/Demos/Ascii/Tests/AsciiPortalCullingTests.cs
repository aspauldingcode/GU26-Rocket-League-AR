using NUnit.Framework;
using UnityEngine;

namespace CardboardXR.Demos.Ascii.Tests
{
    public class AsciiPortalCullingTests
    {
        // A 1 m by 2.1 m opening at the origin; the room lies toward +z.
        static readonly AsciiPortalCulling.Door k_Door = new()
        {
            center = Vector3.zero,
            right = Vector3.right,
            up = Vector3.up,
            normal = Vector3.forward,
            halfWidth = AsciiWorld.DoorWidth / 2f,
            height = AsciiWorld.DoorHeight,
        };

        static readonly Vector3 k_Eye = new(0f, 1.6f, -2f);

        static Plane[] Planes() => new Plane[5];

        static bool Seen(Plane[] planes, Vector3 at) =>
            GeometryUtility.TestPlanesAABB(planes, new Bounds(at, Vector3.one * 0.2f));

        static Plane[] LookingAt(Vector3 eye, Vector3 target)
        {
            var view = Matrix4x4.TRS(eye, Quaternion.LookRotation(target - eye), new Vector3(1f, 1f, -1f)).inverse;
            var planes = new Plane[6];
            GeometryUtility.CalculateFrustumPlanes(Matrix4x4.Perspective(60f, 1f, 0.05f, 50f) * view, planes);
            return planes;
        }

        [Test]
        public void RoomStraightThroughTheDoorwayIsDrawn()
        {
            var planes = Planes();
            Assert.AreEqual(PortalView.Through, AsciiPortalCulling.View(k_Eye, k_Door, null, planes));
            Assert.IsTrue(Seen(planes, new Vector3(0f, 1.2f, 3f)));
        }

        [Test]
        public void RoomBehindTheWallBesideTheDoorIsCulled()
        {
            var planes = Planes();
            AsciiPortalCulling.View(k_Eye, k_Door, null, planes);
            Assert.IsFalse(Seen(planes, new Vector3(3f, 1.2f, 1f)), "beside the opening");
            Assert.IsFalse(Seen(planes, new Vector3(0f, 1.2f, -1f)), "on the near side of the door");
            Assert.IsFalse(Seen(planes, new Vector3(0f, 6f, 1f)), "above the lintel");
        }

        [Test]
        public void NothingIsSeenFromBehindTheGateOrThroughAShutDoor()
        {
            Assert.AreEqual(PortalView.None, AsciiPortalCulling.View(new Vector3(0f, 1.6f, 2f), k_Door, null, Planes()));
            var shut = k_Door;
            shut.height = 0f;
            Assert.AreEqual(PortalView.None, AsciiPortalCulling.View(k_Eye, shut, null, Planes()));
        }

        [Test]
        public void ADoorwayOutOfViewSkipsTheRoom()
        {
            var away = LookingAt(k_Eye, k_Eye + Vector3.back);
            Assert.AreEqual(PortalView.None, AsciiPortalCulling.View(k_Eye, k_Door, away, Planes()));
            var toward = LookingAt(k_Eye, Vector3.up);
            Assert.AreEqual(PortalView.Through, AsciiPortalCulling.View(k_Eye, k_Door, toward, Planes()));
        }

        [Test]
        public void RightAtTheDoorPlaneEverythingDraws()
        {
            var eye = new Vector3(0f, 1.6f, -0.01f);
            Assert.AreEqual(PortalView.All, AsciiPortalCulling.View(eye, k_Door, null, Planes()));
        }
    }
}
