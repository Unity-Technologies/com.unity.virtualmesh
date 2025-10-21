#if UNITY_EDITOR
using UnityEngine;
using UnityEditor;

namespace Unity.VirtualMesh.Runtime
{
    public static class UnityResourceUtility
    {
        public static bool EnsureUnityResource<TResource>(ref TResource resourceRef, string assetPath) where TResource : UnityEngine.Object
        {
            if (resourceRef != null)
            {
                return false;
            }

            string fullAssetPath = $"Packages/com.unity.virtualmesh/{assetPath}";
            resourceRef = AssetDatabase.LoadAssetAtPath<TResource>(fullAssetPath);

            if (resourceRef == null)
            {
                Debug.LogError($"[Virtual Mesh] Failed to load {typeof(TResource)} located at {fullAssetPath}");

                return false;
            }

            return true;
        }
    }
}
#endif