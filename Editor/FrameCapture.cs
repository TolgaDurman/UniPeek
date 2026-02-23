using System;
using System.Collections;
using UnityEditor;
using UnityEngine;

namespace UniPeek
{
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
        private int   _targetWidth;
        private int   _targetHeight;
        private float _interval;    // seconds between captures = 1 / fpsCap

        // ── State ─────────────────────────────────────────────────────────────
        private bool   _active;
        private double _lastCaptureTime;
        private bool   _hooked;
        private bool   _wasPlaying;
        private bool   _captureInFlight;

        // ── Play-mode coroutine helper ─────────────────────────────────────────
        private GameObject    _helperGo;
        private CaptureHelper _helper;

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
            _active          = true;
            _capturedFrames  = 0;
            _droppedFrames   = 0;
            _fpsWindowStart  = EditorApplication.timeSinceStartup;
            _fpsWindowCount  = 0;
            _smoothedFps     = 0f;
            _lastCaptureTime = EditorApplication.timeSinceStartup - _interval;
            _wasPlaying      = Application.isPlaying;
            _captureInFlight = false;

            if (_wasPlaying)
                EnsureHelper();

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
            DestroyHelper();
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

        // ── Editor update ─────────────────────────────────────────────────────

        private void OnEditorUpdate()
        {
            if (!_active) return;

            // When WebRTC is active it drives its own video track — skip JPEG.
            if (_useWebRTC) return;

            bool isPlaying = Application.isPlaying;

            // Detect play/edit mode transitions and reset helper accordingly
            if (isPlaying != _wasPlaying)
            {
                _wasPlaying      = isPlaying;
                _captureInFlight = false;
                if (!isPlaying)
                    DestroyHelper();
            }

            double now = EditorApplication.timeSinceStartup;
            if (now - _lastCaptureTime < _interval) return;

            if (isPlaying)
            {
                // Play mode: request capture via coroutine (end-of-frame timing)
                if (_captureInFlight || _encoder.IsEncoding) { _droppedFrames++; return; }
                EnsureHelper();
                _lastCaptureTime = now;
                _captureInFlight = true;
                _helper.RequestCapture();
            }
            else
            {
                // Edit mode: render camera directly to RenderTexture
                _lastCaptureTime = now;
                CaptureFromCamera();
            }
        }

        // ── Play-mode: coroutine helper lifecycle ─────────────────────────────

        private void EnsureHelper()
        {
            if (_helper != null) return;
            _helperGo = new GameObject("[UniPeek] CaptureHelper")
                { hideFlags = HideFlags.HideAndDontSave };
            _helper = _helperGo.AddComponent<CaptureHelper>();
            _helper.FrameReady += OnPlayModeFrame;
        }

        private void DestroyHelper()
        {
            if (_helperGo == null) return;
            if (_helper != null) _helper.FrameReady -= OnPlayModeFrame;
            UnityEngine.Object.Destroy(_helperGo);
            _helperGo = null;
            _helper   = null;
        }

        private void OnPlayModeFrame(Texture2D captured)
        {
            _captureInFlight = false;
            ProcessFrame(captured);
        }

        // ── Edit-mode: direct camera render ───────────────────────────────────

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
                // linear: false → texture is sRGB, matching the RT above.
                toEncode = new Texture2D(_targetWidth, _targetHeight, TextureFormat.RGB24, false, false);
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

        // ── Shared: scale (if needed) and submit to encoder ───────────────────

        private void ProcessFrame(Texture2D captured)
        {
            if (captured == null) { _droppedFrames++; return; }

            if (_encoder.IsEncoding)
            {
                UnityEngine.Object.DestroyImmediate(captured);
                _droppedFrames++;
                return;
            }

            Texture2D     toEncode = null;
            RenderTexture rt       = null;

            try
            {
                // Always blit through an sRGB RT — even when the size already matches.
                //
                // ScreenCapture.CaptureScreenshotAsTexture() returns sRGB pixel data
                // (the final display-ready image).  In a Linear-colour-space project,
                // EncodeToJPG would otherwise treat those bytes as linear values and
                // apply sRGB gamma encoding a second time, producing the washed-out
                // "double gamma" look on the phone.  Blitting to an sRGB RT and reading
                // back into an sRGB Texture2D guarantees the bytes reach EncodeToJPG
                // without any colour-space conversion regardless of project settings.
                rt = RenderTexture.GetTemporary(
                    _targetWidth, _targetHeight, 0,
                    RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
                Graphics.Blit(captured, rt);

                var prev = RenderTexture.active;
                RenderTexture.active = rt;
                toEncode = new Texture2D(_targetWidth, _targetHeight, TextureFormat.RGB24, false, false);
                toEncode.ReadPixels(new Rect(0, 0, _targetWidth, _targetHeight), 0, 0);
                toEncode.Apply();
                RenderTexture.active = prev;

                UnityEngine.Object.DestroyImmediate(captured);
                captured = null;

                bool accepted = _encoder.SubmitFrame(toEncode);
                if (accepted) { toEncode = null; _capturedFrames++; UpdateFpsStats(); }
                else _droppedFrames++;
            }
            catch (Exception ex)
            {
                UniPeekConstants.LogWarning($"[Capture] Frame processing failed: {ex.Message}");
            }
            finally
            {
                if (rt      != null) RenderTexture.ReleaseTemporary(rt);
                if (captured!= null) UnityEngine.Object.DestroyImmediate(captured);
                if (toEncode!= null) UnityEngine.Object.DestroyImmediate(toEncode);
            }
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

    // ── Play-mode coroutine capture helper ────────────────────────────────────

    /// <summary>
    /// Lightweight MonoBehaviour that runs a <c>WaitForEndOfFrame</c> coroutine
    /// so <c>ScreenCapture.CaptureScreenshotAsTexture</c> is called at the correct
    /// point in the player loop (after all cameras and UI have rendered).
    /// </summary>
    internal sealed class CaptureHelper : MonoBehaviour
    {
        internal event Action<Texture2D> FrameReady;
        private bool _busy;

        internal void RequestCapture()
        {
            if (_busy) return;
            _busy = true;
            StartCoroutine(CaptureCoroutine());
        }

        private IEnumerator CaptureCoroutine()
        {
            yield return new WaitForEndOfFrame();
            Texture2D tex = null;
            try { tex = ScreenCapture.CaptureScreenshotAsTexture(); }
            catch { /* returns null; caller handles gracefully */ }
            _busy = false;
            FrameReady?.Invoke(tex);
        }
    }
}
