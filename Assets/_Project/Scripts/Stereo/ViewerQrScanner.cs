using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unity.Collections;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;
using ZXing;
using ZXing.Common;

namespace CardboardXR.Stereo
{
    // Looks for a QR code in AR camera images and reports its text. Decodes luminance on a
    // worker thread a few times a second. On Android the sensor buffer is often rotated relative
    // to the screen, so AutoRotate is on; Y-plane copies also honor pixelStride (ARCore can be >1).
    // When the AR session is "on" but not delivering CPU frames, CameraFrameFeed (pinch camera)
    // is used as a fallback so Share scanning still works in 3DoF and broken-AR cases.
    public class ViewerQrScanner : MonoBehaviour
    {
        [SerializeField] ARCameraManager m_CameraManager;
        [SerializeField] float m_ScanInterval = 0.2f;
        [SerializeField] int m_PreviewWidth = 640;
        // Decode at most this many pixels on the long edge — full 4K frames stall Android.
        [SerializeField] int m_DecodeMaxEdge = 960;

        // Only touched by one decode task at a time.
        readonly BarcodeReaderGeneric m_Reader = new()
        {
            AutoRotate = true,
            Options = new DecodingOptions
            {
                PossibleFormats = new List<BarcodeFormat> { BarcodeFormat.QR_CODE },
                TryHarder = true,
                // PureBarcode helps when the laptop screen fills most of the frame.
                PureBarcode = false,
            },
        };

        Task<string> m_Decode;
        float m_NextScan;
        byte[] m_Luminance;
        byte[] m_ConvertBuffer;
        Texture2D m_Preview;
        bool m_LoggedArFail;
        bool m_LoggedFeed;

        public event Action<string> CodeFound;

        int m_FeedVersion;

        public Texture Preview => m_Preview;

        // Also read CameraFrameFeed when the AR camera gives no images, as in 3DoF, where the pinch
        // sensor holds the camera. Share turns this on whenever AR CPU frames go quiet.
        public bool UseFeed { get; set; }

        // When the last camera image, from either source, was looked at.
        public float LastImageTime { get; private set; } = float.NegativeInfinity;

        // True while the last successful image came from CameraFrameFeed rather than AR.
        public bool LastImageFromFeed { get; private set; }

        void OnEnable()
        {
            LastImageTime = Time.unscaledTime;
            m_LoggedArFail = false;
            m_LoggedFeed = false;
        }

        // A decode still running when scanning stops finishes on its own; its result is dropped.
        void OnDisable()
        {
            m_Decode = null;
            CameraFrameFeed.Wanted = false;
        }

        void OnDestroy()
        {
            if (m_Preview != null)
                Destroy(m_Preview);
        }

        void Update()
        {
            if (m_Decode != null && m_Decode.IsCompleted)
            {
                var text = m_Decode.Status == TaskStatus.RanToCompletion ? m_Decode.Result : null;
                m_Decode = null;
                if (!string.IsNullOrEmpty(text))
                    CodeFound?.Invoke(text);
            }

            if (TryScanAr())
                return;

            // AR is enabled but quiet, or 3DoF: take the pinch camera's frames when asked.
            CameraFrameFeed.Wanted = UseFeed;
            if (!UseFeed || m_Decode != null || Time.unscaledTime < m_NextScan)
                return;
            if (!CameraFrameFeed.TryTake(ref m_FeedVersion, out var luminance, out var feedWidth, out var feedHeight))
                return;

            LastImageTime = Time.unscaledTime;
            LastImageFromFeed = true;
            m_NextScan = Time.unscaledTime + m_ScanInterval;
            if (!m_LoggedFeed)
            {
                m_LoggedFeed = true;
                Debug.Log($"[Cast] QR scan reading the pinch camera ({feedWidth}x{feedHeight})");
            }
            var copy = DownsampleLuminance(luminance, feedWidth, feedHeight, out var w, out var h);
            m_Decode = Task.Run(() => Decode(copy, w, h));
        }

        bool TryScanAr()
        {
            if (m_CameraManager == null || !m_CameraManager.enabled)
                return false;
            if (!m_CameraManager.TryAcquireLatestCpuImage(out var image))
            {
                if (!m_LoggedArFail && Time.unscaledTime - LastImageTime > 1f)
                {
                    m_LoggedArFail = true;
                    Debug.LogWarning("[Cast] AR camera has no CPU image for QR; will use the pinch feed if enabled");
                }
                return false;
            }

            CameraFrameFeed.Wanted = false;
            using (image)
            {
                LastImageTime = Time.unscaledTime;
                LastImageFromFeed = false;
                UpdatePreview(image);
                if (m_Decode != null || Time.unscaledTime < m_NextScan)
                    return true;

                m_NextScan = Time.unscaledTime + m_ScanInterval;
                if (!TryCopyForDecode(image, out var luminance, out var width, out var height))
                    return true;
                m_Decode = Task.Run(() => Decode(luminance, width, height));
            }
            return true;
        }

        string Decode(byte[] luminance, int width, int height)
        {
            if (luminance == null || width <= 0 || height <= 0)
                return null;
            var source = new PlanarYUVLuminanceSource(luminance, width, height, 0, 0, width, height, false);
            return m_Reader.Decode(source)?.Text;
        }

        // Convert with MirrorX so the buffer matches what the user sees (and the preview), then
        // downsample. Falls back to a stride-aware Y-plane copy if Convert fails.
        bool TryCopyForDecode(XRCpuImage image, out byte[] luminance, out int width, out int height)
        {
            luminance = null;
            width = height = 0;
            var longEdge = Mathf.Max(image.width, image.height);
            var scale = longEdge > m_DecodeMaxEdge ? m_DecodeMaxEdge / (float)longEdge : 1f;
            var outW = Mathf.Max(8, Mathf.RoundToInt(image.width * scale) / 8 * 8);
            var outH = Mathf.Max(8, Mathf.RoundToInt(image.height * scale) / 8 * 8);
            var conversion = new XRCpuImage.ConversionParams
            {
                inputRect = new RectInt(0, 0, image.width, image.height),
                outputDimensions = new Vector2Int(outW, outH),
                outputFormat = TextureFormat.R8,
                transformation = XRCpuImage.Transformation.MirrorX,
            };
            var byteCount = outW * outH;
            if (m_ConvertBuffer == null || m_ConvertBuffer.Length != byteCount)
                m_ConvertBuffer = new byte[byteCount];
            try
            {
                using var native = new NativeArray<byte>(byteCount, Allocator.Temp);
                image.Convert(conversion, native);
                native.CopyTo(m_ConvertBuffer);
                luminance = (byte[])m_ConvertBuffer.Clone();
                width = outW;
                height = outH;
                return true;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Cast] AR image Convert for QR failed ({e.Message}); using Y plane");
            }

            CopyLuminance(image);
            var down = DownsampleLuminance(m_Luminance, image.width, image.height, out width, out height);
            luminance = down;
            return luminance != null;
        }

        // Plane 0 of ARCore and ARKit camera images is 8-bit luminance. Honor rowStride and
        // pixelStride — some Android builds pack Y with pixelStride 2.
        void CopyLuminance(XRCpuImage image)
        {
            var plane = image.GetPlane(0);
            var size = image.width * image.height;
            if (m_Luminance == null || m_Luminance.Length != size)
                m_Luminance = new byte[size];

            if (plane.pixelStride <= 1)
            {
                for (var row = 0; row < image.height; row++)
                    NativeArray<byte>.Copy(plane.data, row * plane.rowStride, m_Luminance, row * image.width, image.width);
                return;
            }

            for (var row = 0; row < image.height; row++)
            {
                var src = row * plane.rowStride;
                var dst = row * image.width;
                for (var col = 0; col < image.width; col++)
                    m_Luminance[dst + col] = plane.data[src + col * plane.pixelStride];
            }
        }

        // Pure helper: shrink a luminance buffer so ZXing stays under a few ms on phones.
        public static byte[] DownsampleLuminance(byte[] source, int width, int height, out int outWidth, out int outHeight,
            int maxEdge = 960)
        {
            outWidth = width;
            outHeight = height;
            if (source == null || width <= 0 || height <= 0)
                return null;
            var longEdge = Mathf.Max(width, height);
            if (longEdge <= maxEdge)
            {
                var clone = new byte[width * height];
                Buffer.BlockCopy(source, 0, clone, 0, clone.Length);
                return clone;
            }

            var scale = maxEdge / (float)longEdge;
            outWidth = Mathf.Max(8, Mathf.RoundToInt(width * scale));
            outHeight = Mathf.Max(8, Mathf.RoundToInt(height * scale));
            var dest = new byte[outWidth * outHeight];
            for (var y = 0; y < outHeight; y++)
            {
                var srcY = Math.Min(height - 1, y * height / outHeight);
                for (var x = 0; x < outWidth; x++)
                {
                    var srcX = Math.Min(width - 1, x * width / outWidth);
                    dest[y * outWidth + x] = source[srcY * width + srcX];
                }
            }
            return dest;
        }

        void UpdatePreview(XRCpuImage image)
        {
            var divisor = Mathf.Max(1, Mathf.CeilToInt(image.width / (float)m_PreviewWidth));
            var conversion = new XRCpuImage.ConversionParams
            {
                inputRect = new RectInt(0, 0, image.width, image.height),
                outputDimensions = new Vector2Int(image.width / divisor, image.height / divisor),
                outputFormat = TextureFormat.RGBA32,
                // CPU images start at the top row; textures start at the bottom.
                transformation = XRCpuImage.Transformation.MirrorX,
            };

            var size = conversion.outputDimensions;
            if (m_Preview == null || m_Preview.width != size.x || m_Preview.height != size.y)
            {
                if (m_Preview != null)
                    Destroy(m_Preview);
                m_Preview = new Texture2D(size.x, size.y, TextureFormat.RGBA32, false) { name = "QR Preview" };
            }

            image.Convert(conversion, m_Preview.GetRawTextureData<byte>());
            m_Preview.Apply(false);
        }
    }
}
