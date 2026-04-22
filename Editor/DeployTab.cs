using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace UniPeek
{
    internal static class DeployTab
    {
        // ── Persistent prefs ──────────────────────────────────────────────────
        private const string PrefUseCurrentScene       = "UniPeek_Deploy_UseCurrentScene";
        private const string PrefSkipWebGLSwitchDialog = "UniPeek_SkipWebGLSwitchDialog";
        private const string SessionBuildComplete = "UniPeek_BuildComplete";
        private const string SessionBuildFailed   = "UniPeek_BuildFailed";

        // ── GUI state ─────────────────────────────────────────────────────────
        private static bool      _useCurrentScene;
        private static bool      _buildComplete;
        private static bool      _buildFailed;
        private static string    _failReason = string.Empty;
        private static string    _serverError = string.Empty;
        private static Texture2D _qrTexture;

        public static bool NeedsRepaint { get; set; }

        private static GUIStyle _sectionLabelStyle;
        private static bool _sectionLabelIsProSkin;

        private static string _outputPath;
        private static string OutputPath =>
            _outputPath ??= Path.GetFullPath(
                Path.Combine(Application.dataPath, "../Builds/UniPeek/WebGL"));

        // ── Lifecycle (called from UniPeekWindow) ─────────────────────────────

        internal static void OnEnable()
        {
            _useCurrentScene = EditorPrefs.GetBool(PrefUseCurrentScene, false);
            _buildComplete   = SessionState.GetBool(SessionBuildComplete, false);
            _buildFailed     = SessionState.GetBool(SessionBuildFailed,   false);
            WebGLBuilder.OnBuildStarted  += HandleBuildStarted;
            WebGLBuilder.OnBuildComplete += HandleBuildComplete;
            WebGLBuilder.OnBuildFailed   += HandleBuildFailed;
        }

        internal static void OnDisable()
        {
            WebGLBuilder.OnBuildStarted  -= HandleBuildStarted;
            WebGLBuilder.OnBuildComplete -= HandleBuildComplete;
            WebGLBuilder.OnBuildFailed   -= HandleBuildFailed;
            if (WebGLFileServer.IsRunning) WebGLFileServer.Stop();
            DestroyQR();
        }

        // ── OnInspectorUpdate (called from UniPeekWindow at ~10 Hz) ──────────

        internal static void OnInspectorUpdate()
        {
            if (WebGLBuilder.IsBuilding || NeedsRepaint)
                NeedsRepaint = true;
        }

        // ── GUI ───────────────────────────────────────────────────────────────

        internal static void DrawGUI(float windowWidth)
        {
            GUILayout.Space(10f);

            if (WebGLBuilder.IsBuilding)
            {
                DrawBuildInProgress();
                return;
            }

            if (_buildComplete)
            {
                DrawBuildComplete(windowWidth);
                return;
            }

            if (_buildFailed)
            {
                DrawBuildFailed();
                return;
            }

            DrawBuildSetup();
        }

        // ── Sections ──────────────────────────────────────────────────────────

        private static void DrawBuildSetup()
        {
            DrawSectionLabel("Scene Source");

            bool newUseCurrentScene;
            EditorGUI.BeginChangeCheck();
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label("Use current open scene", GUILayout.Width(180f));
                newUseCurrentScene = EditorGUILayout.Toggle(_useCurrentScene);
            }
            if (EditorGUI.EndChangeCheck())
            {
                _useCurrentScene = newUseCurrentScene;
                EditorPrefs.SetBool(PrefUseCurrentScene, _useCurrentScene);
            }

            if (!_useCurrentScene)
            {
                EditorGUILayout.HelpBox(
                    "Builds all enabled scenes from Build Settings.",
                    MessageType.Info);
            }
            else
            {
                var scene = UnityEditor.SceneManagement.EditorSceneManager.GetActiveScene();
                EditorGUILayout.HelpBox(
                    $"Will build: {scene.name}",
                    MessageType.Info);
            }

            GUILayout.Space(8f);
            DrawSectionLabel("Output");

            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label(OutputPath, EditorStyles.miniLabel, GUILayout.ExpandWidth(true));
                if (GUILayout.Button("Open", EditorStyles.miniButton, GUILayout.Width(42f)))
                {
                    string folderToOpen = Directory.Exists(OutputPath) ? OutputPath : Path.GetDirectoryName(OutputPath);
                    EditorUtility.RevealInFinder(folderToOpen);
                }
            }

            GUILayout.Space(12f);

            var prevColor = GUI.backgroundColor;
            GUI.backgroundColor = new Color(0.28f, 0.76f, 0.44f);
            if (GUILayout.Button("▶   Build WebGL", GUILayout.Height(42f)))
                StartBuild();
            GUI.backgroundColor = prevColor;
        }

        private static void DrawBuildInProgress()
        {
            GUILayout.Space(4f);
            GUILayout.Label("Building WebGL…", EditorStyles.boldLabel);
            GUILayout.Space(6f);

            var rect = GUILayoutUtility.GetRect(0f, 20f, GUILayout.ExpandWidth(true));
            EditorGUI.ProgressBar(rect, WebGLBuilder.CurrentProgress, WebGLBuilder.CurrentStage);

            GUILayout.Space(4f);
            EditorGUILayout.HelpBox(
                "The editor is temporarily unresponsive while Unity builds. " +
                "Build progress is shown in the status bar below.",
                MessageType.Info);
        }

        private static void DrawBuildComplete(float windowWidth)
        {
            var prevColor = GUI.color;
            GUI.color = new Color(0.18f, 0.80f, 0.32f);
            GUILayout.Label("✓  Build complete", EditorStyles.boldLabel);
            GUI.color = prevColor;

            GUILayout.Space(4f);
            GUILayout.Label(OutputPath, EditorStyles.miniLabel);
            GUILayout.Space(8f);

            if (!string.IsNullOrEmpty(_serverError))
            {
                EditorGUILayout.HelpBox(_serverError, MessageType.Error);
                GUILayout.Space(4f);
            }

            if (!WebGLFileServer.IsRunning)
            {
                var btnColor = GUI.backgroundColor;
                GUI.backgroundColor = new Color(0.28f, 0.60f, 1.00f);
                if (GUILayout.Button("▶   Serve & Open in Browser", GUILayout.Height(36f)))
                    ServeAndOpen();
                GUI.backgroundColor = btnColor;
            }
            else
            {
                DrawSectionLabel("Local Server");

                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Label(WebGLFileServer.LanUrl, EditorStyles.boldLabel, GUILayout.ExpandWidth(true));
                    if (GUILayout.Button("Copy", EditorStyles.miniButton, GUILayout.Width(42f)))
                        EditorGUIUtility.systemCopyBuffer = WebGLFileServer.LanUrl;
                    if (GUILayout.Button("Open", EditorStyles.miniButton, GUILayout.Width(42f)))
                        Application.OpenURL(WebGLFileServer.LanUrl);
                }

                GUILayout.Space(4f);

                if (_qrTexture != null)
                {
                    float size = Mathf.Min(windowWidth - 48f, 180f);
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        GUILayout.FlexibleSpace();
                        GUILayout.Label(_qrTexture, GUILayout.Width(size), GUILayout.Height(size));
                        GUILayout.FlexibleSpace();
                    }
                    GUILayout.Label("Scan to open on a LAN device", EditorStyles.centeredGreyMiniLabel);
                }

                GUILayout.Space(8f);

                var prevBg = GUI.backgroundColor;
                GUI.backgroundColor = new Color(0.88f, 0.30f, 0.30f);
                if (GUILayout.Button("■   Stop Server", GUILayout.Height(30f)))
                {
                    WebGLFileServer.Stop();
                    DestroyQR();
                }
                GUI.backgroundColor = prevBg;
            }

            GUILayout.Space(8f);

            if (GUILayout.Button("New Build", EditorStyles.miniButton))
                ResetState();
        }

        private static void DrawBuildFailed()
        {
            var prevColor = GUI.color;
            GUI.color = new Color(0.9f, 0.3f, 0.3f);
            GUILayout.Label("✕  Build failed", EditorStyles.boldLabel);
            GUI.color = prevColor;

            GUILayout.Space(4f);
            if (!string.IsNullOrEmpty(_failReason))
                EditorGUILayout.HelpBox(_failReason, MessageType.Error);

            GUILayout.Space(8f);
            if (GUILayout.Button("Retry", GUILayout.Height(36f)))
                StartBuild();

            using (new GUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Reset", EditorStyles.miniButton))
                    ResetState();
                if (GUILayout.Button("Open Console", EditorStyles.miniButton))
                    EditorApplication.ExecuteMenuItem("Window/General/Console");
            }
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private static void DrawSectionLabel(string title)
        {
            GUILayout.Space(2f);
            using var row = new EditorGUILayout.HorizontalScope();
            if (_sectionLabelStyle == null || EditorGUIUtility.isProSkin != _sectionLabelIsProSkin)
            {
                _sectionLabelStyle = new GUIStyle(EditorStyles.miniLabel)
                {
                    fontStyle = FontStyle.Bold,
                    normal    = { textColor = new Color(0.50f, 0.50f, 0.50f) },
                };
                _sectionLabelIsProSkin = EditorGUIUtility.isProSkin;
            }
            GUILayout.Label(title.ToUpper(), _sectionLabelStyle);
            GUILayout.Space(4f);
        }

        private static void StartBuild()
        {
            if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.WebGL &&
                !EditorPrefs.GetBool(PrefSkipWebGLSwitchDialog, false))
            {
                // 0 = Build WebGL, 1 = Don't Ask Again, 2 = Cancel
                int choice = EditorUtility.DisplayDialogComplex(
                    "Switch to WebGL?",
                    "Building WebGL requires temporarily switching the active build target to WebGL.\n\n" +
                    "The project will be restored to its current platform after the build completes.",
                    "Build WebGL",
                    "Don't Ask Again",
                    "Cancel");

                if (choice == 2) return;
                if (choice == 1) EditorPrefs.SetBool(PrefSkipWebGLSwitchDialog, true);
            }

            _buildComplete = false;
            _buildFailed   = false;
            _failReason    = string.Empty;
            SessionState.SetBool(SessionBuildComplete, false);
            SessionState.SetBool(SessionBuildFailed,   false);
            NeedsRepaint   = true;

            // Defer the build call so it runs after the current IMGUI frame
            // completes both its Layout and Repaint passes. Calling
            // BuildPipeline.BuildPlayer synchronously from inside OnGUI
            // blocks the main thread mid-frame and leaves the layout group
            // stack in an inconsistent state, causing EndLayoutGroup errors.
            var config = new WebGLBuildConfig
            {
                UseCurrentScene = _useCurrentScene,
                OutputPath      = OutputPath,
            };
            EditorApplication.delayCall += () => WebGLBuilder.Build(config);
        }

        private static void ServeAndOpen()
        {
            _serverError = string.Empty;
            try
            {
                WebGLFileServer.Start(OutputPath);
            }
            catch (System.Exception ex)
            {
                _serverError = ex.Message.Contains("Access")
                    ? "Access denied — run Unity as administrator or register the URL with: netsh http add urlacl url=http://+:8080/ user=Everyone"
                    : ex.Message;
                return;
            }
            _qrTexture = QRCodeGenerator.GenerateQRTexture(WebGLFileServer.LanUrl, pixelsPerModule: 8);
            Application.OpenURL(WebGLFileServer.LanUrl);
        }

        private static void ResetState()
        {
            _buildComplete = false;
            _buildFailed   = false;
            _failReason    = string.Empty;
            _serverError   = string.Empty;
            SessionState.SetBool(SessionBuildComplete, false);
            SessionState.SetBool(SessionBuildFailed,   false);
            if (WebGLFileServer.IsRunning) WebGLFileServer.Stop();
            DestroyQR();
        }

        private static void DestroyQR()
        {
            if (_qrTexture != null)
            {
                Object.DestroyImmediate(_qrTexture);
                _qrTexture = null;
            }
        }

        // ── Event handlers (fired on main thread by WebGLBuilder) ─────────────

        private static void HandleBuildStarted()
        {
            _buildComplete = false;
            _buildFailed   = false;
            SessionState.SetBool(SessionBuildComplete, false);
            SessionState.SetBool(SessionBuildFailed,   false);
            NeedsRepaint   = true;
        }

        private static void HandleBuildComplete(BuildReport _)
        {
            _buildComplete = true;
            _buildFailed   = false;
            SessionState.SetBool(SessionBuildComplete, true);
            SessionState.SetBool(SessionBuildFailed,   false);
            NeedsRepaint   = true;
        }

        private static void HandleBuildFailed(BuildReport report)
        {
            _buildFailed   = true;
            _buildComplete = false;
            _failReason    = ExtractFailReason(report);
            SessionState.SetBool(SessionBuildComplete, false);
            SessionState.SetBool(SessionBuildFailed,   true);
            NeedsRepaint   = true;
        }

        private static string ExtractFailReason(BuildReport report)
        {
            if (report == null)
            {
                return WebGLBuilder.CurrentStage switch
                {
                    "WebGL module not installed" =>
                        "WebGL Build Support is not installed.\n\nOpen Unity Hub → Installs → your Unity version → Add Modules → WebGL Build Support.",
                    "Save your scene before building" =>
                        "Active scene has no file path.\nSave the scene first (File > Save), then retry.",
                    "No scenes to build" =>
                        "No enabled scenes found in Build Settings.\n\nAdd a scene via File > Build Settings, or enable 'Use current open scene' above.",
                    _ => "Build failed before it could start. Check the Console for details."
                };
            }

            var sb = new System.Text.StringBuilder();
            foreach (var step in report.steps)
            {
                foreach (var msg in step.messages)
                {
                    if (msg.type == UnityEngine.LogType.Error || msg.type == UnityEngine.LogType.Exception)
                    {
                        sb.AppendLine(msg.content);
                        if (sb.Length > 800) { sb.AppendLine("…(see Console for full output)"); goto done; }
                    }
                }
            }
            done:
            string errors = sb.ToString().Trim();
            return string.IsNullOrEmpty(errors)
                ? $"Build result: {report.summary.result} (0 errors reported). Check the Console for details."
                : errors;
        }
    }
}
