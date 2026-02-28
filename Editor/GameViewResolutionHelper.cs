using System;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEditorInternal;

namespace UniPeek
{
    /// <summary>
    /// Sets the Game View to a connected device's resolution.
    /// Call <see cref="SetResolution"/> from a button — safe to call from OnGUI.
    /// </summary>
    public static class GameViewResolutionHelper
    {
        private const string LabelPrefix = "UniPeek - ";

        /// <summary>
        /// Replaces any existing "UniPeek - " Game View entry with one labelled
        /// "UniPeek - {deviceName}" at the given dimensions and selects it.
        /// Deferred one editor tick so it is safe to invoke from an OnGUI button handler.
        /// </summary>
        public static void SetResolution(int width, int height, string deviceName)
        {
            EditorApplication.delayCall += () => Apply(width, height, deviceName);
        }

        private static void Apply(int width, int height, string deviceName)
        {
            try
            {
                Type gameViewSizeType     = typeof(Editor).Assembly.GetType("UnityEditor.GameViewSize");
                Type gameViewSizesType    = typeof(Editor).Assembly.GetType("UnityEditor.GameViewSizes");
                Type gameViewSizeTypeEnum = typeof(Editor).Assembly.GetType("UnityEditor.GameViewSizeType");
                Type gameViewType         = typeof(Editor).Assembly.GetType("UnityEditor.GameView");

                if (gameViewSizeType == null || gameViewSizesType == null
                    || gameViewSizeTypeEnum == null || gameViewType == null)
                {
                    Debug.LogError("[UniPeek] Failed to load required GameView types.");
                    return;
                }

                Type   singletonType = typeof(ScriptableSingleton<>).MakeGenericType(gameViewSizesType);
                object instance      = singletonType.GetProperty("instance").GetValue(null, null);
                object group         = gameViewSizesType.GetMethod("GetGroup")
                    .Invoke(instance, new object[] { (int)GameViewSizeGroupType.Standalone });

                var getTexts     = group.GetType().GetMethod("GetDisplayTexts");
                var addCustom    = group.GetType().GetMethod("AddCustomSize");
                var removeCustom = group.GetType().GetMethod("RemoveCustomSize");
                int builtinCount = (int)group.GetType().GetMethod("GetBuiltinCount").Invoke(group, null);

                // Remove every existing "UniPeek - " entry so the list never accumulates.
                bool found;
                do
                {
                    found = false;
                    string[] t = getTexts.Invoke(group, null) as string[];
                    for (int i = t.Length - 1; i >= builtinCount; i--)
                    {
                        if (t[i].StartsWith(LabelPrefix))
                        {
                            removeCustom.Invoke(group, new object[] { i - builtinCount });
                            found = true;
                            break;
                        }
                    }
                } while (found);

                // Record the current count — the new entry will land at exactly this position.
                int idx = (getTexts.Invoke(group, null) as string[]).Length;

                // Add the fresh entry.
                string label     = $"{LabelPrefix}{deviceName}";
                var    ctor      = gameViewSizeType.GetConstructor(
                    new Type[] { gameViewSizeTypeEnum, typeof(int), typeof(int), typeof(string) });
                var    fixedType = Enum.Parse(gameViewSizeTypeEnum, "FixedResolution");
                addCustom.Invoke(group, new object[] { ctor.Invoke(new object[] { fixedType, width, height, label }) });

                // Verify it was actually added.
                int countAfter = (getTexts.Invoke(group, null) as string[]).Length;
                if (countAfter != idx + 1) { Debug.LogError("[UniPeek] Failed to add resolution entry."); return; }

                var wins = Resources.FindObjectsOfTypeAll(gameViewType);
                if (wins == null || wins.Length == 0)
                {
                    Debug.LogWarning("[UniPeek] Game View window is not open.");
                    return;
                }

                EditorWindow win = wins[0] as EditorWindow;
                if (win == null) return;

                var prop = gameViewType.GetProperty(
                    "selectedSizeIndex",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                prop?.SetValue(win, idx, null);

                Debug.Log("[UniPeek] Game View -> " + width + "x" + height + "  (" + label + ")");
            }
            catch (Exception ex)
            {
                Debug.LogError("[UniPeek] SetResolution failed: " + ex.Message + "\n" + ex.StackTrace);
            }
        }
    }
}
