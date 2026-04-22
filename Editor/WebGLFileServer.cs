using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading;
using UnityEditor;

namespace UniPeek
{
    [InitializeOnLoad]
    public static class WebGLFileServer
    {
        private static volatile HttpListener _listener;
        private static          Thread       _thread;
        private static volatile string       _rootPath;

        public static string LanUrl    { get; private set; }
        public static int    Port      { get; private set; }
        public static bool   IsRunning => _listener?.IsListening == true;

        static WebGLFileServer()
        {
            EditorApplication.quitting += Stop;
        }

        public static void Start(string rootPath, int preferredPort = 8080)
        {
            if (IsRunning) Stop();

            _rootPath = rootPath;
            Port      = FindAvailablePort(preferredPort);

            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://+:{Port}/");
            _listener.Start();

            string ip = QRCodeGenerator.GetLocalIPv4();
            LanUrl = $"http://{ip}:{Port}";

            _thread = new Thread(ListenerLoop) { IsBackground = true, Name = "UniPeek-WebGL-HTTP" };
            _thread.Start();
        }

        public static void Stop()
        {
            _listener?.Stop();
            _listener?.Close();
            _listener = null;
            _thread?.Join(500);
            _thread  = null;
            LanUrl   = null;
        }

        // ── Internal ──────────────────────────────────────────────────────────

        private static int FindAvailablePort(int preferred)
        {
            for (int p = preferred; p <= preferred + 19; p++)
            {
                try
                {
                    var test = new HttpListener();
                    test.Prefixes.Add($"http://+:{p}/");
                    test.Start();
                    test.Stop();
                    test.Close();
                    return p;
                }
                catch { }
            }
            return preferred;
        }

        private static void ListenerLoop()
        {
            while (_listener?.IsListening == true)
            {
                try
                {
                    var ctx = _listener.GetContext();
                    ThreadPool.QueueUserWorkItem(_ => HandleRequest(ctx));
                }
                catch (HttpListenerException) { break; }
                catch (System.ObjectDisposedException) { break; }
            }
        }

        private static void HandleRequest(HttpListenerContext ctx)
        {
            var req = ctx.Request;
            var res = ctx.Response;

            string root = _rootPath;
            if (root == null)
            {
                res.StatusCode = 503;
                res.Close();
                return;
            }

            try
            {
                string urlPath = req.Url.AbsolutePath.TrimStart('/');
                if (string.IsNullOrEmpty(urlPath)) urlPath = "index.html";

                string filePath = Path.Combine(
                    root,
                    urlPath.Replace('/', Path.DirectorySeparatorChar));

                string actualPath       = filePath;
                string contentEncoding  = null;
                string mimeSourcePath   = filePath;

                if (!File.Exists(filePath))
                {
                    if (File.Exists(filePath + ".br"))
                    {
                        actualPath      = filePath + ".br";
                        contentEncoding = "br";
                    }
                    else if (File.Exists(filePath + ".gz"))
                    {
                        actualPath      = filePath + ".gz";
                        contentEncoding = "gzip";
                    }
                }
                else
                {
                    // File exists with its original name — check if it IS a compressed file.
                    // Unity's default template requests .js.gz / .wasm.gz etc. directly.
                    string ext = Path.GetExtension(filePath).ToLowerInvariant();
                    if (ext == ".gz")  { contentEncoding = "gzip"; mimeSourcePath = filePath[..^3]; }
                    else if (ext == ".br") { contentEncoding = "br";   mimeSourcePath = filePath[..^3]; }
                }

                if (!File.Exists(actualPath))
                {
                    res.StatusCode = 404;
                    return;
                }

                res.ContentType = GetMimeType(mimeSourcePath);
                if (contentEncoding != null)
                    res.Headers.Add("Content-Encoding", contentEncoding);
                res.Headers.Add("Access-Control-Allow-Origin", "*");

                try
                {
                    byte[] data = File.ReadAllBytes(actualPath);
                    res.ContentLength64 = data.Length;
                    res.OutputStream.Write(data, 0, data.Length);
                }
                catch (IOException)
                {
                    res.StatusCode = 500;
                }
            }
            finally
            {
                res.Close();
            }
        }

        private static readonly Dictionary<string, string> MimeTypes = new()
        {
            [".html"] = "text/html",
            [".js"]   = "application/javascript",
            [".wasm"] = "application/wasm",
            [".data"] = "application/octet-stream",
            [".css"]  = "text/css",
            [".ico"]  = "image/x-icon",
            [".png"]  = "image/png",
            [".json"] = "application/json",
            [".txt"]  = "text/plain",
        };

        private static string GetMimeType(string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            return MimeTypes.TryGetValue(ext, out string mime) ? mime : "application/octet-stream";
        }
    }
}
