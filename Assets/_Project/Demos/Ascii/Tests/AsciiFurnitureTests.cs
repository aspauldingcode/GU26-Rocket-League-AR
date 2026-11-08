using NUnit.Framework;
using UnityEngine;
using UnityEngine.XR.ARSubsystems;

namespace CardboardXR.Demos.Ascii.Tests
{
    public class AsciiFurnitureTests
    {
        const float k_Floor = 0f;

        [Test]
        public void TablesAndSeatsAreObstacles()
        {
            Assert.IsTrue(AsciiFurniture.IsObstacle(PlaneAlignment.HorizontalUp, PlaneClassification.Table, 0.75f, k_Floor, new Vector2(1.2f, 0.8f)));
            Assert.IsTrue(AsciiFurniture.IsObstacle(PlaneAlignment.HorizontalUp, PlaneClassification.Seat, 0.45f, k_Floor, new Vector2(0.4f, 0.4f)));
        }

        [Test]
        public void RaisedHorizontalSurfacesAreObstacles()
        {
            Assert.IsTrue(AsciiFurniture.IsObstacle(PlaneAlignment.HorizontalUp, PlaneClassification.None, 0.7f, k_Floor, new Vector2(1f, 0.6f)));
            Assert.IsFalse(AsciiFurniture.IsObstacle(PlaneAlignment.HorizontalUp, PlaneClassification.None, 0.05f, k_Floor, new Vector2(3f, 3f)));
            Assert.IsFalse(AsciiFurniture.IsObstacle(PlaneAlignment.HorizontalUp, PlaneClassification.None, 0.4f, k_Floor, new Vector2(0.1f, 0.1f)));
        }

        [Test]
        public void FloorWallsAndCeilingAreNotObstacles()
        {
            Assert.IsFalse(AsciiFurniture.IsObstacle(PlaneAlignment.HorizontalUp, PlaneClassification.Floor, 0f, k_Floor, new Vector2(4f, 4f)));
            Assert.IsFalse(AsciiFurniture.IsObstacle(PlaneAlignment.Vertical, PlaneClassification.Wall, 1f, k_Floor, new Vector2(3f, 2.4f)));
            Assert.IsFalse(AsciiFurniture.IsObstacle(PlaneAlignment.HorizontalDown, PlaneClassification.Ceiling, 2.5f, k_Floor, new Vector2(4f, 4f)));
            Assert.IsFalse(AsciiFurniture.IsObstacle(PlaneAlignment.Vertical, PlaneClassification.None, 1.2f, k_Floor, new Vector2(2f, 2.4f)));
        }

        [Test]
        public void ChairBacksAreObstaclesAndWallsAreNot()
        {
            Assert.IsTrue(AsciiFurniture.IsObstacle(PlaneAlignment.Vertical, PlaneClassification.None, 0.6f, k_Floor, new Vector2(0.5f, 0.9f)));
            Assert.IsFalse(AsciiFurniture.IsObstacle(PlaneAlignment.Vertical, PlaneClassification.None, 1.4f, k_Floor, new Vector2(3f, 2.5f)));
        }

        [Test]
        public void MeshChunksSplitFurnitureFromWallsAndFloors()
        {
            Assert.IsTrue(AsciiFurniture.MeshChunkIsFurniture(new Vector3(0.5f, 1f, 0.5f), 0f, k_Floor));
            Assert.IsTrue(AsciiFurniture.MeshChunkIsFurniture(new Vector3(1.2f, 0.75f, 0.8f), 0f, k_Floor));
            Assert.IsFalse(AsciiFurniture.MeshChunkIsFurniture(new Vector3(3f, 2.4f, 0.1f), 0f, k_Floor));
            Assert.IsFalse(AsciiFurniture.MeshChunkIsFurniture(new Vector3(3f, 0.05f, 3f), 0f, k_Floor));
            Assert.IsFalse(AsciiFurniture.MeshChunkIsFurniture(new Vector3(4f, 2.5f, 4f), 0f, k_Floor));
        }

        [Test]
        public void PrismReachesTheFloor()
        {
            Assert.AreEqual(0.75f, AsciiFurniture.PrismDrop(0.75f, k_Floor), 1e-4f);
            Assert.Greater(AsciiFurniture.PrismDrop(k_Floor, k_Floor), 0f);
        }

        [Test]
        public void FloorIsTheLowestLargePlane()
        {
            float? floor = null;
            floor = AsciiFurniture.FloorY(floor, PlaneAlignment.HorizontalUp, PlaneClassification.None, 0.72f, 1.2f);
            floor = AsciiFurniture.FloorY(floor, PlaneAlignment.HorizontalUp, PlaneClassification.None, 0.02f, 4f);
            floor = AsciiFurniture.FloorY(floor, PlaneAlignment.HorizontalUp, PlaneClassification.Table, 0.75f, 2f);
            Assert.AreEqual(0.02f, floor.Value, 1e-4f);
        }
    }
}
