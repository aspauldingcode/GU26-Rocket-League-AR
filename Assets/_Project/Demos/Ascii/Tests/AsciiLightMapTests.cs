using CardboardXR.Demos.Ascii;
using NUnit.Framework;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.ARSubsystems;

namespace CardboardXR.Demos.Ascii.Tests
{
    public class AsciiLightMapTests
    {
        [Test]
        public void CeilingPlanesBecomeDownwardShafts()
        {
            Assert.IsTrue(AsciiLightMap.TryCeiling(PlaneAlignment.HorizontalDown, PlaneClassification.None,
                new Vector3(0f, 2.5f, 0f), Vector3.down, new Vector2(2f, 2f), out var light));
            Assert.AreEqual(AsciiLightKind.Ceiling, light.Kind);
            Assert.Greater(Vector3.Dot(light.Direction, Vector3.down), 0.9f);
            Assert.Less(light.Position.y, 2.5f);
            Assert.IsFalse(AsciiLightMap.TryCeiling(PlaneAlignment.HorizontalUp, PlaneClassification.Floor,
                Vector3.zero, Vector3.up, new Vector2(3f, 3f), out _));
        }

        [Test]
        public void ClassifiedWindowsCastInward()
        {
            Assert.IsTrue(AsciiLightMap.TryWindow(PlaneAlignment.Vertical, PlaneClassification.Window,
                new Vector3(2f, 1.4f, 0f), Vector3.left, new Vector2(1.2f, 1.5f), 0f, out var light));
            Assert.AreEqual(AsciiLightKind.Window, light.Kind);
            Assert.Greater(Vector3.Dot(light.Direction, Vector3.right), 0.9f);
        }

        [Test]
        public void MidHeightOpeningsCountAsWindows()
        {
            Assert.IsTrue(AsciiLightMap.TryWindow(PlaneAlignment.Vertical, PlaneClassification.None,
                new Vector3(0f, 1.5f, 3f), Vector3.back, new Vector2(1f, 1.2f), 0f, out var light));
            Assert.AreEqual(AsciiLightKind.Window, light.Kind);
            Assert.IsFalse(AsciiLightMap.TryWindow(PlaneAlignment.Vertical, PlaneClassification.None,
                new Vector3(0f, 1.2f, 3f), Vector3.back, new Vector2(3f, 2.5f), 0f, out _));
        }

        [Test]
        public void CollectPrefersWindowsThenCeilingsThenFallback()
        {
            var candidates = new List<AsciiLight>
            {
                new()
                {
                    Position = Vector3.up * 2.5f, Direction = Vector3.down, Power = 1f,
                    Kind = AsciiLightKind.Ceiling,
                },
                new()
                {
                    Position = Vector3.right, Direction = Vector3.left, Power = 1f,
                    Kind = AsciiLightKind.Window,
                },
            };
            var into = new List<AsciiLight>();
            AsciiLightMap.Collect(candidates, Vector3.zero, into);
            Assert.AreEqual(2, into.Count);
            Assert.AreEqual(AsciiLightKind.Window, into[0].Kind);
            Assert.AreEqual(AsciiLightKind.Ceiling, into[1].Kind);

            into.Clear();
            AsciiLightMap.Collect(null, Vector3.zero, into);
            Assert.AreEqual(1, into.Count);
            Assert.AreEqual(AsciiLightKind.Fallback, into[0].Kind);
        }

        [Test]
        public void PrimaryDirectionPointsAtStrongestLamp()
        {
            var lights = new List<AsciiLight>
            {
                new()
                {
                    Position = new Vector3(0f, 3f, 0f), Direction = Vector3.down, Power = 0.4f,
                    Kind = AsciiLightKind.Ceiling,
                },
                new()
                {
                    Position = new Vector3(4f, 1.5f, 0f), Direction = Vector3.left, Power = 1f,
                    Kind = AsciiLightKind.Window,
                },
            };
            var dir = AsciiLightMap.PrimaryDirection(lights, Vector3.zero);
            Assert.Greater(Vector3.Dot(dir.normalized, new Vector3(4f, 1.5f, 0f).normalized), 0.95f);
        }

        [Test]
        public void FogRisesDownTheHall()
        {
            var fog = AsciiLightMap.FogParams(2f, 8f, 0.8f, 0.5f);
            Assert.Less(AsciiLightMap.FogAmount(1f, fog), 0.05f);
            Assert.Greater(AsciiLightMap.FogAmount(5f, fog), 0.25f);
            Assert.Greater(AsciiLightMap.FogAmount(8f, fog), AsciiLightMap.FogAmount(5f, fog));
            Assert.AreEqual(0.8f, AsciiLightMap.FogAmount(20f, fog), 1e-3f);
        }

        [Test]
        public void CollectCapsAtMaxLights()
        {
            var candidates = new List<AsciiLight>();
            for (var i = 0; i < 8; i++)
            {
                candidates.Add(new AsciiLight
                {
                    Position = Vector3.up * i, Direction = Vector3.down, Power = 1f,
                    Kind = AsciiLightKind.Ceiling,
                });
            }
            var into = new List<AsciiLight>();
            Assert.AreEqual(AsciiLightMap.MaxLights, AsciiLightMap.Collect(candidates, Vector3.zero, into));
        }
    }
}
