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
        private bool m_BakeOpaqueObjectsOnly = true;
        private float m_SimplificationTargetError = 0.005f;

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
                VirtualMeshBakerAPI.ConvertShaders(filters, m_BakeOpaqueObjectsOnly);

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
                VirtualMeshBakerAPI.ConvertMeshes(filters, m_BakeOpaqueObjectsOnly, m_SimplificationTargetError);

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
                VirtualMeshBakerAPI.ConvertShaders(filters, m_BakeOpaqueObjectsOnly);
                VirtualMeshBakerAPI.ConvertMeshes(filters, m_BakeOpaqueObjectsOnly, m_SimplificationTargetError);

                AssetDatabase.Refresh();
            }
        }

        public void CreateGUI()
        {
            VisualElement root = rootVisualElement;
            root.style.paddingTop = 10;
            root.style.paddingBottom = 10;
            root.style.paddingLeft = 10;
            root.style.paddingRight = 10;

            var rootObjectField = new ObjectField("Root Object");
            rootObjectField.tooltip = "Sets the root object that contains all meshes to bake as children.";
            rootObjectField.labelElement.style.width = 300;
            rootObjectField.allowSceneObjects = true;
            rootObjectField.objectType = typeof(GameObject);
            rootObjectField.RegisterValueChangedCallback(
                evt => {
                    m_RootObject = (GameObject)rootObjectField.value;
                });
            root.Add(rootObjectField);

            var bakeInactiveObjectsToggle = new Toggle("Bake Inactive Objects");
            bakeInactiveObjectsToggle.tooltip = "Determines if inactive objects should be baked or not.";
            bakeInactiveObjectsToggle.labelElement.style.width = 300;
            bakeInactiveObjectsToggle.value = false;
            bakeInactiveObjectsToggle.RegisterValueChangedCallback(
                evt => {
                    m_BakeInactiveObjects = bakeInactiveObjectsToggle.value;
                });
            root.Add(bakeInactiveObjectsToggle);

            var bakeOnlyOpaqueToggle = new Toggle("Bake Opaque Objects Only");
            bakeOnlyOpaqueToggle.tooltip = "Determines if only objects with opaque queue shaders should be baked or not (transparent object baking is not recommended).";
            bakeOnlyOpaqueToggle.labelElement.style.width = 300;
            bakeOnlyOpaqueToggle.value = true;
            bakeOnlyOpaqueToggle.RegisterValueChangedCallback(
                evt => {
                    m_BakeOpaqueObjectsOnly = bakeOnlyOpaqueToggle.value;
                });
            root.Add(bakeOnlyOpaqueToggle);

            var simplificationErrorTargetSlider = new Slider("Cluster Simplification Target Error", 0.001f, 1.0f);
            simplificationErrorTargetSlider.tooltip = "Sets the simplification error target between each cluster LOD level. A lower value results in smoother transitions between parent and children clusters but is less efficient at reducing triangles.";
            simplificationErrorTargetSlider.labelElement.style.width = 300;
            simplificationErrorTargetSlider.value = 0.005f;
            simplificationErrorTargetSlider.showInputField = true;
            simplificationErrorTargetSlider.RegisterValueChangedCallback(
                evt => {
                    m_SimplificationTargetError = simplificationErrorTargetSlider.value;
                });
            root.Add(simplificationErrorTargetSlider);

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
