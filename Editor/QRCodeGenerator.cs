using System;
using System.Net;
using System.Net.Sockets;
using UnityEngine;
using QRCoder;

namespace UniPeek
{
    /// <summary>
    /// Generates QR code <see cref="Texture2D"/> images for the UniPeek Editor window.
    /// <para>
    /// The payload encoded in the QR is a JSON string that the companion Flutter app
    /// deserialises to discover the WebSocket host and port:
    /// <code>{"ip":"192.168.1.x","port":7777,"mode":"direct","name":"DESKTOP-ABCD"}</code>
    /// </para>
    /// <para>Requires <c>QRCoder.dll</c> to be present under <c>Assets/Plugins/UniPeek/lib/</c>.</para>
    /// </summary>
    public static class QRCodeGenerator
    {
        // ── Internal state ────────────────────────────────────────────────────
        private static string   _lastIp;
        private static Texture2D _cachedTexture;

        // ── Public API ────────────────────────────────────────────────────────

        /// <summary>
        /// Returns a <see cref="Texture2D"/> QR code encoding the UniPeek connection
        /// payload for the given <paramref name="port"/>.
        /// <para>
        /// The result is cached and regenerated only when the machine's local IP
        /// address changes (e.g. after a network switch).
        /// </para>
        /// </summary>
        /// <param name="port">WebSocket port to embed in the payload (default 7777).</param>
        /// <param name="pixelsPerModule">Size of each QR dot in pixels (default 10).</param>
        /// <returns>A <see cref="Texture2D"/> containing the QR code, or <c>null</c> on error.</returns>
        public static Texture2D GetConnectionQR(int port = UniPeekConstants.DefaultPort, int pixelsPerModule = 10)
        {
            string currentIp = GetLocalIPv4();

            if (_cachedTexture != null && currentIp == _lastIp)
                return _cachedTexture;

            // IP changed (or first call) — regenerate
            _lastIp = currentIp;
            DestroyCachedTexture();

            string payload = BuildPayload(currentIp, port);
            _cachedTexture = GenerateQRTexture(payload, pixelsPerModule);
            return _cachedTexture;
        }

        /// <summary>
        /// Returns the local IPv4 address that will be embedded in the QR payload,
        /// or <c>"127.0.0.1"</c> if no suitable address is found.
        /// </summary>
        public static string GetLocalIPv4()
        {
            try
            {
                string host = Dns.GetHostName();
                foreach (IPAddress addr in Dns.GetHostEntry(host).AddressList)
                {
                    if (addr.AddressFamily == AddressFamily.InterNetwork &&
                        !IPAddress.IsLoopback(addr))
                        return addr.ToString();
                }
            }
            catch (Exception ex)
            {
                UniPeekConstants.LogWarning($"Could not determine local IP: {ex.Message}");
            }
            return "127.0.0.1";
        }

        /// <summary>
        /// Destroys the cached QR <see cref="Texture2D"/> and resets the IP cache,
        /// forcing a fresh generation on the next call to <see cref="GetConnectionQR"/>.
        /// </summary>
        public static void Invalidate()
        {
            _lastIp = null;
            DestroyCachedTexture();
        }

        /// <summary>
        /// Generates a QR code <see cref="Texture2D"/> from an arbitrary string payload.
        /// </summary>
        /// <param name="payload">The text to encode.</param>
        /// <param name="pixelsPerModule">Size of each QR dot in pixels.</param>
        /// <returns>A new <see cref="Texture2D"/>, or <c>null</c> on error.</returns>
        public static Texture2D GenerateQRTexture(string payload, int pixelsPerModule = 10)
        {
            if (string.IsNullOrWhiteSpace(payload))
            {
                UniPeekConstants.LogError("QR payload cannot be null or empty.");
                return null;
            }

            try
            {
                using var generator = new global::QRCoder.QRCodeGenerator();
                using var data      = generator.CreateQrCode(payload, global::QRCoder.QRCodeGenerator.ECCLevel.Q);
                using var code      = new QRCode(data);

                System.Drawing.Bitmap bmp = code.GetGraphic(pixelsPerModule);
                return BitmapToTexture2D(bmp);
            }
            catch (Exception ex)
            {
                UniPeekConstants.LogError($"QR generation failed: {ex.Message}");
                return null;
            }
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        /// <summary>
        /// Builds the Universal Link connection payload.
        /// Format: <c>https://unipeek.app/connect?ip=X&amp;port=Y&amp;name=MACHINE</c>
        /// <para>
        /// When the app is installed, iOS/Android intercept this URL and open UniPeek
        /// directly with the IP and port pre-filled.  When not installed, the browser
        /// opens the download page at <c>unipeek.app/connect</c>.
        /// </para>
        /// </summary>
        private static string BuildPayload(string ip, int port)
        {
            string name = Uri.EscapeDataString(Environment.MachineName);
            return $"https://unipeek.app/connect?ip={ip}&port={port}&name={name}";
        }

        /// <summary>Converts a <see cref="System.Drawing.Bitmap"/> to a Unity <see cref="Texture2D"/>.</summary>
        private static Texture2D BitmapToTexture2D(System.Drawing.Bitmap bmp)
        {
            int w = bmp.Width;
            int h = bmp.Height;

            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false)
            {
                filterMode = FilterMode.Point,  // keep QR dots crisp
                wrapMode   = TextureWrapMode.Clamp,
            };

            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    System.Drawing.Color p = bmp.GetPixel(x, y);
                    tex.SetPixel(x, h - 1 - y, new Color32(p.R, p.G, p.B, p.A));
                }
            }

            tex.Apply();
            return tex;
        }

        private static void DestroyCachedTexture()
        {
            if (_cachedTexture != null)
            {
                UnityEngine.Object.DestroyImmediate(_cachedTexture);
                _cachedTexture = null;
            }
        }
    }
}
