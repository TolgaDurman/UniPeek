using System;
using System.Diagnostics;
using UnityEditor;

namespace UniPeek
{
    /// <summary>
    /// Manages OS-level firewall rules required for the UniPeek WebSocket server.
    /// <para>
    /// On <b>Windows</b>: adds a permanent inbound TCP rule via <c>netsh</c> with a
    /// one-time UAC elevation prompt, then records completion in <see cref="EditorPrefs"/>
    /// so the rule is never added twice.
    /// </para>
    /// <para>
    /// On <b>macOS / Linux</b>: no-op — the OS automatically prompts the user when
    /// a process first binds to a port.
    /// </para>
    /// </summary>
    public static class FirewallHelper
    {
        private const string PrefKey  = "UniPeek_FirewallConfigured";
        private const string RuleName = "UniPeek";

        /// <summary>
        /// Ensures an inbound firewall rule exists for <paramref name="port"/>.
        /// On Windows the <c>netsh</c> command is run with UAC elevation the first time;
        /// subsequent calls return immediately because the result is persisted in
        /// <see cref="EditorPrefs"/>.
        /// </summary>
        /// <param name="port">TCP port to open (defaults to <see cref="UniPeekConstants.DefaultPort"/>).</param>
        public static void EnsureFirewallRule(int port = UniPeekConstants.DefaultPort)
        {
#if UNITY_EDITOR_WIN
            if (EditorPrefs.GetBool(PrefKey, false))
                return;

            AddWindowsFirewallRule(port);
#endif
        }

        /// <summary>
        /// Clears the stored flag so the rule will be re-evaluated on the next
        /// <see cref="EnsureFirewallRule"/> call. Useful after manually deleting the rule.
        /// </summary>
        public static void ResetFlag() => EditorPrefs.DeleteKey(PrefKey);

        /// <summary>
        /// Returns <c>true</c> when the firewall rule has already been successfully added.
        /// </summary>
        public static bool IsConfigured => EditorPrefs.GetBool(PrefKey, false);

#if UNITY_EDITOR_WIN
        private static void AddWindowsFirewallRule(int port)
        {
            try
            {
                string args =
                    $"advfirewall firewall add rule " +
                    $"name=\"{RuleName}\" " +
                    $"dir=in action=allow protocol=TCP localport={port}";

                var psi = new ProcessStartInfo("netsh", args)
                {
                    UseShellExecute  = true,
                    Verb             = "runas",   // triggers one-time UAC elevation
                    WindowStyle      = ProcessWindowStyle.Hidden,
                    CreateNoWindow   = true,
                };

                using var proc = Process.Start(psi);
                proc?.WaitForExit(8000);

                EditorPrefs.SetBool(PrefKey, true);
                UniPeekConstants.Log($"Windows firewall rule '{RuleName}' added for TCP port {port}.");
            }
            catch (Exception ex)
            {
                UniPeekConstants.LogWarning(
                    $"Could not add firewall rule automatically: {ex.Message}. " +
                    "You may need to add it manually:\n" +
                    $"  netsh advfirewall firewall add rule name=\"{RuleName}\" " +
                    $"dir=in action=allow protocol=TCP localport={port}");
            }
        }
#endif
    }
}
