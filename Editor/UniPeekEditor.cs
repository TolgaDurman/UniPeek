using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace UniPeek
{
    /// <summary>
    /// Main UniPeek Editor window (<c>Window ▶ UniPeek</c>).
    /// <para>
    /// Provides controls for starting/stopping streaming, selecting resolution,
    /// quality and FPS cap, displays a QR code for pairing the companion app, and
    /// shows live connection status and per-device info.
    /// </para>
    /// </summary>
    public sealed class UniPeekWindow : EditorWindow
    {
        // ── Resolution options ────────────────────────────────────────────────
        private static readonly string[] ResolutionLabels = { "540p", "720p", "1080p" };
        private static readonly (int w, int h)[] Resolutions =
        {
            (960,  540),
            (1280, 720),
            (1920, 1080),
        };

        // ── Quality options ───────────────────────────────────────────────────
        private static readonly string[] QualityLabels =
            { "Performance (50)", "Balanced (75)", "Quality (85)", "Ultra (92)" };
        private static readonly int[] QualityValues = { 50, 75, 85, 92 };

        // ── FPS cap options ───────────────────────────────────────────────────
        private static readonly string[] FpsLabels  = { "10 fps", "20 fps", "30 fps", "60 fps" };
        private static readonly int[]    FpsValues   = { 10, 20, 30, 60 };

        // ── Persistent settings ───────────────────────────────────────────────
        private int  _resolutionIdx;
        private int  _qualityIdx;
        private int  _fpsIdx;
        private bool _autoStopOnPlay;
        private bool _autoStopOnFocusLoss;

        // EditorPrefs key for the "start streaming once play mode is fully entered" flag.
        // Using EditorPrefs instead of a field because a domain reload wipes all fields.
        private const string PrefPendingStart = "UniPeek_PendingStart";

        // ── Runtime state ─────────────────────────────────────────────────────
        private bool   _streaming;
        private float  _captureFps;
        private float  _encodeMs;
        private float  _rttMs;
        private bool   _webRtcActive;

        // ── QR code ───────────────────────────────────────────────────────────
        private Texture2D _qrTexture;

        // ── GUIStyles (initialized lazily) ────────────────────────────────────
        private GUIStyle _statusStyle;
        private GUIStyle _headerStyle;
        private GUIStyle _versionStyle;
        private bool     _stylesInitialized;

        // ── Logo ──────────────────────────────────────────────────────────────
        private Texture2D _logoTexture;

        // ── Reverse-connection UI state ───────────────────────────────────────
        private string  _reverseIp = string.Empty;
        private bool    _showReversePanel;
        private Vector2 _scrollPos;

        // ─────────────────────────────────────────────────────────────────────
        // Menu item
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>Opens the UniPeek window docked next to the Inspector.</summary>
        [MenuItem("Window/UniPeek/Open")]
        public static void ShowWindow()
        {
            var logoTex = AssetDatabase.LoadAssetAtPath<Texture2D>(
                "Assets/Plugins/UniPeek/Textures/unipeek-logo.png");

            var window = GetWindow<UniPeekWindow>(
                utility: false, title: "UniPeek",
                focus: true);

            window.titleContent = new GUIContent(
                "UniPeek", logoTex, "UniPeek — Game View streaming");
            window.minSize = new Vector2(300f, 520f);
        }

        // ─────────────────────────────────────────────────────────────────────
        // EditorWindow lifecycle
        // ─────────────────────────────────────────────────────────────────────

        private void OnEnable()
        {
            LoadPrefs();
            SubscribeToManager();

            _logoTexture = AssetDatabase.LoadAssetAtPath<Texture2D>(
                "Assets/Plugins/UniPeek/Textures/unipeek-logo.png");

            EditorApplication.playModeStateChanged += OnPlayModeChanged;

            // After a domain reload the EditorWindow is recreated while Unity is already
            // in play mode.  EnteredPlayMode may have fired before OnEnable ran, so we
            // check the pending flag here as well as in OnPlayModeChanged — whichever
            // runs first claims the flag via DeleteKey, the other is a no-op.
            if (Application.isPlaying && EditorPrefs.GetBool(PrefPendingStart, false))
            {
                EditorPrefs.DeleteKey(PrefPendingStart);
                DoStartStreaming();
            }
        }

        private void OnDisable()
        {
            UnsubscribeFromManager();
            EditorApplication.playModeStateChanged -= OnPlayModeChanged;
        }

        private void OnFocus()
        {
            // When window regains focus, refresh QR texture in case IP changed
            if (_streaming)
                RefreshQR();
        }

        private void OnLostFocus()
        {
            if (_autoStopOnFocusLoss && _streaming)
                StopStreaming();
        }

        private void OnPlayModeChanged(PlayModeStateChange state)
        {
            // Resume streaming that was deferred until after the domain reload.
            if (state == PlayModeStateChange.EnteredPlayMode && EditorPrefs.GetBool(PrefPendingStart, false))
            {
                EditorPrefs.DeleteKey(PrefPendingStart);
                DoStartStreaming();
                return;
            }

            // Stop streaming whenever play mode ends (user pressed Stop or domain reload)
            if (_streaming && state == PlayModeStateChange.ExitingPlayMode)
                StopStreaming();
        }

        // ─────────────────────────────────────────────────────────────────────
        // GUI
        // ─────────────────────────────────────────────────────────────────────

        private void OnGUI()
        {
            InitStyles();

            using var scrollView = new EditorGUILayout.ScrollViewScope(_scrollPos);
            _scrollPos = scrollView.scrollPosition;

            DrawHeader();
            DrawStatus();

            GUILayout.Space(6f);
            DrawQRCode();
            GUILayout.Space(6f);

            DrawSettings();
            GUILayout.Space(4f);
            DrawStatsBar();
            GUILayout.Space(6f);

            DrawMainButton();
            GUILayout.Space(4f);
            DrawReverseConnectionPanel();
            GUILayout.Space(6f);

            DrawDeviceList();
            GUILayout.Space(4f);
            DrawProNotice();

            GUILayout.FlexibleSpace();
            DrawFooter();

            if(GUILayout.Button("Change Game View Resolution (for testing)"))
            {
                ChangeResolution();
            }
        }

        private void ChangeResolution()
        {
            GameViewResolutionHelper.AddCustomResolution(1920, 1080,"Test 1080p");
        }

        // ── Sections ──────────────────────────────────────────────────────────

        private void DrawHeader()
        {
            using var row = new EditorGUILayout.HorizontalScope(EditorStyles.toolbar);

            if (_logoTexture != null)
                GUILayout.Label(_logoTexture, GUILayout.Width(20f), GUILayout.Height(20f));

            GUILayout.Label("UniPeek", _headerStyle, GUILayout.ExpandWidth(true));
            GUILayout.Label($"v{UniPeekConstants.Version}", _versionStyle);
        }

        private void DrawStatus()
        {
            var mgr   = ConnectionManager.Instance;
            var state = mgr.State;

            Color dot;
            string label;
            switch (state)
            {
                case ConnectionState.Advertising:
                    dot   = new Color(1f, 0.75f, 0f);   // amber
                    label = "Waiting for device…";
                    break;
                case ConnectionState.Connected:
                    dot   = new Color(0.2f, 0.9f, 0.3f); // green
                    int n = mgr.ConnectedDevices.Count;
                    label = n == 1
                        ? $"Connected — {mgr.ConnectedDevices[0].DeviceName}"
                        : $"Connected — {n} devices";
                    break;
                case ConnectionState.ReverseConnecting:
                    dot   = new Color(0.3f, 0.6f, 1f);   // blue
                    label = "Connecting (reverse)…";
                    break;
                default:
                    dot   = new Color(0.5f, 0.5f, 0.5f); // grey
                    label = "Disconnected";
                    break;
            }

            using var row = new EditorGUILayout.HorizontalScope();
            DrawColorDot(dot);
            GUILayout.Label(label, _statusStyle);
        }

        private void DrawQRCode()
        {
            if (!_streaming) return;

            // Only show QR when waiting — once connected, hide it to save space
            if (ConnectionManager.Instance.State == ConnectionState.Connected) return;

            RefreshQR();

            if (_qrTexture == null)
            {
                EditorGUILayout.HelpBox(
                    "Could not generate QR code (check local network connection).",
                    MessageType.Warning);
                return;
            }

            float available = position.width - 32f;
            float size      = Mathf.Min(available, 240f);

            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.FlexibleSpace();
                GUILayout.Label(_qrTexture,
                    GUILayout.Width(size), GUILayout.Height(size));
                GUILayout.FlexibleSpace();
            }

            string ip = QRCodeGenerator.GetLocalIPv4();
            EditorGUILayout.LabelField(
                $"Scan with the UniPeek app  ·  {ip}:{UniPeekConstants.DefaultPort}",
                EditorStyles.centeredGreyMiniLabel);
        }

        private void DrawSettings()
        {
            EditorGUI.BeginChangeCheck();

            using (new EditorGUI.DisabledGroupScope(_streaming))
            {
                _resolutionIdx = EditorGUILayout.Popup("Resolution", _resolutionIdx, ResolutionLabels);
                _qualityIdx    = EditorGUILayout.Popup("Quality",    _qualityIdx,    QualityLabels);
                _fpsIdx        = EditorGUILayout.Popup("FPS Cap",    _fpsIdx,        FpsLabels);
            }

            GUILayout.Space(2f);
            _autoStopOnPlay      = EditorGUILayout.Toggle("Stop on Play Mode",  _autoStopOnPlay);
            _autoStopOnFocusLoss = EditorGUILayout.Toggle("Stop on Focus Loss", _autoStopOnFocusLoss);

            if (EditorGUI.EndChangeCheck())
                SavePrefs();
        }

        private void DrawStatsBar()
        {
            if (!_streaming) return;

            using var row = new EditorGUILayout.HorizontalScope(EditorStyles.helpBox);

            if (_webRtcActive)
            {
                // WebRTC mode: show transport label + RTT indicator
                Color dot = RttColor(_rttMs);
                DrawColorDot(dot);
                GUILayout.Label("WebRTC", GUILayout.Width(52f));
                GUILayout.Label(_rttMs > 0f ? $"RTT: {_rttMs:F0} ms" : "RTT: —",
                    GUILayout.Width(80f));
            }
            else
            {
                GUILayout.Label($"Capture FPS: {_captureFps:F1}", GUILayout.Width(120f));
                GUILayout.Label($"Encode: {_encodeMs:F0} ms",     GUILayout.Width(90f));
            }

            int clientCount = ConnectionManager.Instance.ConnectedDevices.Count;
            GUILayout.Label($"Clients: {clientCount}", GUILayout.ExpandWidth(true));
        }

        private static Color RttColor(float rttMs)
        {
            if (rttMs <= 0f)                                return new Color(0.5f, 0.5f, 0.5f);
            if (rttMs < UniPeekConstants.RttGreenMs)        return new Color(0.2f, 0.9f, 0.3f);
            if (rttMs < UniPeekConstants.RttYellowMs)       return new Color(1f,   0.9f, 0f);
            if (rttMs < UniPeekConstants.RttOrangeMs)       return new Color(1f,   0.5f, 0f);
            return new Color(0.9f, 0.2f, 0.2f);
        }

        private void DrawMainButton()
        {
            Color prev = GUI.backgroundColor;
            GUI.backgroundColor = _streaming
                ? new Color(1f, 0.4f, 0.4f)
                : new Color(0.4f, 0.85f, 0.5f);

            string label = _streaming ? "■  Stop Streaming" : "▶  Start Streaming";
            if (GUILayout.Button(label, GUILayout.Height(36f)))
            {
                if (_streaming) StopStreaming();
                else            StartStreaming();
            }
            GUI.backgroundColor = prev;
        }

        private void DrawReverseConnectionPanel()
        {
            _showReversePanel = EditorGUILayout.Foldout(
                _showReversePanel, "Reverse Connection (phone ← Unity)", true);

            if (!_showReversePanel) return;

            using var indent = new EditorGUI.IndentLevelScope();
            _reverseIp = EditorGUILayout.TextField("Phone IP", _reverseIp);

            using (new EditorGUI.DisabledGroupScope(
                !_streaming || string.IsNullOrWhiteSpace(_reverseIp)))
            {
                if (GUILayout.Button("Connect to Phone"))
                    ConnectionManager.Instance.ConnectReverse(_reverseIp);
            }

            EditorGUILayout.HelpBox(
                "Use when the phone can't reach this machine " +
                "(e.g. strict firewall). The app must be in 'Listen' mode.",
                MessageType.Info);
        }

        private void DrawDeviceList()
        {
            var devices = ConnectionManager.Instance.ConnectedDevices;
            if (devices.Count == 0) return;

            EditorGUILayout.LabelField("Connected Devices", EditorStyles.boldLabel);

            foreach (var d in devices)
            {
                using var row = new EditorGUILayout.HorizontalScope(EditorStyles.helpBox);
                DrawColorDot(new Color(0.2f, 0.9f, 0.3f));
                GUILayout.Label(d.DeviceName, GUILayout.ExpandWidth(true));
                GUILayout.Label(d.ConnectedAt.ToLocalTime().ToString("HH:mm:ss"),
                    EditorStyles.miniLabel, GUILayout.Width(60f));
            }
        }

        private void DrawProNotice()
        {
            EditorGUILayout.HelpBox(
                "Pro features (multi-device, 1080p, 60 fps) are unlocked via the UniPeek app.",
                MessageType.None);
        }

        private void DrawFooter()
        {
            using var row = new EditorGUILayout.HorizontalScope(EditorStyles.toolbar);
            GUILayout.Label($"Port {UniPeekConstants.DefaultPort}", EditorStyles.miniLabel);
            GUILayout.FlexibleSpace();

            if (GUILayout.Button("Docs", EditorStyles.toolbarButton, GUILayout.Width(38f)))
                Application.OpenURL("https://github.com/your-org/UniPeek#readme");

            if (GUILayout.Button("Reset FW", EditorStyles.toolbarButton, GUILayout.Width(56f)))
                FirewallHelper.ResetAndReConfigure();
        }

        // ── Style / utility helpers ────────────────────────────────────────────

        private void InitStyles()
        {
            if (_stylesInitialized) return;
            _stylesInitialized = true;

            _headerStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize  = 14,
                alignment = TextAnchor.MiddleLeft,
            };

            _versionStyle = new GUIStyle(EditorStyles.miniLabel)
            {
                alignment = TextAnchor.MiddleRight,
                normal    = { textColor = new Color(0.6f, 0.6f, 0.6f) },
            };

            _statusStyle = new GUIStyle(EditorStyles.label)
            {
                fontStyle = FontStyle.Italic,
            };
        }

        private static void DrawColorDot(Color color)
        {
            var prev = GUI.color;
            GUI.color = color;
            GUILayout.Label("●", GUILayout.Width(16f), GUILayout.Height(16f));
            GUI.color = prev;
        }

        // ─────────────────────────────────────────────────────────────────────
        // Streaming control
        // ─────────────────────────────────────────────────────────────────────

        private void StartStreaming()
        {
            if (_streaming) return;

            // Persist settings so they survive a domain reload.
            SavePrefs();
            Application.runInBackground = true;

            if (!EditorApplication.isPlaying)
            {
                // Setting isPlaying triggers a domain reload which destroys the
                // ConnectionManager singleton and all its background threads before
                // streaming would even begin.  Instead, plant a flag in EditorPrefs
                // (which survives the reload) and finish starting inside EnteredPlayMode /
                // OnEnable — whichever fires first after the domain is back up.
                EditorPrefs.SetBool(PrefPendingStart, true);
                EditorApplication.isPlaying = true;
                return;
            }

            // Already in play mode — no domain reload, start immediately.
            DoStartStreaming();
        }

        private void DoStartStreaming()
        {
            var (w, h) = Resolutions[_resolutionIdx];
            int quality = QualityValues[_qualityIdx];
            int fps     = FpsValues[_fpsIdx];
            ConnectionManager.Instance.ApplyConfig(w, h, quality, fps);

            ConnectionManager.Instance.StartStreaming();
            _streaming = true;

            Application.runInBackground = true;
            RefreshQR();
            Repaint();
        }

        private void StopStreaming()
        {
            if (!_streaming) return;
            ConnectionManager.Instance.StopStreaming();
            _streaming = false;
            _captureFps = 0f;
            _encodeMs   = 0f;
            DestroyQR();
            Repaint();
        }

        // ─────────────────────────────────────────────────────────────────────
        // ConnectionManager event subscriptions
        // ─────────────────────────────────────────────────────────────────────

        private void SubscribeToManager()
        {
            var mgr = ConnectionManager.Instance;
            mgr.StateChanged      += OnStateChanged;
            mgr.DeviceConnected   += OnDeviceConnected;
            mgr.DeviceDisconnected+= OnDeviceDisconnected;
            mgr.StatsUpdated      += OnStatsUpdated;
            mgr.RttUpdated        += OnRttUpdated;
        }

        private void UnsubscribeFromManager()
        {
            // Guard against the manager being disposed before the window
            if (ConnectionManager.Instance == null) return;
            var mgr = ConnectionManager.Instance;
            mgr.StateChanged      -= OnStateChanged;
            mgr.DeviceConnected   -= OnDeviceConnected;
            mgr.DeviceDisconnected-= OnDeviceDisconnected;
            mgr.StatsUpdated      -= OnStatsUpdated;
            mgr.RttUpdated        -= OnRttUpdated;
        }

        private void OnStateChanged(ConnectionState _) => Repaint();
        private void OnDeviceConnected(DeviceInfo _)   => Repaint();
        private void OnDeviceDisconnected(DeviceInfo _)=> Repaint();

        private void OnStatsUpdated(float fps, float encodeMs)
        {
            _captureFps  = fps;
            _encodeMs    = encodeMs;
            _webRtcActive = ConnectionManager.Instance.WebRtcActive;
            Repaint();
        }

        private void OnRttUpdated(float rttMs)
        {
            _rttMs        = rttMs;
            _webRtcActive = ConnectionManager.Instance.WebRtcActive;
            Repaint();
        }

        // ─────────────────────────────────────────────────────────────────────
        // QR helpers
        // ─────────────────────────────────────────────────────────────────────

        private void RefreshQR()
        {
            _qrTexture = QRCodeGenerator.GetConnectionQR(
                UniPeekConstants.DefaultPort, pixelsPerModule: 8);
        }

        private void DestroyQR()
        {
            // QRCodeGenerator caches and manages its own texture lifetime;
            // just null our reference.
            _qrTexture = null;
        }

        // ─────────────────────────────────────────────────────────────────────
        // EditorPrefs persistence
        // ─────────────────────────────────────────────────────────────────────

        private void LoadPrefs()
        {
            _resolutionIdx       = EditorPrefs.GetInt(UniPeekConstants.PrefResolution, 1); // default 720p
            _qualityIdx          = EditorPrefs.GetInt(UniPeekConstants.PrefQuality,    1); // default Balanced
            _fpsIdx              = EditorPrefs.GetInt(UniPeekConstants.PrefFpsCap,     2); // default 30 fps
            _autoStopOnPlay      = EditorPrefs.GetBool(UniPeekConstants.PrefAutoStopPlay,  true);
            _autoStopOnFocusLoss = EditorPrefs.GetBool(UniPeekConstants.PrefAutoStopFocus, false);
        }

        private void SavePrefs()
        {
            EditorPrefs.SetInt (UniPeekConstants.PrefResolution,  _resolutionIdx);
            EditorPrefs.SetInt (UniPeekConstants.PrefQuality,     _qualityIdx);
            EditorPrefs.SetInt (UniPeekConstants.PrefFpsCap,      _fpsIdx);
            EditorPrefs.SetBool(UniPeekConstants.PrefAutoStopPlay,  _autoStopOnPlay);
            EditorPrefs.SetBool(UniPeekConstants.PrefAutoStopFocus, _autoStopOnFocusLoss);
        }
    }
}
