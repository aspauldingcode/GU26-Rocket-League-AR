using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using ZXing;
using ZXing.Common;
using ZXing.QrCode;

namespace CardboardXR.Stereo
{
    // Private demo cast: the phone publishes a mono UI view to a GitHub Pages watcher over WebRTC.
    // The Pages site only watches; room codes stay in the QR so the open internet does not get a
    // public AR stream, and the site never syncs an ordinary phone camera.
    public static class WebRtcCast
    {
        public const string PagesBaseUrl = "https://aspauldingcode.github.io/GU26-Rocket-League-AR/";

        // Short room id for the QR and PeerJS host id.
        public static string NewRoomCode()
        {
            const string alphabet = "abcdefghijklmnopqrstuvwxyz0123456789";
            var bytes = new byte[8];
            var rng = new System.Random();
            rng.NextBytes(bytes);
            var text = new StringBuilder(8);
            for (var i = 0; i < bytes.Length; i++)
                text.Append(alphabet[bytes[i] % alphabet.Length]);
            return text.ToString();
        }

        public static string ViewerUrl(string room) =>
            $"{PagesBaseUrl}?room={Uri.EscapeDataString(room)}";

        // Cache-bust so a restarted WebView does not keep a dead PeerJS page around.
        public static string HostUrl(string room) =>
            $"{PagesBaseUrl}?role=host&unity=1&room={Uri.EscapeDataString(room)}&t={DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";

        // Matches webrtc.js nextRetryMs: backoff for reconnect messaging / host rebind.
        public static int NextRetryMs(int current, int startMs = 1200, int maxMs = 8000)
        {
            var next = (int)(Math.Max(current, 1) * 1.6);
            if (next < startMs) next = startMs;
            if (next > maxMs) next = maxMs;
            return next;
        }

        // Laptop page QR, or a bare room code typed/scanned from the site.
        public static bool TryParseRoom(string text, out string room)
        {
            room = null;
            if (string.IsNullOrWhiteSpace(text))
                return false;
            text = text.Trim();
            if (System.Text.RegularExpressions.Regex.IsMatch(text, @"^[a-zA-Z0-9]{4,12}$"))
            {
                room = text.ToLowerInvariant();
                return true;
            }
            if (!Uri.TryCreate(text, UriKind.Absolute, out var uri))
                return false;
            var query = uri.Query;
            if (string.IsNullOrEmpty(query))
                return false;
            foreach (var part in query.TrimStart('?').Split('&'))
            {
                var kv = part.Split(new[] { '=' }, 2);
                if (kv.Length != 2 || !kv[0].Equals("room", StringComparison.OrdinalIgnoreCase))
                    continue;
                var value = Uri.UnescapeDataString(kv[1]).Trim().ToLowerInvariant();
                if (!System.Text.RegularExpressions.Regex.IsMatch(value, @"^[a-z0-9]{4,12}$"))
                    return false;
                room = value;
                return true;
            }
            return false;
        }

        // Black modules on white, for a RawImage in the Material cast panel.
        public static Texture2D MakeQr(string text, int size = 256)
        {
            var hints = new Dictionary<EncodeHintType, object>
            {
                [EncodeHintType.CHARACTER_SET] = "UTF-8",
                [EncodeHintType.MARGIN] = 1,
                [EncodeHintType.ERROR_CORRECTION] = ZXing.QrCode.Internal.ErrorCorrectionLevel.M,
            };
            var matrix = new QRCodeWriter().encode(text, BarcodeFormat.QR_CODE, size, size, hints);
            var texture = new Texture2D(matrix.Width, matrix.Height, TextureFormat.RGBA32, false)
            {
                name = "Cast QR",
                filterMode = FilterMode.Point,
            };
            var colors = new Color32[matrix.Width * matrix.Height];
            var black = new Color32(0, 0, 0, 255);
            var white = new Color32(255, 255, 255, 255);
            for (var y = 0; y < matrix.Height; y++)
            {
                // Texture rows are bottom-up; flip so the QR is upright in UI.
                var row = (matrix.Height - 1 - y) * matrix.Width;
                for (var x = 0; x < matrix.Width; x++)
                    colors[row + x] = matrix[x, y] ? black : white;
            }
            texture.SetPixels32(colors);
            texture.Apply(false, true);
            return texture;
        }
    }
}
