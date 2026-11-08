using CardboardXR.Demos.Ascii;
using NUnit.Framework;
using UnityEngine;

namespace CardboardXR.Demos.Ascii.Tests
{
    public class AsciiCrossingTests
    {
        const float k_Half = AsciiWorld.DoorWidth / 2f;
        const float k_Height = AsciiWorld.DoorHeight;

        static int Cross(Vector3 before, Vector3 after) =>
            AsciiCrossing.Crossing(before, after, k_Half, k_Height);

        [Test]
        public void WalkingThroughTheGateGoesInAndBackOut()
        {
            Assert.AreEqual(1, Cross(new Vector3(0f, 1.6f, -0.2f), new Vector3(0f, 1.6f, 0.2f)));
            Assert.AreEqual(-1, Cross(new Vector3(0.1f, 1.6f, 0.2f), new Vector3(0.1f, 1.6f, -0.2f)));
        }

        [Test]
        public void SmallStepThroughTheDoorStillCounts()
        {
            Assert.AreEqual(1, Cross(new Vector3(0f, 1.6f, -0.05f), new Vector3(0f, 1.6f, 0.05f)));
        }

        [Test]
        public void StayingOnOneSideIsNoCrossing()
        {
            Assert.AreEqual(0, Cross(new Vector3(0f, 1.6f, -1f), new Vector3(0f, 1.6f, -0.2f)));
            Assert.AreEqual(0, Cross(new Vector3(0f, 1.6f, 0.2f), new Vector3(0f, 1.6f, 1f)));
            Assert.AreEqual(0, Cross(new Vector3(0f, 1.6f, -0.05f), new Vector3(0f, 1.6f, -0.01f)));
        }

        [Test]
        public void GoingAroundTheFrameIsNoCrossing()
        {
            Assert.AreEqual(0, Cross(new Vector3(0.95f, 1.6f, -0.2f), new Vector3(0.95f, 1.6f, 0.2f)));
            Assert.AreEqual(0, Cross(new Vector3(0f, 2.5f, -0.2f), new Vector3(0f, 2.5f, 0.2f)));
        }
    }
}
