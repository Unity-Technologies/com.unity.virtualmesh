using System.Runtime.InteropServices;

namespace MeshOptimizer
{
    internal static unsafe class MeshOptimizerNative
    {
        private const string MeshOptimizerDLL = "meshoptimizer.dll";
        
        [DllImport(MeshOptimizerDLL, CharSet = CharSet.Auto, CallingConvention = CallingConvention.Cdecl)]
        internal static extern uint meshopt_generateVertexRemap(uint* destination, uint* indices, uint indexCount, void* vertices, uint vertexCount, uint vertexSize);
        
        [DllImport(MeshOptimizerDLL, CharSet = CharSet.Auto, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void meshopt_remapIndexBuffer(uint* destination, uint* indices, uint indexCount, uint* remap);
        
        [DllImport(MeshOptimizerDLL, CharSet = CharSet.Auto, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void meshopt_remapVertexBuffer(void* destination, void* vertices, uint vertexCount, uint vertexSize, uint* remap);
        
        [DllImport(MeshOptimizerDLL, CharSet = CharSet.Auto, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void meshopt_optimizeVertexCache(uint* destination, uint* indices, uint indexCount, uint vertexCount);
        
        [DllImport(MeshOptimizerDLL, CharSet = CharSet.Auto, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void meshopt_optimizeOverdraw(uint* destination, uint* indices, uint indexCount, void* vertexPositions, uint vertexCount, uint stride, float threshold);

        [DllImport(MeshOptimizerDLL, CharSet = CharSet.Auto, CallingConvention = CallingConvention.Cdecl)]
        internal static extern uint meshopt_optimizeVertexFetch(void* destination, uint* indices, uint indexCount, void* vertices, uint vertexCount, uint vertexSize);

        [DllImport(MeshOptimizerDLL, CharSet = CharSet.Auto, CallingConvention = CallingConvention.Cdecl)]
        internal static extern uint meshopt_simplify(uint* destination, uint* indices, uint indexCount, void* vertexPositions, uint vertexCount, uint vertexPositionsstride, uint targetindexCount, float targetError, uint options, float* resultError);

        [DllImport(MeshOptimizerDLL, CharSet = CharSet.Auto, CallingConvention = CallingConvention.Cdecl)]
        internal static extern uint meshopt_simplifyWithAttributes(uint* destination, uint* indices, uint indexCount, void* vertexPositions, uint vertexCount, uint vertexPositionsStride, void* vertexAttributes, uint vertexAttributesStride, float* attributeWeights, uint attributeCount, byte* vertexLock, uint targetIndexCount, float targetError, uint options, float* resultError);

        [DllImport(MeshOptimizerDLL, CharSet = CharSet.Auto, CallingConvention = CallingConvention.Cdecl)]
        internal static extern uint meshopt_simplifySloppy(uint* destination, uint* indices, uint indexCount, void* vertexPositions, uint vertexCount, uint vertexPositionsstride, uint targetindexCount, float targetError, float* resultError);

        [DllImport(MeshOptimizerDLL, CharSet = CharSet.Auto, CallingConvention = CallingConvention.Cdecl)]
        internal static extern uint meshopt_buildMeshlets(void* meshlets, uint* meshletvertices, byte* meshletTriangles, uint* indices, uint indexCount, void* vertexPositions, uint vertexCount, uint vertexPositionsstride, uint maxvertices, uint maxTriangles, float coneWeight);

        [DllImport(MeshOptimizerDLL, CharSet = CharSet.Auto, CallingConvention = CallingConvention.Cdecl)]
        internal static extern uint meshopt_buildMeshletsBound(uint indexCount, uint maxvertices, uint maxTriangles);

        [DllImport(MeshOptimizerDLL, CharSet = CharSet.Auto, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void meshopt_optimizeMeshlet(uint* meshletvertices, byte* meshletTriangles, uint triangleCount, uint vertexCount);

        [DllImport(MeshOptimizerDLL, CharSet = CharSet.Auto, CallingConvention = CallingConvention.Cdecl)]
        internal static extern uint meshopt_partitionClusters(uint* destination, uint* clusterIndices, uint totalIndexCount, uint* clusterIndexCounts, uint clusterCount, uint vertexCount, uint targetPartitionSize);

        [DllImport(MeshOptimizerDLL, CharSet = CharSet.Auto, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void meshopt_spatialSortRemap(uint* destination, void* vertexPositions, uint vertexCount, uint vertexPositionsstride);

        [DllImport(MeshOptimizerDLL, CharSet = CharSet.Auto, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void meshopt_spatialSortTriangles(uint* destination, uint* indices, uint indexCount, void* vertexPositions, uint vertexCount, uint vertexPositionsstride);
    }
}
