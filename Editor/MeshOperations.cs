using System;
using static MeshOptimizer.MeshOptimizerNative;

namespace Unity.VirtualMesh.Editor
{
    internal static unsafe class MeshOperations
    {
        internal static Tuple<Vertex[], uint[]> Reindex(Vertex[] vertices, uint[] indices, uint vertexSize)
        {
            var remap = new uint[vertices.Length];

            fixed (uint* remapPtr = remap)
            fixed (uint* indicesPtr = indices)
            fixed (Vertex* verticesPtr = vertices)
            {
                uint indexCount = (uint)(indices?.Length ?? vertices.Length);
                var totalVertices = meshopt_generateVertexRemap(remapPtr, indicesPtr, indexCount, verticesPtr, (uint)vertices.Length, vertexSize);

                var outIndices = new uint[indexCount];
                fixed (uint* outIndicesPtr = outIndices)
                {
                    meshopt_remapIndexBuffer(outIndicesPtr, indicesPtr, indexCount, remapPtr);
                }

                var outVertices = new Vertex[totalVertices];
                fixed (Vertex* outVerticesPtr = outVertices)
                {
                    meshopt_remapVertexBuffer(outVerticesPtr, verticesPtr, (uint)vertices.Length, vertexSize, remapPtr);
                }

                return Tuple.Create(outVertices, outIndices);
            }
        }

        internal static void OptimizeVertexCache(uint[] indices, uint vertexCount)
        {
            fixed (uint* indicesPtr = indices)
            {
                meshopt_optimizeVertexCache(indicesPtr, indicesPtr, (uint)indices.Length, vertexCount);
            }
        }

        internal static void OptimizeOverdraw(uint[] indices, Vertex[] vertices, uint stride, float threshold)
        {
            fixed (uint* indicesPtr = indices)
            fixed (Vertex* verticesPtr = vertices)
            {
                meshopt_optimizeOverdraw(indicesPtr, indicesPtr, (uint)indices.Length, verticesPtr, (uint)vertices.Length, stride, threshold);
            }
        }

        internal static uint OptimizeVertexFetch(uint[] indices, Vertex[] vertices, uint vertexSize)
        {
            fixed (uint* indicesPtr = indices)
            fixed (Vertex* verticesPtr = vertices)
            {
                return meshopt_optimizeVertexFetch(verticesPtr, indicesPtr, (uint)indices.Length, verticesPtr, (uint)vertices.Length, vertexSize);
            }
        }

        internal static uint Simplify(uint[] indices, Vertex[] vertices, uint stride, uint targetIndexCount, float targetError, uint options, float[] resultError)
        {
            var attributeWeights = new float[] {
                0.5f, // tangent X
                0.5f, // tangent Y
                0.5f, // tangent Z
                0.0f, // tangent W
                0.8f, // normal X
                0.8f, // normal Y
                0.8f, // normal Z
                0.0f, // color R
                0.0f, // color G
                0.0f, // color B
                0.5f, // UV0 U
                0.5f, // UV0 V
                0.2f, // UV1 U
                0.2f, // UV1 V
            };

            fixed (uint* indicesPtr = indices)
            fixed (Vertex* verticesPtr = vertices)
            fixed (float* attributeWeightsPtr = attributeWeights)
            fixed (float* resultErrorPtr = resultError)
            {
                //return meshopt_simplify(indicesPtr, indicesPtr, (uint)indices.Length, verticesPtr, (uint)vertices.Length, stride, targetIndexCount, targetError, options, resultErrorPtr);
                return meshopt_simplifyWithAttributes(indicesPtr, indicesPtr, (uint)indices.Length, verticesPtr, (uint)vertices.Length, stride, &(verticesPtr[0].tangent), stride, attributeWeightsPtr, stride / 4 - 3, null, targetIndexCount, targetError, options, resultErrorPtr);
            }
        }

        internal static uint BuildMeshlets(Meshlet[] meshlets, uint[] meshletVertices, byte[] meshletTriangles, uint[] indices, Vertex[] vertices, uint stride, uint maxVertices, uint maxTriangles, float coneWeight)
        {
            fixed (Meshlet* meshletsPtr = meshlets)
            fixed (uint* meshletVerticesPtr = meshletVertices)
            fixed (byte* meshletTrianglesPtr = meshletTriangles)
            fixed (uint* indicesPtr = indices)
            fixed (Vertex* verticesPtr = vertices)
            {
                var result = meshopt_buildMeshlets(meshletsPtr, meshletVerticesPtr, meshletTrianglesPtr, indicesPtr, (uint)indices.Length, verticesPtr, (uint)vertices.Length, stride, maxVertices, maxTriangles, coneWeight);

                for (int i = 0; i < result; i++)
                {
                    var meshlet = meshletsPtr[i];
                    meshopt_optimizeMeshlet(&meshletVerticesPtr[meshlet.vertexOffset], &meshletTrianglesPtr[meshlet.triangleOffset], meshlet.triangleCount, meshlet.vertexCount);
                }

                return result;
            }
        }

        internal static uint BuildMeshletsBound(uint indexCount, uint maxVertices, uint maxTriangles)
        {
            return meshopt_buildMeshletsBound(indexCount, maxVertices, maxTriangles);
        }

        internal static uint PartitionMeshlets(uint[] destination, uint[] clusterIndices, uint[] clusterSizes, Vertex[] vertices, uint targetPartitionSize)
        {
            fixed (uint* destinationPtr = destination)
            fixed (uint* clusterIndicesPtr = clusterIndices)
            fixed (uint* clusterSizesPtr = clusterSizes)
            {
                return meshopt_partitionClusters(destinationPtr, clusterIndicesPtr, (uint)clusterIndices.Length, clusterSizesPtr, (uint)clusterSizes.Length, (uint)vertices.Length, targetPartitionSize);
            }
        }

        internal static void SpatialSortTriangles(uint[] indices, Vertex[] vertices, uint stride)
        {
            fixed (uint* indicesPtr = indices)
            fixed (Vertex* verticesPtr = vertices)
            {
                meshopt_spatialSortTriangles(indicesPtr, indicesPtr, (uint)indices.Length, verticesPtr, (uint)vertices.Length, stride);
            }
        }
    }
}
