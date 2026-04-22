using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace UniPeek
{
    public struct WebGLBuildConfig
    {
        public bool UseCurrentScene;
        public string OutputPath;
    }

    public sealed class WebGLBuilder : IPreprocessBuildWithReport, IPostprocessBuildWithReport
    {
        // ── Static events ─────────────────────────────────────────────────────
        public static event Action              OnBuildStarted;
        public static event Action<BuildReport> OnBuildComplete;
        public static event Action<BuildReport> OnBuildFailed;

        // ── Static progress state (polled by DeployTab at 10 Hz) ─────────────
        public static float  CurrentProgress { get; private set; }
        public static string CurrentStage    { get; private set; } = string.Empty;
        public static bool   IsBuilding      { get; private set; }

        // ── Private static ────────────────────────────────────────────────────
        private static int              _progressId          = -1;
        private static string           _savedTemplate;
        private static bool             _isBuildingFromUniPeek;
        private static BuildTarget      _savedBuildTarget;
        private static BuildTargetGroup _savedBuildTargetGroup;

        // Required by IPreprocessBuildWithReport / IPostprocessBuildWithReport
        public int callbackOrder => 0;

        // ── Build callbacks (fired by Unity during BuildPipeline.BuildPlayer) ─

        public void OnPreprocessBuild(BuildReport report)
        {
            if (!_isBuildingFromUniPeek) return;
            CurrentProgress = 0.1f;
            CurrentStage    = "Preprocessing…";
            if (_progressId >= 0)
                Progress.Report(_progressId, 0.1f, "Preprocessing…");
        }

        public void OnPostprocessBuild(BuildReport report)
        {
            if (!_isBuildingFromUniPeek) return;

            _isBuildingFromUniPeek = false;
            RestoreTemplate();

            bool succeeded = report.summary.result == BuildResult.Succeeded ||
                (report.summary.result == BuildResult.Unknown &&
                 report.summary.totalErrors == 0 &&
                 File.Exists(Path.Combine(report.summary.outputPath, "index.html")));

            if (succeeded)
            {
                try   { InjectTemplate(report.summary.outputPath); }
                catch (Exception ex) { Debug.LogWarning($"[UniPeek] Template injection failed: {ex.Message}"); }

                try   { WriteManifest(report.summary.outputPath); }
                catch (Exception ex) { Debug.LogWarning($"[UniPeek] manifest.json write failed: {ex.Message}"); }

                CurrentProgress = 1f;
                CurrentStage    = "Done";
                FinishProgress(Progress.Status.Succeeded);
                IsBuilding = false;
                Debug.Log($"[UniPeek] WebGL build complete → {report.summary.outputPath}");
                OnBuildComplete?.Invoke(report);
            }
            else
            {
                CurrentStage = "Failed";
                FinishProgress(Progress.Status.Failed);
                IsBuilding = false;
                Debug.LogError($"[UniPeek] WebGL build failed: {report.summary.result}. Total errors: {report.summary.totalErrors}");
                OnBuildFailed?.Invoke(report);
            }
        }

        // ── Public API ────────────────────────────────────────────────────────

        public static void Build(WebGLBuildConfig config)
        {
            if (IsBuilding) return;

            IsBuilding             = true;
            CurrentProgress        = 0f;
            CurrentStage           = "Starting…";
            _isBuildingFromUniPeek = true;

            if (!BuildPipeline.IsBuildTargetSupported(BuildTargetGroup.WebGL, BuildTarget.WebGL))
            {
                IsBuilding   = false;
                CurrentStage = "WebGL module not installed";
                Debug.LogError("[UniPeek] WebGL Build Support is not installed. Open Unity Hub → Installs → your Unity version → Add Modules → WebGL Build Support.");
                OnBuildFailed?.Invoke(null);
                return;
            }

            if (config.UseCurrentScene)
            {
                string scenePath = EditorSceneManager.GetActiveScene().path;
                if (string.IsNullOrEmpty(scenePath))
                {
                    IsBuilding   = false;
                    CurrentStage = "Save your scene before building";
                    Debug.LogError("[UniPeek] WebGL build failed: active scene has no path. Save the scene first (File > Save).");
                    OnBuildFailed?.Invoke(null);
                    return;
                }
            }

            _progressId = Progress.Start("UniPeek WebGL Build", "Building…", Progress.Options.None);

            _savedBuildTarget      = EditorUserBuildSettings.activeBuildTarget;
            _savedBuildTargetGroup = EditorUserBuildSettings.selectedBuildTargetGroup;

            _savedTemplate = PlayerSettings.WebGL.template ?? string.Empty;
            // Always use Minimal as the Unity template — InjectTemplate overwrites
            // index.html in post-process with our custom fullscreen version.
            PlayerSettings.WebGL.template = "APPLICATION:Minimal";

            string[] scenes = config.UseCurrentScene
                ? new[] { EditorSceneManager.GetActiveScene().path }
                : GetEnabledScenes();

            if (scenes.Length == 0)
            {
                IsBuilding   = false;
                CurrentStage = "No scenes to build";
                FinishProgress(Progress.Status.Failed);
                Debug.LogError("[UniPeek] WebGL build failed: no enabled scenes found in Build Settings. Add a scene via File > Build Settings, or enable 'Use current open scene'.");
                OnBuildFailed?.Invoke(null);
                return;
            }

            Debug.Log($"[UniPeek] Starting WebGL build → {config.OutputPath}\nScenes ({scenes.Length}): {string.Join(", ", scenes)}");

            var options = new BuildPlayerOptions
            {
                scenes           = scenes,
                locationPathName = config.OutputPath,
                target           = BuildTarget.WebGL,
                targetGroup      = BuildTargetGroup.WebGL,
                options          = BuildOptions.None,
            };

            OnBuildStarted?.Invoke();

            BuildReport report = null;
            try
            {
                report = BuildPipeline.BuildPlayer(options);
            }
            finally
            {
                // OnPostprocessBuild fires synchronously inside BuildPlayer and clears
                // IsBuilding on success/failure. Reach here only if an exception escaped.
                if (IsBuilding)
                {
                    _isBuildingFromUniPeek = false;
                    RestoreTemplate();
                    CurrentStage = "Failed";
                    FinishProgress(Progress.Status.Failed);
                    IsBuilding = false;
                    OnBuildFailed?.Invoke(report);
                }
            }
        }

        // ── Template injection ────────────────────────────────────────────────

        private static void InjectTemplate(string outputPath)
        {
            string templatePath = Path.Combine(
                Application.dataPath,
                "Plugins", "UniPeek", "WebGLTemplates", "UniPeek", "index.html");

            if (!File.Exists(templatePath))
            {
                // Fallback: patch the Minimal template output with fullscreen CSS
                PatchMinimalFullscreen(outputPath);
                return;
            }

            string buildDir = Path.Combine(outputPath, "Build");

            string loaderFile    = Directory.GetFiles(buildDir, "*.loader.js").FirstOrDefault();
            string frameworkFile = Directory.GetFiles(buildDir, "*.framework.js*").FirstOrDefault();
            string dataFile      = Directory.GetFiles(buildDir, "*.data*")
                                       .Where(f => !f.EndsWith(".meta")).FirstOrDefault();
            string wasmFile      = Directory.GetFiles(buildDir, "*.wasm*")
                                       .Where(f => !f.EndsWith(".meta")).FirstOrDefault();

            if (loaderFile == null || frameworkFile == null || dataFile == null || wasmFile == null)
            {
                Debug.LogWarning("[UniPeek] Could not locate all build files for template injection, patching fallback.");
                PatchMinimalFullscreen(outputPath);
                return;
            }

            string html = File.ReadAllText(templatePath)
                .Replace("%%LOADER_URL%%",      "Build/" + Path.GetFileName(loaderFile))
                .Replace("%%FRAMEWORK_URL%%",   "Build/" + Path.GetFileName(frameworkFile))
                .Replace("%%DATA_URL%%",         "Build/" + Path.GetFileName(dataFile))
                .Replace("%%CODE_URL%%",         "Build/" + Path.GetFileName(wasmFile))
                .Replace("%%PRODUCT_NAME%%",     PlayerSettings.productName)
                .Replace("%%COMPANY_NAME%%",     PlayerSettings.companyName)
                .Replace("%%PRODUCT_VERSION%%",  PlayerSettings.bundleVersion);

            File.WriteAllText(Path.Combine(outputPath, "index.html"), html);
        }

        private static void PatchMinimalFullscreen(string outputPath)
        {
            string indexPath = Path.Combine(outputPath, "index.html");
            if (!File.Exists(indexPath)) return;

            const string fullscreenCss =
                "<style>\n" +
                "  *{margin:0;padding:0;box-sizing:border-box}\n" +
                "  html,body{width:100%;height:100%;background:#000;overflow:hidden}\n" +
                "  #unity-canvas{width:100%!important;height:100%!important;position:fixed!important;top:0;left:0;touch-action:none}\n" +
                "  #unity-container,.unity-desktop,.unity-mobile{width:100%!important;height:100%!important;position:fixed!important;top:0;left:0}\n" +
                "  #unity-footer,#unity-loading-bar{display:none!important}\n" +
                "</style>";

            string html = File.ReadAllText(indexPath)
                .Replace("</head>", fullscreenCss + "\n</head>");

            File.WriteAllText(indexPath, html);
        }

        private static void WriteManifest(string outputPath)
        {
            var files = new List<string>();
            long totalBytes = 0;

            foreach (string file in Directory.GetFiles(outputPath, "*", SearchOption.AllDirectories))
            {
                string relative = file.Substring(outputPath.Length)
                                     .TrimStart(Path.DirectorySeparatorChar, '/')
                                     .Replace(Path.DirectorySeparatorChar, '/');

                if (string.Equals(relative, "manifest.json", StringComparison.OrdinalIgnoreCase)) continue;

                files.Add(relative);
                totalBytes += new FileInfo(file).Length;
            }

            // Minimal hand-built JSON — avoids a dependency on Newtonsoft.Json.
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("{");
            sb.AppendLine($"  \"id\": \"{DateTime.UtcNow:yyyyMMddTHHmmssZ}\",");
            sb.AppendLine($"  \"productName\": \"{EscapeJson(PlayerSettings.productName)}\",");
            sb.AppendLine($"  \"companyName\": \"{EscapeJson(PlayerSettings.companyName)}\",");
            sb.AppendLine($"  \"version\": \"{EscapeJson(PlayerSettings.bundleVersion)}\",");
            sb.AppendLine($"  \"totalBytes\": {totalBytes},");
            sb.AppendLine($"  \"files\": [");
            for (int i = 0; i < files.Count; i++)
            {
                string comma = i < files.Count - 1 ? "," : "";
                sb.AppendLine($"    \"{EscapeJson(files[i])}\"{comma}");
            }
            sb.AppendLine("  ]");
            sb.Append("}");

            File.WriteAllText(Path.Combine(outputPath, "manifest.json"), sb.ToString());
            Debug.Log($"[UniPeek] manifest.json written → {files.Count} files, {totalBytes / 1024} KB");
        }

        private static string EscapeJson(string s) =>
            (s ?? "").Replace("\\", "\\\\")
             .Replace("\"", "\\\"")
             .Replace("\n", "\\n")
             .Replace("\r", "\\r")
             .Replace("\t", "\\t");

        // ── Helpers ───────────────────────────────────────────────────────────

        private static string[] GetEnabledScenes()
        {
            var result = new List<string>();
            foreach (var s in EditorBuildSettings.scenes)
                if (s.enabled) result.Add(s.path);
            return result.ToArray();
        }

        private static void RestoreTemplate()
        {
            if (_savedTemplate == null) return;
            PlayerSettings.WebGL.template = _savedTemplate;
            _savedTemplate = null;

            if (_savedBuildTarget != BuildTarget.WebGL)
                EditorUserBuildSettings.SwitchActiveBuildTarget(_savedBuildTargetGroup, _savedBuildTarget);
        }

        private static void FinishProgress(Progress.Status status)
        {
            if (_progressId < 0) return;
            Progress.Finish(_progressId, status);
            _progressId = -1;
        }
    }
}
