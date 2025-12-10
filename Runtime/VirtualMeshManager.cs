using System;
using System.IO;
using System.Collections.Generic;
using Unity.Jobs;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.Assertions;

namespace Unity.VirtualMesh.Runtime
{
    /// <summary>
    /// Contains cached shader property ID values used throughout the virtual mesh render feature.
    /// </summary>
    public static class VirtualMeshShaderProperties
    {
        public static readonly string GBufferPassName = "GBuffer";
        public static readonly string LitForwardPassName = "ForwardLit";
        public static readonly string SGForwardPassName = "Universal Forward";

        public static readonly int ShadowBias = Shader.PropertyToID("_ShadowBias");
        public static readonly int MainLightShadowmapSize = Shader.PropertyToID("_MainLightShadowmapSize");

        public static readonly int SHAr = Shader.PropertyToID("unity_SHAr");
        public static readonly int SHAg = Shader.PropertyToID("unity_SHAg");
        public static readonly int SHAb = Shader.PropertyToID("unity_SHAb");
        public static readonly int SHBr = Shader.PropertyToID("unity_SHBr");
        public static readonly int SHBg = Shader.PropertyToID("unity_SHBg");
        public static readonly int SHBb = Shader.PropertyToID("unity_SHBb");
        public static readonly int SHC = Shader.PropertyToID("unity_SHC");

        public static readonly int Mip0 = Shader.PropertyToID("_Mip0");
        public static readonly int Mip1 = Shader.PropertyToID("_Mip1");
        public static readonly int Mip2 = Shader.PropertyToID("_Mip2");
        public static readonly int Mip3 = Shader.PropertyToID("_Mip3");
        public static readonly int Mip4 = Shader.PropertyToID("_Mip4");
        public static readonly int Mip5 = Shader.PropertyToID("_Mip5");
        public static readonly int Mip6 = Shader.PropertyToID("_Mip6");
        public static readonly int Mip7 = Shader.PropertyToID("_Mip7");

        public static readonly int DepthTexture = Shader.PropertyToID("_DepthTexture");
        public static readonly int DepthPyramid = Shader.PropertyToID("_DepthPyramid");

        public static readonly int CopySourceStart = Shader.PropertyToID("CopySourceStart");
        public static readonly int CopyDestStart = Shader.PropertyToID("CopyDestStart");
        public static readonly int CopyLength = Shader.PropertyToID("CopyLength");

        public static readonly int PageID = Shader.PropertyToID("PageID");
        public static readonly int SlotID = Shader.PropertyToID("SlotID");

        public static readonly int CopySourceSRV = Shader.PropertyToID("CopySourceSRV");
        public static readonly int CopyDestUAV = Shader.PropertyToID("CopyDestUAV");
        public static readonly int StatusBufferUAV = Shader.PropertyToID("StatusBufferUAV");

        public static readonly int PageStrideConstants = Shader.PropertyToID("PageStrideConstants");

        public static readonly int MaterialCount = Shader.PropertyToID("MaterialCount");
        public static readonly int SortLevel = Shader.PropertyToID("SortLevel");
        public static readonly int DispatchPassOffset = Shader.PropertyToID("DispatchPassOffset");

        public static readonly int DrawArgsBufferSRV = Shader.PropertyToID("DrawArgsBufferSRV");
        public static readonly int DrawArgsBufferUAV = Shader.PropertyToID("DrawArgsBufferUAV");

        public static readonly int DispatchArgsBufferSRV = Shader.PropertyToID("DispatchArgsBufferSRV");
        public static readonly int DispatchArgsBufferUAV = Shader.PropertyToID("DispatchArgsBufferUAV");

        public static readonly int ShadowDrawArgsBufferUAV = Shader.PropertyToID("ShadowDrawArgsBufferUAV");

        public static readonly int GroupDataBuffer = Shader.PropertyToID("GroupDataBuffer");
        public static readonly int InstanceDataBuffer = Shader.PropertyToID("InstanceDataBuffer");
        public static readonly int CompactedInstanceDataBuffer = Shader.PropertyToID("CompactedInstanceDataBuffer");

        public static readonly int TriangleDataBufferSRV = Shader.PropertyToID("TriangleDataBufferSRV");
        public static readonly int TriangleDataBufferUAV = Shader.PropertyToID("TriangleDataBufferUAV");
        public static readonly int ShadowTriangleDataBufferSRV = Shader.PropertyToID("ShadowTriangleDataBufferSRV");
        public static readonly int ShadowTriangleDataBufferUAV = Shader.PropertyToID("ShadowTriangleDataBufferUAV");

        public static readonly int PageDataBufferSRV = Shader.PropertyToID("PageDataBufferSRV");

        public static readonly int FeedbackBufferSRV = Shader.PropertyToID("FeedbackBufferSRV");
        public static readonly int FeedbackBufferUAV = Shader.PropertyToID("FeedbackBufferUAV");

        public static readonly int PageStatusBufferSRV = Shader.PropertyToID("PageStatusBufferSRV");

        public static readonly int TriangleBuffer = Shader.PropertyToID("TriangleBuffer");
        public static readonly int CompactedTriangleBuffer = Shader.PropertyToID("CompactedTriangleBuffer");

        public static readonly int TriangleVisibilityBufferSRV = Shader.PropertyToID("TriangleVisibilityBufferSRV");
        public static readonly int TriangleVisibilityBufferUAV = Shader.PropertyToID("TriangleVisibilityBufferUAV");

        public static readonly int VertexPositionBuffer = Shader.PropertyToID("VertexPositionBuffer");
        public static readonly int VertexAttributeBuffer = Shader.PropertyToID("VertexAttributeBuffer");
    }

    /// <summary>
    /// Component to attach to the scene's main camera.
    /// This component contains all the resources (such as buffers) that are used by the virtual mesh runtime and handles the streaming of geometry from disk to GPU.
    /// </summary>
    public class VirtualMeshManager : MonoBehaviour
    {
        private static VirtualMeshManager s_Instance = null;
        public static VirtualMeshManager Instance => s_Instance;

        private const int k_InstanceDataSize = 4;
        private const int k_GroupDataSize = 4;
        private const int k_ClusterTriangleCount = 64;
        private const int k_MaxMemoryPageCount = 256;
        private const int k_UploadBufferCount = 12;
        private const int k_MemoryPageMaxInstanceCount = 1600;

        [SerializeField]
        private ComputeShader m_CopyPassesShader;

        [SerializeField]
        private Material m_PlaceholderMaterial;

        private Mesh m_BoundingMesh;
        private Bounds m_Bounds = new Bounds(Vector3.zero, 10000.0f * Vector3.one);

        private NativeArray<MemoryPageStatus> m_MemoryPageStatus;

        public enum MemoryPageStatus : int
        {
            Unloaded = 0,
            Waiting = 1,
            Loading = 2,
            Loaded = 3,
            TooFar = 4
        }

        public enum MeshHeaderValue : int
        {
            packedBoundsX = 0,
            packedBoundsY = 1,
            packedBoundsZ = 2,
            TotalInstanceCount = 3,
            TotalGroupCount = 4,
            VertexValueCount = 5,
            IndexValueCount = 6,

            Size = 7
        }

        private bool m_Initialized = false;
        private bool m_HeaderJobRunning = false;
        private bool[] m_DataJobRunning;

        private int m_MemoryPageCount = 0;
        private int m_LoadableMemoryPageCount = 128;
        private int m_PingPongBufferIndex = 0;
        private int m_ShadowCacadeCount = 1;

        private float m_CameraLoadDistanceThreshold = 20.0f;

        private AssetBundle m_MaterialAssetBundle = null;
        private AssetBundle m_PlaceholderAssetBundle = null;

        private NativeArray<uint> m_MeshHeader;
        private Material[] m_Materials;
        private List<uint> m_MaterialVertexCounts;
        private RenderParams[] m_RenderParams;
        private Mesh[] m_Placeholders;
        private RenderParams[] m_PlaceholderRenderParams;

        private GraphicsBuffer m_PageStrideConstants;
        private GraphicsBuffer m_PageDataBuffer;
        private GraphicsBuffer m_VertexPositionBuffer;
        private GraphicsBuffer m_VertexAttributeBuffer;
        private GraphicsBuffer m_IndexBuffer;
        private GraphicsBuffer m_CompactedIndexBuffer;
        private GraphicsBuffer[] m_TriangleVisibilityBuffer;
        private GraphicsBuffer m_GroupDataBuffer;
        private GraphicsBuffer m_InstanceDataBuffer;
        private GraphicsBuffer m_TriangleDataBuffer;
        private GraphicsBuffer m_ShadowTriangleDataBuffer;
        private GraphicsBuffer m_FeedbackBuffer;
        private GraphicsBuffer m_PageStatusBuffer;
        private FencedBufferPool[] m_UploadBuffers;

        private GraphicsBuffer m_DispatchArgsBuffer;
        private GraphicsBuffer m_DrawArgsBuffer;
        private GraphicsBuffer m_ShadowDrawArgsBuffer;

        private Queue<int> m_UploadIndexQueue;
        private Queue<int> m_UnloadIndexQueue;
        private int[] m_PageIDs;
        private int[] m_SlotIDs;
        private int[] m_PageStrideValues;
        private int m_UploadBufferSize;

        private NativeArray<int> m_LoadedMemoryPages;

        /// <summary>
        /// C# job to perform the initial loading of memory page headers.
        /// </summary>
        public struct LoadMeshHeaderJob : IJobParallelFor
        {
            public int headerSize;

            [NativeDisableParallelForRestriction]
            [NativeDisableContainerSafetyRestriction]
            public NativeArray<uint> header;

            public void Execute(int id)
            {
                // generate file path
                string name = (id + 1).ToString("X8");
                string assetPath = $"{ResourcePathDefinitions.virtualMeshDataPath}/{name}.vmesh";
                using (var stream = BetterStreamingAssets.OpenRead(assetPath))
                {
                    stream.Seek(0, SeekOrigin.Begin);

                    var dataArray = new byte[stream.Length];
                    stream.Read(dataArray);

                    using (var ms = new MemoryStream(dataArray))
                    {
                        using (var br = new BinaryReader(ms))
                        {
                            for (int i = 0; i < headerSize; i++)
                                header[id * headerSize + i] = br.ReadUInt32();
                        }
                    }
                }
            }
        }
        private JobHandle m_LoadMeshHeaderJobHandle;

        /// <summary>
        /// C# job to stream in a specific memory page's contents.
        /// </summary>
        public struct LoadMeshDataJob : IJob
        {
            public int pageID;
            public int slotID;

            public int vertexValueCount;
            public int indexValueCount;
            public int groupDataCount;
            public int instanceDataCount;

            public int vertexValueOffset;
            public int indexValueOffset;
            public int groupDataOffset;
            public int instanceDataOffset;

            [NativeDisableContainerSafetyRestriction]
            public NativeArray<uint> array;

            public void Execute()
            {
                // generate file path
                string name = (pageID + 1).ToString("X8");
                string assetPath = $"{ResourcePathDefinitions.virtualMeshDataPath}/{name}.vmeshdata";
                using (var stream = BetterStreamingAssets.OpenRead(assetPath))
                {
                    stream.Seek(0, SeekOrigin.Begin);

                    var dataArray = new byte[stream.Length];
                    stream.Read(dataArray);

                    using (var ms = new MemoryStream(dataArray))
                    {
                        using (var br = new BinaryReader(ms))
                        {
                            for (int i = 0; i < vertexValueCount; i++)
                                array[i] = br.ReadUInt32();

                            for (int i = 0; i < vertexValueCount * 2; i++)
                                array[vertexValueOffset + i] = br.ReadUInt32();

                            for (int i = 0; i < indexValueCount; i++)
                                array[vertexValueOffset + vertexValueOffset * 2 + i] = br.ReadUInt32();

                            for (int i = 0; i < groupDataCount; i++)
                                array[vertexValueOffset + vertexValueOffset * 2 + indexValueOffset + i] = br.ReadUInt32();

                            for (int i = 0; i < instanceDataCount; i++)
                                array[vertexValueOffset + vertexValueOffset * 2 + indexValueOffset + groupDataOffset + i] = br.ReadUInt32();
                        }
                    }
                }
            }
        }
        private JobHandle[] m_LoadMeshDataJobHandles;

        /// <summary>
        /// The number of memory pages that are currently used to contain the whole scene.
        /// </summary>
        public int TotalPageCount => m_MemoryPageCount;

        /// <summary>
        /// The maximum number of cluster instances that can be rendered at a given time.
        /// </summary>
        public int TotalInstanceCount => k_MemoryPageMaxInstanceCount * m_LoadableMemoryPageCount;

        /// <summary>
        /// The maximum number of triangles that can be held in a memory page's index buffer range.
        /// </summary>
        public int TotalTriangleCount => m_PageStrideValues[1] * m_LoadableMemoryPageCount / 96;

        /// <summary>
        /// The number of main light shadow cascades that is currently being used to size shadow caster drawing buffers.
        /// </summary>
        public int ShadowCascadeCount
        {
            get => m_ShadowCacadeCount;
            set
            {
                if (m_ShadowCacadeCount != value)
                {
                    m_ShadowCacadeCount = Math.Max(value, 1);
                    AllocateShadowDrawBuffers();
                }
            }
        }

        /// <summary>
        /// The distance from the camera to use for computing the LOD projection error on the GPU.
        /// This value determines the cut of the cluster LOD hierarchy to be selected for rendering and works the same way as an LOD bias.
        /// </summary>
        public float CameraLoadDistanceThreshold
        {
            get => m_CameraLoadDistanceThreshold;
            set
            {
                m_CameraLoadDistanceThreshold = value;
                m_PageStrideConstants.SetData(new uint[1] { BitConverter.ToUInt32(BitConverter.GetBytes(m_CameraLoadDistanceThreshold), 0) }, 0, 7, 1);
            }
        }

        /// <summary>
        /// Checks if the virtual mesh runtime has been initialized.
        /// </summary>
        public bool IsInitialized => m_Initialized;

        /// <summary>
        /// Checks if the virtual mesh runtime is enabled.
        /// </summary>
        public bool IsEnabled = true;

        /// <summary>
        /// Checks if the empty bounding mesh surrounding virtual meshes is enabled.
        /// </summary>
        public bool IsBoundingMeshEnabled = false;

        /// <summary>
        /// Checks if the placeholder system is enabled.
        /// </summary>
        public bool IsPlaceholderEnabled = false;

        /// <summary>
        /// Checks if the debug rendering view is enabled.
        /// </summary>
        public bool IsDebugViewEnabled = false;

        /// <summary>
        /// The constant buffer containing stride values to index into memory page buffers that contain virtual mesh data.
        /// </summary>
		public ref GraphicsBuffer PageStrideConstantBuffer => ref m_PageStrideConstants;

        /// <summary>
        /// The buffer that contains all the metadata read from memory page headers.
        /// </summary>
        public ref GraphicsBuffer PageDataBuffer => ref m_PageDataBuffer;

        /// <summary>
        /// The buffer that contains all the vertex positions loaded from memory pages.
        /// </summary>
        public ref GraphicsBuffer VertexPositionBuffer => ref m_VertexPositionBuffer;

        /// <summary>
        /// The buffer that contains all the vertex attributes loaded from memory pages.
        /// </summary>
        public ref GraphicsBuffer VertexAttributeBuffer => ref m_VertexAttributeBuffer;

        /// <summary>
        /// The buffer that contains all the triangle indices loaded from memory pages.
        /// </summary>
        public ref GraphicsBuffer IndexBuffer => ref m_IndexBuffer;

        /// <summary>
        /// The buffer that contains the post-culling compacted triangle indices ready for drawing use.
        /// </summary>
        public ref GraphicsBuffer CompactedIndexBuffer => ref m_CompactedIndexBuffer;

        /// <summary>
        /// The buffer that contains one-bit flags representing triangle visibility for the previous frame.
        /// </summary>
        public ref GraphicsBuffer PreviousTriangleVisibilityBuffer => ref m_TriangleVisibilityBuffer[m_PingPongBufferIndex % 2];

        /// <summary>
        /// The buffer that contains one-bit flags representing triangle visibility for the current frame.
        /// </summary>
        public ref GraphicsBuffer CurrentTriangleVisibilityBuffer => ref m_TriangleVisibilityBuffer[(m_PingPongBufferIndex + 1) % 2];

        /// <summary>
        /// The buffer that contains all the per-LOD hierarchy metadata loaded from memory pages.
        /// </summary>
        public ref GraphicsBuffer GroupDataBuffer => ref m_GroupDataBuffer;

        /// <summary>
        /// The buffer that contains all the per-cluster metadata loaded from memory pages.
        /// </summary>
        public ref GraphicsBuffer InstanceDataBuffer => ref m_InstanceDataBuffer;

        /// <summary>
        /// The buffer that contains compacted per-cluster metadata for triangles that survived culling.
        /// </summary>
        public ref GraphicsBuffer TriangleDataBuffer => ref m_TriangleDataBuffer;

        /// <summary>
        /// The buffer that contains compacted per-cluster metadata for shadow caster triangles that survived culling.
        /// </summary>
        public ref GraphicsBuffer ShadowTriangleDataBuffer => ref m_ShadowTriangleDataBuffer;

        /// <summary>
        /// The buffer containing the memory page streaming decision data computed on the GPU and read back to the CPU.
        /// </summary>
        public ref GraphicsBuffer FeedbackBuffer => ref m_FeedbackBuffer;

        /// <summary>
        /// The buffer containing flags to indicate to the GPU if pages are currently being streamed or if they are ready to be processed.
        /// </summary>
        public ref GraphicsBuffer PageStatusBuffer => ref m_PageStatusBuffer;

        /// <summary>
        /// Updates the page status buffer based on a specific page's streaming condition.
        /// </summary>
        private void DispatchStatusBufferUpdate(int pageID, int slotID)
        {
			m_CopyPassesShader.SetInt(VirtualMeshShaderProperties.PageID, pageID);
			m_CopyPassesShader.SetInt(VirtualMeshShaderProperties.SlotID, slotID);
			m_CopyPassesShader.SetBuffer(1, VirtualMeshShaderProperties.StatusBufferUAV, m_PageStatusBuffer);
			m_CopyPassesShader.Dispatch(1, 1, 1, 1);
        }

        /// <summary>
        /// Starts writing operations on an upload buffer to begin streaming.
        /// </summary>
        private NativeArray<uint> LockUploadBufferForWrite(int index, int count)
        {
            m_UploadBuffers[index].BeginFrame();
            var buffer = m_UploadBuffers[index].GetCurrentFrameBuffer();
            return buffer.LockBufferForWrite<uint>(0, count);
        }

        /// <summary>
        /// Stops writing operations on an upload buffer to finish streaming.
        /// </summary>
        private GraphicsBuffer UnlockUploadBufferForWrite(int index, int count)
        {
            var buffer = m_UploadBuffers[index].GetCurrentFrameBuffer();
            buffer.UnlockBufferAfterWrite<uint>(count);
            m_UploadBuffers[index].EndFrame();
            return buffer;
        }

        /// <summary>
        /// The array that contains all the materials used by virtual meshes in the current scene.
        /// The system iterates over this array when performing per-material draws.
        /// </summary>
        public Material[] Materials => m_Materials;

        /// <summary>
        /// The list that contains how many vertices are expected to be drawn for each material.
        /// This data is generated to cull draws that do not result in any visible geometry during baking.
        /// </summary>
        public List<uint> MaterialVertexCounts => m_MaterialVertexCounts;

        /// <summary>
        /// The indirect argument buffer used for indirect compute dispatches in the virtual mesh pipeline.
        /// </summary>
        public ref GraphicsBuffer DispatchArgsBuffer => ref m_DispatchArgsBuffer;

        /// <summary>
        /// The indirect argument buffer used for indirect draws in the virtual mesh pipeline.
        /// </summary>
        public ref GraphicsBuffer DrawArgsBuffer => ref m_DrawArgsBuffer;

        /// <summary>
        /// The indirect argument buffer used for indirect shadow caster draws in the virtual mesh pipeline.
        /// </summary>
        public ref GraphicsBuffer ShadowDrawArgsBuffer => ref m_ShadowDrawArgsBuffer;

        /// <summary>
        /// Processes the GPU's memory page streaming decision data and handles the streaming loop.
        /// </summary>
        public void FeedbackReadbackCallback(AsyncGPUReadbackRequest request)
        {
            if (!m_Initialized || m_HeaderJobRunning)
                return;

            if (request.hasError)
                return;

            // TODO get rid of the queues
            m_UploadIndexQueue.Clear();
            m_UnloadIndexQueue.Clear();
            var data = request.GetData<uint>();
            for (int i = 0; i < m_MemoryPageCount; i++)
            {
                int index = (int)((data[i] & 0xfffff) >> 1);
                uint lodLevel = data[i] >> 20;
                bool tooFar = (data[i] & 0x1) == 1;

                var status = m_MemoryPageStatus[index];
                bool requested = lodLevel != 0 && m_MeshHeader[index * (int)MeshHeaderValue.Size + (int)MeshHeaderValue.TotalInstanceCount] != 0;
                bool unload = false;
                switch (status)
                {
                    case MemoryPageStatus.Unloaded:
                        {
                            if (requested)
                            {
                                m_UploadIndexQueue.Enqueue(index);
                                status = MemoryPageStatus.Waiting;

                                if (IsPlaceholderEnabled && _ExternalActivatePlacholderCallback != null)
                                    _ExternalActivatePlacholderCallback(index);
                            }
                            else if (tooFar)
                            {
                                status = MemoryPageStatus.TooFar;

                                if (IsPlaceholderEnabled && _ExternalActivatePlacholderCallback != null)
                                    _ExternalActivatePlacholderCallback(index);
                            }
                        }
                        break;
                    case MemoryPageStatus.Loaded:
                        {
                            if (!requested)
                            {
                                unload = true;
                                status = MemoryPageStatus.Unloaded;
                            }
                            else if (tooFar)
                            {
                                unload = true;
                                status = MemoryPageStatus.TooFar;

                                if (IsPlaceholderEnabled && _ExternalActivatePlacholderCallback != null)
                                    _ExternalActivatePlacholderCallback(index);
                            }
                        }
                        break;
                    case MemoryPageStatus.Waiting:
                        {
                            if (requested)
                            {
                                m_UploadIndexQueue.Enqueue(index);
                            }
                            else if (tooFar)
                            {
                                status = MemoryPageStatus.TooFar;
                            }
                            else
                            {
                                status = MemoryPageStatus.Unloaded;

                                if (IsPlaceholderEnabled && _ExternalDeactivatePlacholderCallback != null)
                                    _ExternalDeactivatePlacholderCallback(index);
                            }
                        }
                        break;
                    case MemoryPageStatus.Loading:
                        {
                        }
                        break;
                    case MemoryPageStatus.TooFar:
                        {
                            if (requested)
                            {
                                m_UploadIndexQueue.Enqueue(index);
                                status = MemoryPageStatus.Waiting;
                            }
                            else if (!tooFar)
                            {
                                status = MemoryPageStatus.Unloaded;

                                if (IsPlaceholderEnabled && _ExternalDeactivatePlacholderCallback != null)
                                    _ExternalDeactivatePlacholderCallback(index);
                            }
                        }
                        break;
                }

                if (unload)
                {
                    for (int j = 0; j < m_LoadableMemoryPageCount; j++)
                    {
                        if (m_LoadedMemoryPages[j] == index)
                        {
                            m_UnloadIndexQueue.Enqueue(j);
                            break;
                        }
                    }
                }

                m_MemoryPageStatus[index] = status;
            }

            if (IsEnabled)
            {
                StreamingJobsKickoff();
                StreamingJobsWrapup();
            }
        }

        /// <summary>
        /// Registers per-material draws for virtual meshes based on the post-culling compacted index buffer.
        /// This will generate draw calls in the opaque rendering passes and corresponds to the second pass of the two-pass occlusion culling algorithm.
        /// </summary>
        private void DrawVirtualMesh()
        {
            if (!IsInitialized)
                return;

            for (int i = 0; i < m_Materials.Length; i++)
            {
                // skip materials with 0 vertices
                uint vertexCount = m_MaterialVertexCounts[i];
                if (i > 0 && vertexCount == 0)
                    continue;

                var renderParams = m_RenderParams[i];

                if (_ExternalDrawVirtualMeshCallback != null)
                    _ExternalDrawVirtualMeshCallback(renderParams.matProps);

                Graphics.RenderPrimitivesIndexedIndirect(renderParams, MeshTopology.Triangles, m_CompactedIndexBuffer, m_DrawArgsBuffer, 1, i);
            }
        }

        /// <summary>
        /// Registers an external callback for custom logic to happen when registering virtual mesh draws into the existing SRP passes.
        /// </summary>
        public void SetupExternalDrawVirtualMesh(ExternalDrawVirtualMeshCallback callback)
        {
            _ExternalDrawVirtualMeshCallback = callback;
        }
        public delegate void ExternalDrawVirtualMeshCallback(MaterialPropertyBlock properties);
        private ExternalDrawVirtualMeshCallback _ExternalDrawVirtualMeshCallback = null;

        /// <summary>
        /// Registers placeholder draws for memory pages that require a placeholder for the current frame.
        /// </summary>
        private void DrawPlaceholders()
        {
            if (!IsInitialized || !IsPlaceholderEnabled)
                return;

			if (_ExternalPlaceholdersCallback != null)
				_ExternalPlaceholdersCallback();
			else
            {
                for (int i = 0; i < m_Placeholders.Length; i++)
                {
                    if (i < m_Placeholders.Length && m_Placeholders[i] != null)
                    {
                        var status = m_MemoryPageStatus[i];
                        if (status == MemoryPageStatus.Waiting || status == MemoryPageStatus.Loading || status == MemoryPageStatus.TooFar)
                            Graphics.RenderMesh(m_PlaceholderRenderParams[i], m_Placeholders[i], 0, Matrix4x4.identity);
                    }
                }
            }
        }

        /// <summary>
        /// Registers an external callback for custom logic to happen when registering placeholder draws into the existing SRP passes.
        /// </summary>
        public void SetupExternalDrawPlaceholders(ExternalPlaceholdersCallback callback)
        {
            _ExternalPlaceholdersCallback = callback;
        }
        public delegate void ExternalPlaceholdersCallback();
        private ExternalPlaceholdersCallback _ExternalPlaceholdersCallback = null;

        /// <summary>
        /// Registers an external callback for custom logic to happen when a specific placeholder activates.
        /// </summary>
        public void SetupExternalActivatePlacholderCallback(ExternalActivatePlacholderCallback callback)
        {
            _ExternalActivatePlacholderCallback = callback;
        }
        public delegate void ExternalActivatePlacholderCallback(int index);
        private ExternalActivatePlacholderCallback _ExternalActivatePlacholderCallback = null;

        /// <summary>
        /// Registers an external callback for custom logic to happen when a specific placeholder deactivates.
        /// </summary>
        public void SetupExternalDeactivatePlacholderCallback(ExternalDeactivatePlacholderCallback callback)
        {
            _ExternalDeactivatePlacholderCallback = callback;
        }
        public delegate void ExternalDeactivatePlacholderCallback(int index);
        private ExternalDeactivatePlacholderCallback _ExternalDeactivatePlacholderCallback = null;

        /// <summary>
        /// Draws a mesh that represents a bounding box around all virtual geometry in the current scene.
        /// This mesh is required when the scene contains no shadow casters other than virtual meshes, in which case URP will cull shadow caster passes.
        /// </summary>
        private void DrawBoundingMesh()
        {
            // draw empty mesh to prevent SRP culling results from disabling things like shadows
            if (m_BoundingMesh != null)
                Graphics.DrawMesh(m_BoundingMesh, Matrix4x4.identity, m_PlaceholderMaterial, 0);
#if UNITY_EDITOR
            var center = m_BoundingMesh.bounds.center;
            var extents = m_BoundingMesh.bounds.extents;

            var corners = new Vector3[8];
            corners[0] = center + new Vector3(-extents.x, -extents.y, -extents.z);
            corners[1] = center + new Vector3(extents.x, -extents.y, -extents.z);
            corners[2] = center + new Vector3(-extents.x, extents.y, -extents.z);
            corners[3] = center + new Vector3(extents.x, extents.y, -extents.z);
            corners[4] = center + new Vector3(-extents.x, -extents.y, extents.z);
            corners[5] = center + new Vector3(extents.x, -extents.y, extents.z);
            corners[6] = center + new Vector3(-extents.x, extents.y, extents.z);
            corners[7] = center + new Vector3(extents.x, extents.y, extents.z);

            Debug.DrawLine(corners[0], corners[1], Color.magenta);
            Debug.DrawLine(corners[0], corners[2], Color.magenta);
            Debug.DrawLine(corners[0], corners[4], Color.magenta);
            Debug.DrawLine(corners[2], corners[3], Color.magenta);

            Debug.DrawLine(corners[3], corners[1], Color.magenta);
            Debug.DrawLine(corners[2], corners[6], Color.magenta);
            Debug.DrawLine(corners[3], corners[7], Color.magenta);
            Debug.DrawLine(corners[1], corners[5], Color.magenta);

            Debug.DrawLine(corners[4], corners[5], Color.magenta);
            Debug.DrawLine(corners[4], corners[6], Color.magenta);
            Debug.DrawLine(corners[6], corners[7], Color.magenta);
            Debug.DrawLine(corners[7], corners[5], Color.magenta);
#endif
        }

        /// <summary>
        /// Loads a global metadata file that contains constant data used for streaming and allocating buffers with correct sizes.
        /// </summary>
        private bool RequestMetadata()
        {
            m_PageStrideValues = new int[4];
            m_MaterialVertexCounts = new List<uint>();

            var boundingMeshBoundsCenter = new Vector3();
            var boundingMeshBoundsExtents = new Vector3();

            // fetch metadata
            string path = $"{ResourcePathDefinitions.virtualMeshDataPath}/metadata.vmesh";
            using (var stream = BetterStreamingAssets.OpenRead(path))
            {
                stream.Seek(0, SeekOrigin.Begin);

                var dataArray = new byte[stream.Length];
                stream.Read(dataArray);

                using (var ms = new MemoryStream(dataArray))
                {
                    using (var br = new BinaryReader(ms))
                    {
                        m_MemoryPageCount = (int)br.ReadUInt32(); // filled page count
                        br.ReadUInt32(); // total instance count
                        m_PageStrideValues[0] = (int)br.ReadUInt32(); // VertexValuePageStride
                        m_PageStrideValues[1] = (int)br.ReadUInt32(); // IndexValuePageStride
                        m_PageStrideValues[2] = (int)br.ReadUInt32() * k_GroupDataSize; // GroupDataPageStride
                        boundingMeshBoundsCenter.x = br.ReadSingle(); // total bounds center X
                        boundingMeshBoundsCenter.y = br.ReadSingle(); // total bounds center Y
                        boundingMeshBoundsCenter.z = br.ReadSingle(); // total bounds center Z
                        boundingMeshBoundsExtents.x = br.ReadSingle(); // total bounds extents X
                        boundingMeshBoundsExtents.y = br.ReadSingle(); // total bounds extents Y
                        boundingMeshBoundsExtents.z = br.ReadSingle(); // total bounds extents Z
                        br.ReadUInt32(); // total vertex count
                        m_PageStrideValues[3] = k_InstanceDataSize * k_MemoryPageMaxInstanceCount; // InstanceDataPageStride

                        uint count = br.ReadUInt32();
                        for (int i = 0; i < count; i++)
                            m_MaterialVertexCounts.Add(br.ReadUInt32());
                    }
                }
            }

            m_BoundingMesh = new Mesh();
            m_BoundingMesh.bounds = new Bounds(boundingMeshBoundsCenter, boundingMeshBoundsExtents * 2.0f);

            // set the page count to a power of 2 value for the GPU sort pass
            if (m_MemoryPageCount <= 32)
                m_MemoryPageCount = 32;
            else if (m_MemoryPageCount <= 64)
                m_MemoryPageCount = 64;
            else if (m_MemoryPageCount <= 128)
                m_MemoryPageCount = 128;
            else
                m_MemoryPageCount = k_MaxMemoryPageCount;

            // all pages are loadable
            if (m_MemoryPageCount < m_LoadableMemoryPageCount)
                m_LoadableMemoryPageCount = m_MemoryPageCount;

#if UNITY_EDITOR
            if (m_PageStrideValues[0] == 0 || m_PageStrideValues[1] == 0 || m_PageStrideValues[2] == 0 || m_PageStrideValues[3] == 0)
            {
                Debug.LogError("[Virtual Mesh] WARNING: No baked mesh detected");
                return false;
            }
#endif

            return true;
        }

        /// <summary>
        /// Dispatches jobs to load memory page headers that contain metadata about each page's contents for streaming and allocating buffers with correct sizes.
        /// </summary>
        private bool RequestHeaders()
        {
            if (m_Initialized || m_HeaderJobRunning)
                return false;

            LoadMeshHeaderJob job = new LoadMeshHeaderJob
            {
                headerSize = (int)MeshHeaderValue.Size,
                header = m_MeshHeader
            };
            m_LoadMeshHeaderJobHandle = job.Schedule(m_MemoryPageCount, m_MemoryPageCount);

            m_HeaderJobRunning = true;

#if UNITY_EDITOR
            Debug.Log($"[Virtual Mesh] Running with {m_MemoryPageCount} pages");
#endif

            return true;
        }

        /// <summary>
        /// Loads all the materials used by virtual meshes from an asset bundle built during balking.
        /// </summary>
        private bool RequestMaterials()
        {
            if (m_Initialized)
                return false;

            // load materials
            m_MaterialAssetBundle = AssetBundle.LoadFromFile($"{Application.streamingAssetsPath}/{ResourcePathDefinitions.materialBundleFullPath}");
            if (m_MaterialAssetBundle == null)
            {
                m_Materials = Array.Empty<Material>();
                return false;
            }

            // fetch collection
            var collections = m_MaterialAssetBundle.LoadAllAssets<MaterialCollection>();
            m_Materials = collections[0].Materials;

            // create RenderParams objects
            m_RenderParams = new RenderParams[m_Materials.Length];
            for (int i = 0; i < m_Materials.Length; i++)
            {
                var param = new RenderParams(m_Materials[i]);
                param.camera = GetComponent<Camera>();
                param.worldBounds = m_Bounds;
                param.shadowCastingMode = ShadowCastingMode.Off; // vmesh shadow maps use a custom pass
                param.receiveShadows = true;

                param.matProps = new MaterialPropertyBlock();
                param.matProps.SetBuffer(VirtualMeshShaderProperties.VertexPositionBuffer, m_VertexPositionBuffer);
                param.matProps.SetBuffer(VirtualMeshShaderProperties.VertexAttributeBuffer, m_VertexAttributeBuffer);

                m_RenderParams[i] = param;
            }

#if UNITY_EDITOR
            Debug.Log($"[Virtual Mesh] Retrieved {m_Materials.Length} different materials");
#endif
            return true;
        }

        /// <summary>
        /// Loads all the placeholder meshes from an asset bundle built during balking.
        /// </summary>
        private bool RequestPlaceholders()
        {
            if (!m_Initialized || m_HeaderJobRunning)
                return false;

            // load placeholders
            m_PlaceholderAssetBundle = AssetBundle.LoadFromFile($"{Application.streamingAssetsPath}/{ResourcePathDefinitions.placeholderBundleFullPath}");
            if (m_PlaceholderAssetBundle == null)
            {
                m_Placeholders = Array.Empty<Mesh>();
                return false;
            }

            // fetch collection
            var collections = m_PlaceholderAssetBundle.LoadAllAssets<MeshCollection>();
            m_Placeholders = collections[0].Meshes;

            if (m_MemoryPageCount < m_Placeholders.Length)
                Array.Resize(ref m_Placeholders, m_MemoryPageCount);

            // create RenderParams objects
            if (m_PlaceholderMaterial != null)
            {
                m_PlaceholderRenderParams = new RenderParams[m_Placeholders.Length];
                for (int i = 0; i < m_Placeholders.Length; i++)
                {
                    m_Placeholders[i].RecalculateBounds();

                    var param = new RenderParams(m_PlaceholderMaterial);
                    param.worldBounds = m_Placeholders[i].bounds;
                    param.shadowCastingMode = ShadowCastingMode.On;
                    param.receiveShadows = true;

                    m_PlaceholderRenderParams[i] = param;
                }
            }
#if UNITY_EDITOR
            else
                Debug.LogError($"[Virtual Mesh] Placeholder material not found");

            Debug.Log($"[Virtual Mesh] Retrieved {m_Placeholders.Length} different placeholder meshes");
#endif
            return true;
        }

        /// <summary>
        /// Checks if any streaming jobs are currently running.
        /// </summary>
        private bool AnyDataJobRunning()
        {
            bool running = false;
            for (int i = 0; i < k_UploadBufferCount; i++)
                running = running || m_DataJobRunning[i];

            return running;
        }

        /// <summary>
        /// Allocates the indirect draw argument buffer when first used.
        /// This buffer is created separately because its size depends on metadata loaded with I/O delays.
        /// </summary>
        private void AllocateDrawBuffers()
        {
            var drawArgs = new GraphicsBuffer.IndirectDrawIndexedArgs[m_Materials.Length];
            for (int i = 0; i < m_Materials.Length; i++)
                drawArgs[i] = new GraphicsBuffer.IndirectDrawIndexedArgs { instanceCount = 1 };
            m_DrawArgsBuffer = new GraphicsBuffer(GraphicsBuffer.Target.IndirectArguments, m_Materials.Length, GraphicsBuffer.IndirectDrawIndexedArgs.size);
            m_DrawArgsBuffer.SetData(drawArgs);
        }

        /// <summary>
        /// Allocates shadow caster-related buffers when first used.
        /// These buffers are created separately because their size can change based on cascade count settings.
        /// </summary>
        private void AllocateShadowDrawBuffers()
        {
            if (m_CompactedIndexBuffer != null)
                m_CompactedIndexBuffer.Dispose();

            // the index buffer used for draws must fit [shadow cascade count + 1] times the max number of indices (one for drawing opaques plus one for each cascade)
            m_CompactedIndexBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Index | GraphicsBuffer.Target.Raw, (m_ShadowCacadeCount + 1) * m_PageStrideValues[1] * m_LoadableMemoryPageCount, sizeof(uint));

            if (m_ShadowDrawArgsBuffer != null)
                m_ShadowDrawArgsBuffer.Dispose();

            var shadowDrawArgs = new GraphicsBuffer.IndirectDrawIndexedArgs[m_ShadowCacadeCount];
            for (int i = 0; i < m_ShadowCacadeCount; i++)
                shadowDrawArgs[i] = new GraphicsBuffer.IndirectDrawIndexedArgs { instanceCount = 1, startIndex = (uint)((i + 1) * m_PageStrideValues[1] * m_LoadableMemoryPageCount) };
            m_ShadowDrawArgsBuffer = new GraphicsBuffer(GraphicsBuffer.Target.IndirectArguments, m_ShadowCacadeCount, GraphicsBuffer.IndirectDrawIndexedArgs.size);
            m_ShadowDrawArgsBuffer.SetData(shadowDrawArgs);
        }

        /// <summary>
        /// Allocates all resources and buffers used by the virtual mesh runtime.
        /// </summary>
        private void AllocateData()
        {
            if (m_Initialized)
                return;

            // metadata buffers
            m_MeshHeader = new NativeArray<uint>((int)MeshHeaderValue.Size * m_MemoryPageCount, Allocator.Persistent);

            m_PageStrideConstants = new GraphicsBuffer(GraphicsBuffer.Target.Constant, 8, sizeof(uint));
            m_PageStrideConstants.SetData(new uint[8] {
                (uint)m_PageStrideValues[0],
                (uint)m_PageStrideValues[1],
                (uint)(m_PageStrideValues[2] / k_GroupDataSize),
                (uint)m_LoadableMemoryPageCount,
                (uint)(k_MemoryPageMaxInstanceCount * m_LoadableMemoryPageCount),
                (uint)m_MemoryPageCount,
                (uint)(m_PageStrideValues[1] * m_LoadableMemoryPageCount / 96 / 4),
                BitConverter.ToUInt32(BitConverter.GetBytes(m_CameraLoadDistanceThreshold), 0)
            });

            m_PageDataBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Raw, m_MemoryPageCount * 4, sizeof(uint));

#if UNITY_EDITOR
            if (m_PageStrideValues[0] * m_LoadableMemoryPageCount > int.MaxValue || m_PageStrideValues[1] * m_LoadableMemoryPageCount > int.MaxValue)
            {
                Debug.LogError("[Virtual Mesh] WARNING: Vertex/Index data buffers overflow");
                return;
            }
#endif

            // gpu geometry data buffers
            m_VertexPositionBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Raw, m_PageStrideValues[0] * m_LoadableMemoryPageCount, sizeof(uint));
            m_VertexAttributeBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Raw, m_PageStrideValues[0] * m_LoadableMemoryPageCount * 2, sizeof(uint));
            m_IndexBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Raw, m_PageStrideValues[1] * m_LoadableMemoryPageCount, sizeof(uint));

            m_TriangleVisibilityBuffer = new GraphicsBuffer[2];
            m_TriangleVisibilityBuffer[0] = new GraphicsBuffer(GraphicsBuffer.Target.Raw, m_PageStrideValues[1] * m_LoadableMemoryPageCount / 96, sizeof(uint));
            m_TriangleVisibilityBuffer[1] = new GraphicsBuffer(GraphicsBuffer.Target.Raw, m_PageStrideValues[1] * m_LoadableMemoryPageCount / 96, sizeof(uint));

            m_GroupDataBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Raw, m_PageStrideValues[2] * m_LoadableMemoryPageCount, sizeof(uint));
            m_InstanceDataBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Raw, m_PageStrideValues[3] * m_LoadableMemoryPageCount, sizeof(uint));
            m_TriangleDataBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Raw, k_MemoryPageMaxInstanceCount * m_LoadableMemoryPageCount * 3, sizeof(uint));
            m_ShadowTriangleDataBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Raw, k_MemoryPageMaxInstanceCount * m_LoadableMemoryPageCount * 3, sizeof(uint));

            // streaming buffers
            m_DataJobRunning = new bool[k_UploadBufferCount];
            for (int i = 0; i < k_UploadBufferCount; i++)
                m_DataJobRunning[i] = false;
            m_LoadMeshDataJobHandles = new JobHandle[k_UploadBufferCount];

            m_PageIDs = new int[k_UploadBufferCount];
            m_SlotIDs = new int[k_UploadBufferCount];

            m_MemoryPageStatus = new NativeArray<MemoryPageStatus>(m_MemoryPageCount, Allocator.Persistent);

            m_LoadedMemoryPages = new NativeArray<int>(m_LoadableMemoryPageCount, Allocator.Persistent);
            for (int i = 0; i < m_LoadableMemoryPageCount; i++)
                m_LoadedMemoryPages[i] = -1;

            m_FeedbackBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Raw, k_MaxMemoryPageCount, sizeof(uint));
            m_PageStatusBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Raw, GraphicsBuffer.UsageFlags.LockBufferForWrite, m_LoadableMemoryPageCount, sizeof(uint));

            m_UploadBufferSize = m_PageStrideValues[0] + m_PageStrideValues[0] * 2 + m_PageStrideValues[1] + m_PageStrideValues[2] + m_PageStrideValues[3];
            m_UploadBuffers = new FencedBufferPool[k_UploadBufferCount];
            for (int i = 0; i < k_UploadBufferCount; i++)
                m_UploadBuffers[i] = new FencedBufferPool(m_UploadBufferSize, sizeof(uint));

            m_UploadIndexQueue = new Queue<int>(m_LoadableMemoryPageCount);
            m_UnloadIndexQueue = new Queue<int>(m_LoadableMemoryPageCount);

            // indirect argument buffers
            var dispatchArgs = new uint[6] { 0, 1, 1, 0, 1, 1 };
            m_DispatchArgsBuffer = new GraphicsBuffer(GraphicsBuffer.Target.IndirectArguments, 2, sizeof(uint) * 6);
            m_DispatchArgsBuffer.SetData(dispatchArgs);
        }

        /// <summary>
        /// Disposes all resources and buffers used by the virtual mesh runtime.
        /// </summary>
        private void DisposeData()
        {
            if (!m_Initialized)
                return;

            if (m_HeaderJobRunning || AnyDataJobRunning())
            {
                m_LoadMeshHeaderJobHandle.Complete();
                m_HeaderJobRunning = false;

                for (int i = 0; i < k_UploadBufferCount; i++)
                {
                    m_LoadMeshDataJobHandles[i].Complete();
                    m_DataJobRunning[i] = false;
                }
            }

            AsyncGPUReadback.WaitAllRequests();

            m_MemoryPageStatus.Dispose();
            m_MemoryPageStatus = default;

            m_MeshHeader.Dispose();
            m_MeshHeader = default;

            m_PageStrideConstants.Dispose();
            m_PageDataBuffer.Release();

            m_VertexPositionBuffer.Release();
            m_VertexAttributeBuffer.Release();
            m_IndexBuffer.Release();
            m_CompactedIndexBuffer.Release();

            m_TriangleVisibilityBuffer[0].Release();
            m_TriangleVisibilityBuffer[1].Release();

            m_GroupDataBuffer.Release();
            m_InstanceDataBuffer.Release();
            m_TriangleDataBuffer.Release();
            m_ShadowTriangleDataBuffer.Release();

            m_LoadedMemoryPages.Dispose();
            m_LoadedMemoryPages = default;

            m_FeedbackBuffer.Release();
            m_PageStatusBuffer.Release();

            for (int i = 0; i < m_UploadBuffers.Length; i++)
                m_UploadBuffers[i].Dispose();

            m_DispatchArgsBuffer.Release();
            m_DrawArgsBuffer.Release();
            m_ShadowDrawArgsBuffer.Release();

            m_Initialized = false;
        }

        /// <summary>
        /// Dispatches jobs to perform memory page streaming from disk to GPU upload buffers.
        /// </summary>
        private void StreamingJobsKickoff()
        {
            // consume unload tasks
            while (m_UnloadIndexQueue.Count != 0)
            {
                var index = m_UnloadIndexQueue.Dequeue();
                m_LoadedMemoryPages[index] = -1;
                DispatchStatusBufferUpdate(-1, index);
            }

            // consume upload tasks
            bool couldLoad = true;
            while (m_UploadIndexQueue.Count != 0 && couldLoad)
            {
                couldLoad = false;

                // search for an open page slot
                for (int i = 0; i < m_LoadableMemoryPageCount; i++)
                {
                    if (m_UploadIndexQueue.Count == 0)
                        break;

                    if (m_LoadedMemoryPages[i] == -1)
                    {
                        // search for an available upload buffer
                        for (int j = 0; j < k_UploadBufferCount; j++)
                        {
                            if (!m_DataJobRunning[j])
                            {
                                m_PageIDs[j] = m_UploadIndexQueue.Dequeue();
                                m_SlotIDs[j] = i;

                                m_MemoryPageStatus[m_PageIDs[j]] = MemoryPageStatus.Loading;
                                m_LoadedMemoryPages[m_SlotIDs[j]] = -2;

                                var uploadArray = LockUploadBufferForWrite(j, m_UploadBufferSize);

                                var groupCount = m_MeshHeader[m_PageIDs[j] * (int)MeshHeaderValue.Size + (int)MeshHeaderValue.TotalGroupCount];
                                var instanceCount = m_MeshHeader[m_PageIDs[j] * (int)MeshHeaderValue.Size + (int)MeshHeaderValue.TotalInstanceCount];
                                var vertexCount = m_MeshHeader[m_PageIDs[j] * (int)MeshHeaderValue.Size + (int)MeshHeaderValue.VertexValueCount];
                                var indexCount = m_MeshHeader[m_PageIDs[j] * (int)MeshHeaderValue.Size + (int)MeshHeaderValue.IndexValueCount];

                                LoadMeshDataJob job = new LoadMeshDataJob
                                {
                                    pageID = m_PageIDs[j],
                                    slotID = m_SlotIDs[j],

                                    vertexValueCount = (int)vertexCount,
                                    indexValueCount = (int)indexCount,
                                    groupDataCount = (int)groupCount * k_GroupDataSize,
                                    instanceDataCount = (int)instanceCount * k_InstanceDataSize,

                                    vertexValueOffset = m_PageStrideValues[0],
                                    indexValueOffset = m_PageStrideValues[1],
                                    groupDataOffset = m_PageStrideValues[2],
                                    instanceDataOffset = m_PageStrideValues[3],

                                    array = uploadArray
                                };
                                m_LoadMeshDataJobHandles[j] = job.Schedule();
                                m_DataJobRunning[j] = true;

                                couldLoad = true;

                                break;
                            }
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Checks for finishing streaming jobs that require GPU data copies.
        /// </summary>
        private void StreamingJobsWrapup()
        {
            for (int i = 0; i < k_UploadBufferCount; i++)
            {
                if (m_DataJobRunning[i] && m_LoadMeshDataJobHandles[i].IsCompleted)
                {
                    // dispatch compute copies
                    DispatchCopies(i);

                    // update status
                    m_MemoryPageStatus[m_PageIDs[i]] = MemoryPageStatus.Loaded;
                    m_LoadedMemoryPages[m_SlotIDs[i]] = m_PageIDs[i];
                    DispatchStatusBufferUpdate(m_PageIDs[i], m_SlotIDs[i]);

                    if (IsPlaceholderEnabled && _ExternalDeactivatePlacholderCallback != null)
                        _ExternalDeactivatePlacholderCallback(m_PageIDs[i]);

                    // flag job as done
                    m_DataJobRunning[i] = false;
                }
            }
        }

        /// <summary>
        /// Dispatches GPU copies from upload buffers into resident geometry buffers after streaming.
        /// </summary>
        private void DispatchCopies(int uploadBufferID)
        {
            if (m_CopyPassesShader == null)
                return;

            var uploadBuffer = UnlockUploadBufferForWrite(uploadBufferID, m_UploadBufferSize);

            int threadGroupCount;

            // copy vertex positions
            int valueCount = Mathf.FloorToInt(m_PageStrideValues[0] / 4.0f);
            int passCount = Mathf.CeilToInt(valueCount / 64000.0f);
            for (int j = 0; j < passCount; j++)
            {
                threadGroupCount = valueCount > 64000 ? 1000 : Mathf.CeilToInt(valueCount / 64.0f);
				m_CopyPassesShader.SetInt(VirtualMeshShaderProperties.CopySourceStart, j * 64000 * 4);
				m_CopyPassesShader.SetInt(VirtualMeshShaderProperties.CopyDestStart, m_SlotIDs[uploadBufferID] * m_PageStrideValues[0] + j * 64000 * 4);
				m_CopyPassesShader.SetInt(VirtualMeshShaderProperties.CopyLength, (j == passCount - 1 ? valueCount : 64000) * 4);
				m_CopyPassesShader.SetBuffer(0, VirtualMeshShaderProperties.CopySourceSRV, uploadBuffer);
				m_CopyPassesShader.SetBuffer(0, VirtualMeshShaderProperties.CopyDestUAV, m_VertexPositionBuffer);
				m_CopyPassesShader.Dispatch(0, threadGroupCount, 1, 1);

                valueCount -= 64000;
            }

            // copy vertex attributes
            valueCount = Mathf.FloorToInt(m_PageStrideValues[0] * 2 / 4.0f);
            passCount = Mathf.CeilToInt(valueCount / 64000.0f);
            for (int j = 0; j < passCount; j++)
            {
                threadGroupCount = valueCount > 64000 ? 1000 : Mathf.CeilToInt(valueCount / 64.0f);
				m_CopyPassesShader.SetInt(VirtualMeshShaderProperties.CopySourceStart, m_PageStrideValues[0] + j * 64000 * 4);
				m_CopyPassesShader.SetInt(VirtualMeshShaderProperties.CopyDestStart, m_SlotIDs[uploadBufferID] * m_PageStrideValues[0] * 2 + j * 64000 * 4);
				m_CopyPassesShader.SetInt(VirtualMeshShaderProperties.CopyLength, (j == passCount - 1 ? valueCount : 64000) * 4);
				m_CopyPassesShader.SetBuffer(0, VirtualMeshShaderProperties.CopySourceSRV, uploadBuffer);
				m_CopyPassesShader.SetBuffer(0, VirtualMeshShaderProperties.CopyDestUAV, m_VertexAttributeBuffer);
				m_CopyPassesShader.Dispatch(0, threadGroupCount, 1, 1);

                valueCount -= 64000;
            }

            // copy indices
            valueCount = Mathf.FloorToInt(m_PageStrideValues[1] / 4.0f);
            passCount = Mathf.CeilToInt(valueCount / 64000.0f);
            for (int j = 0; j < passCount; j++)
            {
                threadGroupCount = valueCount > 64000 ? 1000 : Mathf.CeilToInt(valueCount / 64.0f);
				m_CopyPassesShader.SetInt(VirtualMeshShaderProperties.CopySourceStart, m_PageStrideValues[0] + m_PageStrideValues[0] * 2 + j * 64000 * 4);
				m_CopyPassesShader.SetInt(VirtualMeshShaderProperties.CopyDestStart, m_SlotIDs[uploadBufferID] * m_PageStrideValues[1] + j * 64000 * 4);
				m_CopyPassesShader.SetInt(VirtualMeshShaderProperties.CopyLength, (j == passCount - 1 ? valueCount : 64000) * 4);
				m_CopyPassesShader.SetBuffer(0, VirtualMeshShaderProperties.CopySourceSRV, uploadBuffer);
				m_CopyPassesShader.SetBuffer(0, VirtualMeshShaderProperties.CopyDestUAV, m_IndexBuffer);
				m_CopyPassesShader.Dispatch(0, threadGroupCount, 1, 1);

                valueCount -= 64000;
            }

            // copy group data
            {
                threadGroupCount = Mathf.CeilToInt(m_PageStrideValues[2] / 4.0f / 64.0f);
				m_CopyPassesShader.SetInt(VirtualMeshShaderProperties.CopySourceStart, m_PageStrideValues[0] + m_PageStrideValues[0] * 2 + m_PageStrideValues[1]);
				m_CopyPassesShader.SetInt(VirtualMeshShaderProperties.CopyDestStart, m_SlotIDs[uploadBufferID] * m_PageStrideValues[2]);
				m_CopyPassesShader.SetInt(VirtualMeshShaderProperties.CopyLength, m_PageStrideValues[2]);
				m_CopyPassesShader.SetBuffer(0, VirtualMeshShaderProperties.CopySourceSRV, uploadBuffer);
				m_CopyPassesShader.SetBuffer(0, VirtualMeshShaderProperties.CopyDestUAV, m_GroupDataBuffer);
				m_CopyPassesShader.Dispatch(0, threadGroupCount, 1, 1);
            }

            // copy instance data
            {
                threadGroupCount = Mathf.CeilToInt(m_PageStrideValues[3] / 4.0f / 64.0f);
				m_CopyPassesShader.SetInt(VirtualMeshShaderProperties.CopySourceStart, m_PageStrideValues[0] + m_PageStrideValues[0] * 2 + m_PageStrideValues[1] + m_PageStrideValues[2]);
				m_CopyPassesShader.SetInt(VirtualMeshShaderProperties.CopyDestStart, m_SlotIDs[uploadBufferID] * m_PageStrideValues[3]);
				m_CopyPassesShader.SetInt(VirtualMeshShaderProperties.CopyLength, m_PageStrideValues[3]);
				m_CopyPassesShader.SetBuffer(0, VirtualMeshShaderProperties.CopySourceSRV, uploadBuffer);
				m_CopyPassesShader.SetBuffer(0, VirtualMeshShaderProperties.CopyDestUAV, m_InstanceDataBuffer);
				m_CopyPassesShader.Dispatch(0, threadGroupCount, 1, 1);
            }
        }

        void OnEnable()
        {
            BetterStreamingAssets.Initialize();

            if (RequestMetadata())
            {
                RequestMaterials();

                AllocateData();
                AllocateDrawBuffers();
                AllocateShadowDrawBuffers();

                RequestHeaders();

                m_Initialized = true;
            }

            s_Instance = this;
        }

        void OnDisable()
        {
            if (s_Instance != this)
                return;

            if (m_MaterialAssetBundle != null)
                m_MaterialAssetBundle.Unload(true);

            if (m_PlaceholderAssetBundle != null)
                m_PlaceholderAssetBundle.Unload(true);

            s_Instance = null;

            DisposeData();
        }

        void Update()
        {
            if (!m_Initialized)
                return;

            // only called once to intercept header jobs finishing
            if (m_HeaderJobRunning && m_LoadMeshHeaderJobHandle.IsCompleted)
            {
                m_HeaderJobRunning = false;

                var temp = new uint[m_MemoryPageCount * 4];
                for (int i = 0; i < m_MemoryPageCount; i++)
                {
#if UNITY_EDITOR
                    if (m_MeshHeader[i * (int)MeshHeaderValue.Size + (int)MeshHeaderValue.TotalInstanceCount] > 65536)
                        Debug.LogError("[Virtual Mesh] WARNING: Page max instance count overflow");
#endif
                    temp[i * 4 + 0] = m_MeshHeader[i * (int)MeshHeaderValue.Size + (int)MeshHeaderValue.packedBoundsX];
                    temp[i * 4 + 1] = m_MeshHeader[i * (int)MeshHeaderValue.Size + (int)MeshHeaderValue.packedBoundsY];
                    temp[i * 4 + 2] = m_MeshHeader[i * (int)MeshHeaderValue.Size + (int)MeshHeaderValue.packedBoundsZ];
                    temp[i * 4 + 3] = m_MeshHeader[i * (int)MeshHeaderValue.Size + (int)MeshHeaderValue.TotalInstanceCount];
                }
                m_PageDataBuffer.SetData(temp);

                RequestPlaceholders();
            }

            if (IsEnabled)
            {
                if (IsBoundingMeshEnabled)
                    DrawBoundingMesh();

#if UNITY_EDITOR || DEVELOPMENT_BUILD
                if (!IsDebugViewEnabled)
#endif
                {
                    DrawVirtualMesh();
                    DrawPlaceholders();
                }
            }

            m_PingPongBufferIndex = ++m_PingPongBufferIndex % 2;
        }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        private GUIStyle m_Style = null;

        void OnGUI()
        {
            if (!m_Initialized || !IsEnabled)
                return;

            int unloaded = 0;
            int loaded = 0;
            int waiting = 0;
            for (int i = 0; i < m_MemoryPageCount; i++)
            {
                switch (m_MemoryPageStatus[i])
                {
                    case MemoryPageStatus.Unloaded:
                        {
                            unloaded++;
                        }
                        break;
                    case MemoryPageStatus.Loaded:
                        {
                            loaded++;
                        }
                        break;
                    case MemoryPageStatus.Waiting:
                        {
                            waiting++;
                        }
                        break;
                    case MemoryPageStatus.TooFar:
                        {
                            unloaded++;
                        }
                        break;
                }
            }

            if (m_Style == null)
            {
                m_Style = new GUIStyle();
                m_Style.fontSize = 30;
                m_Style.normal.textColor = Color.white;
            }

            GUI.Label(new Rect(10, 10, 1200, 30), $"Graphics Device: {SystemInfo.graphicsDeviceType}, Shader Level: {SystemInfo.graphicsShaderLevel}, Compute: {SystemInfo.supportsComputeShaders} (Async: {SystemInfo.supportsAsyncCompute}), GPU Readback: {SystemInfo.supportsAsyncGPUReadback}", m_Style);
            GUI.Label(new Rect(10, 40, 1200, 30), $"Memory page status: {loaded} loaded, {unloaded} unloaded, {waiting} waiting (job running: {AnyDataJobRunning()})", m_Style);
        }
#endif
    }

    /// <summary>
    /// Pooled version of a GraphicsBuffer.
    /// </summary>
    internal class BufferPool : IDisposable
    {
        private List<GraphicsBuffer> m_Buffers;
        private Stack<int> m_FreeBufferIds;

        private int m_Count;
        private int m_Stride;
        private GraphicsBuffer.Target m_Target;
        private GraphicsBuffer.UsageFlags m_UsageFlags;

        public BufferPool(int count, int stride, GraphicsBuffer.Target target, GraphicsBuffer.UsageFlags usageFlags)
        {
            m_Buffers = new List<GraphicsBuffer>();
            m_FreeBufferIds = new Stack<int>();

            m_Count = count;
            m_Stride = stride;
            m_Target = target;
            m_UsageFlags = usageFlags;
        }

        public void Dispose()
        {
            for (int i = 0; i < m_Buffers.Count; ++i)
                m_Buffers[i].Dispose();
        }

        private int AllocateBuffer()
        {
            var id = m_Buffers.Count;
            var cb = new GraphicsBuffer(m_Target, m_UsageFlags, m_Count, m_Stride);
            m_Buffers.Add(cb);
            return id;
        }

        public int GetBufferId()
        {
            if (m_FreeBufferIds.Count == 0)
                return AllocateBuffer();

            return m_FreeBufferIds.Pop();
        }

        public GraphicsBuffer GetBufferFromId(int id)
        {
            return m_Buffers[id];
        }

        public void PutBufferId(int id)
        {
            m_FreeBufferIds.Push(id);
        }

        public int TotalBufferCount => m_Buffers.Count;
        public int TotalBufferSize => TotalBufferCount * m_Count * m_Stride;
    }

    /// <summary>
    /// Fenced buffer that requires at least 3 frames before allowing reuse.
    /// For use with async GPU readbacks.
    /// </summary>
    internal class FencedBufferPool : IDisposable
    {
        public int BufferSize { get; private set; }

        Queue<int> m_FrameData;

        BufferPool m_DataBufferPool;

        int m_CurrentFrameBufferID;

        public FencedBufferPool(int size, int stride)
        {
            m_FrameData = new Queue<int>();
            m_CurrentFrameBufferID = -1;
            m_DataBufferPool = new BufferPool(size, stride, GraphicsBuffer.Target.Raw, GraphicsBuffer.UsageFlags.LockBufferForWrite);
            BufferSize = size;
        }

        public void Dispose()
        {
            m_DataBufferPool?.Dispose();
            m_CurrentFrameBufferID = -1;
        }

        public void BeginFrame()
        {
            Assert.IsTrue(m_CurrentFrameBufferID == -1);
            RecoverBuffers();
            m_CurrentFrameBufferID = m_DataBufferPool.GetBufferId();
        }

        public void EndFrame()
        {
            Assert.IsFalse(m_CurrentFrameBufferID == -1);
            m_FrameData.Enqueue(m_CurrentFrameBufferID);
            m_CurrentFrameBufferID = -1;
        }

        public GraphicsBuffer GetCurrentFrameBuffer()
        {
            Assert.IsFalse(m_CurrentFrameBufferID == -1);
            return m_DataBufferPool.GetBufferFromId(m_CurrentFrameBufferID);
        }

        void RecoverBuffers()
        {
            while (m_FrameData.Count > Math.Max(3, QualitySettings.maxQueuedFrames) + 1) // hardcoded (cf. SparseUploader.cs in Entities Graphics)
                m_DataBufferPool.PutBufferId(m_FrameData.Dequeue());
        }
    }
}
