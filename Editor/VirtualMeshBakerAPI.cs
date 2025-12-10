using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Collections;
using Unity.Mathematics;
using Unity.VirtualMesh.Runtime;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Unity.VirtualMesh.Editor
{
    /// <summary>
    /// The data representing a meshlet intended to work with meshoptimizer.
    /// </summary>
    internal struct Meshlet
    {
        public uint vertexOffset;
        public uint triangleOffset;
        public uint vertexCount;
        public uint triangleCount;
    };

    /// <summary>
    /// The data representing a single vertex intended to work with meshoptimizer.
    /// This structure contains the vertex attributes that are supported during conversion to virtual mesh data.
    /// </summary>
    internal struct Vertex
    {
        public Vector3 position;
        public Vector4 tangent;
        public Vector3 normal;
        public Vector3 color;
        public Vector2 uv0;
        public Vector2 uv1;
    };

    /// <summary>
    /// The core logic of the virtual mesh baking system is contained in this class.
    /// </summary>
    public static unsafe class VirtualMeshBakerAPI
    {
        const int k_ClusterTriangleCount = 64;
        const int k_ClusterVertexMaxCount = 120;
        const int k_MemoryPageMaxInstanceCount = 1600;
        const int k_MemoryPageCount = 256;

        const bool k_SimplifyPlaceholders = true;
        const bool k_ExportOBJ = false;
        const bool k_PackIndices = true;
        const bool k_PackClusterGroupVertices = true;

        /// <summary>
        /// Fills a list of MeshFilter objects that should be baked based on a root object specified by the user.
        /// </summary>
        /// <param name="root">Root GameObject that contains all geometry to consider for baking under its hierarchy.</param>
        /// <param name="list">List of MeshFilters that should be used for baking.</param>
        /// <param name="bakeInactiveObjects">Flag to consider inactive GameObjects when filling the output list.</param>
        public static bool GetFilterList(GameObject root, List<MeshFilter> list, bool bakeInactiveObjects = false)
        {
            if (root == null)
                return false;

            int progressIndex = 0;
            var lodGroups = root.GetComponentsInChildren<LODGroup>(bakeInactiveObjects);
            var meshFilters = root.GetComponentsInChildren<MeshFilter>(bakeInactiveObjects);

            foreach (var group in lodGroups)
            {
                progressIndex++;
                if (UpdateEditorProgressBar(
                    $"Collecting mesh filters: {progressIndex} / {lodGroups.Length + meshFilters.Length}",
                    progressIndex * 1.0f / (lodGroups.Length + meshFilters.Length)))
                    return false;

                Renderer[] renderers = group.GetLODs()[0].renderers;
                foreach (var renderer in renderers)
                {
                    if (renderer == null || renderer.GetType() != typeof(MeshRenderer))
                        continue;

                    var filter = renderer.gameObject.GetComponent<MeshFilter>();
                    if (filter.sharedMesh == null)
                        continue;

                    list.Add(filter);
                }
            }

            foreach (var filter in meshFilters)
            {
                progressIndex++;
                if (UpdateEditorProgressBar(
                    $"Collecting mesh filters: {progressIndex} / {lodGroups.Length + meshFilters.Length}",
                    progressIndex * 1.0f / (lodGroups.Length + meshFilters.Length)))
                    return false;

                if (filter.sharedMesh == null)
                    continue;

                // only pick filters not belonging to any LODGroup
                bool found = false;
                foreach (var group in root.GetComponentsInChildren<LODGroup>(bakeInactiveObjects))
                {
                    foreach (var lod in group.GetLODs())
                    {
                        foreach (var renderer in lod.renderers)
                        {
                            if (renderer == null || renderer.GetType() != typeof(MeshRenderer))
                                continue;

                            if (renderer.gameObject.name.Equals(filter.gameObject.name))
                            {
                                found = true;
                                break;
                            }
                        }

                        if (found) break;
                    }

                    if (found) break;
                }

                if (found)
                    continue;

                list.Add(filter);
            }

            EditorUtility.ClearProgressBar();

            return list.Count > 0;
        }

        /// <summary>
        /// Creates the various folders and file structures needed by the virtual mesh baker.
        /// </summary>
        /// <param name="clearCache">Flag to indicate whether the folders should be cleaned up if they already exist.</param>
        public static void EnsureCacheAndSaveDirectories(bool clearCache = false)
        {
            var paths = new List<string>
            {
                $"{Application.dataPath}/VirtualMeshCache",
                $"{Application.dataPath}/VirtualMeshCache/Shaders",
                $"{Application.dataPath}/VirtualMeshCache/Materials",
                $"{Application.dataPath}/VirtualMeshCache/Meshes",
                $"{Application.dataPath}/VirtualMeshCache/Obj",
                $"{Application.streamingAssetsPath}/{ResourcePathDefinitions.virtualMeshDataPath}",
                $"{Application.streamingAssetsPath}/{ResourcePathDefinitions.materialBundlePath}",
                $"{Application.streamingAssetsPath}/{ResourcePathDefinitions.placeholderBundlePath}"
            };

            foreach (var path in paths)
            {
                var directory = Directory.Exists(path) ? new DirectoryInfo(path) : Directory.CreateDirectory(path);

                if (clearCache)
                    foreach (FileInfo file in directory.GetFiles())
                    {
                        if (path.Equals($"{Application.streamingAssetsPath}/{ResourcePathDefinitions.virtualMeshDataPath}"))
                        {
                            if (!file.Name.Equals("Materials.meta") &&
                                !file.Name.Equals("Placeholders.meta"))
                                file.Delete();
                        }
                        else if (path.Equals($"{Application.dataPath}/VirtualMeshCache"))
                        {
                            if (!file.Name.Equals("Shader.meta") &&
                                !file.Name.Equals("Materials.meta") &&
                                !file.Name.Equals("Meshes.meta") &&
                                !file.Name.Equals("Obj.meta"))
                                file.Delete();
                        }
                        else
                            file.Delete();
                    }
            }
        }

        /// <summary>
        /// Converts shaders into versions that contain vertex shaders that the virtual mesh system supports.
        /// </summary>
        /// <param name="meshFilters">List of MeshFilters to bake.</param>
        /// <param name="bakeOpaqueObjectsOnly">Flag to indicate if only objects with opaque render queues should be baked.</param>
        public static bool ConvertShaders(List<MeshFilter> meshFilters, bool bakeOpaqueObjectsOnly = true)
        {
            if (meshFilters.Count == 0)
                return false;

            var materials = new List<Material>();

            // build unique material list
            foreach (var filter in meshFilters)
            {
                var mesh = filter.sharedMesh;
                if (mesh.subMeshCount != filter.gameObject.GetComponent<MeshRenderer>().sharedMaterials.Length)
                    continue;

                for (int k = 0; k < mesh.subMeshCount; k++)
                {
                    var desc = mesh.GetSubMesh(k);
                    var tri = mesh.GetTriangles(k);
                    var material = filter.gameObject.GetComponent<MeshRenderer>().sharedMaterials[k];

                    if (desc.topology != MeshTopology.Triangles)
                        continue;

                    if (!CheckSupportedShader(material.shader, bakeOpaqueObjectsOnly))
                        continue;

                    int materialIndex = materials.FindIndex(x => material.GetInstanceID().Equals(x.GetInstanceID()));
                    if (materialIndex == -1)
                    {
                        materialIndex = materials.Count;
                        materials.Add(material);
                    }
                }
            }

            int progressIndex = 0;

            // generate shader file assets
            for (int i = 0; i < materials.Count; i++)
            {
                progressIndex++;
                if (UpdateEditorProgressBar(
                    $"Converting materials: {progressIndex} / {materials.Count}",
                    progressIndex * 1.0f / materials.Count))
                    return false;

                var shader = materials[i].shader;
                var path = AssetDatabase.GetAssetPath(shader);

                if (Path.GetExtension(path).Equals(".shader"))
                {
                    // only URP Lit is supported by default but other shaders can be baked here as well
                    if (shader.name.Equals("Universal Render Pipeline/Lit"))
                    {
                        // get source and hack it
                        var source = File.ReadAllText(path, Encoding.UTF8).Replace(
                            "Universal Render Pipeline/Lit",
                            "Universal Render Pipeline/Lit_VMESH")
                            .Replace(
                            "Packages/com.unity.render-pipelines.universal/Shaders/LitForwardPass.hlsl",
                            "Packages/com.unity.virtualmesh/Runtime/ShaderLibrary/LitForwardPass.hlsl")
                            .Replace(
                            "Packages/com.unity.render-pipelines.universal/Shaders/LitGBufferPass.hlsl",
                            "Packages/com.unity.virtualmesh/Runtime/ShaderLibrary/LitGBufferPass.hlsl")
                            .Replace(
                            "Packages/com.unity.render-pipelines.universal/Shaders/ShadowCasterPass.hlsl",
                            "Packages/com.unity.virtualmesh/Runtime/ShaderLibrary/LitShadowCasterPass.hlsl")
                            .Replace(
                            "Packages/com.unity.render-pipelines.universal/Shaders/DepthOnlyPass.hlsl",
                            "Packages/com.unity.virtualmesh/Runtime/ShaderLibrary/LitDepthOnlyPass.hlsl")
                            .Replace(
                            "Packages/com.unity.render-pipelines.universal/Shaders/LitDepthNormalsPass.hlsl",
                            "Packages/com.unity.virtualmesh/Runtime/ShaderLibrary/LitDepthNormalsPass.hlsl")
                            .Replace(
                            "Packages/com.unity.render-pipelines.universal/Shaders/Utils/Universal2D.hlsl",
                            "Packages/com.unity.virtualmesh/Runtime/ShaderLibrary/LitUniversal2DPass.hlsl")
                            .Replace("Name \"GBuffer\"", "Name \"GBuffer\" Stencil { Ref 33 ReadMask 0 WriteMask 96 Comp Always Pass Replace Fail Keep ZFail Keep }");

                        // output shader file
                        File.WriteAllText($"{ResourcePathDefinitions.shadersCachePath}/{Path.GetFileNameWithoutExtension(path)}_VMESH.shader", source);
                        AssetDatabase.ImportAsset($"{ResourcePathDefinitions.shadersCachePath}/{Path.GetFileNameWithoutExtension(path)}_VMESH.shader");
                    }
                }
                else if (Path.GetExtension(path).Equals(".shadergraph"))
                {
                    // get source and hack it
                    var source = ShaderGraphHelper.GetShaderText(path).Replace(
                        "Packages/com.unity.render-pipelines.universal/Editor/ShaderGraph/Includes/PBRForwardPass.hlsl",
                        "Packages/com.unity.virtualmesh/Runtime/ShaderLibrary/PBRForwardPass.hlsl")
                        .Replace(
                        "Packages/com.unity.render-pipelines.universal/Editor/ShaderGraph/Includes/PBRGBufferPass.hlsl",
                        "Packages/com.unity.virtualmesh/Runtime/ShaderLibrary/PBRGBufferPass.hlsl")
                        .Replace(
                        "Packages/com.unity.render-pipelines.universal/Editor/ShaderGraph/Includes/ShadowCasterPass.hlsl",
                        "Packages/com.unity.virtualmesh/Runtime/ShaderLibrary/PBRShadowCasterPass.hlsl")
                        .Replace(
                        "Packages/com.unity.render-pipelines.universal/Editor/ShaderGraph/Includes/DepthOnlyPass.hlsl",
                        "Packages/com.unity.virtualmesh/Runtime/ShaderLibrary/PBRDepthOnlyPass.hlsl")
                        .Replace(
                        "Packages/com.unity.render-pipelines.universal/Editor/ShaderGraph/Includes/DepthNormalsOnlyPass.hlsl",
                        "Packages/com.unity.virtualmesh/Runtime/ShaderLibrary/PBRDepthNormalsOnlyPass.hlsl")
                        .Replace(
                        "Packages/com.unity.render-pipelines.universal/Editor/ShaderGraph/Includes/PBR2DPass.hlsl",
                        "Packages/com.unity.virtualmesh/Runtime/ShaderLibrary/PBR2DPass.hlsl")
                        .Replace("Name \"GBuffer\"", "Name \"GBuffer\" Stencil { Ref 33 ReadMask 0 WriteMask 96 Comp Always Pass Replace Fail Keep ZFail Keep }");

                    // output shader file
                    File.WriteAllText($"{ResourcePathDefinitions.shadersCachePath}/{Path.GetFileNameWithoutExtension(path)}_VMESH.shader", source);
                    AssetDatabase.ImportAsset($"{ResourcePathDefinitions.shadersCachePath}/{Path.GetFileNameWithoutExtension(path)}_VMESH.shader");
                }
            }

            return true;
        }

        /// <summary>
        /// Converts meshes into custom binary files used by the virtual mesh system at runtime.
        /// This also outputs asset bundles containing placeholder meshes and materials.
        /// </summary>
        /// <param name="meshFilters">List of MeshFilters to bake.</param>
        /// <param name="bakeOpaqueObjectsOnly">Flag to indicate if only objects with opaque render queues should be baked.</param>
        /// <param name="simplificationTargetError">Flag to indicate if only objects with opaque render queues should be baked.</param>
        public static void ConvertMeshes(List<MeshFilter> meshFilters, bool bakeOpaqueObjectsOnly = true, float simplificationTargetError = 0.01f)
        {
            var materials = new List<Material>();
            var materialData = new List<MaterialData>();

            if (meshFilters.Count == 0)
            {
                Debug.Log("[Virtual Mesh] Convert Meshes: CONVERSION FINISHED (no input)");
                return;
            }

            // prepare page headers
            var memoryPageData = new List<MemoryPageData>(k_MemoryPageCount);
            for (int i = 0; i < k_MemoryPageCount; i++)
            {
                memoryPageData.Add(new MemoryPageData
                {
                    totalInstanceCount = 0,
                    totalGroupCount = 0,
                    vertexValueCount = 0,
                    indexValueCount = 0,
                    leafClusterCount = 0,
                    combinedBoundsMin = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue),
                    combinedBoundsMax = new Vector3(float.MinValue, float.MinValue, float.MinValue),
                    placeholderVertices = new List<Vertex>(),
                    placeholderIndices = new List<uint>()
                });
            }

            int submeshCount = 0;
            int degenerativeClusterCount = 0;

            uint bakingVertexByteSize = (uint)sizeof(Vertex);
            uint meshletVertexMaxCount = k_ClusterVertexMaxCount;
            uint meshletTriangleMaxCount = k_ClusterTriangleCount;

            int progressIndex = 0;

            // process input mesh filters
            foreach (var filter in meshFilters)
            {
                progressIndex++;
                if (UpdateEditorProgressBar(
                    $"Converting meshes: {progressIndex} / {meshFilters.Count}",
                    progressIndex * 1.0f / meshFilters.Count))
                    return;
                
                var mesh = filter.sharedMesh;
                var renderer = filter.gameObject.GetComponent<MeshRenderer>();
                var transform = filter.transform;

                var sharedMaterials = renderer.sharedMaterials;

                // submesh and materials counts differ
                if (mesh.subMeshCount != sharedMaterials.Length)
                    continue;

                // deal with negative scales
                bool negativeScale = CheckOddNegativeScale(filter);

                mesh.RecalculateNormals();
                mesh.RecalculateTangents();
                mesh.RecalculateBounds();

                var v = mesh.vertices;
                var n = mesh.normals;
                var t = mesh.tangents;
                var uv0 = mesh.uv;
                var uv1 = mesh.uv2;
                var col = mesh.colors;

                var vertices = new List<Vertex>();
                var indices = new List<uint>();

                for (int k = 0; k < mesh.subMeshCount; k++)
                {
                    var desc = mesh.GetSubMesh(k);
                    var tri = mesh.GetTriangles(k);
                    var material = sharedMaterials[k];

                    if (desc.topology != MeshTopology.Triangles)
                        continue;

                    if (!CheckSupportedShader(material.shader, bakeOpaqueObjectsOnly))
                        continue;

                    // identify material
                    int materialIndex = materials.FindIndex(x => material.GetInstanceID().Equals(x.GetInstanceID()));
                    if (materialIndex == -1)
                    {
                        materialIndex = materials.Count;
                        materials.Add(material);
                        materialData.Add(new MaterialData
                        {
                            vertexCount = 0
                        });
                    }

                    submeshCount++;
                    vertices.Clear();
                    indices.Clear();

                    // fill buffers
                    var defaultVertexColor = Color.cyan;
                    for (int i = 0; i < tri.Length; i += 3)
                    {
                        var tangent0 = transform.localToWorldMatrix.MultiplyVector(new Vector3(t[tri[i + 0]].x, t[tri[i + 0]].y, t[tri[i + 0]].z));
                        var color0 = col.Length != 0 ? col[tri[i + 0]] : defaultVertexColor;
                        var vertex0 = new Vertex()
                        {
                            position = transform.localToWorldMatrix.MultiplyPoint(v[tri[i + 0]]),
                            normal = n.Length != 0 ? transform.localToWorldMatrix.MultiplyVector(n[tri[i + 0]]) : Vector3.zero,
                            tangent = t.Length != 0 ? new Vector4(tangent0.x, tangent0.y, tangent0.z, t[tri[i + 0]].w) : Vector4.zero,
                            color = new Vector3(color0.r, color0.g, color0.b),
                            uv0 = uv0.Length != 0 ? uv0[tri[i + 0]] : Vector2.zero,
                            uv1 = uv1.Length != 0 ? uv1[tri[i + 0]] : Vector2.zero
                        };
                        var tangent1 = transform.localToWorldMatrix.MultiplyVector(new Vector3(t[tri[i + 1]].x, t[tri[i + 1]].y, t[tri[i + 1]].z));
                        var color1 = col.Length != 0 ? col[tri[i + 1]] : defaultVertexColor;
                        var vertex1 = new Vertex()
                        {
                            position = transform.localToWorldMatrix.MultiplyPoint(v[tri[i + 1]]),
                            normal = n.Length != 0 ? transform.localToWorldMatrix.MultiplyVector(n[tri[i + 1]]) : Vector3.zero,
                            tangent = t.Length != 0 ? new Vector4(tangent1.x, tangent1.y, tangent1.z, t[tri[i + 1]].w) : Vector4.zero,
                            color = new Vector3(color1.r, color1.g, color1.b),
                            uv0 = uv0.Length != 0 ? uv0[tri[i + 1]] : Vector2.zero,
                            uv1 = uv1.Length != 0 ? uv1[tri[i + 1]] : Vector2.zero
                        };
                        var tangent2 = transform.localToWorldMatrix.MultiplyVector(new Vector3(t[tri[i + 2]].x, t[tri[i + 2]].y, t[tri[i + 2]].z));
                        var color2 = col.Length != 0 ? col[tri[i + 2]] : defaultVertexColor;
                        var vertex2 = new Vertex()
                        {
                            position = transform.localToWorldMatrix.MultiplyPoint(v[tri[i + 2]]),
                            normal = n.Length != 0 ? transform.localToWorldMatrix.MultiplyVector(n[tri[i + 2]]) : Vector3.zero,
                            tangent = t.Length != 0 ? new Vector4(tangent2.x, tangent2.y, tangent2.z, t[tri[i + 2]].w) : Vector4.zero,
                            color = new Vector3(color2.r, color2.g, color2.b),
                            uv0 = uv0.Length != 0 ? uv0[tri[i + 2]] : Vector2.zero,
                            uv1 = uv1.Length != 0 ? uv1[tri[i + 2]] : Vector2.zero
                        };

                        vertices.Add(negativeScale ? vertex1 : vertex0);
                        indices.Add((uint)indices.Count);

                        vertices.Add(negativeScale ? vertex0 : vertex1);
                        indices.Add((uint)indices.Count);

                        vertices.Add(vertex2);
                        indices.Add((uint)indices.Count);
                    }

                    // optimize and process LOD0
                    var result = MeshOperations.Reindex(vertices.ToArray(), indices.ToArray(), bakingVertexByteSize);
                    var verticesLOD0 = result.Item1;
                    var indicesLOD0 = result.Item2;

                    // compress size to f16 at submesh granularity to avoid seams when packing later
                    for (int i = 0; i < verticesLOD0.Length; i++)
                    {
                        var lowPrecisionPosX = math.f16tof32(math.f32tof16(verticesLOD0[i].position.x));
                        var lowPrecisionPosY = math.f16tof32(math.f32tof16(verticesLOD0[i].position.y));
                        var lowPrecisionPosZ = math.f16tof32(math.f32tof16(verticesLOD0[i].position.z));

                        verticesLOD0[i].position = new Vector3(lowPrecisionPosX, lowPrecisionPosY, lowPrecisionPosZ);
                    }

                    // compute meshlets
                    MeshOperations.SpatialSortTriangles(indicesLOD0, verticesLOD0, bakingVertexByteSize);
                    var meshletsLOD0MaxCount = MeshOperations.BuildMeshletsBound((uint)indicesLOD0.Length, meshletVertexMaxCount, meshletTriangleMaxCount);
                    var meshletsLOD0 = new Meshlet[meshletsLOD0MaxCount];
                    var meshletVerticesLOD0 = new uint[meshletsLOD0MaxCount * meshletVertexMaxCount];
                    var meshletTrianglesLOD0 = new byte[meshletsLOD0MaxCount * meshletTriangleMaxCount * 3];
                    var meshletCountLOD0 = MeshOperations.BuildMeshlets(meshletsLOD0, meshletVerticesLOD0, meshletTrianglesLOD0, indicesLOD0, verticesLOD0, bakingVertexByteSize, meshletVertexMaxCount, meshletTriangleMaxCount, 0.0f);

                    // create partitions of meshlets to simplify together
                    var meshletPartition = new uint[meshletCountLOD0];
                    var meshletIndicesLOD0 = new List<uint>();
                    var meshletSizesLOD0 = new uint[meshletCountLOD0];
                    for (int i = 0; i < meshletCountLOD0; i++)
                    {
                        var meshlet = meshletsLOD0[i];
                        for (int j = 0; j < meshlet.triangleCount * 3; j++)
                        {
                            meshletIndicesLOD0.Add(meshletVerticesLOD0[meshlet.vertexOffset + meshletTrianglesLOD0[meshlet.triangleOffset + j]]);
                        }

                        meshletSizesLOD0[i] = meshlet.triangleCount * 3;
                    }
                    uint partitionTargetSize = k_PackIndices ? 5 : 16; // max is 5 for packed indices for now (2^10 indices = 1024 verts = 5 leaf clusters of 192 indices)
                    var partitionCountLOD0 = MeshOperations.PartitionMeshlets(meshletPartition, meshletIndicesLOD0.ToArray(), meshletSizesLOD0, verticesLOD0, partitionTargetSize);

                    var meshlets = new List<Meshlet>[partitionCountLOD0];
                    for (int i = 0; i < meshletCountLOD0; i++)
                    {
                        uint partition = meshletPartition[i];
                        if (meshlets[partition] == null)
                            meshlets[partition] = new List<Meshlet>();

                        meshlets[partition].Add(meshletsLOD0[i]);
                    }

                    var copyIndices = new List<uint>();
                    var lodIndices = new List<uint>();

                    uint optimizeIndexCount = 0;
                    var optimizeIndices = new List<List<uint>>();
                    uint optimizeVertexCount = 0;
                    var optimizeVertices = new List<List<Vertex>>();
                    var optimizeVerticesPacked = new List<Vertex>();

                    var clusterTypes = new List<uint>();
                    var clusterChildrenTypes = new List<uint>();
                    var clusterErrors = new List<uint>();
                    var clusterChildrenErrors = new List<uint>();

                    // process meshlets by group
                    for (int p = 0; p < partitionCountLOD0; p++)
                    {
                        if (meshlets[p].Count == 0)
                            continue;

                        uint clusterCount = (uint)meshlets[p].Count;

                        if (k_PackClusterGroupVertices)
                            copyIndices.Clear();
                        lodIndices.Clear();

                        optimizeIndexCount = 0;
                        optimizeIndices.Clear();
                        optimizeVerticesPacked.Clear();
                        if (!k_PackClusterGroupVertices)
                        {
                            optimizeVertexCount = 0;
                            optimizeVertices.Clear();
                        }
                        clusterTypes.Clear();
                        clusterChildrenTypes.Clear();
                        clusterErrors.Clear();
                        clusterChildrenErrors.Clear();

                        float selfError = 0.0f;

                        // record leaf clusters
                        for (int m = 0; m < meshlets[p].Count; m++)
                        {
                            if (!k_PackClusterGroupVertices)
                            {
                                copyIndices.Clear();
                                optimizeVertices.Add(new List<Vertex>());
                            }
                            optimizeIndices.Add(new List<uint>());
                            clusterChildrenTypes.Add(clusterCount == 1 ? 0x0u : 0x1u);
                            clusterChildrenErrors.Add(0);

                            // gather indices
                            for (int i = 0; i < meshlets[p][m].triangleCount; i++)
                            {
                                for (int l = 0; l < 3; l++)
                                {
                                    uint vertexIndex = meshletVerticesLOD0[meshlets[p][m].vertexOffset + meshletTrianglesLOD0[meshlets[p][m].triangleOffset + i * 3 + l]];
                                    lodIndices.Add(vertexIndex);

                                    var index = copyIndices.IndexOf(vertexIndex);
                                    if (index == -1) // not found
                                    {
                                        index = copyIndices.Count;
                                        copyIndices.Add(vertexIndex);
                                        optimizeVerticesPacked.Add(verticesLOD0[vertexIndex]);

                                        if (!k_PackClusterGroupVertices)
                                        {
                                            optimizeVertexCount++;
                                            optimizeVertices.Last().Add(verticesLOD0[meshletVerticesLOD0[vertexIndex]]);
                                        }
                                    }
                                    optimizeIndexCount++;
                                    optimizeIndices.Last().Add((uint)index);
                                }
                            }

                            if (meshlets[p][m].triangleCount < meshletTriangleMaxCount)
                                degenerativeClusterCount++;
                        }

                        var leafClusterCount = clusterCount;

                        // build cluster hierarchy
                        if (clusterCount > 1)
                        {
                            uint previousMeshletCount = clusterCount;
                            int iterations = 8;
                            for (int j = 0; j < iterations; j++)
                            {
                                // simplify children clusters
                                uint targetIndexCount = (uint)(lodIndices.Count * 0.5f);
                                float[] resultError = new float[1];
                                var lodIndicesArray = lodIndices.ToArray();
                                uint lodIndexCount = MeshOperations.Simplify(lodIndicesArray, verticesLOD0, bakingVertexByteSize, targetIndexCount, simplificationTargetError, 0x1, resultError);

                                // early stop based on simlyfication fail
                                bool reachedSimplifyLimit = lodIndexCount == lodIndicesArray.Length || resultError[0] == 0.0f;

                                var lodIndicesResized = new uint[lodIndexCount];
                                Array.Copy(lodIndicesArray, lodIndicesResized, lodIndexCount);

                                // clusterize the current hierarchy level
                                MeshOperations.SpatialSortTriangles(lodIndicesResized, verticesLOD0, bakingVertexByteSize);
                                var meshletsLODXMaxCount = MeshOperations.BuildMeshletsBound((uint)lodIndicesResized.Length, meshletVertexMaxCount, meshletTriangleMaxCount);
                                var meshletsLODX = new Meshlet[meshletsLODXMaxCount];
                                var meshletVerticesLODX = new uint[meshletsLODXMaxCount * meshletVertexMaxCount];
                                var meshletTrianglesLODX = new byte[meshletsLODXMaxCount * meshletTriangleMaxCount * 3];
                                var meshletCountLODX = MeshOperations.BuildMeshlets(meshletsLODX, meshletVerticesLODX, meshletTrianglesLODX, lodIndicesResized, verticesLOD0, bakingVertexByteSize, meshletVertexMaxCount, meshletTriangleMaxCount, 0.0f);

                                // compute LOD projection error
                                selfError += resultError[0];
                                for (int q = 0; q < clusterChildrenErrors.Count; q++)
                                    clusterChildrenErrors[q] = math.f32tof16(ComputeLODProjectionError(selfError)) << 16 | clusterChildrenErrors[q];

                                // early stop based on clusterization fail
                                bool reachedClusterizationLimit = meshletCountLODX == previousMeshletCount;
                                previousMeshletCount = meshletCountLODX;

                                if (reachedSimplifyLimit || reachedClusterizationLimit)
                                {
                                    for (int q = 0; q < clusterChildrenTypes.Count; q++)
                                        clusterChildrenTypes[q] = j == 0 ? 0x0u : 0x3u;

                                    for (int q = 0; q < clusterChildrenErrors.Count; q++)
                                        clusterChildrenErrors[q] = j == 0 ? 0 : clusterChildrenErrors[q];

                                    // don't record this hierarchy level
                                    break;
                                }
                                else
                                {
                                    clusterTypes.AddRange(clusterChildrenTypes);
                                    clusterChildrenTypes.Clear();

                                    clusterErrors.AddRange(clusterChildrenErrors);
                                    clusterChildrenErrors.Clear();
                                }

                                // early stop based on hierachy root reached
                                bool reachedRoot = meshletCountLODX == 1 || j == iterations - 1;

                                // record parent clusters
                                for (int m = 0; m < meshletCountLODX; m++)
                                {
                                    if (!k_PackClusterGroupVertices)
                                    {
                                        copyIndices.Clear();
                                        optimizeVertices.Add(new List<Vertex>());
                                    }
                                    optimizeIndices.Add(new List<uint>());
                                    clusterChildrenTypes.Add(reachedRoot ? 0x3u : 0x2u);
                                    clusterChildrenErrors.Add(math.f32tof16(ComputeLODProjectionError(selfError)));

                                    // gather indices
                                    for (int i = 0; i < meshletsLODX[m].triangleCount; i++)
                                    {
                                        for (int l = 0; l < 3; l++)
                                        {
                                            uint vertexIndex = meshletVerticesLODX[meshletsLODX[m].vertexOffset + meshletTrianglesLODX[meshletsLODX[m].triangleOffset + i * 3 + l]];

                                            var index = copyIndices.IndexOf(vertexIndex);
                                            if (index == -1) // not found
                                            {
                                                if (k_PackClusterGroupVertices)
                                                    Debug.LogError($"[Virtual Mesh] Found index on parent cluster not belonging to any child");
                                                else
                                                {
                                                    index = copyIndices.Count;
                                                    copyIndices.Add(vertexIndex);

                                                    if (!k_PackClusterGroupVertices)
                                                    {
                                                        optimizeVertexCount++;
                                                        optimizeVertices.Last().Add(verticesLOD0[vertexIndex]);
                                                    }
                                                }
                                            }
                                            optimizeIndexCount++;
                                            optimizeIndices.Last().Add((uint)index);
                                        }
                                    }

                                    if (meshletsLODX[m].triangleCount < meshletTriangleMaxCount)
                                        degenerativeClusterCount++;
                                }

                                // break if reached root
                                clusterCount += meshletCountLODX;
                                if (reachedRoot)
                                    break;

                                lodIndices.Clear();
                                lodIndices.AddRange(lodIndicesResized);
                            }
                        }

                        clusterTypes.AddRange(clusterChildrenTypes);
                        clusterErrors.AddRange(clusterChildrenErrors);

                        // select page
                        int selectedPageIndex = 0;
                        {
                            // for now we try to fit every cluster from the same group into the same page
                            while (selectedPageIndex < k_MemoryPageCount && memoryPageData[selectedPageIndex].totalInstanceCount + clusterCount > k_MemoryPageMaxInstanceCount)
                                selectedPageIndex++;

                            // skip the group if we don't find any page that fits it
                            if (selectedPageIndex == k_MemoryPageCount)
                                continue;
                        }

                        // add to placeholder buffers
                        int vertexShiftOffset = memoryPageData[selectedPageIndex].placeholderVertices.Count;
                        memoryPageData[selectedPageIndex].placeholderVertices.AddRange(optimizeVerticesPacked);
                        for (int i = 0; i < leafClusterCount; i++)
                        {
                            var placeHolderIndices = new uint[optimizeIndices[i].Count];
                            for (int j = 0; j < optimizeIndices[i].Count; j++)
                                placeHolderIndices[j] = (uint)vertexShiftOffset + optimizeIndices[i][j];

                            memoryPageData[selectedPageIndex].placeholderIndices.AddRange(placeHolderIndices);
                        }

                        // merge buffers and optimize for locality
                        int indexValueOffset = 0, vertexValueOffset = 0;
                        var optimizedIB = new uint[optimizeIndexCount];
                        var optimizedVB = k_PackClusterGroupVertices ? optimizeVerticesPacked.ToArray() : new Vertex[optimizeVertexCount];
                        for (int i = optimizeIndices.Count - 1; i >= 0; i--)
                        {
                            var indexBuffer = optimizeIndices[i].ToArray();
                            MeshOperations.SpatialSortTriangles(indexBuffer, optimizedVB, bakingVertexByteSize);
                            MeshOperations.OptimizeVertexCache(indexBuffer, k_PackClusterGroupVertices ? (uint)optimizeVerticesPacked.Count : (uint)optimizeVertices[i].Count);
                            MeshOperations.OptimizeOverdraw(indexBuffer, optimizedVB, bakingVertexByteSize, 1.0f);

                            if (!k_PackClusterGroupVertices)
                            {
                                var vertexBuffer = optimizeVertices[i].ToArray();
                                MeshOperations.OptimizeVertexFetch(indexBuffer, vertexBuffer, bakingVertexByteSize);

                                Array.Copy(vertexBuffer, 0, optimizedVB, vertexValueOffset, vertexBuffer.Length);
                                vertexValueOffset += vertexBuffer.Length;
                            }

                            Array.Copy(indexBuffer, 0, optimizedIB, indexValueOffset, indexBuffer.Length);
                            indexValueOffset += indexBuffer.Length;
                        }

                        if (k_PackClusterGroupVertices)
                            MeshOperations.OptimizeVertexFetch(optimizedIB, optimizedVB, bakingVertexByteSize);

                        // add group indices
                        indexValueOffset = 0;
                        vertexValueOffset = 0;
                        int indexValueFetchOffset = 0;
                        for (int i = optimizeIndices.Count - 1; i >= 0; i--)
                        {
                            memoryPageData[selectedPageIndex].ReserveAdditionalIndices(k_PackIndices ? optimizeIndices[i].Count / 3 : optimizeIndices[i].Count);

                            if (k_PackIndices)
                                for (int j = 0; j < optimizeIndices[i].Count; j += 3)
                                {
                                    uint packedIndex = 0;
                                    packedIndex |= optimizedIB[indexValueOffset * 3 + j + 0] & 0x3ff;
                                    packedIndex |= (optimizedIB[indexValueOffset * 3 + j + 1] << 10) & 0xffc00;
                                    packedIndex |= (optimizedIB[indexValueOffset * 3 + j + 2] << 20) & 0x3ff00000;

                                    if (optimizeIndices[i][j + 0] > 0x3ff || optimizeIndices[i][j + 1] > 0x3ff || optimizeIndices[i][j + 2] > 0x3ff)
                                        Debug.LogError("[Virtual Mesh] WARNING: Can't pack index values into 10 bits");

                                    memoryPageData[selectedPageIndex].SetIndex((int)memoryPageData[selectedPageIndex].indexValueCount + indexValueOffset + j / 3, packedIndex);
                                }
                            else
                                for (int j = 0; j < optimizeIndices[i].Count; j++)
                                    memoryPageData[selectedPageIndex].SetIndex((int)memoryPageData[selectedPageIndex].indexValueCount + indexValueOffset + j, optimizedIB[indexValueFetchOffset + j]);

                            uint lodMask = memoryPageData[selectedPageIndex].totalGroupCount << 2 | clusterTypes[i];

                            if (optimizeIndices[i].Count > 256)
                                Debug.LogError($"[Virtual Mesh] WARNING: Can't pack cluster index count into 8 bits, the detected value is {optimizeIndices[i].Count}");

                            int dataStart = memoryPageData[selectedPageIndex].dataNativeArray.Length;
                            memoryPageData[selectedPageIndex].ReserveAdditionalData(4);
                            memoryPageData[selectedPageIndex].SetData(dataStart + 0, memoryPageData[selectedPageIndex].indexValueCount + (uint)indexValueOffset);
                            memoryPageData[selectedPageIndex].SetData(dataStart + 1, memoryPageData[selectedPageIndex].vertexValueCount + (k_PackClusterGroupVertices ? 0 : (uint)vertexValueOffset));
                            memoryPageData[selectedPageIndex].SetData(dataStart + 2, (uint)optimizeIndices[i].Count << 24 | (lodMask & 0xffffff));
                            memoryPageData[selectedPageIndex].SetData(dataStart + 3, clusterErrors[i]);

                            indexValueOffset += k_PackIndices ? optimizeIndices[i].Count / 3 : optimizeIndices[i].Count;
                            if (!k_PackClusterGroupVertices)
                                vertexValueOffset += optimizeVertices[i].Count * 2;
                            indexValueFetchOffset += optimizeIndices[i].Count;
                        }

                        // add group vertices
                        memoryPageData[selectedPageIndex].ReserveAdditionalVertexPositions(optimizedVB.Length * 2);
                        memoryPageData[selectedPageIndex].ReserveAdditionalVertexAttributes(optimizedVB.Length * 4);
                        for (int i = 0; i < optimizedVB.Length; i++)
                        {
                            // position and tangent W
                            memoryPageData[selectedPageIndex].SetVertexPosition((int)memoryPageData[selectedPageIndex].vertexValueCount + i * 2 + 0, math.f32tof16(optimizedVB[i].position.x) << 16 | math.f32tof16(optimizedVB[i].position.y));
                            memoryPageData[selectedPageIndex].SetVertexPosition((int)memoryPageData[selectedPageIndex].vertexValueCount + i * 2 + 1, (optimizedVB[i].tangent.w == 1.0 ? 1u : 0u) << 16 | math.f32tof16(optimizedVB[i].position.z));

                            // normal
                            memoryPageData[selectedPageIndex].SetVertexAttribute((int)memoryPageData[selectedPageIndex].vertexValueCount * 2 + i * 4 + 0, PackNormal(optimizedVB[i].normal));

                            // tangent
                            memoryPageData[selectedPageIndex].SetVertexAttribute((int)memoryPageData[selectedPageIndex].vertexValueCount * 2 + i * 4 + 1, PackTangent(optimizedVB[i].tangent));

                            // uv0 and uv1
                            memoryPageData[selectedPageIndex].SetVertexAttribute((int)memoryPageData[selectedPageIndex].vertexValueCount * 2 + i * 4 + 2, math.f32tof16(optimizedVB[i].uv1.x) << 16 | math.f32tof16(optimizedVB[i].uv0.x));
                            memoryPageData[selectedPageIndex].SetVertexAttribute((int)memoryPageData[selectedPageIndex].vertexValueCount * 2 + i * 4 + 3, math.f32tof16(optimizedVB[i].uv1.y) << 16 | math.f32tof16(optimizedVB[i].uv0.y));
                        }

                        // compute group bounds
                        Vector3 clusterGroupBoundsMin = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
                        Vector3 clusterGroupBoundsMax = new Vector3(float.MinValue, float.MinValue, float.MinValue);
                        for (int i = 0; i < optimizedVB.Length; i++)
                        {
                            clusterGroupBoundsMin = Vector3.Min(clusterGroupBoundsMin, optimizedVB[i].position);
                            clusterGroupBoundsMax = Vector3.Max(clusterGroupBoundsMax, optimizedVB[i].position);
                        }

                        // add group data
                        Vector3 center = (clusterGroupBoundsMax + clusterGroupBoundsMin) * 0.5f;
                        Vector3 extents = (clusterGroupBoundsMax - clusterGroupBoundsMin) * 0.5f;
                        var boundsCenterX = math.f32tof16(center.x);
                        var boundsCenterY = math.f32tof16(center.y);
                        var boundsCenterZ = math.f32tof16(center.z);
                        var boundsExtentsX = math.f32tof16(extents.x);
                        var boundsExtentsY = math.f32tof16(extents.y);
                        var boundsExtentsZ = math.f32tof16(extents.z);
                        int groupDataStart = memoryPageData[selectedPageIndex].groupDataNativeArray.Length;
                        memoryPageData[selectedPageIndex].ReserveAdditionalGroupData(4);
                        memoryPageData[selectedPageIndex].SetGroupData(groupDataStart + 0, boundsCenterX << 16 | boundsExtentsX);
                        memoryPageData[selectedPageIndex].SetGroupData(groupDataStart + 1, boundsCenterY << 16 | boundsExtentsY);
                        memoryPageData[selectedPageIndex].SetGroupData(groupDataStart + 2, boundsCenterZ << 16 | boundsExtentsZ);
                        memoryPageData[selectedPageIndex].SetGroupData(groupDataStart + 3, (uint)materialIndex << 16); // space for material merge

                        // update page data
                        {
                            var pageData = memoryPageData[selectedPageIndex];
                            pageData.totalInstanceCount += (uint)optimizeIndices.Count;
                            pageData.totalGroupCount += 1;
                            pageData.vertexValueCount += (uint)optimizedVB.Length * 2;
                            pageData.indexValueCount += (uint)indexValueOffset;
                            pageData.combinedBoundsMin = Vector3.Min(pageData.combinedBoundsMin, clusterGroupBoundsMin);
                            pageData.combinedBoundsMax = Vector3.Max(pageData.combinedBoundsMax, clusterGroupBoundsMax);
                            memoryPageData[selectedPageIndex] = pageData;
                        }

                        // record leaf cluster stat
                        memoryPageData[selectedPageIndex].leafClusterCount += leafClusterCount;

                        materialData[materialIndex].vertexCount += (uint)optimizedVB.Length;
                    }
                }
            }

            // simplify placeholders
            if (k_SimplifyPlaceholders)
            {
                for (int i = 0; i < k_MemoryPageCount; i++)
                {
                    int decimationIterations = 5;
                    for (int j = 0; j < decimationIterations; j++)
                    {
                        var placeholderIndices = memoryPageData[i].placeholderIndices.ToArray();
                        var placeholderVertices = memoryPageData[i].placeholderVertices.ToArray();
                        var targetIndexCount = (uint)(placeholderIndices.Length * 0.5f);
                        var targetError = 0.1f;
                        var placeholderIndexCount = MeshOperations.Simplify(placeholderIndices, placeholderVertices, bakingVertexByteSize, targetIndexCount, targetError, 0x1, new float[] { 0.0f });

                        if (placeholderIndexCount == placeholderIndices.Length)
                            break;

                        var placeholderIndicesResized = new uint[placeholderIndexCount];
                        Array.Copy(placeholderIndices, placeholderIndicesResized, placeholderIndexCount);

                        var placeholderVertexCount = MeshOperations.OptimizeVertexFetch(placeholderIndicesResized, placeholderVertices, bakingVertexByteSize);

                        var placeholderVerticesResized = new Vertex[placeholderVertexCount];
                        Array.Copy(placeholderVertices, placeholderVerticesResized, placeholderVertexCount);

                        memoryPageData[i].placeholderIndices.Clear();
                        memoryPageData[i].placeholderIndices.AddRange(placeholderIndicesResized);
                        memoryPageData[i].placeholderVertices.Clear();
                        memoryPageData[i].placeholderVertices.AddRange(placeholderVerticesResized);
                    }
                }
            }

            // write OBJ
            if (k_ExportOBJ)
            {
                for (int i = 0; i < k_MemoryPageCount; i++)
                {
                    var pageData = memoryPageData[i];

                    var sb = new StringBuilder();

                    sb.AppendLine(string.Format("# {0} {1}", pageData.placeholderVertices.Count, pageData.placeholderIndices.Count));

                    foreach (var vertex in pageData.placeholderVertices)
                    {
                        sb.AppendLine($"v {string.Format("{0:0.######}", -vertex.position.x)} {string.Format("{0:0.######}", vertex.position.y)} {string.Format("{0:0.######}", vertex.position.z)} {string.Format("{0:0.######}", vertex.color.x)} {string.Format("{0:0.######}", vertex.color.y)} {string.Format("{0:0.######}", vertex.color.z)}");
                        sb.AppendLine($"vn {string.Format("{0:0.######}", -vertex.normal.x)} {string.Format("{0:0.######}", vertex.normal.y)} {string.Format("{0:0.######}", vertex.normal.z)}");
                        sb.AppendLine($"vt {string.Format("{0:0.######}", vertex.uv0.x)} {string.Format("{0:0.######}", vertex.uv0.y)}");
                    }

                    for (int j = 0; j < pageData.placeholderIndices.Count; j += 3)
                    {
                        sb.AppendLine(
                                string.Format(
                                    "f {1}/{1}/{1} {0}/{0}/{0} {2}/{2}/{2}",
                                    pageData.placeholderIndices[j + 0] + 1,
                                    pageData.placeholderIndices[j + 1] + 1,
                                    pageData.placeholderIndices[j + 2] + 1
                                )
                            );
                    }

                    string objPath = $"{Application.dataPath}/VirtualMeshCache/Obj/{i}.obj";
                    using (StreamWriter sw = new StreamWriter(objPath))
                        sw.Write(sb.ToString());
                }
            }

            // generate files
            WriteFiles(memoryPageData, materialData, submeshCount, degenerativeClusterCount);

            BuildMaterialAssetBundle(materials);
            BuildPlaceholderAssetBundle(memoryPageData);

            EditorUtility.ClearProgressBar();
        }

        /// <summary>
        /// Displays and updates an editor progress bar during baking.
        /// </summary>
        private static bool UpdateEditorProgressBar(string info, float progress)
        {
            bool cancelled = EditorUtility.DisplayCancelableProgressBar("Virtual Mesh Baking...", info, progress);
            if (cancelled)
                EditorUtility.ClearProgressBar();

            return cancelled;
        }

        /// <summary>
        /// Checks if the specified shader is supported by the virtual mesh system for rendering.
        /// </summary>
        /// <param name="supportOpaqueOnly">Flag to indicate if only objects with opaque render queues should be supported.</param>
        private static bool CheckSupportedShader(Shader shader, bool supportOpaqueOnly)
        {
            var shaderPath = AssetDatabase.GetAssetPath(shader);
            bool supportedType =
                Path.GetExtension(shaderPath).Equals(".shadergraph") ||
                shader.name.Equals("Universal Render Pipeline/Lit");

            var renderQueue = shader.renderQueue;
            bool supportedQueue = !supportOpaqueOnly || (renderQueue >= (int)RenderQueue.Geometry && renderQueue <= (int)RenderQueue.GeometryLast);

            return supportedType && supportedQueue;
        }

        /// <summary>
        /// Checks if the mesh has flipped scales that influence its triangles' index order and facing direction.
        /// </summary>
        private static bool CheckOddNegativeScale(MeshFilter filter)
        {
            Func<Transform, bool> hasOddNegativeScale = transform =>
            {
                var matrix = transform.worldToLocalMatrix;
                var x = new Vector3(matrix.m00, matrix.m10, matrix.m20);
                var y = new Vector3(matrix.m01, matrix.m11, matrix.m21);
                var z = new Vector3(matrix.m02, matrix.m12, matrix.m22);

                return Vector3.Dot(Vector3.Cross(x, y), z) < 0.0f;
            };

            var transform = filter.transform;
            bool negativeScale = hasOddNegativeScale(transform);
            while (transform.parent != null)
            {
                // odd negative scale is XOR against parent
                negativeScale ^= hasOddNegativeScale(transform.parent);

                transform = transform.parent;
            }

            return negativeScale;
        }

        /// <summary>
        /// Outputs baking data into files on disk.
        /// </summary>
        /// <param name="memoryPageData">List of baked memory page data to be converted into files and built during mesh conversion.</param>
        /// <param name="materialData">List of baked material information to be converted into a metadata file and built during mesh conversion.</param>
        /// <param name="submeshCount">The number of submeshes that have been processed during conversion, used for debug statistics.</param>
        /// <param name="degenerativeClusterCount">The number of generated clusters that are smaller than the maximum allowed size of a meshlet, used for debug statistics.</param>
        private static void WriteFiles(List<MemoryPageData> memoryPageData, List<MaterialData> materialData, int submeshCount, int degenerativeClusterCount)
        {
            // write geometry files
            uint totalInstanceCount = 0;
            uint minInstanceCount = uint.MaxValue;
            uint maxInstanceCount = 0;
            uint maxGroupCount = 0;
            uint maxVertexValueCount = 0;
            uint maxIndexValueCount = 0;
            uint filledPagedCount = 0;
            float averageLeafClusterRatio = 0.0f;
            Vector3 totalBoundsMin = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            Vector3 totalBoundsMax = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            for (int i = 0; i < k_MemoryPageCount; i++)
            {
                var pageData = memoryPageData[i];
                Vector3 center = (pageData.combinedBoundsMax + pageData.combinedBoundsMin) * 0.5f;
                Vector3 extents = (pageData.combinedBoundsMax - pageData.combinedBoundsMin) * 0.5f;
                totalBoundsMin = Vector3.Min(totalBoundsMin, pageData.combinedBoundsMin);
                totalBoundsMax = Vector3.Max(totalBoundsMax, pageData.combinedBoundsMax);

                // generate header file path
                var index = i + 1;
                string name = index.ToString("X8");
                string path = $"{Application.streamingAssetsPath}/{ResourcePathDefinitions.virtualMeshDataPath}/{name}.vmesh";

                // write header file
                using (var fs = new FileStream(path, FileMode.Create))
                {
                    using (var ms = new MemoryStream())
                    {
                        using (var bw = new BinaryWriter(ms))
                        {
                            // converting to f16 likely shrinks the AABB but for the page bounds we don't care
                            // (packing the AABB as center/extents saves a few instructions at runtime)
                            var boundsCenterX = math.f32tof16(center.x);
                            var boundsCenterY = math.f32tof16(center.y);
                            var boundsCenterZ = math.f32tof16(center.z);
                            var boundsExtentsX = math.f32tof16(extents.x);
                            var boundsExtentsY = math.f32tof16(extents.y);
                            var boundsExtentsZ = math.f32tof16(extents.z);
                            bw.Write(boundsCenterX << 16 | boundsExtentsX);
                            bw.Write(boundsCenterY << 16 | boundsExtentsY);
                            bw.Write(boundsCenterZ << 16 | boundsExtentsZ);
                            bw.Write(pageData.totalInstanceCount);
                            bw.Write(pageData.totalGroupCount);
                            bw.Write(pageData.vertexValueCount);
                            bw.Write(pageData.indexValueCount);
                        }
                        var dataArray = ms.ToArray();
                        fs.Write(dataArray, 0, dataArray.Length);
                    }
                    fs.Close();
                }

                path += "data";

                // write data file
                using (var fs = new FileStream(path, FileMode.Create))
                {
                    using (var ms = new MemoryStream())
                    {
                        using (var bw = new BinaryWriter(ms))
                        {
                            foreach (var v in pageData.vertexPositionNativeArray)
                                bw.Write(v);

                            foreach (var v in pageData.vertexAttributeNativeArray)
                                bw.Write(v);

                            foreach (var tri in pageData.indexNativeArray)
                                bw.Write(tri);

                            foreach (var d in pageData.groupDataNativeArray)
                                bw.Write(d);

                            foreach (var d in pageData.dataNativeArray)
                                bw.Write(d);
                        }
                        var dataArray = ms.ToArray();
                        fs.Write(dataArray, 0, dataArray.Length);
                    }
                    fs.Close();
                }

                totalInstanceCount += pageData.totalInstanceCount;
                minInstanceCount = math.min(minInstanceCount, pageData.totalInstanceCount);
                maxInstanceCount = math.max(maxInstanceCount, pageData.totalInstanceCount);
                maxGroupCount = math.max(maxGroupCount, pageData.totalGroupCount);
                maxVertexValueCount = math.max(maxVertexValueCount, pageData.vertexValueCount);
                maxIndexValueCount = math.max(maxIndexValueCount, pageData.indexValueCount);

                if (pageData.leafClusterCount != 0)
                {
                    filledPagedCount++;
                    averageLeafClusterRatio += pageData.leafClusterCount / 1600.0f;
                }

                pageData.DisposeNativeArrays();
            }

            Vector3 totalBoundsCenter = (totalBoundsMax + totalBoundsMin) * 0.5f;
            Vector3 totalBoundsExtents = (totalBoundsMax - totalBoundsMin) * 0.5f;
            averageLeafClusterRatio /= filledPagedCount;

            // write metadata file
            {
                uint totalVertexCount = 0;
                for (int i = 0; i < materialData.Count; i++)
                    totalVertexCount += materialData[i].vertexCount;

                string path = $"{Application.streamingAssetsPath}/{ResourcePathDefinitions.virtualMeshDataPath}/metadata.vmesh";

                // write metadata file
                using (var fs = new FileStream(path, FileMode.Create))
                {
                    using (var ms = new MemoryStream())
                    {
                        using (var bw = new BinaryWriter(ms))
                        {
                            bw.Write(filledPagedCount);
                            bw.Write(totalInstanceCount);
                            bw.Write(maxVertexValueCount);
                            bw.Write(maxIndexValueCount);
                            bw.Write(maxGroupCount);
                            bw.Write(totalBoundsCenter.x);
                            bw.Write(totalBoundsCenter.y);
                            bw.Write(totalBoundsCenter.z);
                            bw.Write(totalBoundsExtents.x);
                            bw.Write(totalBoundsExtents.y);
                            bw.Write(totalBoundsExtents.z);
                            bw.Write(totalVertexCount);
                            bw.Write(materialData.Count);
                            for (int i = 0; i < materialData.Count; i++)
                                bw.Write(materialData[i].vertexCount);
                        }
                        var dataArray = ms.ToArray();
                        fs.Write(dataArray, 0, dataArray.Length);
                    }
                    fs.Close();
                }
            }

            Debug.Log($"[Virtual Mesh] Serialized {totalInstanceCount} instances into {filledPagedCount} pages (min: {minInstanceCount}, max: {maxInstanceCount}) from {submeshCount} submeshes (degenerative cluster ratio: {(float)degenerativeClusterCount / (float)totalInstanceCount}, average leaf cluster ratio: {averageLeafClusterRatio}, max vertex values: {maxVertexValueCount}, max index values: {maxIndexValueCount}, max group count: {maxGroupCount}, material count: {materialData.Count})");
        }

        /// <summary>
        /// Generates and saves an asset bundle containing all the materials used by the virtualized meshes.
        /// </summary>
        private static void BuildMaterialAssetBundle(List<Material> materials)
        {
            var collection = ScriptableObject.CreateInstance<MaterialCollection>();
            collection.Materials = new Material[materials.Count];

            // generate materials
            for (int i = 0; i < materials.Count; i++)
            {
                var shaderPath = $"{ResourcePathDefinitions.shadersCachePath}/{Path.GetFileNameWithoutExtension(AssetDatabase.GetAssetPath(materials[i].shader))}_VMESH.shader";
                var shader = AssetDatabase.LoadAssetAtPath<Shader>(shaderPath);
                if (shader == null)
                {
                    Debug.LogError($"[Virtual Mesh] Could not create copy material of shader: {shaderPath}");
                    continue;
                }

                var copyMaterial = new Material(shader);
                copyMaterial.CopyPropertiesFromMaterial(materials[i]);

                var copyMaterialPath = $"{ResourcePathDefinitions.materialsCachePath}/{i}.mat";
                AssetDatabase.CreateAsset(copyMaterial, copyMaterialPath);

                collection.Materials[i] = copyMaterial;
            }

            // save collection as asset
            AssetDatabase.CreateAsset(collection, ResourcePathDefinitions.materialsCollectionFullPath);
            AssetDatabase.ImportAsset(ResourcePathDefinitions.materialsCollectionFullPath, ImportAssetOptions.ForceUpdate);

            // build asset bundle
            var buildMap = new AssetBundleBuild[]
            {
                new()
                {
                    assetBundleName = ResourcePathDefinitions.materialBundleName,
                    assetNames =  new[] { AssetDatabase.GetAssetPath(collection) }
                }
            };

            BuildPipeline.BuildAssetBundles(
                $"{Application.streamingAssetsPath}/{ResourcePathDefinitions.materialBundlePath}",
                buildMap,
                BuildAssetBundleOptions.ChunkBasedCompression | BuildAssetBundleOptions.ForceRebuildAssetBundle,
                EditorUserBuildSettings.activeBuildTarget);
        }

        /// <summary>
        /// Generates and saves an asset bundle containing the placeholder meshes corresponding to each memory page that has been generated during baking.
        /// </summary>
        private static void BuildPlaceholderAssetBundle(List<MemoryPageData> memoryPageData)
        {
            var collection = ScriptableObject.CreateInstance<MeshCollection>();
            collection.Meshes = new Mesh[memoryPageData.Count];

            // generate placeholders
            for (int i = 0; i < memoryPageData.Count; i++)
            {
                var vertexCount = memoryPageData[i].placeholderVertices.Count;
                var indexCount = memoryPageData[i].placeholderIndices.Count;
                var positionData = new Vector3[vertexCount];
                var normalData = new Vector3[vertexCount];
                var colorData = new Color[vertexCount];
                var uv0Data = new Vector2[vertexCount];
                var indices = new int[indexCount];

                for (int j = 0; j < vertexCount; j++)
                {
                    positionData[j] = memoryPageData[i].placeholderVertices[j].position;
                    normalData[j] = memoryPageData[i].placeholderVertices[j].normal;
                    var color = memoryPageData[i].placeholderVertices[j].color;
                    colorData[j] = new Color(color.x, color.y, color.z);
                    uv0Data[j] = memoryPageData[i].placeholderVertices[j].uv0;
                }

                for (int j = 0; j < indexCount; j++)
                    indices[j] = (int)memoryPageData[i].placeholderIndices[j];

                // create mesh
                var mesh = new Mesh();
                mesh.indexFormat = IndexFormat.UInt32;
                mesh.SetVertices(positionData);
                mesh.SetNormals(normalData);
                mesh.SetColors(colorData);
                mesh.SetUVs(0, uv0Data);
                mesh.SetTriangles(indices, 0);

                mesh.RecalculateBounds();
                mesh.RecalculateNormals();
                mesh.RecalculateTangents();

                string path = $"{ResourcePathDefinitions.meshesCachePath}/{i}.asset";
                AssetDatabase.CreateAsset(mesh, path);

                collection.Meshes[i] = mesh;
            }

            // save collection as asset
            AssetDatabase.CreateAsset(collection, ResourcePathDefinitions.meshesCollectionFullPath);
            AssetDatabase.ImportAsset(ResourcePathDefinitions.meshesCollectionFullPath, ImportAssetOptions.ForceUpdate);

            // build asset bundle
            var buildMap = new AssetBundleBuild[]
            {
                new()
                {
                    assetBundleName = ResourcePathDefinitions.placeholderBundleName,
                    assetNames =  new[] { AssetDatabase.GetAssetPath(collection) }
                }
            };

            BuildPipeline.BuildAssetBundles(
                $"{Application.streamingAssetsPath}/{ResourcePathDefinitions.placeholderBundlePath}",
                buildMap,
                BuildAssetBundleOptions.ChunkBasedCompression | BuildAssetBundleOptions.ForceRebuildAssetBundle,
                EditorUserBuildSettings.activeBuildTarget);
        }

        /// <summary>
        /// Packs a single vertex's normal data into 32 bits using octahedral encoding.
        /// </summary>
        private static uint PackNormal(float3 n)
        {
            n /= math.max(math.dot(math.abs(n), 1.0f), 1e-6f);
            float t = math.saturate(-n.z);

            float2 result = n.xy + new float2(n.x >= 0.0f ? t : -t, n.y >= 0.0f ? t : -t);
            result.x = math.saturate(result.x * 0.5f + 0.5f) * 65535.0f;
            result.y = math.saturate(result.y * 0.5f + 0.5f) * 65535.0f;

            return (uint)result.x << 16 | (uint)result.y & 0xffff;
        }

        /// <summary>
        /// Packs a single vertex's pair of UV data into 32 bits using 16-bit quantization.
        /// </summary>
        private static uint PackUV(float2 uv)
        {
            uv.x *= 65535.0f;
            uv.y *= 65535.0f;
            uv.x += 0.5f;
            uv.y += 0.5f;

            return (uint)uv.x << 16 | (uint)uv.y & 0xffff;
        }

        /// <summary>
        /// Packs a single vertex's tangent data into 32 bits using octahedral encoding (the fourth component is saved separately).
        /// </summary>
        private static uint PackTangent(float4 n)
        {
            n /= math.max(math.dot(math.abs(n.xyz), 1.0f), 1e-6f);
            float t = math.saturate(-n.z);

            float2 result = n.xy + new float2(n.x >= 0.0f ? t : -t, n.y >= 0.0f ? t : -t);
            result.x = math.saturate(result.x * 0.5f + 0.5f) * 65535.0f;
            result.y = math.saturate(result.y * 0.5f + 0.5f) * 65535.0f;
            //result.y = math.saturate(result.y * 0.5f + 0.5f) * 32767.0f;

            return (uint)result.x << 16 | (uint)result.y & 0xffff;
            //return (uint)result.x << 16 | ((uint)result.y & 0x7fff) << 1 | (n.w == 1.0f ? 0x0u : 0x1u);
        }

        /// <summary>
        /// Computes the projected size's factor of a triangle cluster based on its simplification rate, used to select LODs on the GPU.
        /// </summary>
        /// <param name="simplificationError">The simplification ratio or error rate resulting from simplifying the cluster group level that contains the current cluster's parents.</param>
        private static float ComputeLODProjectionError(float simplificationError)
        {
            return simplificationError * 500.0f / math.tan(Camera.main.fieldOfView * math.PI / 180.0f * 0.5f);
        }

        /// <summary>
        /// The data representing a memory page's contents to be serialized, built iteratively during baking.
        /// </summary>
        private class MemoryPageData
        {
            public uint totalInstanceCount;
            public uint totalGroupCount;

            public uint vertexValueCount;
            public uint indexValueCount;

            public uint leafClusterCount;

            public Vector3 combinedBoundsMin;
            public Vector3 combinedBoundsMax;

            public NativeArray<uint> vertexPositionNativeArray;
            public NativeArray<uint> vertexAttributeNativeArray;
            public NativeArray<uint> indexNativeArray;
            public NativeArray<uint> dataNativeArray;
            public NativeArray<uint> groupDataNativeArray;

            public List<Vertex> placeholderVertices;
            public List<uint> placeholderIndices;

            public void DisposeNativeArrays()
            {
                if (vertexPositionNativeArray.IsCreated)
                    vertexPositionNativeArray.Dispose();

                if (vertexAttributeNativeArray.IsCreated)
                    vertexAttributeNativeArray.Dispose();

                if (indexNativeArray.IsCreated)
                    indexNativeArray.Dispose();

                if (dataNativeArray.IsCreated)
                    dataNativeArray.Dispose();

                if (groupDataNativeArray.IsCreated)
                    groupDataNativeArray.Dispose();
            }

            public void ReserveAdditionalVertexPositions(int count)
                => ReserveAdditionalUints(count, ref vertexPositionNativeArray);

            public void ReserveAdditionalVertexAttributes(int count)
                => ReserveAdditionalUints(count, ref vertexAttributeNativeArray);

            public void ReserveAdditionalIndices(int count)
                => ReserveAdditionalUints(count, ref indexNativeArray);

            public void ReserveAdditionalData(int count)
                => ReserveAdditionalUints(count, ref dataNativeArray);

            public void ReserveAdditionalGroupData(int count)
                => ReserveAdditionalUints(count, ref groupDataNativeArray);

            public void SetVertexPosition(int i, uint val)
                => vertexPositionNativeArray[i] = val;

            public void SetVertexAttribute(int i, uint val)
                => vertexAttributeNativeArray[i] = val;

            public void SetIndex(int i, uint val)
                => indexNativeArray[i] = val;

            public void SetData(int i, uint val)
                => dataNativeArray[i] = val;

            public void SetGroupData(int i, uint val)
                => groupDataNativeArray[i] = val;

            private unsafe void ReserveAdditionalUints(int count, ref NativeArray<uint> targetNativeArray)
            {
                if (count <= 0)
                    return;

                if (!targetNativeArray.IsCreated)
                    targetNativeArray = new NativeArray<uint>(count, Allocator.Persistent);
                else
                {
                    int newCount = targetNativeArray.Length + count;
                    var newNativeArray = new NativeArray<uint>(newCount, Allocator.Persistent);
                    UnsafeUtility.MemCpy(newNativeArray.GetUnsafePtr(), targetNativeArray.GetUnsafePtr(), targetNativeArray.Length * sizeof(uint));
                    targetNativeArray.Dispose();
                    targetNativeArray = newNativeArray;
                }
            }
        }

        /// <summary>
        /// The data representing information about materials used by virtualized meshes, built iteratively during baking.
        /// </summary>
        private class MaterialData
        {
            public uint vertexCount;
        }
    }
}
