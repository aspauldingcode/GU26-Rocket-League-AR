using CardboardXR.Stereo;
using NUnit.Framework;
using UnityEditor.Events;
using UnityEngine;
using UnityEngine.UI;

namespace CardboardXR.Stereo.Tests
{
    public class ViewerSetupShareTests
    {
        // Stands in for ViewerSetupController: what matters is a method named ShareScreen,
        // which is what the scene's serialized call points at.
        class ShareTarget : MonoBehaviour
        {
            public void ShareScreen() { }
            public void SomethingElse() { }
        }

        // The 3D toolbar Share carries StereoBuilder's serialized onClick call. It must
        // count as already wired, or EnsureToolbarShare adds a runtime listener too and
        // every press runs the toggle twice — starting and cancelling the scan at once.
        [Test]
        public void ASerializedShareCallCountsAsWired()
        {
            var buttonObject = new GameObject("Share", typeof(RectTransform));
            var button = buttonObject.AddComponent<Button>();
            var targetObject = new GameObject("Viewer Setup");
            var target = targetObject.AddComponent<ShareTarget>();

            Assert.IsFalse(ViewerSetupController.HasSerializedShare(button, target));
            UnityEventTools.AddVoidPersistentListener(button.onClick, target.ShareScreen);
            Assert.IsTrue(ViewerSetupController.HasSerializedShare(button, target));

            Object.DestroyImmediate(buttonObject);
            Object.DestroyImmediate(targetObject);
        }

        [Test]
        public void ASerializedCallToAnotherMethodDoesNotCount()
        {
            var buttonObject = new GameObject("Share", typeof(RectTransform));
            var button = buttonObject.AddComponent<Button>();
            var targetObject = new GameObject("Viewer Setup");
            var target = targetObject.AddComponent<ShareTarget>();

            UnityEventTools.AddVoidPersistentListener(button.onClick, target.SomethingElse);
            Assert.IsFalse(ViewerSetupController.HasSerializedShare(button, target));

            Object.DestroyImmediate(buttonObject);
            Object.DestroyImmediate(targetObject);
        }

        // The duplicate press arrives in the same instant; a deliberate toggle-off comes
        // later (and the scan chip has its own Cancel besides).
        [Test]
        public void ASecondPressWithinTheWindowIsTheSamePress()
        {
            Assert.IsTrue(ViewerSetupController.IsShareRepeat(10f, 10f));
            Assert.IsTrue(ViewerSetupController.IsShareRepeat(10f, 10.2f));
            Assert.IsFalse(ViewerSetupController.IsShareRepeat(10f, 11f));
            Assert.IsFalse(ViewerSetupController.IsShareRepeat(float.NegativeInfinity, 10f),
                "the first press ever is never a repeat");
        }
    }
}
