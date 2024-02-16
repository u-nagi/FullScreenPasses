using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

[DisallowMultipleRendererFeature]
public class FullScreenPassesRendererFeature : ScriptableRendererFeature
{
    /// <summary>
    /// An injection point for the full screen pass. This is similar to RenderPassEvent enum but limits to only supported events.
    /// </summary>
    public enum InjectionPoint
    {
        /// <summary>
        /// Inject a full screen pass before transparents are rendered
        /// </summary>
        BeforeRenderingTransparents = RenderPassEvent.BeforeRenderingTransparents,
        /// <summary>
        /// Inject a full screen pass before post processing is rendered
        /// </summary>
        BeforeRenderingPostProcessing = RenderPassEvent.BeforeRenderingPostProcessing,
        /// <summary>
        /// Inject a full screen pass after post processing is rendered
        /// </summary>
        AfterRenderingPostProcessing = RenderPassEvent.AfterRenderingPostProcessing
    }

    [Serializable]
    public class FullScreenPass
    {
        public Material passMaterial;

        public InjectionPoint injectionPoint = InjectionPoint.AfterRenderingPostProcessing;

        //public ScriptableRenderPassInput requirements = ScriptableRenderPassInput.Color;

#if UNITY_EDITOR
        [MaterialPass]
#endif
        public int passIndex = 0;

        internal FullScreenRenderPass fullScreenPass;
        internal bool requiresColor;
        internal bool injectedBeforeTransparents;
    }

    [SerializeField]
    private FullScreenPass[] fullScreenPasses = new FullScreenPass[] { };

    public override void Create()
    {
        for (int i = 0; i < fullScreenPasses.Length; i++)
        {
            if (fullScreenPasses[i] == null)
                continue;

            fullScreenPasses[i].fullScreenPass = new FullScreenRenderPass();
            fullScreenPasses[i].fullScreenPass.renderPassEvent = (RenderPassEvent)fullScreenPasses[i].injectionPoint;

            //// This copy of requirements is used as a parameter to configure input in order to avoid copy color pass
            //ScriptableRenderPassInput modifiedRequirements = fullScreenPasses[i].requirements;

            //fullScreenPasses[i].requiresColor = (fullScreenPasses[i].requirements & ScriptableRenderPassInput.Color) != 0;
            //fullScreenPasses[i].injectedBeforeTransparents = fullScreenPasses[i].injectionPoint <= InjectionPoint.BeforeRenderingTransparents;

            //if (fullScreenPasses[i].requiresColor && !fullScreenPasses[i].injectedBeforeTransparents)
            //{
            //    // Removing Color flag in order to avoid unnecessary CopyColor pass
            //    // Does not apply to before rendering transparents, due to how depth and color are being handled until
            //    // that injection point.
            //    modifiedRequirements ^= ScriptableRenderPassInput.Color;
            //}
            //fullScreenPasses[i].fullScreenPass.ConfigureInput(modifiedRequirements);
        }
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        for (int i = 0; i < fullScreenPasses.Length; i++)
        {
            if (fullScreenPasses[i].fullScreenPass == null)
                fullScreenPasses[i].fullScreenPass = new FullScreenRenderPass();

            if (fullScreenPasses[i].passMaterial == null)
            {
                Debug.LogWarningFormat("Missing Post Processing effect Material. {0} Fullscreen pass will not execute. Check for missing reference in the assigned renderer.", GetType().Name);
                continue;
            }

            fullScreenPasses[i].fullScreenPass.renderPassEvent = (RenderPassEvent)fullScreenPasses[i].injectionPoint;
            fullScreenPasses[i].fullScreenPass.Setup(fullScreenPasses[i].passMaterial, fullScreenPasses[i].passIndex, fullScreenPasses[i].requiresColor, fullScreenPasses[i].injectedBeforeTransparents, "FullScreenPassesRendererFeature: " + fullScreenPasses[i].passMaterial.name, renderingData);

            renderer.EnqueuePass(fullScreenPasses[i].fullScreenPass);
        }
    }

    protected override void Dispose(bool disposing)
    {
        for (int i = 0; i < fullScreenPasses.Length; i++)
        {
            fullScreenPasses[i].fullScreenPass?.Dispose();
        }
    }

    internal class FullScreenRenderPass : ScriptableRenderPass
    {
        private Material m_PassMaterial;
        private int m_PassIndex;
        private bool m_RequiresColor;
        private bool m_IsBeforeTransparents;
        private PassData m_PassData;
        private ProfilingSampler m_ProfilingSampler;
        private RTHandle m_CopiedColor;
        private static readonly int m_BlitTextureShaderID = Shader.PropertyToID("_BlitTexture");

        public void Setup(Material mat, int index, bool requiresColor, bool isBeforeTransparents, string featureName, in RenderingData renderingData)
        {
            m_PassMaterial = mat;
            m_PassIndex = index;
            m_RequiresColor = requiresColor;
            m_IsBeforeTransparents = isBeforeTransparents;
            m_ProfilingSampler ??= new ProfilingSampler(featureName);

            var colorCopyDescriptor = renderingData.cameraData.cameraTargetDescriptor;
            colorCopyDescriptor.depthBufferBits = (int)DepthBits.None;
            RenderingUtils.ReAllocateIfNeeded(ref m_CopiedColor, colorCopyDescriptor, name: "_FullscreenPassColorCopy");

            m_PassData ??= new PassData();
        }

        public void Dispose()
        {
            m_CopiedColor?.Release();
        }


        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            m_PassData.effectMaterial = m_PassMaterial;
            m_PassData.passIndex = m_PassIndex;
            m_PassData.requiresColor = m_RequiresColor;
            m_PassData.isBeforeTransparents = m_IsBeforeTransparents;
            m_PassData.profilingSampler = m_ProfilingSampler;
            m_PassData.copiedColor = m_CopiedColor;

            ExecutePass(m_PassData, ref renderingData, ref context);
        }

        // RG friendly method
        private static void ExecutePass(PassData passData, ref RenderingData renderingData, ref ScriptableRenderContext context)
        {
            var passMaterial = passData.effectMaterial;
            var passIndex = passData.passIndex;
            var requiresColor = passData.requiresColor;
            var isBeforeTransparents = passData.isBeforeTransparents;
            var copiedColor = passData.copiedColor;
            var profilingSampler = passData.profilingSampler;

            if (passMaterial == null)
            {
                // should not happen as we check it in feature
                return;
            }

            if (renderingData.cameraData.isPreviewCamera)
            {
                return;
            }

            CommandBuffer cmd = renderingData.commandBuffer;
            var cameraData = renderingData.cameraData;

            using (new ProfilingScope(cmd, profilingSampler))
            {
                //if (requiresColor)
                {
                    // For some reason BlitCameraTexture(cmd, dest, dest) scenario (as with before transparents effects) blitter fails to correctly blit the data
                    // Sometimes it copies only one effect out of two, sometimes second, sometimes data is invalid (as if sampling failed?).
                    // Adding RTHandle in between solves this issue.
                    var source = isBeforeTransparents ? cameraData.renderer.GetCameraColorBackBuffer(cmd) : cameraData.renderer.cameraColorTargetHandle;

                    Blitter.BlitCameraTexture(cmd, source, copiedColor);
                    passMaterial.SetTexture(m_BlitTextureShaderID, copiedColor);
                }

                CoreUtils.SetRenderTarget(cmd, cameraData.renderer.GetCameraColorBackBuffer(cmd));
                CoreUtils.DrawFullScreen(cmd, passMaterial, shaderPassId: passIndex);
                context.ExecuteCommandBuffer(cmd);
                cmd.Clear();
            }
        }


        private class PassData
        {
            internal Material effectMaterial;
            internal int passIndex;
            internal bool requiresColor;
            internal bool isBeforeTransparents;
            public ProfilingSampler profilingSampler;
            public RTHandle copiedColor;
        }
    }
}
