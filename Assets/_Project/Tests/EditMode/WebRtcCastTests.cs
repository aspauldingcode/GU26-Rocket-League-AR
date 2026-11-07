using CardboardXR.Stereo;
using NUnit.Framework;

namespace CardboardXR.Stereo.Tests
{
    public class WebRtcCastTests
    {
        [Test]
        public void RoomCodesAreShortAndUrlSafe()
        {
            for (var i = 0; i < 20; i++)
            {
                var room = WebRtcCast.NewRoomCode();
                Assert.That(room.Length, Is.EqualTo(8));
                Assert.That(room, Does.Match("^[a-z0-9]+$"));
            }
        }

        [Test]
        public void ViewerUrlCarriesTheRoomAndNotHostRole()
        {
            var url = WebRtcCast.ViewerUrl("demo42ab");
            Assert.That(url, Does.Contain("room=demo42ab"));
            Assert.That(url, Does.Not.Contain("role=host"));
            Assert.That(url, Does.Not.Contain("unity=1"));
        }

        [Test]
        public void HostUrlIsAppOnly()
        {
            var url = WebRtcCast.HostUrl("demo42ab");
            Assert.That(url, Does.Contain("role=host"));
            Assert.That(url, Does.Contain("unity=1"));
            Assert.That(url, Does.Contain("room=demo42ab"));
        }

        [Test]
        public void QrTextureIsSquare()
        {
            var texture = WebRtcCast.MakeQr(WebRtcCast.ViewerUrl("abcd1234"), 128);
            Assert.That(texture.width, Is.EqualTo(128));
            Assert.That(texture.height, Is.EqualTo(128));
            UnityEngine.Object.DestroyImmediate(texture);
        }
    }
}
