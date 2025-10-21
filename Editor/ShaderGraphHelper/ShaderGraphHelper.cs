using System.Collections.Generic;
using UnityEditor.ShaderGraph;

namespace Unity.VirtualMesh.Editor
{
    public static class ShaderGraphHelper
    {
        public static string GetShaderText(string path)
        {
            return ShaderGraphImporter.GetShaderText(path, out s_ConfiguredTextures_List, null, out s_GraphData);
        }

        private static List<PropertyCollector.TextureInfo> s_ConfiguredTextures_List = new List<PropertyCollector.TextureInfo>();
        private static GraphData s_GraphData;
    }
}
