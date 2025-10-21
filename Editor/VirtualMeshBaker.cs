using System.Collections.Generic;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace Unity.VirtualMesh.Editor
{
    /// <summary>
    /// Editor tool to convert a scene's specific GameObjects into custom files used by the virtual mesh system.
    /// </summary>
    public class VirtualMeshBaker : EditorWindow
    {
        private GameObject m_RootObject = null;
        private bool m_BakeInactiveObjects = false;

        /// <summary>
        /// Sets up or processes the scene before baking if needed.
        /// </summary>
        [MenuItem("Virtual Mesh/Setup Scene")]
        public static void SetupScene()
        {
        }

        /// <summary>
        /// Opens the baking tool interface.
        /// </summary>
        [MenuItem("Virtual Mesh/Open Baker")]
        public static void OpenWindow()
        {
            VirtualMeshBaker window = GetWindow<VirtualMeshBaker>();
            window.titleContent = new GUIContent("VirtualMeshBaker");
            window.minSize = new Vector2(450, 200);
            window.maxSize = new Vector2(1920, 720);
        }

        /// <summary>
        /// Bakes and saves shaders files.
        /// This requires a mesh bake afterwards.
        /// </summary>
        private void BakeShaders()
        {
            var filters = new List<MeshFilter>();
            VirtualMeshBakerAPI.GetFilterList(m_RootObject, filters, m_BakeInactiveObjects);
            if (filters.Count > 0)
            {
                VirtualMeshBakerAPI.EnsureCacheAndSaveDirectories();
                VirtualMeshBakerAPI.ConvertShaders(filters);

                AssetDatabase.Refresh();
            }
        }

        /// <summary>
        /// Bakes meshes and generates all the files that the virtual mesh runtime uses.
        /// This can be called without a full bake if no changes have been made to shaders.
        /// </summary>
        private void BakeMeshes()
        {
            var filters = new List<MeshFilter>();
            VirtualMeshBakerAPI.GetFilterList(m_RootObject, filters, m_BakeInactiveObjects);
            if (filters.Count > 0)
            {
                VirtualMeshBakerAPI.EnsureCacheAndSaveDirectories();
                VirtualMeshBakerAPI.ConvertMeshes(filters);

                AssetDatabase.Refresh();
            }
        }

        /// <summary>
        /// Performs a full bake of the current scene.
        /// This bakes both shaders and meshes to ensure a clean state.
        /// </summary>
        private void FullBake()
        {
            var filters = new List<MeshFilter>();
            VirtualMeshBakerAPI.GetFilterList(m_RootObject, filters, m_BakeInactiveObjects);
            if (filters.Count > 0)
            {
                VirtualMeshBakerAPI.EnsureCacheAndSaveDirectories(true);
                VirtualMeshBakerAPI.ConvertShaders(filters);
                VirtualMeshBakerAPI.ConvertMeshes(filters);

                AssetDatabase.Refresh();
            }
        }

        public void CreateGUI()
        {
            VisualElement root = rootVisualElement;

            var label = new Label("VMESH Baker v1.0");
            root.Add(label);

            var rootObjectField = new ObjectField("Root Object");
            rootObjectField.allowSceneObjects = true;
            rootObjectField.objectType = typeof(GameObject);
            rootObjectField.RegisterValueChangedCallback(
                evt => {
                    m_RootObject = (GameObject)rootObjectField.value;
                });
            root.Add(rootObjectField);

            var bakeInactiveObjectsToggle = new Toggle();
            bakeInactiveObjectsToggle.label = "Bake Inactive Objects";
            bakeInactiveObjectsToggle.RegisterValueChangedCallback(
                evt => {
                    m_BakeInactiveObjects = bakeInactiveObjectsToggle.value;
                });
            root.Add(bakeInactiveObjectsToggle);

            var convertShadersButton = new Button(BakeShaders);
            convertShadersButton.text = "Bake Shaders Only";
            root.Add(convertShadersButton);

            var convertMeshesButton = new Button(BakeMeshes);
            convertMeshesButton.text = "Bake Meshes Only";
            root.Add(convertMeshesButton);

            var fullBakeButton = new Button(FullBake);
            fullBakeButton.text = "Full Bake";
            root.Add(fullBakeButton);
        }
    }
}
