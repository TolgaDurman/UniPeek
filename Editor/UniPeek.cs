using UnityEngine;

namespace UniPeek
{
    /// <summary>
    /// Shared constants, port numbers, and logging utilities for the UniPeek plugin.
    /// All other UniPeek components reference this class for configuration defaults.
    /// </summary>
    public static class UniPeekConstants
    {
        // ── Version ──────────────────────────────────────────────────────────
        /// <summary>Current plugin version string.</summary>
        public const string Version = "1.0.0";

        // ── Networking ───────────────────────────────────────────────────────
        /// <summary>Default WebSocket server port (normal mode — Unity listens, phone connects).</summary>
        public const int DefaultPort = 7777;

        /// <summary>WebSocket port used in reverse connection mode (phone acts as server, Unity connects out).</summary>
        public const int ReversePort = 7778;

        // ── mDNS / DNS-SD ────────────────────────────────────────────────────
        /// <summary>mDNS service type broadcast on the local network.</summary>
        public const string ServiceType = "_unipeek._tcp";

        /// <summary>mDNS multicast group (RFC 6762).</summary>
        public const string MdnsMulticastAddress = "224.0.0.251";

        /// <summary>mDNS UDP port (RFC 6762).</summary>
        public const int MdnsPort = 5353;

        // ── RTT thresholds (milliseconds) ───────────────────────────────────
        /// <summary>RTT below this value is shown as green.</summary>
        public const float RttGreenMs  =  20f;
        /// <summary>RTT below this value is shown as yellow.</summary>
        public const float RttYellowMs =  50f;
        /// <summary>RTT below this value is shown as orange.</summary>
        public const float RttOrangeMs = 100f;
        // RTT ≥ RttOrangeMs is shown as red.

        /// <summary>Interval in seconds between RTT ping messages.</summary>
        public const float PingIntervalSeconds = 30f;

        // ── EditorPrefs keys ─────────────────────────────────────────────────
        /// <summary>EditorPrefs key for the saved resolution index.</summary>
        public const string PrefResolution = "UniPeek_Resolution";

        /// <summary>EditorPrefs key for the saved quality index.</summary>
        public const string PrefQuality = "UniPeek_Quality";

        /// <summary>EditorPrefs key for the saved FPS-cap index.</summary>
        public const string PrefFpsCap = "UniPeek_FpsCap";

        /// <summary>EditorPrefs key for the auto-stop-on-play-mode toggle.</summary>
        public const string PrefAutoStopPlay = "UniPeek_AutoStopPlay";

        /// <summary>EditorPrefs key for the auto-stop-on-focus-loss toggle.</summary>
        public const string PrefAutoStopFocus = "UniPeek_AutoStopFocus";

        // ── Logging helpers ──────────────────────────────────────────────────
        /// <summary>Writes an info-level message tagged with [UniPeek].</summary>
        public static void Log(string message) => Debug.Log($"[UniPeek] {message}");

        /// <summary>Writes a warning-level message tagged with [UniPeek].</summary>
        public static void LogWarning(string message) => Debug.LogWarning($"[UniPeek] {message}");

        /// <summary>Writes an error-level message tagged with [UniPeek].</summary>
        public static void LogError(string message) => Debug.LogError($"[UniPeek] {message}");
    }
}
