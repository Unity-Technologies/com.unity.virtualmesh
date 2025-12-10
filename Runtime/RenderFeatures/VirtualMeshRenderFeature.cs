using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.Rendering.RenderGraphModule;

namespace Unity.VirtualMesh.Runtime
{
    /// <summary>
    /// Custom render feature to add to the main camera's renderer.
    /// This feature handles all the rendering logic and custom passes that perform GPU-driven culling and LOD processing for virtual meshes.
    /// </summary>
    public class VirtualMeshRenderFeature : ScriptableRendererFeature
    {
        [System.Serializable]
        public class Settings
        {
            public RenderingPath renderMode = RenderingPath.DeferredShading;

            public RenderTexture depthPyramid;
            public ComputeShader computePassesShader;
            public ComputeShader pyramidGenerationPassesShader;
            public Material customPassMaterial = null;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            public Material debugPassMaterial = null;
#endif
#if UNITY_EDITOR
            public void EnsureAllUnityResources()
            {
                UnityResourceUtility.EnsureUnityResource(ref depthPyramid, "Runtime/RenderTextures/DepthPyramid.renderTexture");
                UnityResourceUtility.EnsureUnityResource(ref computePassesShader, "Runtime/Shaders/DXCVisibilityPasses.compute");
                UnityResourceUtility.EnsureUnityResource(ref pyramidGenerationPassesShader, "Runtime/Shaders/DepthPyramidPass.compute");
                UnityResourceUtility.EnsureUnityResource(ref customPassMaterial, "Runtime/Materials/CustomPassMaterial.mat");
                UnityResourceUtility.EnsureUnityResource(ref debugPassMaterial, "Runtime/Materials/DebugPassMaterial.mat");
            }
#endif
        }

        public Settings settings = new Settings();
        private DrawShadowPass m_DrawShadowPass;
        private RefreshDepthPyramidPass m_RefreshDepthPass;
        private VisibilityPass m_VisibilityPass;

        public override void Create()
        {
#if UNITY_EDITOR
            settings.EnsureAllUnityResources();
#endif

            m_DrawShadowPass = new DrawShadowPass(name + " Draw Shadow Pass");
            m_RefreshDepthPass = new RefreshDepthPyramidPass(name + " Refresh Depth Pyramid Pass");
            m_VisibilityPass = new VisibilityPass(name + " Visibility Pass");
        }

        protected override void Dispose(bool disposing)
        {
            if (!disposing)
                return;
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            if (settings.depthPyramid == null || settings.customPassMaterial == null)
                return;

            if (VirtualMeshManager.Instance == null || !VirtualMeshManager.Instance.IsInitialized || VirtualMeshManager.Instance.Materials.Length == 0)
                return;

            if (!VirtualMeshManager.Instance.IsEnabled)
                return;

            // shadows must be drawn after normal shadow rendering
            m_DrawShadowPass.renderPassEvent = RenderPassEvent.AfterRenderingShadows;
            m_DrawShadowPass.settings = settings;
            renderer.EnqueuePass(m_DrawShadowPass);

            // occluder depth must be refreshed before visibility
            m_RefreshDepthPass.renderPassEvent = RenderPassEvent.BeforeRenderingGbuffer;
            m_RefreshDepthPass.settings = settings;
            renderer.EnqueuePass(m_RefreshDepthPass);

            // visibility must solve before drawing geometry or any potential prepass
            m_VisibilityPass.renderPassEvent = RenderPassEvent.BeforeRenderingGbuffer;
            m_VisibilityPass.settings = settings;
            renderer.EnqueuePass(m_VisibilityPass);
        }
    }

    /// <summary>
    /// Custom render pass to draw shadow casters by using culling results from the visibility pass.
    /// Shadows for virtual meshes are drawn here instead of the default shadow caster passes.
    /// </summary>
    internal class DrawShadowPass : ScriptableRenderPass
    {
        public VirtualMeshRenderFeature.Settings settings;

        private string m_ProfilerTag;
        private ProfilingSampler m_ProfilingSampler;

        private ShadowSliceData[] m_CascadeSlices;

        private class PassData
        {
            internal VirtualMeshRenderFeature.Settings settings;
            internal ProfilingSampler profilingSampler;
            internal UniversalCameraData cameraData;
            internal UniversalLightData lightData;
            internal UniversalShadowData shadowData;
            internal TextureHandle shadowTexture;
            internal ShadowSliceData[] cascadeSlices;
        }

        public DrawShadowPass(string tag)
        {
            m_ProfilerTag = tag;
            m_ProfilingSampler = new ProfilingSampler(m_ProfilerTag);

            m_CascadeSlices = new ShadowSliceData[4];
        }

        static void ExecutePass(PassData data, UnsafeGraphContext context)
        {
            var cmd = CommandBufferHelpers.GetNativeCommandBuffer(context.cmd);
            using (new ProfilingScope(cmd, data.profilingSampler))
            {
                cmd.SetGlobalDepthBias(1.0f, 2.5f);

                // set render target
                cmd.SetRenderTarget(data.shadowTexture);

                // _MainLightShadowmapSize is needed but only set when soft shadows are enabled
                if (!data.shadowData.supportsSoftShadows)
                    cmd.SetGlobalVector(VirtualMeshShaderProperties.MainLightShadowmapSize, new Vector4(1.0f, 1.0f, data.shadowData.mainLightShadowmapWidth, data.shadowData.mainLightShadowmapHeight));

                var lightIndex = data.lightData.mainLightIndex;
                var shadowLight = data.lightData.visibleLights[lightIndex];
                var cascadeCount = data.shadowData.mainLightShadowCascadesCount;

                for (int cascadeIndex = cascadeCount - 1; cascadeIndex >= 0; cascadeIndex--)
                {
                    // set constants (the light direction and position should have already been set by the previous pass so we only need to update the shadow bias)
                    Vector4 shadowBias = ShadowUtils.GetShadowBias(ref shadowLight, lightIndex, data.shadowData, data.cascadeSlices[cascadeIndex].projectionMatrix, data.cascadeSlices[cascadeIndex].resolution);
                    cmd.SetGlobalVector(VirtualMeshShaderProperties.ShadowBias, shadowBias);

                    // set viewport
                    cmd.SetViewport(new Rect(data.cascadeSlices[cascadeIndex].offsetX, data.cascadeSlices[cascadeIndex].offsetY, data.cascadeSlices[cascadeIndex].resolution, data.cascadeSlices[cascadeIndex].resolution));
                    cmd.SetViewProjectionMatrices(data.cascadeSlices[cascadeIndex].viewMatrix, data.cascadeSlices[cascadeIndex].projectionMatrix);

                    // draw vmesh geometry
                    data.settings.customPassMaterial.SetBuffer(VirtualMeshShaderProperties.VertexPositionBuffer, VirtualMeshManager.Instance.VertexPositionBuffer);
                    data.settings.customPassMaterial.SetBuffer(VirtualMeshShaderProperties.VertexAttributeBuffer, VirtualMeshManager.Instance.VertexAttributeBuffer);
                    cmd.DrawProceduralIndirect(VirtualMeshManager.Instance.CompactedIndexBuffer, Matrix4x4.identity, data.settings.customPassMaterial, 0, MeshTopology.Triangles, VirtualMeshManager.Instance.ShadowDrawArgsBuffer, cascadeIndex * VirtualMeshManager.Instance.ShadowDrawArgsBuffer.stride);
                }

                // revert depth bias and disable scissor
                cmd.DisableScissorRect();
                cmd.SetGlobalDepthBias(0.0f, 0.0f);
            }
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            if (VirtualMeshManager.Instance == null || !VirtualMeshManager.Instance.IsInitialized)
                return;

            var renderingData = frameData.Get<UniversalRenderingData>();
            var resourceData = frameData.Get<UniversalResourceData>();
            var cameraData = frameData.Get<UniversalCameraData>();
            var lightData = frameData.Get<UniversalLightData>();
            var shadowData = frameData.Get<UniversalShadowData>();

            var shadowTexture = resourceData.mainShadowsTexture;
            var shadowTextureWidth = shadowData.mainLightShadowmapWidth;
            var shadowTextureHeight = shadowData.mainLightShadowmapHeight;
            if (!shadowTexture.IsValid() || shadowTextureWidth <= 1)
                return;

            var lightIndex = lightData.mainLightIndex;
            if (lightIndex == -1 || !renderingData.cullResults.GetShadowCasterBounds(lightIndex, out Bounds bounds))
                return;

            var shadowLight = lightData.visibleLights[lightIndex];
            var cascadeCount = shadowData.mainLightShadowCascadesCount;
            var shadowResolution = ShadowUtils.GetMaxTileResolutionInAtlas(shadowTextureWidth, shadowTextureHeight, cascadeCount);

            // check shadow cascade count to resize buffers if needed
            VirtualMeshManager.Instance.ShadowCascadeCount = cascadeCount;

            for (int cascadeIndex = 0; cascadeIndex < cascadeCount; cascadeIndex++)
            {
                // get matrix and slice data
                Vector4 cascadeSplitDistance;
                ShadowUtils.ExtractDirectionalLightMatrix(ref renderingData.cullResults, shadowData, lightIndex, cascadeIndex,
                    shadowTextureWidth, (cascadeCount == 2) ? shadowTextureHeight >> 1 : shadowTextureHeight,
                    shadowResolution, shadowLight.light.shadowNearPlane,
                    out cascadeSplitDistance, out m_CascadeSlices[cascadeIndex]);
            }

            using (var builder = renderGraph.AddUnsafePass<PassData>(m_ProfilerTag, out var passData))
            {
                passData.settings = settings;
                passData.profilingSampler = m_ProfilingSampler;
                passData.cameraData = cameraData;
                passData.lightData = lightData;
                passData.shadowData = shadowData;
                passData.shadowTexture = resourceData.mainShadowsTexture;
                passData.cascadeSlices = m_CascadeSlices;

                builder.UseTexture(passData.shadowTexture, AccessFlags.Write);

                builder.AllowPassCulling(false);
                builder.AllowGlobalStateModification(true);

                builder.SetRenderFunc((PassData data, UnsafeGraphContext context) => ExecutePass(data, context));
            }
        }
    }

    /// <summary>
    /// Custom render pass to draw previously visible geometry (first pass in the two-pass occlusion culling algorithm) and generate a depth pyramid from it.
    /// </summary>
    internal class RefreshDepthPyramidPass : ScriptableRenderPass
    {
        public VirtualMeshRenderFeature.Settings settings;

        private string m_ProfilerTag;
        private ProfilingSampler m_ProfilingSampler;

        private class PassData
        {
            internal VirtualMeshRenderFeature.Settings settings;
            internal ProfilingSampler profilingSampler;
            internal UniversalCameraData cameraData;
            internal TextureHandle[] gBuffer;
            internal TextureHandle cameraColor;
            internal TextureHandle cameraDepth;
        }

        public RefreshDepthPyramidPass(string tag)
        {
            m_ProfilerTag = tag;
            m_ProfilingSampler = new ProfilingSampler(m_ProfilerTag);
        }

        static void ExecutePass(PassData data, UnsafeGraphContext context)
        {
            var cmd = CommandBufferHelpers.GetNativeCommandBuffer(context.cmd);
            using (new ProfilingScope(cmd, data.profilingSampler))
            {
                cmd.SetComputeIntParam(data.settings.computePassesShader, VirtualMeshShaderProperties.MaterialCount, VirtualMeshManager.Instance.Materials.Length);

                // reset draw args
                data.settings.computePassesShader.SetBuffer(8, VirtualMeshShaderProperties.DrawArgsBufferUAV, VirtualMeshManager.Instance.DrawArgsBuffer);
                cmd.DispatchCompute(data.settings.computePassesShader, 8, 1, 1, 1);

                // write to draw buffers
                data.settings.computePassesShader.SetBuffer(4, VirtualMeshShaderProperties.TriangleBuffer, VirtualMeshManager.Instance.IndexBuffer);
                data.settings.computePassesShader.SetBuffer(4, VirtualMeshShaderProperties.CompactedTriangleBuffer, VirtualMeshManager.Instance.CompactedIndexBuffer);
                data.settings.computePassesShader.SetBuffer(4, VirtualMeshShaderProperties.TriangleVisibilityBufferSRV, VirtualMeshManager.Instance.PreviousTriangleVisibilityBuffer);
                data.settings.computePassesShader.SetBuffer(4, VirtualMeshShaderProperties.TriangleDataBufferSRV, VirtualMeshManager.Instance.TriangleDataBuffer);
                data.settings.computePassesShader.SetBuffer(4, VirtualMeshShaderProperties.DrawArgsBufferUAV, VirtualMeshManager.Instance.DrawArgsBuffer);
                cmd.DispatchCompute(data.settings.computePassesShader, 4, VirtualMeshManager.Instance.DispatchArgsBuffer, 0);

                // render previous frame geometry
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                if (VirtualMeshManager.Instance.IsDebugViewEnabled)
                {
                    // needed for the debug visualisation pass
                    cmd.ClearRenderTarget(true, true, Color.black);

                    // set render target
                    cmd.SetRenderTarget(data.cameraColor, data.cameraDepth);

                    // set transform matrices
                    cmd.SetViewProjectionMatrices(data.cameraData.GetViewMatrix(), data.cameraData.GetProjectionMatrix());

                    // bind buffers once
                    cmd.SetGlobalBuffer(VirtualMeshShaderProperties.VertexPositionBuffer, VirtualMeshManager.Instance.VertexPositionBuffer);

                    // draw geometry
                    for (int i = 0; i < VirtualMeshManager.Instance.Materials.Length; i++)
                    {
                        uint vertexCount = VirtualMeshManager.Instance.MaterialVertexCounts[i];
                        if (i > 0 && vertexCount == 0)
                            continue;

                        cmd.DrawProceduralIndirect(VirtualMeshManager.Instance.CompactedIndexBuffer, Matrix4x4.identity, data.settings.debugPassMaterial, 0, MeshTopology.Triangles, VirtualMeshManager.Instance.DrawArgsBuffer, i * VirtualMeshManager.Instance.DrawArgsBuffer.stride);
                    }
                }
                else
#endif
                {
                    // set render targets
                    if (data.settings.renderMode == RenderingPath.DeferredShading)
                    {
                        // TODO get rid of the allocation here
                        var renderTargetIdentifiers = new RenderTargetIdentifier[data.gBuffer.Length];
                        for (int i = 0; i < renderTargetIdentifiers.Length; i++)
                            renderTargetIdentifiers[i] = data.gBuffer[i];

                        cmd.SetRenderTarget(renderTargetIdentifiers, data.cameraDepth);
                    }
                    else
                        cmd.SetRenderTarget(data.cameraColor, data.cameraDepth);

                    // set transform matrices
                    cmd.SetViewProjectionMatrices(data.cameraData.GetViewMatrix(), data.cameraData.GetProjectionMatrix());

                    // set environment lighting coefficients (TODO should be cached)
                    SphericalHarmonicsL2 sh;
                    LightProbes.GetInterpolatedProbe(Vector3.zero, null, out sh);
                    cmd.SetGlobalVector(VirtualMeshShaderProperties.SHAr, new Vector4(sh[0, 3], sh[0, 1], sh[0, 2], sh[0, 0] - sh[0, 6]));
                    cmd.SetGlobalVector(VirtualMeshShaderProperties.SHAg, new Vector4(sh[1, 3], sh[1, 1], sh[1, 2], sh[1, 0] - sh[1, 6]));
                    cmd.SetGlobalVector(VirtualMeshShaderProperties.SHAb, new Vector4(sh[2, 3], sh[2, 1], sh[2, 2], sh[2, 0] - sh[2, 6]));
                    cmd.SetGlobalVector(VirtualMeshShaderProperties.SHBr, new Vector4(sh[0, 4], sh[0, 5], sh[0, 6] * 3, sh[0, 7]));
                    cmd.SetGlobalVector(VirtualMeshShaderProperties.SHBg, new Vector4(sh[1, 4], sh[1, 5], sh[1, 6] * 3, sh[1, 7]));
                    cmd.SetGlobalVector(VirtualMeshShaderProperties.SHBb, new Vector4(sh[2, 4], sh[2, 5], sh[2, 6] * 3, sh[2, 7]));
                    cmd.SetGlobalVector(VirtualMeshShaderProperties.SHC, new Vector4(sh[0, 8], sh[1, 8], sh[2, 8], 1));

                    // bind buffers once
                    cmd.SetGlobalBuffer(VirtualMeshShaderProperties.VertexPositionBuffer, VirtualMeshManager.Instance.VertexPositionBuffer);
                    cmd.SetGlobalBuffer(VirtualMeshShaderProperties.VertexAttributeBuffer, VirtualMeshManager.Instance.VertexAttributeBuffer);

                    // draw geometry
                    for (int i = 0; i < VirtualMeshManager.Instance.Materials.Length; i++)
                    {
                        uint vertexCount = VirtualMeshManager.Instance.MaterialVertexCounts[i];
                        if (i > 0 && vertexCount == 0)
                            continue;

                        var mat = VirtualMeshManager.Instance.Materials[i];

                        // try to find the correct pass (TODO improve this)
                        var pass = mat.FindPass(data.settings.renderMode == RenderingPath.DeferredShading ? VirtualMeshShaderProperties.GBufferPassName : VirtualMeshShaderProperties.SGForwardPassName);
                        if (pass == -1)
                            pass = mat.FindPass(VirtualMeshShaderProperties.LitForwardPassName);

                        if (pass != -1)
                            cmd.DrawProceduralIndirect(VirtualMeshManager.Instance.CompactedIndexBuffer, Matrix4x4.identity, mat, pass, MeshTopology.Triangles, VirtualMeshManager.Instance.DrawArgsBuffer, i * VirtualMeshManager.Instance.DrawArgsBuffer.stride);
                    }
                }

                // blit depth to top mip
                data.settings.customPassMaterial.SetTexture(VirtualMeshShaderProperties.DepthTexture, data.cameraDepth);
                cmd.SetRenderTarget(new RenderTargetIdentifier(data.settings.depthPyramid, 0), RenderBufferLoadAction.DontCare, RenderBufferStoreAction.Store);
                cmd.DrawProcedural(Matrix4x4.identity, data.settings.customPassMaterial, 1, MeshTopology.Triangles, 3, 1);

				// build mip chain
				int threadGroupCount = Mathf.CeilToInt(data.settings.depthPyramid.width / 16.0f);
				data.settings.pyramidGenerationPassesShader.SetTexture(0, VirtualMeshShaderProperties.Mip0, data.settings.depthPyramid, 0);
				data.settings.pyramidGenerationPassesShader.SetTexture(0, VirtualMeshShaderProperties.Mip1, data.settings.depthPyramid, 1);
				data.settings.pyramidGenerationPassesShader.SetTexture(0, VirtualMeshShaderProperties.Mip2, data.settings.depthPyramid, 2);
				data.settings.pyramidGenerationPassesShader.SetTexture(0, VirtualMeshShaderProperties.Mip3, data.settings.depthPyramid, 3);
				data.settings.pyramidGenerationPassesShader.SetTexture(0, VirtualMeshShaderProperties.Mip4, data.settings.depthPyramid, 4);
				data.settings.pyramidGenerationPassesShader.SetTexture(0, VirtualMeshShaderProperties.Mip5, data.settings.depthPyramid, 5);
				data.settings.pyramidGenerationPassesShader.SetTexture(0, VirtualMeshShaderProperties.Mip6, data.settings.depthPyramid, 6);
				data.settings.pyramidGenerationPassesShader.SetTexture(0, VirtualMeshShaderProperties.Mip7, data.settings.depthPyramid, 7);
				cmd.DispatchCompute(data.settings.pyramidGenerationPassesShader, 0, threadGroupCount, threadGroupCount, 1);
			}
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            if (VirtualMeshManager.Instance == null || !VirtualMeshManager.Instance.IsInitialized)
                return;

            var resourceData = frameData.Get<UniversalResourceData>();
            var cameraData = frameData.Get<UniversalCameraData>();

            using (var builder = renderGraph.AddUnsafePass<PassData>(m_ProfilerTag, out var passData))
            {
                passData.settings = settings;
                passData.profilingSampler = m_ProfilingSampler;
                passData.cameraData = cameraData;
                passData.gBuffer = resourceData.gBuffer;
                passData.cameraColor = resourceData.cameraColor;
                passData.cameraDepth = resourceData.cameraDepth;

                if (settings.renderMode == RenderingPath.DeferredShading)
                {
                    for (int i = 0; i < passData.gBuffer.Length; i++)
                        builder.UseTexture(passData.gBuffer[i], AccessFlags.Write);
                }
                else
                    builder.UseTexture(passData.cameraColor, AccessFlags.Write);

                builder.UseTexture(passData.cameraDepth, AccessFlags.ReadWrite);

                builder.AllowPassCulling(false);

                builder.SetRenderFunc((PassData data, UnsafeGraphContext context) => ExecutePass(data, context));
            }
        }
    }

    /// <summary>
    /// Custom compute pass to perform LOD and streaming decisions as well as several culling passes.
    /// </summary>
    internal class VisibilityPass : ScriptableRenderPass
    {
        public VirtualMeshRenderFeature.Settings settings;

        private string m_ProfilerTag;
        private ProfilingSampler m_ProfilingSampler;

        private static readonly string[] CascadeVariants = { "VMESH_SHADOW_CASCADE_0", "VMESH_SHADOW_CASCADE_1", "VMESH_SHADOW_CASCADE_2", "VMESH_SHADOW_CASCADE_3" };

        private class PassData
        {
            internal VirtualMeshRenderFeature.Settings settings;
            internal ProfilingSampler profilingSampler;
            internal int shadowCascadeCount;
        }

        public VisibilityPass(string tag)
        {
            m_ProfilerTag = tag;
            m_ProfilingSampler = new ProfilingSampler(m_ProfilerTag);
        }

        static void ExecutePass(PassData data, UnsafeGraphContext context)
        {
            var cmd = CommandBufferHelpers.GetNativeCommandBuffer(context.cmd);
            using (new ProfilingScope(cmd, data.profilingSampler))
            {
				int threadGroupCount;

                // setup constants
                {
                    var constantBuffer = VirtualMeshManager.Instance.PageStrideConstantBuffer;
                    cmd.SetComputeConstantBufferParam(data.settings.computePassesShader, VirtualMeshShaderProperties.PageStrideConstants, constantBuffer, 0, constantBuffer.count * constantBuffer.stride);
                    cmd.SetComputeIntParam(data.settings.computePassesShader, VirtualMeshShaderProperties.MaterialCount, VirtualMeshManager.Instance.Materials.Length);
                }

				// reset triangle visibility pass
				{
                    // we have to run several dispatches to make sure we stay within hardware thread count limitations
					int triangleSubcount = VirtualMeshManager.Instance.TotalTriangleCount / 4;
					int passCount = Mathf.CeilToInt(VirtualMeshManager.Instance.TotalTriangleCount / 4 / 128000.0f);
					for (int i = 0; i < passCount; i++)
					{
						cmd.SetComputeIntParam(data.settings.computePassesShader, VirtualMeshShaderProperties.DispatchPassOffset, i * 128000);

						threadGroupCount = triangleSubcount > 128000 ? 1000 : Mathf.CeilToInt(triangleSubcount / 128.0f);
						data.settings.computePassesShader.SetBuffer(9, VirtualMeshShaderProperties.TriangleVisibilityBufferUAV, VirtualMeshManager.Instance.CurrentTriangleVisibilityBuffer);
						cmd.DispatchCompute(data.settings.computePassesShader, 9, threadGroupCount, 1, 1);

						triangleSubcount -= 128000;
					}
				}

				// init counter pass
				data.settings.computePassesShader.SetBuffer(7, VirtualMeshShaderProperties.DispatchArgsBufferUAV, VirtualMeshManager.Instance.DispatchArgsBuffer);
				data.settings.computePassesShader.SetBuffer(7, VirtualMeshShaderProperties.DrawArgsBufferUAV, VirtualMeshManager.Instance.DrawArgsBuffer);
				data.settings.computePassesShader.SetBuffer(7, VirtualMeshShaderProperties.ShadowDrawArgsBufferUAV, VirtualMeshManager.Instance.ShadowDrawArgsBuffer);
				cmd.DispatchCompute(data.settings.computePassesShader, 7, 1, 1, 1);

				// page culling pass
				threadGroupCount = Mathf.CeilToInt(VirtualMeshManager.Instance.TotalPageCount / 64.0f);
				data.settings.computePassesShader.SetTexture(1, VirtualMeshShaderProperties.DepthPyramid, data.settings.depthPyramid);
				data.settings.computePassesShader.SetBuffer(1, VirtualMeshShaderProperties.PageDataBufferSRV, VirtualMeshManager.Instance.PageDataBuffer);
				data.settings.computePassesShader.SetBuffer(1, VirtualMeshShaderProperties.FeedbackBufferUAV, VirtualMeshManager.Instance.FeedbackBuffer);
				cmd.DispatchCompute(data.settings.computePassesShader, 1, threadGroupCount, 1, 1);

				// feedback sort pass
				for (uint level = 2; level <= VirtualMeshManager.Instance.TotalPageCount; level <<= 1)
				{
					cmd.SetComputeIntParam(data.settings.computePassesShader, VirtualMeshShaderProperties.SortLevel, (int)level);

					data.settings.computePassesShader.SetBuffer(0, VirtualMeshShaderProperties.FeedbackBufferUAV, VirtualMeshManager.Instance.FeedbackBuffer);
					cmd.DispatchCompute(data.settings.computePassesShader, 0, 1, 1, 1);
				}

				// request feedback on the CPU
				cmd.RequestAsyncReadback(VirtualMeshManager.Instance.FeedbackBuffer, VirtualMeshManager.Instance.FeedbackReadbackCallback);

				// check if we have anything to draw (TODO we should do something more clever and check for the actual number of instances that survive page culling)
				var instanceCount = VirtualMeshManager.Instance.TotalInstanceCount;
				if (instanceCount == 0)
					return;

				// cluster culling pass
				{
                    // we have to run several dispatches to make sure we stay within hardware thread count limitations
                    int instanceSubcount = instanceCount;
					int passCount = Mathf.CeilToInt(instanceCount / 128000.0f);
					for (int i = 0; i < passCount; i++)
					{
						cmd.SetComputeIntParam(data.settings.computePassesShader, VirtualMeshShaderProperties.DispatchPassOffset, i * 128000);

						threadGroupCount = instanceSubcount > 128000 ? 1000 : Mathf.CeilToInt(instanceSubcount / 128.0f);
						data.settings.computePassesShader.SetTexture(2, VirtualMeshShaderProperties.DepthPyramid, data.settings.depthPyramid);
						data.settings.computePassesShader.SetBuffer(2, VirtualMeshShaderProperties.DrawArgsBufferUAV, VirtualMeshManager.Instance.DrawArgsBuffer);
						data.settings.computePassesShader.SetBuffer(2, VirtualMeshShaderProperties.TriangleDataBufferUAV, VirtualMeshManager.Instance.TriangleDataBuffer);
						data.settings.computePassesShader.SetBuffer(2, VirtualMeshShaderProperties.ShadowTriangleDataBufferUAV, VirtualMeshManager.Instance.ShadowTriangleDataBuffer);
						data.settings.computePassesShader.SetBuffer(2, VirtualMeshShaderProperties.GroupDataBuffer, VirtualMeshManager.Instance.GroupDataBuffer);
						data.settings.computePassesShader.SetBuffer(2, VirtualMeshShaderProperties.InstanceDataBuffer, VirtualMeshManager.Instance.InstanceDataBuffer);
						data.settings.computePassesShader.SetBuffer(2, VirtualMeshShaderProperties.PageStatusBufferSRV, VirtualMeshManager.Instance.PageStatusBuffer);
						data.settings.computePassesShader.SetBuffer(2, VirtualMeshShaderProperties.PageDataBufferSRV, VirtualMeshManager.Instance.PageDataBuffer);
						data.settings.computePassesShader.SetBuffer(2, VirtualMeshShaderProperties.DispatchArgsBufferUAV, VirtualMeshManager.Instance.DispatchArgsBuffer);
						cmd.DispatchCompute(data.settings.computePassesShader, 2, threadGroupCount, 1, 1);

						instanceSubcount -= 128000;
					}
				}

				// material scan pass
				data.settings.computePassesShader.SetBuffer(6, VirtualMeshShaderProperties.DrawArgsBufferUAV, VirtualMeshManager.Instance.DrawArgsBuffer);
				cmd.DispatchCompute(data.settings.computePassesShader, 6, 1, 1, 1);

				// triangle culling pass (for dissocluded opaques pass)
				data.settings.computePassesShader.SetTexture(3, VirtualMeshShaderProperties.DepthPyramid, data.settings.depthPyramid);
                data.settings.computePassesShader.SetBuffer(3, VirtualMeshShaderProperties.VertexPositionBuffer, VirtualMeshManager.Instance.VertexPositionBuffer);
                data.settings.computePassesShader.SetBuffer(3, VirtualMeshShaderProperties.TriangleBuffer, VirtualMeshManager.Instance.IndexBuffer);
                data.settings.computePassesShader.SetBuffer(3, VirtualMeshShaderProperties.CompactedTriangleBuffer, VirtualMeshManager.Instance.CompactedIndexBuffer);
                data.settings.computePassesShader.SetBuffer(3, VirtualMeshShaderProperties.TriangleVisibilityBufferSRV, VirtualMeshManager.Instance.PreviousTriangleVisibilityBuffer);
                data.settings.computePassesShader.SetBuffer(3, VirtualMeshShaderProperties.TriangleVisibilityBufferUAV, VirtualMeshManager.Instance.CurrentTriangleVisibilityBuffer);
                data.settings.computePassesShader.SetBuffer(3, VirtualMeshShaderProperties.TriangleDataBufferSRV, VirtualMeshManager.Instance.TriangleDataBuffer);
                data.settings.computePassesShader.SetBuffer(3, VirtualMeshShaderProperties.DrawArgsBufferUAV, VirtualMeshManager.Instance.DrawArgsBuffer);
                cmd.DispatchCompute(data.settings.computePassesShader, 3, VirtualMeshManager.Instance.DispatchArgsBuffer, 0);

                // triangle culling pass (for next frame's main light shadows)
                for (int cascadeIndex = data.shadowCascadeCount - 1; cascadeIndex >= 0; cascadeIndex--)
                {
                    for (int i = 0; i < CascadeVariants.Length; i++)
                    {
                        if (i == cascadeIndex)
                            cmd.EnableShaderKeyword(CascadeVariants[i]);
                        else
                            cmd.DisableShaderKeyword(CascadeVariants[i]);
                    }

                    data.settings.computePassesShader.SetBuffer(5, VirtualMeshShaderProperties.VertexPositionBuffer, VirtualMeshManager.Instance.VertexPositionBuffer);
                    data.settings.computePassesShader.SetBuffer(5, VirtualMeshShaderProperties.TriangleBuffer, VirtualMeshManager.Instance.IndexBuffer);
                    data.settings.computePassesShader.SetBuffer(5, VirtualMeshShaderProperties.CompactedTriangleBuffer, VirtualMeshManager.Instance.CompactedIndexBuffer);
                    data.settings.computePassesShader.SetBuffer(5, VirtualMeshShaderProperties.ShadowTriangleDataBufferSRV, VirtualMeshManager.Instance.ShadowTriangleDataBuffer);
                    data.settings.computePassesShader.SetBuffer(5, VirtualMeshShaderProperties.ShadowDrawArgsBufferUAV, VirtualMeshManager.Instance.ShadowDrawArgsBuffer);
                    cmd.DispatchCompute(data.settings.computePassesShader, 5, VirtualMeshManager.Instance.DispatchArgsBuffer, 12);
                }
            }
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            if (VirtualMeshManager.Instance == null || !VirtualMeshManager.Instance.IsInitialized)
                return;

            using (var builder = renderGraph.AddUnsafePass<PassData>(m_ProfilerTag, out var passData))
            {
                passData.settings = settings;
                passData.profilingSampler = m_ProfilingSampler;
                passData.shadowCascadeCount = frameData.Get<UniversalShadowData>().mainLightShadowCascadesCount;

                builder.AllowPassCulling(false);

                builder.SetRenderFunc((PassData data, UnsafeGraphContext context) => ExecutePass(data, context));
            }
        }
    }
}
