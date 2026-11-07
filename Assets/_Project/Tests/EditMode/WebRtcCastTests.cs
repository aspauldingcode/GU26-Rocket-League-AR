using CardboardXR.Stereo;
using NUnit.Framework;
using UnityEngine;

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
        public void HostUrlIsAppOnlyAndCacheBusted()
        {
            var url = WebRtcCast.HostUrl("demo42ab");
            Assert.That(url, Does.Contain("role=host"));
            Assert.That(url, Does.Contain("unity=1"));
            Assert.That(url, Does.Contain("room=demo42ab"));
            Assert.That(url, Does.Contain("&t="));
        }

        [Test]
        public void RetryBackoffClimbsThenCaps()
        {
            var a = WebRtcCast.NextRetryMs(1200);
            var b = WebRtcCast.NextRetryMs(a);
            var c = WebRtcCast.NextRetryMs(8000);
            Assert.That(a, Is.GreaterThan(1200));
            Assert.That(b, Is.GreaterThan(a));
            Assert.That(c, Is.EqualTo(8000));
        }

        [Test]
        public void RestartCastIssuesANewRoom()
        {
            var host = new GameObject("Cast Host").AddComponent<WebRtcCastHost>();
            host.StartCast();
            var first = host.Room;
            Assert.That(host.IsCasting, Is.True);
            Assert.That(first, Is.Not.Null.And.Not.Empty);

            host.RestartCast();
            Assert.That(host.IsCasting, Is.True);
            Assert.That(host.Room, Is.Not.EqualTo(first));

            host.StopCast();
            Assert.That(host.IsCasting, Is.False);
            Assert.That(host.Room, Is.Null);
            Object.DestroyImmediate(host.gameObject);
        }

        [Test]
        public void QrTextureIsSquare()
        {
            var texture = WebRtcCast.MakeQr(WebRtcCast.ViewerUrl("abcd1234"), 128);
            Assert.That(texture.width, Is.EqualTo(128));
            Assert.That(texture.height, Is.EqualTo(128));
            Object.DestroyImmediate(texture);
        }
    }
}
