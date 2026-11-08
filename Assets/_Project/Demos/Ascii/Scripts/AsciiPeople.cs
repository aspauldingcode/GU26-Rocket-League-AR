using CardboardXR.Stereo;
using Unity.InferenceEngine;
using UnityEngine;

namespace CardboardXR.Demos.Ascii
{
    // Finds people in the camera image, so the terminal shows them through the glyphs and nobody in
    // the real room gets walked into. DeepLab v3 (PASCAL VOC, class 15 is a person) on the same
    // Inference Engine the hand tracker runs, a few times a second, on the GPU when it can. The
    // terminal crossfades from the last mask to the new one. Logs [Ascii].
    public class AsciiPeople : MonoBehaviour
    {
        const string k_ModelResource = "AsciiPersonSegmenter";
        const int k_PersonClass = 15;
        const float k_Interval = 0.2f;
        const float k_FadeSeconds = 0.5f;

        static readonly int k_MaskId = Shader.PropertyToID("_AsciiPersonMask");
        static readonly int k_MaskPrevId = Shader.PropertyToID("_AsciiPersonMaskPrev");
        static readonly int k_BlendId = Shader.PropertyToID("_AsciiPersonBlend");
        static readonly int k_OnId = Shader.PropertyToID("_AsciiPersonOn");

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

        // Inside the gate only; outside, passthrough already shows everyone.
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
                Debug.LogWarning("[Ascii] No person segmenter in Resources; people won't show through");
                m_Failed = true;
                return;
            }
            var model = ModelLoader.Load(asset);
            var inputShape = model.inputs[0].shape;
            if (!inputShape.IsStatic() || inputShape.rank != 4)
            {
                Debug.LogWarning($"[Ascii] Person segmenter input {inputShape} isn't a fixed image; people won't show through");
                m_Failed = true;
                return;
            }
            var shape = inputShape.ToTensorShape();
            // LiteRT models keep their NHWC layout; textures convert to NCHW.
            var nhwc = shape[3] == 3;
            var height = nhwc ? shape[1] : shape[2];
            var width = nhwc ? shape[2] : shape[3];
            var classAxis = nhwc ? 3 : 1;

            var graph = new FunctionalGraph();
            var image = graph.AddInput(DataType.Float, new TensorShape(1, 3, height, width));
            var x = 2f * image - 1f;
            if (nhwc)
                x = x.Permute(new[] { 0, 2, 3, 1 });
            var logits = Functional.Forward(model, x)[0];
            var person = Functional.Softmax(logits, classAxis).Select(classAxis, k_PersonClass).Unsqueeze(1);

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
            Debug.Log($"[Ascii] Person segmenter {width}x{height} ({(nhwc ? "NHWC" : "NCHW")}) on {backend}");
        }

        void Update()
        {
            var image = m_Passthrough != null ? m_Passthrough.CameraImage : null;
            var running = m_Active && m_Worker != null && image != null;
            m_Strength = Mathf.MoveTowards(m_Strength, running ? 1f : 0f, Time.unscaledDeltaTime / k_FadeSeconds);
            Shader.SetGlobalFloat(k_OnId, m_Strength);
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

        void OnDestroy()
        {
            Shader.SetGlobalFloat(k_OnId, 0f);
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
