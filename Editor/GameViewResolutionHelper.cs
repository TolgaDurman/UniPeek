using System;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEditorInternal;

namespace UniPeek
{
    /// <summary>How the Game View size entry is expressed.</summary>
    public enum GameViewSizeMode
    {
        /// <summary>Exact pixel dimensions — e.g. 1080 × 2340.</summary>
        FixedResolution,
        /// <summary>Aspect ratio only — e.g. 9:19.5 — Game View scales to window size.</summary>
        AspectRatio,
    }

    /// <summary>
    /// Sets the Game View to a connected device's resolution or aspect ratio.
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
        public static void SetResolution(int width, int height, string deviceName,
            GameViewSizeMode mode = GameViewSizeMode.AspectRatio)
        {
            EditorApplication.delayCall += () => Apply(width, height, deviceName, mode);
        }

        private static void Apply(int width, int height, string deviceName, GameViewSizeMode mode)
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

                // Resolve size type and values.
                string enumName  = mode == GameViewSizeMode.AspectRatio ? "AspectRatio" : "FixedResolution";
                var    sizeType  = Enum.Parse(gameViewSizeTypeEnum, enumName);
                int    sizeW     = width;
                int    sizeH     = height;
                if (mode == GameViewSizeMode.AspectRatio)
                {
                    // Reduce to a simple ratio so the label stays clean (e.g. 1080×2340 → 6:13).
                    int gcd = GCD(width, height);
                    sizeW = width  / gcd;
                    sizeH = height / gcd;
                }

                // Add the fresh entry.
                string label = $"{LabelPrefix}{deviceName}";
                var    ctor  = gameViewSizeType.GetConstructor(
                    new Type[] { gameViewSizeTypeEnum, typeof(int), typeof(int), typeof(string) });
                addCustom.Invoke(group, new object[] { ctor.Invoke(new object[] { sizeType, sizeW, sizeH, label }) });

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

                string modeLabel = mode == GameViewSizeMode.AspectRatio
                    ? $"{sizeW}:{sizeH} (aspect ratio)"
                    : $"{width}×{height} (fixed)";
                Debug.Log($"[UniPeek] Game View -> {modeLabel}  ({label})");
            }
            catch (Exception ex)
            {
                Debug.LogError("[UniPeek] SetResolution failed: " + ex.Message + "\n" + ex.StackTrace);
            }
        }

        private static int GCD(int a, int b) => b == 0 ? a : GCD(b, a % b);
    }
}
