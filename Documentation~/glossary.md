# Glossary

Here are some key concepts and words used throughout the package's documentation and code. For more details regarding how these concepts are implemented, see the [Implementation](implementation.md) section.

### Virtual mesh *runtime*

The part of the virual mesh package that runs during runtime. This includes the GPU pipeline and its various shaders, the streaming and jobified I/O systems, and the placeholder system.

### Virtual mesh *baker*

The part of the virual mesh package that runs only in the editor and handles converting meshes into custom binary files to stream. The baker includes an editor tool, a [meshoptimizer](https://github.com/zeux/meshoptimizer) wrapper, and various utilities like a shader conversion system to override vertex shaders with custom instanced versions.

### *Cluster* (or *meshlet*)

A fixed-size patch of maximum 64 triangles that form part of a mesh. Clusters are treated as instances inside indirect draw calls.

### Cluster *group*

A hierarchical set of clusters with parent-child relationships. A cluster is only drawn with its sibling clusters of the same level and only one level of sibling clusters is drawn at a time. Each cluster group share a single bounding box used for occlusion culling. Triangles within the group use the same vertex buffer.

### *Memory page*

A set of at most N cluster groups, serialized and saved as file on disk. Pages are streamed into memory during runtime, based on a priority value computed from projecting their bounding boxes onto the screen and comparing their sizes.

### Page *slot*

A range of data with constant stride inside SSBO/UAV buffers used during runtime to draw clusters (vertex data, index data, cluster metadata, group metadata, etc.). There is a fixed number of slots where pages can be streamed.

### *Upload buffer*

A set of buffers that correspond to the content of a memory page split into geometry (index, vertex, metadata) buffers used for streaming page data from disk to GPU memory. Depending on the platform, a CPU copy is performed during streaming. After filling the upload buffer, the data is copied from the upload buffer into a page slot inside the large geometry buffers that represent the scene on the GPU.

### *Placeholder*

A mesh built from the simplified and compacted vertex and index buffers from a whole single page. Placeholders are loaded during startup and kept resident as Mesh objects. These meshes are drawn instead of the virtual geometry contained in their respective pages when pages that have been requested by the GPU cannot be drawn or when slots run out.
