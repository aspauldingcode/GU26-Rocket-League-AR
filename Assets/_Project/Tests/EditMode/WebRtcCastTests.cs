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
            var a = WebRtcCast.NextRetryMs(400);
            var b = WebRtcCast.NextRetryMs(a);
            var c = WebRtcCast.NextRetryMs(3000);
            Assert.That(a, Is.GreaterThan(400));
            Assert.That(b, Is.GreaterThan(a));
            Assert.That(c, Is.EqualTo(3000));
        }

        [Test]
        public void HostPageRetriesOnlyWhileCastingAndNotReady()
        {
            Assert.IsTrue(WebRtcCastHost.ShouldRetryHostPage(true, false, 10f, 10f));
            Assert.IsTrue(WebRtcCastHost.ShouldRetryHostPage(true, false, 11f, 10f));
            Assert.IsFalse(WebRtcCastHost.ShouldRetryHostPage(true, false, 9f, 10f),
                "still inside the page's load grace period");
            Assert.IsFalse(WebRtcCastHost.ShouldRetryHostPage(true, true, 11f, 10f),
                "a ready page owns its own relay reconnection");
            Assert.IsFalse(WebRtcCastHost.ShouldRetryHostPage(false, false, 11f, 10f),
                "not casting: nothing to recover");
        }

        [Test]
        public void NetworkReturnIsOnlyFromUnreachable()
        {
            Assert.IsTrue(WebRtcCastHost.NetworkReturned(
                NetworkReachability.NotReachable, NetworkReachability.ReachableViaLocalAreaNetwork));
            Assert.IsTrue(WebRtcCastHost.NetworkReturned(
                NetworkReachability.NotReachable, NetworkReachability.ReachableViaCarrierDataNetwork));
            Assert.IsFalse(WebRtcCastHost.NetworkReturned(
                NetworkReachability.ReachableViaLocalAreaNetwork, NetworkReachability.ReachableViaCarrierDataNetwork),
                "switching networks is not a return from offline");
            Assert.IsFalse(WebRtcCastHost.NetworkReturned(
                NetworkReachability.ReachableViaLocalAreaNetwork, NetworkReachability.NotReachable),
                "losing the network is not its return");
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
        public void TryParseRoomReadsLaptopQrUrls()
        {
            Assert.That(WebRtcCast.TryParseRoom("Abcd1234", out var bare), Is.True);
            Assert.That(bare, Is.EqualTo("abcd1234"));

            var url = WebRtcCast.ViewerUrl("demo42ab");
            Assert.That(WebRtcCast.TryParseRoom(url, out var fromUrl), Is.True);
            Assert.That(fromUrl, Is.EqualTo("demo42ab"));

            Assert.That(WebRtcCast.TryParseRoom("https://example.com/", out _), Is.False);
        }

        [Test]
        public void TryParseRoomToleratesMessyPhoneCameraReads()
        {
            Assert.That(WebRtcCast.TryParseRoom(
                "https://aspauldingcode.github.io/GU26-Rocket-League-AR/?room=demo42ab&aspect=1.778\n",
                out var trimmed), Is.True);
            Assert.That(trimmed, Is.EqualTo("demo42ab"));

            Assert.That(WebRtcCast.TryParseRoom(
                "aspauldingcode.github.io/GU26-Rocket-League-AR/?room=messy01x",
                out var schemeless), Is.True);
            Assert.That(schemeless, Is.EqualTo("messy01x"));

            Assert.That(WebRtcCast.TryParseRoom("noise room=token99 junk", out var token), Is.True);
            Assert.That(token, Is.EqualTo("token99"));
        }

        [Test]
        public void QrLuminanceDownsampleKeepsAspectAndShrinks()
        {
            var source = new byte[1920 * 1080];
            for (var i = 0; i < source.Length; i++)
                source[i] = (byte)(i % 251);
            var down = ViewerQrScanner.DownsampleLuminance(source, 1920, 1080, out var w, out var h, 960);
            Assert.That(down, Is.Not.Null);
            Assert.That(w, Is.EqualTo(960));
            Assert.That(h, Is.EqualTo(540));
            Assert.That(down.Length, Is.EqualTo(w * h));

            var same = ViewerQrScanner.DownsampleLuminance(source, 640, 360, out var sw, out var sh, 960);
            Assert.That(sw, Is.EqualTo(640));
            Assert.That(sh, Is.EqualTo(360));
            Assert.That(same.Length, Is.EqualTo(640 * 360));
        }

        [Test]
        public void StartCastCanJoinAnExistingRoom()
        {
            var host = new GameObject("Cast Host").AddComponent<WebRtcCastHost>();
            host.StartCast("laptop01");
            Assert.That(host.Room, Is.EqualTo("laptop01"));
            host.StopCast();
            Object.DestroyImmediate(host.gameObject);
        }

        [Test]
        public void DefaultCaptureIsSixteenByNine()
        {
            Assert.That(WebRtcCastHost.CaptureWidth / (float)WebRtcCastHost.CaptureHeight, Is.EqualTo(16f / 9f).Within(0.001f));
            Assert.That(WebRtcCastHost.CaptureAspect, Is.EqualTo(16f / 9f).Within(0.001f));
        }

        [Test]
        public void TryParseCastReadsTheWebsitesAspect()
        {
            Assert.That(WebRtcCast.TryParseCast(WebRtcCast.PagesBaseUrl + "?room=abcd1234&aspect=1.600", out var room, out var aspect), Is.True);
            Assert.That(room, Is.EqualTo("abcd1234"));
            Assert.That(aspect, Is.EqualTo(1.6f).Within(0.001f));

            Assert.That(WebRtcCast.TryParseCast(WebRtcCast.ViewerUrl("abcd1234"), out _, out var missing), Is.True);
            Assert.That(missing, Is.EqualTo(16f / 9f).Within(0.001f));

            Assert.That(WebRtcCast.TryParseCast(WebRtcCast.PagesBaseUrl + "?room=abcd1234&aspect=9", out _, out var wide), Is.True);
            Assert.That(wide, Is.EqualTo(WebRtcCast.MaxAspect));
            Assert.That(WebRtcCast.TryParseCast(WebRtcCast.PagesBaseUrl + "?room=abcd1234&aspect=nope", out _, out var bad), Is.True);
            Assert.That(bad, Is.EqualTo(16f / 9f).Within(0.001f));
        }

        [Test]
        public void CastFrameFollowsTheWebsitesAspect()
        {
            Assert.That(WebRtcCastHost.FrameSizeFor(16f / 9f), Is.EqualTo(new Vector2Int(1280, 720)));
            Assert.That(WebRtcCastHost.FrameSizeFor(1.6f), Is.EqualTo(new Vector2Int(1152, 720)));

            var host = new GameObject("Cast Host").AddComponent<WebRtcCastHost>();
            host.StartCast("aspect01", 1.6f);
            Assert.That(host.FrameSize, Is.EqualTo(new Vector2Int(1152, 720)));
            host.RestartCast("aspect02");
            Assert.That(host.FrameSize, Is.EqualTo(new Vector2Int(1152, 720)));
            host.StopCast();
            host.StartCast("aspect03");
            Assert.That(host.FrameSize, Is.EqualTo(new Vector2Int(1280, 720)));
            host.StopCast();
            Object.DestroyImmediate(host.gameObject);
        }

        [Test]
        public void SourceLevelsGetSmallerAndStayMacroblockAligned()
        {
            for (var level = 1; level < WebRtcCastHost.k_SourceLevelCount; level++)
                Assert.That(WebRtcCastHost.SourceHeight(level), Is.LessThan(WebRtcCastHost.SourceHeight(level - 1)));
            for (var level = 0; level < WebRtcCastHost.k_SourceLevelCount; level++)
            {
                var size = WebRtcCastHost.FrameSizeFor(1.6f, level);
                Assert.That(size.x % 16, Is.Zero);
                Assert.That(size.y % 16, Is.Zero);
            }
            Assert.That(WebRtcCastHost.FrameSizeFor(16f / 9f, WebRtcCastHost.k_SourceLevelCount - 1),
                Is.EqualTo(new Vector2Int(768, 432)));
        }

        [Test]
        public void SlowPhoneFramesStepTheSourceDownAndHealthyOnesBackUp()
        {
            Assert.That(WebRtcCastHost.NextSourceLevel(0, 29, 0), Is.EqualTo(0), "a short hitch holds the source");
            Assert.That(WebRtcCastHost.NextSourceLevel(0, 30, 0), Is.EqualTo(1));
            Assert.That(WebRtcCastHost.NextSourceLevel(WebRtcCastHost.k_SourceLevelCount - 1, 100, 0),
                Is.EqualTo(WebRtcCastHost.k_SourceLevelCount - 1), "already the smallest source");
            Assert.That(WebRtcCastHost.NextSourceLevel(2, 0, 149), Is.EqualTo(2),
                "healthy frames must be sustained before stepping up");
            Assert.That(WebRtcCastHost.NextSourceLevel(2, 0, 150), Is.EqualTo(1));
            Assert.That(WebRtcCastHost.NextSourceLevel(0, 0, 500), Is.EqualTo(0), "already the full source");
        }

        [Test]
        public void StartCastDoesNotFlipPhoneDisplayToMono()
        {
            var head = new GameObject("Head");
            var rig = head.AddComponent<StereoRig>();
            // Default IsMono is false (stereo). Share must leave that alone.
            Assert.That(rig.IsMono, Is.False);

            var host = new GameObject("Cast Host").AddComponent<WebRtcCastHost>();
            host.Configure(rig);
            host.StartCast("stereo01");

            Assert.That(host.IsCasting, Is.True);
            Assert.That(rig.IsMono, Is.False, "casting must not call SetMono(true)");
            Assert.That(host.DisplayModeUnchanged, Is.True);

            host.StopCast();
            Assert.That(rig.IsMono, Is.False);
            Object.DestroyImmediate(host.gameObject);
            Object.DestroyImmediate(head);
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
