using UnityEditor;
using UnityEngine;

namespace UniPeek
{
    /// <summary>
    /// Main UniPeek Editor window (<c>Window ▶ UniPeek</c>).
    /// </summary>
    public sealed class UniPeekWindow : EditorWindow
    {
        // ── Persistent settings ───────────────────────────────────────────────
        private bool _requirePlayMode;
        private SocketMode _socketMode;
        private LogLevel   _logLevel;

        // Survives domain reloads; claimed with DeleteKey to prevent double-start.
        private const string PrefPendingStart = "UniPeek_PendingStart";

        // ── Runtime state ─────────────────────────────────────────────────────
        private bool  _streaming;
        private float _captureFps;
        private float _encodeMs;
        private float _rttMs;
        private bool  _webRtcActive;

        // ── QR code ───────────────────────────────────────────────────────────
        private Texture2D _qrTexture;

        // ── Styles (initialized lazily) ───────────────────────────────────────
        private GUIStyle _titleStyle;
        private GUIStyle _versionStyle;
        private GUIStyle _statusTextStyle;
        private GUIStyle _sectionLabelStyle;
        private bool     _stylesInitialized;

        // ── Assets ────────────────────────────────────────────────────────────
        private Texture2D _logoTexture;
        private Texture2D _proIcon;

        // ── Editor name ───────────────────────────────────────────────────────
        private string _editorName = string.Empty;

        // ── Port ──────────────────────────────────────────────────────────────
        private int _port = UniPeekConstants.DefaultPort;

        // ── Reverse-connection UI ─────────────────────────────────────────────
        private string  _reverseIp        = string.Empty;
        private bool    _showReversePanel;
        private Vector2 _scrollPos;

        // ── Colors ────────────────────────────────────────────────────────────
        private static readonly Color ColGreen = new(0.18f, 0.80f, 0.32f);
        private static readonly Color ColAmber = new(1.00f, 0.72f, 0.00f);
        private static readonly Color ColBlue  = new(0.28f, 0.58f, 1.00f);
        private static readonly Color ColGrey  = new(0.45f, 0.45f, 0.45f);

        // ─────────────────────────────────────────────────────────────────────
        // Menu item
        // ─────────────────────────────────────────────────────────────────────

        [MenuItem("Window/UniPeek")]
        public static void ShowWindow()
        {
            var logoTex = AssetDatabase.LoadAssetAtPath<Texture2D>(
                "Assets/Plugins/UniPeek/Textures/unipeek-logo.png");

            var window = GetWindow<UniPeekWindow>(utility: false, title: "UniPeek", focus: true);
            window.titleContent = new GUIContent("UniPeek", logoTex, "UniPeek — Game View streaming");
            window.minSize = new Vector2(280f, 440f);
        }

        // ─────────────────────────────────────────────────────────────────────
        // Lifecycle
        // ─────────────────────────────────────────────────────────────────────

        private void OnEnable()
        {
            LoadPrefs();
            SubscribeToManager();

            _logoTexture = AssetDatabase.LoadAssetAtPath<Texture2D>(
                "Assets/Plugins/UniPeek/Textures/unipeek-logo.png");
            _proIcon = AssetDatabase.LoadAssetAtPath<Texture2D>(
                "Assets/Plugins/UniPeek/Textures/pro-user.png");

            EditorApplication.playModeStateChanged += OnPlayModeChanged;

            // After a domain reload the EditorWindow is recreated while Unity is already
            // in play mode. EnteredPlayMode may have fired before OnEnable ran, so we
            // check the pending flag here as well — whichever runs first claims it.
            if (Application.isPlaying && EditorPrefs.GetBool(PrefPendingStart, false))
            {
                EditorPrefs.DeleteKey(PrefPendingStart);
                DoStartStreaming();
            }
            else if (!_requirePlayMode && EditorPrefs.GetBool(UniPeekConstants.PrefPersistStreaming, false))
            {
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
            if (_streaming) RefreshQR();
        }

        private void OnPlayModeChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.EnteredPlayMode && EditorPrefs.GetBool(PrefPendingStart, false))
            {
                EditorPrefs.DeleteKey(PrefPendingStart);
                DoStartStreaming();
                return;
            }

            if (state == PlayModeStateChange.EnteredPlayMode
                && !_requirePlayMode && !_streaming
                && EditorPrefs.GetBool(UniPeekConstants.PrefPersistStreaming, false))
            {
                DoStartStreaming();
                return;
            }

            if (_requirePlayMode && _streaming && state == PlayModeStateChange.ExitingPlayMode)
                StopStreaming();
        }

        // ─────────────────────────────────────────────────────────────────────
        // GUI
        // ─────────────────────────────────────────────────────────────────────

        private void OnGUI()
        {
            InitStyles();

            DrawHeader();

            using var scroll = new EditorGUILayout.ScrollViewScope(_scrollPos);
            _scrollPos = scroll.scrollPosition;

            GUILayout.Space(10f);
            DrawStatusCard();
            GUILayout.Space(10f);
            DrawMainButton();
            GUILayout.Space(8f);

            DrawQRCode();

            if (_streaming)
            {
                DrawStatsBar();
                GUILayout.Space(8f);
            }

            DrawSectionLabel("Options");
            DrawSettings();
            GUILayout.Space(8f);

            DrawDeviceList();
            DrawReverseConnectionPanel();

            GUILayout.FlexibleSpace();
            DrawFooter();
        }

        // ── Header ────────────────────────────────────────────────────────────

        private void DrawHeader()
        {
            using var row = new EditorGUILayout.HorizontalScope(EditorStyles.toolbar);

            if (_logoTexture != null)
                GUILayout.Label(_logoTexture, GUILayout.Width(18f), GUILayout.Height(18f));

            GUILayout.Label("UniPeek", _titleStyle, GUILayout.ExpandWidth(true));
            GUILayout.Label($"v{UniPeekConstants.Version}", _versionStyle);
        }

        // ── Status card ───────────────────────────────────────────────────────

        private void DrawStatusCard()
        {
            var mgr   = ConnectionManager.Instance;
            var state = mgr.State;

            Color  dotColor;
            string primaryText;
            string secondaryText = string.Empty;

            switch (state)
            {
                case ConnectionState.Advertising:
                    dotColor      = ColAmber;
                    primaryText   = string.IsNullOrWhiteSpace(_editorName)
                        ? System.Environment.MachineName
                        : _editorName;
                    secondaryText = $"{QRCodeGenerator.GetLocalIPv4()}:{ConnectionManager.Instance.Port}";
                    break;
                case ConnectionState.Connected:
                    dotColor = ColGreen;
                    int n    = mgr.ConnectedDevices.Count;
                    primaryText   = n == 1 ? mgr.ConnectedDevices[0].DeviceName : $"{n} devices";
                    secondaryText = "Connected";
                    break;
                case ConnectionState.ReverseConnecting:
                    dotColor      = ColBlue;
                    primaryText   = "Connecting…";
                    secondaryText = "Reverse mode";
                    break;
                default:
                    dotColor    = ColGrey;
                    primaryText = "Not streaming";
                    break;
            }

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            GUILayout.Space(6f);

            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Space(6f);
                DrawColorDot(dotColor);
                GUILayout.Space(4f);
                GUILayout.Label(primaryText, _statusTextStyle, GUILayout.ExpandWidth(true));
                GUILayout.Space(6f);
            }

            if (!string.IsNullOrEmpty(secondaryText))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Space(28f);
                    GUILayout.Label(secondaryText, EditorStyles.miniLabel);
                }
            }

            GUILayout.Space(6f);
            EditorGUILayout.EndVertical();
        }

        // ── Main button ───────────────────────────────────────────────────────

        private void DrawMainButton()
        {
            var prev = GUI.backgroundColor;
            GUI.backgroundColor = _streaming
                ? new Color(0.88f, 0.30f, 0.30f)
                : new Color(0.28f, 0.76f, 0.44f);

            if (GUILayout.Button(
                    _streaming ? "■   Stop Streaming" : "▶   Start Streaming",
                    GUILayout.Height(42f)))
            {
                if (_streaming) StopStreaming();
                else            StartStreaming();
            }

            GUI.backgroundColor = prev;
        }

        // ── QR code ───────────────────────────────────────────────────────────

        private void DrawQRCode()
        {
            if (!_streaming) return;
            if (ConnectionManager.Instance.State == ConnectionState.Connected) return;

            RefreshQR();

            if (_qrTexture == null)
            {
                EditorGUILayout.HelpBox(
                    "Could not generate QR code — check local network connection.",
                    MessageType.Warning);
                GUILayout.Space(6f);
                return;
            }

            float size = Mathf.Min(position.width - 48f, 220f);
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.FlexibleSpace();
                GUILayout.Label(_qrTexture, GUILayout.Width(size), GUILayout.Height(size));
                GUILayout.FlexibleSpace();
            }

            GUILayout.Label(
                "Scan with the UniPeek app to connect",
                EditorStyles.centeredGreyMiniLabel);
            GUILayout.Space(10f);
        }

        // ── Stats bar ─────────────────────────────────────────────────────────

        private void DrawStatsBar()
        {
            using var row = new EditorGUILayout.HorizontalScope(EditorStyles.helpBox);

            if (_webRtcActive)
            {
                DrawColorDot(RttColor(_rttMs), small: true);
                GUILayout.Space(2f);
                GUILayout.Label("WebRTC", EditorStyles.miniLabel, GUILayout.Width(46f));
                GUILayout.Label(
                    _rttMs > 0f ? $"RTT {_rttMs:F0} ms" : "RTT —",
                    EditorStyles.miniLabel, GUILayout.Width(64f));
            }
            else
            {
                GUILayout.Label($"FPS  {_captureFps:F1}", EditorStyles.miniLabel, GUILayout.Width(64f));
                GUILayout.Label($"Enc  {_encodeMs:F0} ms", EditorStyles.miniLabel, GUILayout.Width(70f));
            }

            GUILayout.FlexibleSpace();
            int c = ConnectionManager.Instance.ConnectedDevices.Count;
            GUILayout.Label(c == 1 ? "1 client" : $"{c} clients", EditorStyles.miniLabel);
        }

        // ── Settings ──────────────────────────────────────────────────────────

        private void DrawSettings()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                _editorName = EditorGUILayout.TextField("Editor Name", _editorName);
                if (GUILayout.Button("Set", EditorStyles.miniButton, GUILayout.Width(32f)))
                {
                    SavePrefs();
                    QRCodeGenerator.Invalidate();
                }
            }

            EditorGUI.BeginChangeCheck();
            var pmIndex = EditorGUILayout.Popup("Run in Play Mode", _requirePlayMode ? 0 : 1,
                new[] { "True", "False" });
            if (EditorGUI.EndChangeCheck())
            {
                _requirePlayMode = pmIndex == 0;
                SavePrefs();
            }

            if (!_requirePlayMode)
            {
                GUILayout.Space(4f);
                EditorGUILayout.HelpBox(
                    "Recompiling will cut the connection. Pro users have automatic reconnect. " +
                    "Enabling 'Only run in Play Mode' avoids interruptions.",
                    MessageType.Info);
            }

            GUILayout.Space(4f);
#if UNITY_WEBRTC
            EditorGUI.BeginChangeCheck();
            _socketMode = (SocketMode)EditorGUILayout.EnumPopup("Socket Mode", _socketMode);
            if (EditorGUI.EndChangeCheck())
                SavePrefs();
#else
            EditorGUI.BeginDisabledGroup(true);
            EditorGUILayout.EnumPopup("Socket Mode", SocketMode.WebSocket);
            EditorGUI.EndDisabledGroup();
            EditorGUILayout.HelpBox("Install com.unity.webrtc to enable WebRTC mode.", MessageType.None);
#endif

            EditorGUI.BeginChangeCheck();
            _logLevel = (LogLevel)EditorGUILayout.EnumPopup("Log Level", _logLevel);
            if (EditorGUI.EndChangeCheck())
            {
                UniPeekConstants.CurrentLogLevel = _logLevel;
                SavePrefs();
            }

            GUILayout.Space(4f);
            var mgr = ConnectionManager.Instance;
            EditorGUI.BeginChangeCheck();
            var newMethod = (CaptureMethod)EditorGUILayout.EnumPopup("Capture Method", mgr.ActiveCaptureMethod);
            if (EditorGUI.EndChangeCheck())
                mgr.SetCaptureMethod(newMethod);

            switch (mgr.ActiveCaptureMethod)
            {
                case CaptureMethod.CameraRender:
                    EditorGUILayout.HelpBox(
                        "Camera.Render() → ReadPixels. Synchronous. Works in Edit + Play Mode.",
                        MessageType.None);
                    break;
                case CaptureMethod.AsyncGPUReadback:
                    EditorGUILayout.HelpBox(
                        "Camera.Render() → AsyncGPUReadback. Non-blocking, ~1 frame extra latency.",
                        MessageType.None);
                    break;
            }
        }

        // ── Device list ───────────────────────────────────────────────────────

        private void DrawDeviceList()
        {
            var devices = ConnectionManager.Instance.ConnectedDevices;
            if (devices.Count == 0) return;

            DrawSectionLabel("Connected Devices");

            foreach (var d in devices)
            {
                using var card = new EditorGUILayout.HorizontalScope(EditorStyles.helpBox);
                DrawColorDot(ColGreen);
                GUILayout.Space(2f);
                if (d.IsPro && _proIcon != null)
                    GUILayout.Label(_proIcon, GUILayout.Width(16f), GUILayout.Height(16f));
                GUILayout.Label(d.DeviceName, GUILayout.ExpandWidth(true));
                GUILayout.Label(
                    d.ConnectedAt.ToLocalTime().ToString("HH:mm:ss"),
                    EditorStyles.miniLabel, GUILayout.Width(52f));
            }

            GUILayout.Space(8f);
        }

        // ── Reverse connection ────────────────────────────────────────────────

        private void DrawReverseConnectionPanel()
        {
            _showReversePanel = EditorGUILayout.Foldout(
                _showReversePanel, "Reverse Connection (Android Only)", toggleOnLabelClick: true);

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
                "Use when the phone can't reach this machine (e.g. strict firewall). " +
                "The app must be in 'Listen' mode.",
                MessageType.Info);

            GUILayout.Space(6f);
        }

        // ── Footer ────────────────────────────────────────────────────────────

        private void DrawFooter()
        {
            GUILayout.Label(
                "Pro features (multi-device, 1080p, 60 fps) unlocked via the UniPeek app.",
                EditorStyles.centeredGreyMiniLabel);
            GUILayout.Space(2f);

            using var row = new EditorGUILayout.HorizontalScope(EditorStyles.toolbar);
            GUILayout.Label("Port", EditorStyles.miniLabel, GUILayout.Width(28f));
            EditorGUI.BeginDisabledGroup(_streaming);
            var newPort = EditorGUILayout.IntField(_port, EditorStyles.toolbarTextField, GUILayout.Width(50f));
            if (newPort != _port && newPort > 1024 && newPort <= 65535)
            {
                _port = newPort;
                SavePrefs();
                QRCodeGenerator.Invalidate();
            }
            EditorGUI.EndDisabledGroup();
            GUILayout.FlexibleSpace();

            if (GUILayout.Button("Docs", EditorStyles.toolbarButton, GUILayout.Width(38f)))
                Application.OpenURL("https://unipeek.app");

            if (GUILayout.Button("Reset FW", EditorStyles.toolbarButton, GUILayout.Width(58f)))
                FirewallHelper.ResetAndReConfigure();
        }

        // ── Utilities ─────────────────────────────────────────────────────────

        private void DrawSectionLabel(string title)
        {
            GUILayout.Space(2f);
            using var row = new EditorGUILayout.HorizontalScope();
            GUILayout.Label(title.ToUpper(), _sectionLabelStyle);
            GUILayout.Space(4f);
        }

        private static void DrawColorDot(Color color, bool small = false)
        {
            float w = small ? 14f : 16f;
            var prev = GUI.color;
            GUI.color = color;
            GUILayout.Label("●", GUILayout.Width(w), GUILayout.Height(w));
            GUI.color = prev;
        }

        private static Color RttColor(float rttMs)
        {
            if (rttMs <= 0f)                          return ColGrey;
            if (rttMs < UniPeekConstants.RttGreenMs)  return ColGreen;
            if (rttMs < UniPeekConstants.RttYellowMs) return new Color(1f, 0.9f, 0f);
            if (rttMs < UniPeekConstants.RttOrangeMs) return new Color(1f, 0.5f, 0f);
            return new Color(0.9f, 0.2f, 0.2f);
        }

        private void InitStyles()
        {
            if (_stylesInitialized) return;
            _stylesInitialized = true;

            _titleStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize  = 13,
                alignment = TextAnchor.MiddleLeft,
            };

            _versionStyle = new GUIStyle(EditorStyles.miniLabel)
            {
                alignment = TextAnchor.MiddleRight,
                normal    = { textColor = new Color(0.55f, 0.55f, 0.55f) },
            };

            _statusTextStyle = new GUIStyle(EditorStyles.label)
            {
                fontSize  = 12,
                fontStyle = FontStyle.Bold,
            };

            _sectionLabelStyle = new GUIStyle(EditorStyles.miniLabel)
            {
                fontStyle = FontStyle.Bold,
                normal    = { textColor = new Color(0.50f, 0.50f, 0.50f) },
            };
        }

        // ─────────────────────────────────────────────────────────────────────
        // Streaming control
        // ─────────────────────────────────────────────────────────────────────

        private void StartStreaming()
        {
            if (_streaming) return;

            SavePrefs();
            Application.runInBackground = true;

            if (_requirePlayMode && !EditorApplication.isPlaying)
            {
                // Setting isPlaying triggers a domain reload — plant a flag so streaming
                // resumes once the domain is back up (EnteredPlayMode / OnEnable).
                EditorPrefs.SetBool(PrefPendingStart, true);
                EditorApplication.isPlaying = true;
                return;
            }

            DoStartStreaming();
        }

        private void DoStartStreaming()
        {
            ConnectionManager.Instance.StartStreaming(_port);
            _streaming = true;

            if (!_requirePlayMode)
                EditorPrefs.SetBool(UniPeekConstants.PrefPersistStreaming, true);

            Application.runInBackground = true;
            RefreshQR();
            Repaint();
        }

        private void StopStreaming()
        {
            if (!_streaming) return;
            ConnectionManager.Instance.StopStreaming();
            _streaming  = false;
            _captureFps = 0f;
            _encodeMs   = 0f;
            EditorPrefs.DeleteKey(UniPeekConstants.PrefPersistStreaming);
            DestroyQR();
            Repaint();
        }

        // ─────────────────────────────────────────────────────────────────────
        // ConnectionManager subscriptions
        // ─────────────────────────────────────────────────────────────────────

        private void SubscribeToManager()
        {
            var mgr = ConnectionManager.Instance;
            mgr.StateChanged       += OnStateChanged;
            mgr.DeviceConnected    += OnDeviceConnected;
            mgr.DeviceDisconnected += OnDeviceDisconnected;
            mgr.StatsUpdated       += OnStatsUpdated;
            mgr.RttUpdated         += OnRttUpdated;
        }

        private void UnsubscribeFromManager()
        {
            if (ConnectionManager.Instance == null) return;
            var mgr = ConnectionManager.Instance;
            mgr.StateChanged       -= OnStateChanged;
            mgr.DeviceConnected    -= OnDeviceConnected;
            mgr.DeviceDisconnected -= OnDeviceDisconnected;
            mgr.StatsUpdated       -= OnStatsUpdated;
            mgr.RttUpdated         -= OnRttUpdated;
        }

        private void OnStateChanged(ConnectionState _)  => Repaint();
        private void OnDeviceConnected(DeviceInfo _)    => Repaint();
        private void OnDeviceDisconnected(DeviceInfo _) => Repaint();

        private void OnStatsUpdated(float fps, float encodeMs)
        {
            _captureFps   = fps;
            _encodeMs     = encodeMs;
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
            => _qrTexture = QRCodeGenerator.GetConnectionQR(
                _port, pixelsPerModule: 8);

        private void DestroyQR() => _qrTexture = null;

        // ─────────────────────────────────────────────────────────────────────
        // EditorPrefs + crash-safe file storage
        // ─────────────────────────────────────────────────────────────────────

        // EditorPrefs (NSUserDefaults on macOS) has a delayed disk flush — a hard
        // crash can lose the last write. The editor name is also written to a file
        // so it survives crashes (file I/O is synchronous / immediately flushed).
        private static readonly string EditorNameFilePath =
            System.IO.Path.Combine("UserSettings", "UniPeekEditorName.txt");

        private void LoadPrefs()
        {
            _requirePlayMode = EditorPrefs.GetBool(UniPeekConstants.PrefAutoStopPlay, true);
            _socketMode      = (SocketMode)EditorPrefs.GetInt(UniPeekConstants.PrefSocketMode, (int)SocketMode.WebRTC);
            _logLevel        = (LogLevel)EditorPrefs.GetInt(UniPeekConstants.PrefLogLevel, (int)LogLevel.All);
            _port            = EditorPrefs.GetInt(UniPeekConstants.PrefPort, UniPeekConstants.DefaultPort);
            UniPeekConstants.CurrentLogLevel = _logLevel;

            // File takes priority — it's written synchronously so it's crash-safe.
            if (System.IO.File.Exists(EditorNameFilePath))
                _editorName = System.IO.File.ReadAllText(EditorNameFilePath);
            else
                _editorName = EditorPrefs.GetString(UniPeekConstants.PrefEditorName, string.Empty);
        }

        private void SavePrefs()
        {
            EditorPrefs.SetBool(UniPeekConstants.PrefAutoStopPlay, _requirePlayMode);
            EditorPrefs.SetInt(UniPeekConstants.PrefSocketMode, (int)_socketMode);
            EditorPrefs.SetInt(UniPeekConstants.PrefLogLevel, (int)_logLevel);
            EditorPrefs.SetString(UniPeekConstants.PrefEditorName, _editorName);
            EditorPrefs.SetInt(UniPeekConstants.PrefPort, _port);

            // Also write to file for crash resilience.
            try
            {
                System.IO.Directory.CreateDirectory("UserSettings");
                System.IO.File.WriteAllText(EditorNameFilePath, _editorName);
            }
            catch { /* non-critical */ }
        }
    }
}
