using System;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEditorInternal;

namespace UniPeek
{
    /// <summary>
    /// Static utility for managing Game View resolutions in the Unity Editor.
    /// Provides methods to add, remove, and select custom resolution sizes.
    /// </summary>
    public static class GameViewResolutionHelper
    {
        /// <summary>
        /// Sets the Game View to a specific resolution. If the resolution doesn't exist,
        /// it will be created as a custom size.
        /// </summary>
        public static void SetResolution(int width, int height)
        {
            try
            {
                Type gameViewSizeType = typeof(Editor).Assembly.GetType("UnityEditor.GameViewSize");
                Type gameViewSizesType = typeof(Editor).Assembly.GetType("UnityEditor.GameViewSizes");
                Type gameViewSizeTypeEnum = typeof(Editor).Assembly.GetType("UnityEditor.GameViewSizeType");
                Type gameViewType = typeof(Editor).Assembly.GetType("UnityEditor.GameView");

                if (gameViewSizeType == null || gameViewSizesType == null || gameViewSizeTypeEnum == null || gameViewType == null)
                {
                    Debug.LogError("Failed to load required GameView types from Unity editor assembly.");
                    return;
                }

                // Get the GameViewSizes singleton instance
                Type scriptableSingletonType = typeof(ScriptableSingleton<>).MakeGenericType(gameViewSizesType);
                object singletonInstance = scriptableSingletonType.GetProperty("instance").GetValue(null, null);

                // Get the Standalone group
                MethodInfo getGroupMethod = gameViewSizesType.GetMethod("GetGroup");
                object group = getGroupMethod.Invoke(singletonInstance, new object[] { (int)GameViewSizeGroupType.Standalone });

                // Look for an existing size entry
                string desiredLabel = $"{width}x{height}";
                MethodInfo getDisplayTextsMethod = group.GetType().GetMethod("GetDisplayTexts");
                string[] texts = getDisplayTextsMethod.Invoke(group, null) as string[];
                int idx = Array.IndexOf(texts, desiredLabel);

                if (idx == -1)
                {
                    // Create and add a new custom size
                    Type[] ctorTypes = new Type[] { gameViewSizeTypeEnum, typeof(int), typeof(int), typeof(string) };
                    ConstructorInfo ctor = gameViewSizeType.GetConstructor(ctorTypes);
                    object sizeTypeValue = Enum.Parse(gameViewSizeTypeEnum, "FixedResolution");
                    object newSize = ctor.Invoke(new object[] { sizeTypeValue, width, height, desiredLabel });

                    MethodInfo addCustomSizeMethod = group.GetType().GetMethod("AddCustomSize");
                    addCustomSizeMethod.Invoke(group, new object[] { newSize });

                    // Find the index again after adding
                    texts = getDisplayTextsMethod.Invoke(group, null) as string[];
                    idx = Array.IndexOf(texts, desiredLabel);
                }

                if (idx != -1)
                {
                    // Set the selected size index in the GameView window
                    EditorWindow gameViewWindow = EditorWindow.GetWindow(gameViewType);
                    PropertyInfo selectedSizeIndexProp = gameViewType.GetProperty(
                        "selectedSizeIndex",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    selectedSizeIndexProp.SetValue(gameViewWindow, idx, null);
                    Debug.Log($"Set Game View resolution to {width}x{height}");
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"Failed to set Game View resolution: {ex.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Adds a custom resolution to the Game View size list and optionally selects it.
        /// </summary>
        public static void AddCustomResolution(int width, int height, string label = null, bool selectAfterAdding = true)
        {
            try
            {
                label = label ?? $"{width}x{height}";

                Type gameViewSizeType = typeof(Editor).Assembly.GetType("UnityEditor.GameViewSize");
                Type gameViewSizesType = typeof(Editor).Assembly.GetType("UnityEditor.GameViewSizes");
                Type gameViewSizeTypeEnum = typeof(Editor).Assembly.GetType("UnityEditor.GameViewSizeType");

                if (gameViewSizeType == null || gameViewSizesType == null || gameViewSizeTypeEnum == null)
                {
                    Debug.LogError("Failed to load required GameView types from Unity editor assembly.");
                    return;
                }

                // Get the GameViewSizes singleton instance
                Type scriptableSingletonType = typeof(ScriptableSingleton<>).MakeGenericType(gameViewSizesType);
                object singletonInstance = scriptableSingletonType.GetProperty("instance").GetValue(null, null);

                // Get the Standalone group
                MethodInfo getGroupMethod = gameViewSizesType.GetMethod("GetGroup");
                object group = getGroupMethod.Invoke(singletonInstance, new object[] { (int)GameViewSizeGroupType.Standalone });

                // Create and add new custom size
                Type[] ctorTypes = new Type[] { gameViewSizeTypeEnum, typeof(int), typeof(int), typeof(string) };
                ConstructorInfo ctor = gameViewSizeType.GetConstructor(ctorTypes);
                object sizeTypeValue = Enum.Parse(gameViewSizeTypeEnum, "FixedResolution");
                object newSize = ctor.Invoke(new object[] { sizeTypeValue, width, height, label });

                MethodInfo addCustomSizeMethod = group.GetType().GetMethod("AddCustomSize");
                addCustomSizeMethod.Invoke(group, new object[] { newSize });

                Debug.Log($"Added custom Game View resolution: {label} ({width}x{height})");

                // Optionally select the newly added resolution
                if (selectAfterAdding)
                {
                    SetResolution(width, height);
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"Failed to add custom Game View resolution: {ex.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Removes a custom resolution at the specified index.
        /// </summary>
        public static void RemoveCustomResolution(int index)
        {
            try
            {
                Type gameViewSizesType = typeof(Editor).Assembly.GetType("UnityEditor.GameViewSizes");

                if (gameViewSizesType == null)
                {
                    Debug.LogError("Failed to load GameViewSizes type from Unity editor assembly.");
                    return;
                }

                // Get the GameViewSizes singleton instance
                Type scriptableSingletonType = typeof(ScriptableSingleton<>).MakeGenericType(gameViewSizesType);
                object singletonInstance = scriptableSingletonType.GetProperty("instance").GetValue(null, null);

                // Get the Standalone group
                MethodInfo getGroupMethod = gameViewSizesType.GetMethod("GetGroup");
                object group = getGroupMethod.Invoke(singletonInstance, new object[] { (int)GameViewSizeGroupType.Standalone });

                // Remove custom size
                MethodInfo removeCustomSizeMethod = group.GetType().GetMethod("RemoveCustomSize");
                removeCustomSizeMethod.Invoke(group, new object[] { index });

                Debug.Log($"Removed custom Game View resolution at index {index}");
            }
            catch (Exception ex)
            {
                Debug.LogError($"Failed to remove custom Game View resolution: {ex.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Gets the total count of available resolutions (built-in + custom).
        /// </summary>
        public static int GetResolutionCount()
        {
            try
            {
                Type gameViewSizesType = typeof(Editor).Assembly.GetType("UnityEditor.GameViewSizes");

                if (gameViewSizesType == null)
                    return 0;

                // Get the GameViewSizes singleton instance
                Type scriptableSingletonType = typeof(ScriptableSingleton<>).MakeGenericType(gameViewSizesType);
                object singletonInstance = scriptableSingletonType.GetProperty("instance").GetValue(null, null);

                // Get the Standalone group
                MethodInfo getGroupMethod = gameViewSizesType.GetMethod("GetGroup");
                object group = getGroupMethod.Invoke(singletonInstance, new object[] { (int)GameViewSizeGroupType.Standalone });

                // Get counts
                MethodInfo getBuiltinCountMethod = group.GetType().GetMethod("GetBuiltinCount");
                MethodInfo getCustomCountMethod = group.GetType().GetMethod("GetCustomCount");
                int builtinCount = (int)getBuiltinCountMethod.Invoke(group, null);
                int customCount = (int)getCustomCountMethod.Invoke(group, null);

                return builtinCount + customCount;
            }
            catch (Exception ex)
            {
                Debug.LogError($"Failed to get resolution count: {ex.Message}");
                return 0;
            }
        }

        /// <summary>
        /// Gets the index of the currently selected resolution in the Game View.
        /// </summary>
        public static int GetCurrentResolutionIndex()
        {
            try
            {
                Type gameViewType = typeof(Editor).Assembly.GetType("UnityEditor.GameView");

                if (gameViewType == null)
                    return -1;

                EditorWindow gameViewWindow = EditorWindow.GetWindow(gameViewType);
                PropertyInfo selectedSizeIndexProp = gameViewType.GetProperty(
                    "selectedSizeIndex",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                return (int)selectedSizeIndexProp.GetValue(gameViewWindow);
            }
            catch (Exception ex)
            {
                Debug.LogError($"Failed to get current resolution index: {ex.Message}");
                return -1;
            }
        }

        /// <summary>
        /// Gets the label/name of the resolution at the specified index.
        /// </summary>
        public static string GetResolutionLabel(int index)
        {
            try
            {
                Type gameViewSizesType = typeof(Editor).Assembly.GetType("UnityEditor.GameViewSizes");

                if (gameViewSizesType == null)
                    return null;

                // Get the GameViewSizes singleton instance
                Type scriptableSingletonType = typeof(ScriptableSingleton<>).MakeGenericType(gameViewSizesType);
                object singletonInstance = scriptableSingletonType.GetProperty("instance").GetValue(null, null);

                // Get the Standalone group
                MethodInfo getGroupMethod = gameViewSizesType.GetMethod("GetGroup");
                object group = getGroupMethod.Invoke(singletonInstance, new object[] { (int)GameViewSizeGroupType.Standalone });

                // Get display texts
                MethodInfo getDisplayTextsMethod = group.GetType().GetMethod("GetDisplayTexts");
                string[] texts = getDisplayTextsMethod.Invoke(group, null) as string[];

                if (index >= 0 && index < texts.Length)
                    return texts[index];

                return null;
            }
            catch (Exception ex)
            {
                Debug.LogError($"Failed to get resolution label: {ex.Message}");
                return null;
            }
        }
    }
}
