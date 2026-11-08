using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.RenderGraphModule.Util;
using UnityEngine.Rendering.Universal;

namespace CardboardXR.Demos.Ascii
{
    // Full-screen terminal pass. Off unless AsciiTerminal.Active, so the rest of the app is untouched.
    // Replaces the pure green and yellow shade pixels with glyphs. The door, angels, hands and
    // passthrough are not those hues, so they stay as they are.
    public class AsciiTerminalFeature : ScriptableRendererFeature
    {
        [SerializeField] Material m_Material;

        TerminalPass m_Pass;
        bool m_Logged;

        public Material Material
        {
            get => m_Material;
            set => m_Material = value;
        }

        public override void Create()
        {
            m_Pass = new TerminalPass();
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            if (!AsciiTerminal.Active)
                return;
            if (m_Material == null)
            {
                Debug.LogWarning("[Ascii] Terminal pass has no material");
                return;
            }
            var cameraType = renderingData.cameraData.cameraType;
            if (cameraType == CameraType.Preview || cameraType == CameraType.Reflection)
                return;
            m_Pass.renderPassEvent = RenderPassEvent.BeforeRenderingTransparents;
            m_Pass.requiresIntermediateTexture = true;
            m_Pass.ConfigureInput(ScriptableRenderPassInput.Depth);
            m_Pass.Setup(m_Material);
            renderer.EnqueuePass(m_Pass);
            if (m_Logged)
                return;
            m_Logged = true;
            Debug.Log("[Ascii] Terminal pass running");
        }

        sealed class TerminalPass : ScriptableRenderPass
        {
            static readonly int k_BlitTexture = Shader.PropertyToID("_BlitTexture");
            static readonly int k_BlitScaleBias = Shader.PropertyToID("_BlitScaleBias");

            Material m_Material;
            readonly MaterialPropertyBlock m_Block = new();

            public TerminalPass()
            {
                profilingSampler = new ProfilingSampler("Ascii Terminal");
            }

            public void Setup(Material material) => m_Material = material;

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                var resources = frameData.Get<UniversalResourceData>();
                if (!resources.cameraColor.IsValid())
                    return;

                var desc = renderGraph.GetTextureDesc(resources.cameraColor);
                desc.name = "_AsciiTerminalCopy";
                desc.clearBuffer = false;
                var copy = renderGraph.CreateTexture(desc);
                renderGraph.AddBlitPass(resources.cameraColor, copy, Vector2.one, Vector2.zero, passName: "Ascii Terminal Copy");

                using (var builder = renderGraph.AddRasterRenderPass<PassData>("Ascii Terminal", out var passData, profilingSampler))
                {
                    passData.material = m_Material;
                    passData.block = m_Block;
                    passData.source = copy;
                    passData.destination = resources.activeColorTexture;
                    builder.UseTexture(passData.source, AccessFlags.Read);
                    if (resources.cameraDepthTexture.IsValid())
                        builder.UseTexture(resources.cameraDepthTexture, AccessFlags.Read);
                    builder.SetRenderAttachment(passData.destination, 0, AccessFlags.Write);
                    if (resources.activeDepthTexture.IsValid())
                        builder.SetRenderAttachmentDepth(resources.activeDepthTexture, AccessFlags.Read);
                    builder.SetRenderFunc(static (PassData data, RasterGraphContext context) =>
                    {
                        var source = (RTHandle)data.source;
                        var scale = source != null && source.useScaling
                            ? new Vector2(source.rtHandleProperties.rtHandleScale.x, source.rtHandleProperties.rtHandleScale.y)
                            : Vector2.one;
                        var flip = context.GetTextureUVOrigin(data.source) != context.GetTextureUVOrigin(data.destination);
                        var scaleBias = flip
                            ? new Vector4(scale.x, -scale.y, 0f, scale.y)
                            : new Vector4(scale.x, scale.y, 0f, 0f);
                        data.block.Clear();
                        data.block.SetTexture(k_BlitTexture, source);
                        data.block.SetVector(k_BlitScaleBias, scaleBias);
                        context.cmd.DrawProcedural(Matrix4x4.identity, data.material, 0, MeshTopology.Triangles, 3, 1, data.block);
                    });
                }
            }

            sealed class PassData
            {
                public Material material;
                public MaterialPropertyBlock block;
                public TextureHandle source;
                public TextureHandle destination;
            }
        }
    }

    public static class AsciiTerminal
    {
        public static bool Active { get; set; }
    }
}
