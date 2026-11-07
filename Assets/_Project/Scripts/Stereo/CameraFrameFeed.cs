using Unity.Collections;
using UnityEngine;

namespace CardboardXR.Stereo
{
    // Luminance frames from whatever already holds the rear camera outside an AR session (the 3DoF
    // pinch sensor), for the share QR scan. Opening the AR session there instead took the camera
    // from the pinch sensor, which kept reopening it: on the iPhone neither got frames, so in a 3DoF
    // demo Share never scanned and pinching stopped. Frames are only copied while someone wants them.
    public static class CameraFrameFeed
    {
        const float k_MinInterval = 0.15f;

        static byte[] s_Luminance;
        static int s_Width, s_Height;
        static int s_Version;
        static float s_LastPublish = float.NegativeInfinity;

        // Set by the reader while it wants frames.
        public static bool Wanted { get; set; }

        // Time of the last frame, unscaled.
        public static float LastFrameTime { get; private set; } = float.NegativeInfinity;

        // RGBA bytes, top row first, upright on the screen.
        public static void PublishRgba(NativeArray<byte> rgba, int width, int height)
        {
            if (!Wanted || width <= 0 || height <= 0 || rgba.Length < width * height * 4)
                return;
            var now = Time.unscaledTime;
            if (now - s_LastPublish < k_MinInterval)
                return;
            s_LastPublish = now;
            var size = width * height;
            if (s_Luminance == null || s_Luminance.Length != size)
                s_Luminance = new byte[size];
            for (int i = 0, p = 0; i < size; i++, p += 4)
                s_Luminance[i] = (byte)((rgba[p] * 77 + rgba[p + 1] * 150 + rgba[p + 2] * 29) >> 8);
            s_Width = width;
            s_Height = height;
            s_Version++;
            LastFrameTime = now;
        }

        // A copy of the newest frame if it's newer than lastVersion.
        public static bool TryTake(ref int lastVersion, out byte[] luminance, out int width, out int height)
        {
            luminance = null;
            width = s_Width;
            height = s_Height;
            if (s_Luminance == null || s_Version == lastVersion)
                return false;
            lastVersion = s_Version;
            luminance = (byte[])s_Luminance.Clone();
            return true;
        }
    }
}
