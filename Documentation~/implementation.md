# Implementation

## Architecture and Files

### Runtime files

The main entry point for the package's runtime C# logic is the *Runtime/VirtualMeshManager.cs* component script. This component should be attached to the scene's main camera and handles all the processing required by the virtual mesh system during the Unity game loop. Persistent runtime objects such as GraphicsBuffers are also owned by this component.

The other important runtime C# class is *Runtime/RenderFeatures/VirtualMeshRenderFeature.cs*, which contains the render feature responsible for all the custom render passes that the virtual mesh system injects into Unity's rendering pipeline. These passes use the buffers and resources owned by the `VirtualMeshManager` to perform appropriate shader calls and dispatches that make up the GPU-driven pipeline.

Shaders used by the system during runtime can be found under *Runtime/Shaders*, which contains compute shaders used by the culling and LOD pipeline (*Runtime/Shaders/DXCVisibilityPasses.compute* and *Runtime/Shaders/DepthPyramidPass.compute*), compute shaders used by the streaming system (*Runtime/Shaders/CopyPasses.compute*), and vertex/fragment shaders for custom rendering passes used throughout the system (*Runtime/Shaders/CustomPasses.shader* and *Runtime/Shaders/DebugPasses.shader*).

*Runtime/ShaderLibrary* contains shader code files that are used to replace includes inside URP/Lit (files that start with "Lit") and Shader Graph (files that start with "PBR") shaders, which are used by virtual meshes. These files contain instanced versions of the vertex shaders used by each rendering pass supported by the virtual mesh system.

The virtual mesh system also uses some materials and render textures internally, which can be found under *Runtime/Materials* and *Runtime/RenderTextures*.

Lastly, the streaming system relies on an embedded version of [BetterStreamingAssets](https://github.com/gwiazdorrr/BetterStreamingAssets), which can be found under *Runtime/BetterStreamingAssets*.

### Baker files

*Editor/VirtualMeshBaker.cs* and *Editor/VirtualMeshBakerAPI.cs* respectively contain the editor tool and the baking algorithm.

*Editor/MeshOperations.cs* contains functions that encapsulate [meshoptimizer](https://github.com/zeux/meshoptimizer) APIs used for processing meshes during baking. The plugin itself can be found inside *Editor/Optimizer/Plugins*.

*Editor/ShaderGraphHelper* contains classes used to retrieve the shader source code generated from Shader Graph shaders. This source code is used instead of the Shader Graph assets because vertex shaders need to be swapped for an instanced version compatible with virtual meshes.

## Streaming

The virtual mesh system uses custom binary files to stream mesh data during runtime. These files, saved inside the project's *Assets/StreamingAssets* folder, are called pages and are loaded dynamically based on requests made on the GPU.

These requests are given to the CPU via a `GraphicsBuffer` with the RequestAsyncReadback API. Based on the contents of that buffer, the CPU queues pages that need to be loaded and dispatches Unity C# jobs that handle the file I/O and streaming. Each job writes to an upload buffer comprised of several `GraphicsBuffer` objects with `GraphicsBuffer.UsageFlags.LockBufferForWrite` mode for asynchronous writing to the GPU. The buffers are wrapped in a fenced buffer implementation taken taken from the Entities Graphics package.

Each upload buffer corresponds to the data of an entire page, including metadata and all the cluster index and vertex buffers. Have the C# jobs finish filling them, the buffers are read from a series of compute shader dispatches that copy their content into the large runtime buffers used for drawing virtual meshes (see the `StreamingJobsKickoff` and `StreamingJobsWrapup` functions in *Runtime/VirtualMeshManager.cs*).

There is a limited number of upload buffers, each independent from each other. When the CPU loads a page, it must look for an upload buffer that is not currently being written to by a job. If a buffer is available, it will be pooled to load the designated page and locked until the compute copies are finished.

> [!NOTE]
> Changing the number of upload buffers (`k_UploadBufferCount` in *Runtime/VirtualMeshManager.cs*) has a low impact on the system's functionality and is typically a variable that can be adjusted based on the project's memory budget.

To detect if a page needs loading, the CPU keeps a record of the status of every memory page (see the `MemoryPageStatus` enum in *Runtime/VirtualMeshManager.cs*).

- ***Unloaded*** and ***Loaded*** are the most common statuses that indicate when pages are resident or not.

- ***Waiting*** means that a page has been requested by the GPU and should ideally be loaded, but there are no slot left to put it in, so it is waiting for another page to unload.

- ***Loading*** indicates that a page has been assigned to a slot and that the CPU job reading its file has been dispatched but has not yet completed.

- ***TooFar*** is similar to ***Waiting*** because the GPU has requested the page, but its contents are considered to be too far away from the camera, so the CPU will not load it to save page slots for geometry that is closer to the camera.

Updates to page statuses are made on the GPU based on the bounding boxes surrounding the geometry contained inside each page. Pages are sorted in order of the size of their bounds' projection on the screen so that pages that have a bigger geometry size on screen are requested with higher priority. On top of this, the distance between a page and the camera is taken into account to avoid requesting pages that are very far. This distance value can be changed by adjusting the `m_CameraLoadDistanceThreshold` variable in *Runtime/VirtualMeshManager.cs*.

## Baking

To bake virtual meshes and prepare them for rendering, the system performs several tasks that are implemented inside *Editor/VirtualMeshBakerAPI.cs*.

First, shaders used by objects being baked need to be converted to versions that use vertex shaders compatible with the instancing and attribute unpacking schemes required by virtual meshes. The baker picks up shader source codes and replaces includes corresponding to files with vertex shader code with alternatives that support virtual meshes (see the `ConvertShaders` function in *Editor/VirtualMeshBakerAPI.cs*).

> [!NOTE]
> Shaders that are not compatible with the virtual mesh system are automatically skipped. This behaviour can be adjusted in the `CheckSupportedShader` in *Editor/VirtualMeshBakerAPI.cs*.

The next step is to iterate over GameObjects that need to be converted and apply the following algorithm to convert them (see the `ConvertMeshes` function in *Editor/VirtualMeshBakerAPI.cs*):

1. We first generate a list of `MeshFilter` objects to loop over. For now, the selection criteria is to pick only the highest LOD of every LODGroup hierarchy, or the whole `MeshFilter` if it is a standalone mesh without LODs.

2. For every `MeshFilter` chosen previously, we select the filter's `sharedMesh` and loop over its submeshes.

3. For every submesh, we check if its material's shader is supported or not, or if its topology is not triangles (we only support triangle-based meshes for simplicity). The submesh's material is then assigned to a unique ID, which will be used to group geometry per material for draw calls.

4. Using `MeshOperations.BuildMeshlets`, we split the submesh into meshlets of 64 triangles.

5. The resulting meshlets are partitioned with `MeshOperations.PartitionMeshlets`, which gives us groups of meshlets to use as leaf nodes (highest density LODs) for our cluster groups.

6. For every partition, we merge the meshlets into a single index buffer to perfom consecutive simplification and clustering over it until a resulting cluster hierarchy is built. During each step of the recursion we merge and simplify the clusters corresponding to a layer of the LOD hierarchy and record values that will allow us to switch between these layers during runtime.

7. For every resulting group, we find a page where the whole group's hierarchy fits while not exceeding the max number of instances (= meshlets) allowed per page. This is done by finding a suitable `MemoryPageData` class to record the data in an array of instances each corresponding to a page.

8. For every resulting group, we also keep a separate set of index and vertex buffers corresponding to the leaf clusters. These will be merged with other buffers from the same memory page to form a mesh representing the page's placeholder.

9. After building all the cluster groups, we generate placeholders and serialize every `MemoryPageData` instance into corresponding files that will be streamed during runtime. Lastly, we export asset bundles that hold all the materials and placeholder meshes generated during the process (see the `WriteFiles` function in *Editor/VirtualMeshBakerAPI.cs*).
