namespace Unity.VirtualMesh.Runtime
{
    public static class ResourcePathDefinitions
    {
        public static readonly string virtualMeshDataPath = "VirtualMeshData";
        public static readonly string placeholderBundlePath = $"{virtualMeshDataPath}/Placeholders";
        public static readonly string materialBundlePath = $"{virtualMeshDataPath}/Materials";
        public static readonly string placeholderBundleName = "virtualmeshplaceholders";
        public static readonly string materialBundleName = "virtualmeshmaterials";
        public static readonly string placeholderBundleFullPath = $"{placeholderBundlePath}/{placeholderBundleName}";
        public static readonly string materialBundleFullPath = $"{materialBundlePath}/{materialBundleName}";

        public static readonly string shadersCachePath = "Assets/VirtualMeshCache/Shaders";
        public static readonly string materialsCachePath = "Assets/VirtualMeshCache/Materials";
        public static readonly string meshesCachePath = "Assets/VirtualMeshCache/Meshes";
        public static readonly string objCachePath = "Assets/VirtualMeshCache/Obj";
        public static readonly string materialsCollectionFullPath = $"{materialsCachePath}/MaterialCollection.asset";
        public static readonly string meshesCollectionFullPath = $"{meshesCachePath}/MeshCollection.asset";
    }
}
