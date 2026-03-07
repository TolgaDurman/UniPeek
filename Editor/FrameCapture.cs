using System;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace UniPeek
{
    /// <summary>Strategy used to capture each frame.</summary>
    public enum CaptureMethod
    {
        /// <summary>
        /// <c>Camera.Render()</c> to a <see cref="RenderTexture"/> then <c>ReadPixels</c>.
        /// Works in both Play and Edit Mode. No Game View dependency.
        /// </summary>
        CameraRender,

        /// <summary>
        /// <c>Camera.Render()</c> to a <see cref="RenderTexture"/> then
        /// <c>AsyncGPUReadback.Request()</c>. Non-blocking — no CPU stall — at the cost of
        /// approximately one frame of additional latency. No Game View dependency.
        /// </summary>
        AsyncGPUReadback,
    }

    /// <summary>
    /// Captures the composited Game View on the Unity main thread and forwards
    /// each frame to a <see cref="FrameEncoder"/> for off-thread JPEG encoding.
    /// <para>
    /// <b>Capture strategy:</b>
    /// <list type="bullet">
    ///   <item>In <b>Play mode</b>: a hidden <see cref="CaptureHelper"/> MonoBehaviour
    ///         runs a <c>WaitForEndOfFrame</c> coroutine then calls
    ///         <c>ScreenCapture.CaptureScreenshotAsTexture()</c>, capturing the full
    ///         Game View including UI, post-processing, and overlays.</item>
    ///   <item>In <b>Edit mode</b>: renders the main camera directly to a
    ///         <see cref="RenderTexture"/> (Screen-space UI overlays are not captured).</item>
    ///   <item>Frames are scaled to the target resolution via a GPU blit if the
    ///         captured size differs from <see cref="SetResolution"/>.</item>
    ///   <item>If the encoder is busy the frame is dropped immediately to avoid
    ///         main-thread stalls.</item>
    /// </list>
    /// </para>
    /// </summary>
    public sealed class FrameCapture
    {
        // ── Configuration ─────────────────────────────────────────────────────
        private int           _targetWidth;
        private int           _targetHeight;
        private float         _interval;    // seconds between captures = 1 / fpsCap
        private CaptureMethod _method = CaptureMethod.CameraRender;

        // ── State ─────────────────────────────────────────────────────────────
        private bool   _active;
        private double _lastCaptureTime;
        private bool   _hooked;
        private bool   _asyncRequestInFlight;

        // ── Dependencies ──────────────────────────────────────────────────────
        private readonly FrameEncoder _encoder;

        // ── WebRTC bypass ─────────────────────────────────────────────────────
        private bool _useWebRTC;

        /// <summary>
        /// When <c>true</c>, the JPEG capture pipeline is suspended.
        /// Set this while WebRTC is active; the WebRTC video track handles
        /// capture independently via <c>Camera.CaptureStreamTrack</c>.
        /// </summary>
        public bool UseWebRTC
        {
            get => _useWebRTC;
            set => _useWebRTC = value;
        }

        // ── Stats ─────────────────────────────────────────────────────────────
        private int    _capturedFrames;
        private int    _droppedFrames;
        private double _fpsWindowStart;
        private int    _fpsWindowCount;
        private float  _smoothedFps;

        /// <summary>Smoothed capture rate displayed in the Editor window (frames/second).</summary>
        public float SmoothedFps => _smoothedFps;

        /// <summary>Total frames captured since streaming began.</summary>
        public int CapturedFrames => _capturedFrames;

        /// <summary>Total frames dropped (encoder busy or no client) since streaming began.</summary>
        public int DroppedFrames => _droppedFrames;

        // ── Constructor ───────────────────────────────────────────────────────

        /// <summary>Creates the capture component.</summary>
        /// <param name="encoder">Encoder to pass captured frames to.</param>
        /// <param name="targetWidth">Streaming width in pixels.</param>
        /// <param name="targetHeight">Streaming height in pixels.</param>
        /// <param name="fpsCap">Maximum capture rate (frames per second).</param>
        public FrameCapture(FrameEncoder encoder, int targetWidth, int targetHeight, int fpsCap)
        {
            _encoder      = encoder;
            _targetWidth  = targetWidth;
            _targetHeight = targetHeight;
            _interval     = fpsCap > 0 ? 1f / fpsCap : 1f / 30f;
        }

        // ── Public API ────────────────────────────────────────────────────────

        /// <summary>Activates frame capture. Hooks into <see cref="EditorApplication.update"/>.</summary>
        public void Start()
        {
            if (_active) return;
            _active               = true;
            _capturedFrames       = 0;
            _droppedFrames        = 0;
            _fpsWindowStart       = EditorApplication.timeSinceStartup;
            _fpsWindowCount       = 0;
            _smoothedFps          = 0f;
            _lastCaptureTime      = EditorApplication.timeSinceStartup - _interval;
            _asyncRequestInFlight = false;

            if (!_hooked)
            {
                EditorApplication.update += OnEditorUpdate;
                _hooked = true;
            }
        }

        /// <summary>Deactivates frame capture. Unhooks from <see cref="EditorApplication.update"/>.</summary>
        public void Stop()
        {
            _active = false;
            if (_hooked)
            {
                EditorApplication.update -= OnEditorUpdate;
                _hooked = false;
            }
        }

        /// <summary>Updates the streaming resolution. Takes effect on the next capture.</summary>
        public void SetResolution(int width, int height)
        {
            _targetWidth  = width;
            _targetHeight = height;
        }

        /// <summary>Updates the FPS cap. Takes effect on the next capture.</summary>
        public void SetFpsCap(int fpsCap)
            => _interval = fpsCap > 0 ? 1f / fpsCap : 1f / 30f;

        /// <summary>Switches the capture strategy. Takes effect on the next capture.</summary>
        public void SetCaptureMethod(CaptureMethod method)
        {
            _method = method;
            if (method != CaptureMethod.AsyncGPUReadback)
                _asyncRequestInFlight = false;
        }

        // ── Editor update ─────────────────────────────────────────────────────

        private void OnEditorUpdate()
        {
            if (!_active) return;

            // When WebRTC is active it drives its own video track — skip JPEG.
            if (_useWebRTC) return;

            double now = EditorApplication.timeSinceStartup;
            if (now - _lastCaptureTime < _interval) return;

            _lastCaptureTime = now;
            if (_method == CaptureMethod.AsyncGPUReadback)
                CaptureFromCameraAsync();
            else
                CaptureFromCamera();
        }

        // ── Camera render ─────────────────────────────────────────────────────

        private void CaptureFromCamera()
        {
            if (_encoder.IsEncoding) { _droppedFrames++; return; }

            // Prefer Camera.main; fall back to any enabled camera
            var cam = Camera.main;
            if (cam == null)
            {
                var all = Camera.allCameras;
                cam = all.Length > 0 ? all[0] : null;
            }

            if (cam == null) { _droppedFrames++; return; }

            RenderTexture rt        = null;
            RenderTexture prevActive = RenderTexture.active;
            var           prevTarget = cam.targetTexture;
            Texture2D     toEncode  = null;

            try
            {
                // sRGB read-write: prevents Graphics.Blit from linearising the
                // captured pixels in Linear-colour-space projects, which would
                // make colours appear washed-out on the phone.
                rt = RenderTexture.GetTemporary(
                    _targetWidth, _targetHeight, 24,
                    RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);

                cam.targetTexture = rt;
                cam.Render();
                cam.targetTexture = prevTarget;

                RenderTexture.active = rt;
                // In a Linear project ReadPixels converts the sRGB RT data back to linear, so
                // mark the texture as linear=true so EncodeToJPG applies the sRGB gamma curve once.
                toEncode = new Texture2D(_targetWidth, _targetHeight, TextureFormat.RGB24, false,
                    PlayerSettings.colorSpace == ColorSpace.Linear);
                toEncode.ReadPixels(new Rect(0, 0, _targetWidth, _targetHeight), 0, 0);
                toEncode.Apply();
                RenderTexture.active = prevActive;

                bool accepted = _encoder.SubmitFrame(toEncode);
                if (accepted) { toEncode = null; _capturedFrames++; UpdateFpsStats(); }
                else _droppedFrames++;
            }
            catch (Exception ex)
            {
                cam.targetTexture    = prevTarget;
                RenderTexture.active = prevActive;
                UniPeekConstants.LogWarning($"[Capture] Camera render failed: {ex.Message}");
            }
            finally
            {
                if (rt      != null) RenderTexture.ReleaseTemporary(rt);
                if (toEncode!= null) UnityEngine.Object.DestroyImmediate(toEncode);
            }
        }

        // ── Camera → AsyncGPUReadback (non-blocking) ──────────────────────────

        private void CaptureFromCameraAsync()
        {
            if (_asyncRequestInFlight || _encoder.IsEncoding) { _droppedFrames++; return; }

            var cam = Camera.main;
            if (cam == null)
            {
                var all = Camera.allCameras;
                cam = all.Length > 0 ? all[0] : null;
            }
            if (cam == null) { _droppedFrames++; return; }

            RenderTexture rt        = null;
            var           prevTarget = cam.targetTexture;

            try
            {
                rt = RenderTexture.GetTemporary(
                    _targetWidth, _targetHeight, 24,
                    RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);

                cam.targetTexture = rt;
                cam.Render();
                cam.targetTexture = prevTarget;
            }
            catch (Exception ex)
            {
                cam.targetTexture = prevTarget;
                if (rt != null) RenderTexture.ReleaseTemporary(rt);
                UniPeekConstants.LogWarning($"[Capture] AsyncGPU camera render failed: {ex.Message}");
                return;
            }

            bool linearProject = PlayerSettings.colorSpace == ColorSpace.Linear;
            _asyncRequestInFlight = true;

            // Request non-blocking GPU→CPU readback. Callback fires on main thread.
            AsyncGPUReadback.Request(rt, 0, TextureFormat.RGB24, req =>
            {
                RenderTexture.ReleaseTemporary(rt);
                _asyncRequestInFlight = false;

                if (req.hasError) { _droppedFrames++; return; }
                if (_encoder.IsEncoding) { _droppedFrames++; return; }

                Texture2D tex = null;
                try
                {
                    tex = new Texture2D(_targetWidth, _targetHeight, TextureFormat.RGB24, false, linearProject);
                    tex.LoadRawTextureData(req.GetData<byte>());
                    tex.Apply();

                    bool accepted = _encoder.SubmitFrame(tex);
                    if (accepted) { tex = null; _capturedFrames++; UpdateFpsStats(); }
                    else _droppedFrames++;
                }
                catch (Exception ex)
                {
                    UniPeekConstants.LogWarning($"[Capture] AsyncGPU readback processing failed: {ex.Message}");
                }
                finally
                {
                    if (tex != null) UnityEngine.Object.DestroyImmediate(tex);
                }
            });
        }

        // ── Stats ─────────────────────────────────────────────────────────────

        private void UpdateFpsStats()
        {
            _fpsWindowCount++;
            double elapsed = EditorApplication.timeSinceStartup - _fpsWindowStart;
            if (elapsed >= 1.0)
            {
                _smoothedFps    = (float)(_fpsWindowCount / elapsed);
                _fpsWindowCount = 0;
                _fpsWindowStart = EditorApplication.timeSinceStartup;
            }
        }
    }
}
