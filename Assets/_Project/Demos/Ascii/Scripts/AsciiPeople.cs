using CardboardXR.Stereo;
using CardboardXR.UI;
using Unity.InferenceEngine;
using UnityEngine;

namespace CardboardXR.Demos.Ascii
{
    // Finds people in the camera image, so the terminal draws them as light-blue glyphs and nobody
    // in the real room gets walked into. MediaPipe's landscape selfie segmenter as ONNX (256x144,
    // 106K parameters, one person-confidence channel; the .tflite needs a MediaPipe custom op the
    // importer lacks) on the same Inference Engine the hand tracker runs, four times a
    // second, on the GPU when it can. The terminal crossfades from the last mask to the new one, so
    // the low rate doesn't show. A multi-class model (DeepLab: 21 classes, softmax, class 15 is a
    // person) still works if dropped in instead. Logs [Ascii].
    public class AsciiPeople : MonoBehaviour
    {
        const string k_ModelResource = "AsciiPersonSegmenter";
        const int k_PersonClass = 15;
        const float k_Interval = 0.25f;
        const float k_FadeSeconds = 0.5f;

        static readonly int k_MaskId = Shader.PropertyToID("_AsciiPersonMask");
        static readonly int k_MaskPrevId = Shader.PropertyToID("_AsciiPersonMaskPrev");
        static readonly int k_BlendId = Shader.PropertyToID("_AsciiPersonBlend");
        static readonly int k_OnId = Shader.PropertyToID("_AsciiPersonOn");
        static readonly int k_PanelCenterId = Shader.PropertyToID("_AsciiPanelCenter");
        static readonly int k_PanelRightId = Shader.PropertyToID("_AsciiPanelRight");
        static readonly int k_PanelUpId = Shader.PropertyToID("_AsciiPanelUp");
        static readonly int k_PanelCountId = Shader.PropertyToID("_AsciiPanelCount");
        // ASCII_MAX_PANELS in AsciiGlyphs.hlsl.
        const int k_MaxPanels = 8;
        const float k_PanelSearchInterval = 0.5f;

        readonly Vector4[] m_PanelCenter = new Vector4[k_MaxPanels];
        readonly Vector4[] m_PanelRight = new Vector4[k_MaxPanels];
        readonly Vector4[] m_PanelUp = new Vector4[k_MaxPanels];
        readonly Vector3[] m_Corners = new Vector3[4];
        M3Canvas[] m_Panels = new M3Canvas[0];
        float m_LastPanelSearch = float.NegativeInfinity;

        PassthroughController m_Passthrough;
        Worker m_Worker;
        Tensor<float> m_Input;
        readonly RenderTexture[] m_Masks = new RenderTexture[2];
        int m_Current;
        float m_LastRun = float.NegativeInfinity;
        float m_Strength;
        bool m_Active;
        bool m_Failed;

        public void Configure(PassthroughController passthrough) => m_Passthrough = passthrough;

        // Compile the model ahead of the first crossing so entering the gate doesn't hitch.
        public void Warm()
        {
            if (m_Worker == null && !m_Failed)
                Setup();
        }

        // While the gate is up: inside, and through the doorway from outside.
        public void SetActive(bool active)
        {
            m_Active = active;
            if (active && m_Worker == null && !m_Failed)
                Setup();
        }

        void Setup()
        {
            var asset = Resources.Load<ModelAsset>(k_ModelResource);
            if (asset == null)
            {
                Debug.LogWarning("[Ascii] No person segmenter in Resources; people won't be drawn");
                m_Failed = true;
                return;
            }
            var model = ModelLoader.Load(asset);
            var inputShape = model.inputs[0].shape;
            // A dynamic batch (ONNX exports) is fine: it's always one frame.
            if (inputShape.isRankDynamic || inputShape.rank != 4 || inputShape.Get(1) < 1 || inputShape.Get(2) < 1 ||
                inputShape.Get(3) < 1)
            {
                Debug.LogWarning($"[Ascii] Person segmenter input {inputShape} isn't a fixed image; people won't be drawn");
                m_Failed = true;
                return;
            }
            var shape = new TensorShape(1, inputShape.Get(1), inputShape.Get(2), inputShape.Get(3));
            // LiteRT models keep their NHWC layout; textures convert to NCHW.
            var nhwc = shape[3] == 3;
            var height = nhwc ? shape[1] : shape[2];
            var width = nhwc ? shape[2] : shape[3];
            var classAxis = nhwc ? 3 : 1;
            // One channel: already a person confidence, from a 0..1 image. Many: class scores, from -1..1.
            bool single;
            using (var probe = new Worker(model, BackendType.CPU))
            using (var blank = new Tensor<float>(shape))
            {
                probe.Schedule(blank);
                var probed = probe.PeekOutput().shape;
                single = probed.rank == 4 && probed[classAxis] == 1;
            }

            var graph = new FunctionalGraph();
            var image = graph.AddInput(DataType.Float, new TensorShape(1, 3, height, width));
            var x = single ? image : 2f * image - 1f;
            if (nhwc)
                x = x.Permute(new[] { 0, 2, 3, 1 });
            var output = Functional.Forward(model, x)[0];
            var person = single
                ? output.Select(classAxis, 0).Unsqueeze(1)
                : Functional.Softmax(output, classAxis).Select(classAxis, k_PersonClass).Unsqueeze(1);

            var backend = SystemInfo.supportsComputeShaders ? BackendType.GPUCompute : BackendType.CPU;
            m_Worker = new Worker(graph.Compile(person), backend);
            m_Input = new Tensor<float>(new TensorShape(1, 3, height, width));
            for (var i = 0; i < m_Masks.Length; i++)
            {
                m_Masks[i] = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32)
                {
                    name = $"Ascii Person Mask {i}",
                    enableRandomWrite = true,
                    wrapMode = TextureWrapMode.Clamp,
                    filterMode = FilterMode.Bilinear,
                };
                Graphics.Blit(Texture2D.blackTexture, m_Masks[i]);
            }
            // The first run compiles the GPU kernels; do it now, not on the first frame inside.
            TextureConverter.ToTensor(Texture2D.blackTexture, m_Input);
            m_Worker.Schedule(m_Input);
            TextureConverter.RenderToTexture(m_Worker.PeekOutput() as Tensor<float>, m_Masks[m_Current]);
            Debug.Log($"[Ascii] Person segmenter {width}x{height} ({(nhwc ? "NHWC" : "NCHW")}, " +
                      $"{(single ? "one person channel" : "class scores")}) on {backend}");
        }

        void Update()
        {
            var image = m_Passthrough != null ? m_Passthrough.CameraImage : null;
            var running = m_Active && m_Worker != null && image != null;
            m_Strength = Mathf.MoveTowards(m_Strength, running ? 1f : 0f, Time.unscaledDeltaTime / k_FadeSeconds);
            Shader.SetGlobalFloat(k_OnId, m_Strength);
            if (m_Strength > 0f)
                UploadPanels();
            if (!running)
                return;

            var now = Time.unscaledTime;
            if (now - m_LastRun >= k_Interval)
            {
                m_LastRun = now;
                m_Current = 1 - m_Current;
                TextureConverter.ToTensor(image, m_Input);
                m_Worker.Schedule(m_Input);
                TextureConverter.RenderToTexture(m_Worker.PeekOutput() as Tensor<float>, m_Masks[m_Current]);
                Shader.SetGlobalTexture(k_MaskId, m_Masks[m_Current]);
                Shader.SetGlobalTexture(k_MaskPrevId, m_Masks[1 - m_Current]);
            }
            Shader.SetGlobalFloat(k_BlendId, Mathf.SmoothStep(0f, 1f, (now - m_LastRun) / k_Interval));
        }

        // The terminal draws people before the transparent UI, so through glass panels they'd look
        // like they stand on top of it. It hides them wherever a shown panel covers the view.
        // Panels move and fade every frame; the set of them is only looked up now and then.
        void UploadPanels()
        {
            var now = Time.unscaledTime;
            if (now - m_LastPanelSearch >= k_PanelSearchInterval)
            {
                m_LastPanelSearch = now;
                m_Panels = FindObjectsByType<M3Canvas>(FindObjectsSortMode.None);
            }
            var count = 0;
            foreach (var panel in m_Panels)
            {
                if (count == k_MaxPanels)
                    break;
                if (panel == null || !panel.isActiveAndEnabled)
                    continue;
                var group = panel.GetComponent<CanvasGroup>();
                var alpha = group != null ? group.alpha : 1f;
                if (alpha <= 0.02f)
                    continue;
                ((RectTransform)panel.transform).GetWorldCorners(m_Corners);
                var center = (m_Corners[0] + m_Corners[2]) / 2f;
                m_PanelCenter[count] = new Vector4(center.x, center.y, center.z, alpha);
                m_PanelRight[count] = (m_Corners[3] - m_Corners[0]) / 2f;
                m_PanelUp[count] = (m_Corners[1] - m_Corners[0]) / 2f;
                count++;
            }
            Shader.SetGlobalVectorArray(k_PanelCenterId, m_PanelCenter);
            Shader.SetGlobalVectorArray(k_PanelRightId, m_PanelRight);
            Shader.SetGlobalVectorArray(k_PanelUpId, m_PanelUp);
            Shader.SetGlobalFloat(k_PanelCountId, count);
        }

        void OnDestroy()
        {
            Shader.SetGlobalFloat(k_OnId, 0f);
            Shader.SetGlobalFloat(k_PanelCountId, 0f);
            m_Worker?.Dispose();
            m_Input?.Dispose();
            foreach (var mask in m_Masks)
            {
                if (mask == null)
                    continue;
                mask.Release();
                Destroy(mask);
            }
        }
    }
}
