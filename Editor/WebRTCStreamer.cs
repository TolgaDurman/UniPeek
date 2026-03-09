// Entire file is compiled only when com.unity.webrtc is present in the project.
#if UNITY_WEBRTC

using System;
using System.Collections;
using System.Text;
using Unity.EditorCoroutines.Editor;
using Unity.WebRTC;
using UnityEngine;

namespace UniPeek
{
    /// <summary>
    /// Manages a single WebRTC peer connection that streams the Unity Game View
    /// to the Flutter companion app.
    /// <para>
    /// <b>Video:</b> uses <c>Camera.CaptureStreamTrack</c> — requires play mode.
    /// </para>
    /// <para>
    /// <b>Input:</b> received via an <see cref="RTCDataChannel"/> named "input"
    /// and forwarded to <see cref="InputInjector"/> through <see cref="DataChannelMessage"/>.
    /// </para>
    /// <para>
    /// <b>Signaling:</b> caller is responsible for routing offer/answer/ICE over
    /// the existing WebSocket by subscribing to <see cref="OfferReady"/> and
    /// <see cref="IceCandidateReady"/>, then calling <see cref="SetRemoteAnswer"/>
    /// and <see cref="AddIceCandidate"/> when the remote peer's messages arrive.
    /// </para>
    /// </summary>
    internal sealed class WebRTCStreamer : IDisposable
    {
        // ── Events ────────────────────────────────────────────────────────────

        /// <summary>Fired on the WebRTC thread when an SDP offer is ready for the remote peer.</summary>
        public event Action<string> OfferReady;

        /// <summary>Fired on the WebRTC thread when a local ICE candidate is gathered.</summary>
        public event Action<string, string, int> IceCandidateReady; // (candidate, sdpMid, sdpMLineIndex)

        /// <summary>Fired (main-thread safe via Enqueue) when the P2P connection is established.</summary>
        public event Action Connected;

        /// <summary>Fired (main-thread safe via Enqueue) when the P2P connection is lost or fails.</summary>
        public event Action Disconnected;

        /// <summary>Fired when a UTF-8 JSON message arrives on the DataChannel.</summary>
        public event Action<string> DataChannelMessage;

        // ── Configuration ─────────────────────────────────────────────────────
        private readonly int _width;
        private readonly int _height;

        // ── WebRTC objects ────────────────────────────────────────────────────
        private RTCPeerConnection _pc;
        private VideoStreamTrack  _videoTrack;
        private MediaStream       _mediaStream;
        private RTCDataChannel    _dataChannel;

        private bool _disposed;

        // Capture camera — hidden secondary camera used in Edit Mode
        private RenderTexture _renderTexture;
        private Camera        _captureCamera;
        private GameObject    _cameraGo;

        // Play Mode overlay capture (same CaptureHelper as JPEG pipeline)
        private CaptureHelper _captureHelper;

        // ── Constructor ───────────────────────────────────────────────────────

        /// <param name="width">Video width (pixels). Defaults to 1280.</param>
        /// <param name="height">Video height (pixels). Defaults to 720.</param>
        public WebRTCStreamer(int width = 1280, int height = 720)
        {
            _width  = width;
            _height = height;
        }

        // ── Public API ────────────────────────────────────────────────────────

        /// <summary>
        /// Initialises the WebRTC engine, creates the peer connection, adds the
        /// camera video track and data channel, then starts creating an SDP offer.
        /// <b>Must be called from the Unity main thread.</b>
        /// </summary>
        public void StartNegotiation()
        {
            if (_pc != null || _disposed) return;
            var config = new RTCConfiguration
            {
                iceServers = Array.Empty<RTCIceServer>(),
            };

            _pc = new RTCPeerConnection(ref config);
            _pc.OnIceCandidate          = OnIceCandidate;
            _pc.OnIceConnectionChange   = OnIceConnectionChange;
            _pc.OnConnectionStateChange = OnConnectionStateChange;
            _pc.OnDataChannel           = OnRemoteDataChannel;

            // ── Video track via hidden secondary camera ────────────────────────
            // Using Camera.CaptureStreamTrack redirects camera.targetTexture and
            // breaks the Game View. Instead, create a hidden camera that renders
            // into our own RenderTexture without touching Camera.main.
            var mainCam = Camera.main;
            if (mainCam == null && Camera.allCameras.Length > 0)
                mainCam = Camera.allCameras[0];

            _renderTexture = new RenderTexture(_width, _height, 24, RenderTextureFormat.BGRA32, RenderTextureReadWrite.sRGB);
            _renderTexture.Create();
            _videoTrack  = new VideoStreamTrack(_renderTexture);
            _mediaStream = new MediaStream();
            _mediaStream.AddTrack(_videoTrack);
            _pc.AddTrack(_videoTrack, _mediaStream);

            if (mainCam != null)
            {
                _cameraGo = new GameObject("UniPeekWebRTCCapture")
                    { hideFlags = HideFlags.HideAndDontSave };
                _captureCamera = _cameraGo.AddComponent<Camera>();
                _captureCamera.CopyFrom(mainCam);
                _captureCamera.targetTexture = _renderTexture;
                _captureCamera.enabled = false; // driven manually in UpdateLoop
                UniPeekConstants.Log($"[WebRTC] Capture camera created from '{mainCam.name}'.");
            }
            else
            {
                UniPeekConstants.LogWarning("[WebRTC] No camera found — video track will be blank.");
            }

            // ── Data channel (input messages from Flutter) ────────────────────
            var dcInit = new RTCDataChannelInit { ordered = true };
            _dataChannel          = _pc.CreateDataChannel("input", dcInit);
            _dataChannel.OnMessage = bytes =>
                DataChannelMessage?.Invoke(Encoding.UTF8.GetString(bytes));

            // ── Start offer creation and drive WebRTC update loop ─────────────
            EditorCoroutineUtility.StartCoroutineOwnerless(CreateOfferCoroutine());
            EditorCoroutineUtility.StartCoroutineOwnerless(WebRtcUpdateWrapper());
            EditorCoroutineUtility.StartCoroutineOwnerless(CameraLoop());
        }

        /// <summary>
        /// Applies the SDP answer received from the Flutter app.
        /// May be called from any thread.
        /// </summary>
        public void SetRemoteAnswer(string sdp)
        {
            if (_pc == null || _disposed) return;
            EditorCoroutineUtility.StartCoroutineOwnerless(SetRemoteDescriptionCoroutine(sdp));
        }

        /// <summary>
        /// Adds a remote ICE candidate received from the Flutter app via the WebSocket.
        /// May be called from any thread.
        /// </summary>
        public void AddIceCandidate(string candidate, string sdpMid, int sdpMLineIndex)
        {
            if (_pc == null || _disposed) return;
            var c = new RTCIceCandidate(new RTCIceCandidateInit
            {
                candidate     = candidate,
                sdpMid        = sdpMid,
                sdpMLineIndex = sdpMLineIndex,
            });
            _pc.AddIceCandidate(c);
        }

        /// <summary>
        /// No-op in WebRTC 3.x — the engine is driven by the internal UpdateLoop coroutine.
        /// Kept for API compatibility with ConnectionManager.
        /// </summary>
        public void Tick() { }

        /// <inheritdoc/>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _dataChannel?.Dispose();
            _dataChannel = null;

            _videoTrack?.Dispose();
            _videoTrack = null;

            _mediaStream?.Dispose();
            _mediaStream = null;

            _pc?.Dispose();
            _pc = null;

            // Both coroutines (WebRtcUpdateWrapper, CameraLoop) check _disposed
            // (set to true above) and exit naturally on the next tick.
            // Do NOT use StopCoroutine — it sets m_Routine=null which causes a
            // NullReferenceException if MoveNext is still in the current frame's
            // EditorApplication.update snapshot.

            if (_captureHelper != null)
            {
                UnityEngine.Object.DestroyImmediate(_captureHelper.gameObject);
                _captureHelper = null;
            }

            if (_cameraGo != null)
            {
                UnityEngine.Object.DestroyImmediate(_cameraGo);
                _cameraGo      = null;
                _captureCamera = null;
            }

            if (_renderTexture != null)
            {
                _renderTexture.Release();
                UnityEngine.Object.DestroyImmediate(_renderTexture);
                _renderTexture = null;
            }

            UniPeekConstants.Log("[WebRTC] Streamer disposed.");
        }

        // ── Coroutines ────────────────────────────────────────────────────────

        // Drives WebRTC.Update() one step per editor tick. Using a wrapper instead
        // of running WebRTC.Update() directly as an EditorCoroutine lets us exit via
        // the _disposed flag without calling StopCoroutine (which causes a
        // NullReferenceException in EditorCoroutines when stopped mid-frame).
        private IEnumerator WebRtcUpdateWrapper()
        {
            var updater = WebRTC.Update();
            while (!_disposed)
            {
                updater.MoveNext();
                yield return null;
            }
        }

        // In Play Mode: schedules a full-screen capture (including Overlay canvases)
        // via CaptureHelper, then blits the result into _renderTexture each frame.
        // In Edit Mode: renders the secondary camera directly (no overlay UI support).
        private IEnumerator CameraLoop()
        {
            while (!_disposed)
            {
                if (Application.isPlaying)
                {
                    if (_captureHelper == null)
                    {
                        var go = new GameObject("[UniPeek] WebRTCCapture")
                            { hideFlags = HideFlags.HideAndDontSave };
                        _captureHelper           = go.AddComponent<CaptureHelper>();
                        _captureHelper.OnFrame   = OnCaptureFrame;
                    }
                    _captureHelper.RequestCapture();
                }
                else
                {
                    if (_captureHelper != null)
                    {
                        UnityEngine.Object.DestroyImmediate(_captureHelper.gameObject);
                        _captureHelper = null;
                    }

                    // Recreate the secondary camera if it was destroyed (e.g. the
                    // capture camera was created during Play Mode and Unity destroyed
                    // all Play Mode objects when exiting Play Mode).
                    if (_captureCamera == null)
                    {
                        var mainCam = Camera.main;
                        if (mainCam == null && Camera.allCameras.Length > 0)
                            mainCam = Camera.allCameras[0];

                        if (mainCam != null)
                        {
                            if (_cameraGo != null)
                                UnityEngine.Object.DestroyImmediate(_cameraGo);

                            _cameraGo = new GameObject("UniPeekWebRTCCapture")
                                { hideFlags = HideFlags.HideAndDontSave };
                            _captureCamera = _cameraGo.AddComponent<Camera>();
                            _captureCamera.CopyFrom(mainCam);
                            _captureCamera.targetTexture = _renderTexture;
                            _captureCamera.enabled = false;
                            UniPeekConstants.Log("[WebRTC] Capture camera recreated after Play Mode exit.");
                        }
                    }

                    if (_captureCamera != null)
                    {
                        var main = Camera.main;
                        if (main != null)
                            _captureCamera.transform.SetPositionAndRotation(
                                main.transform.position, main.transform.rotation);
                        _captureCamera.Render();
                    }
                }
                yield return null;
            }
        }

        private void OnCaptureFrame(Texture2D tex)
        {
            if (_renderTexture != null)
                Graphics.Blit(tex, _renderTexture);
            UnityEngine.Object.DestroyImmediate(tex);
        }

        private IEnumerator CreateOfferCoroutine()
        {
            var offerOp = _pc.CreateOffer();
            yield return offerOp;

            if (offerOp.IsError)
            {
                UniPeekConstants.LogError($"[WebRTC] CreateOffer error: {offerOp.Error.message}");
                yield break;
            }

            var desc       = offerOp.Desc;
            var setLocalOp = _pc.SetLocalDescription(ref desc);
            yield return setLocalOp;

            if (setLocalOp.IsError)
            {
                UniPeekConstants.LogError($"[WebRTC] SetLocalDescription error: {setLocalOp.Error.message}");
                yield break;
            }

            UniPeekConstants.Log("[WebRTC] Offer created, sending to Flutter.");
            OfferReady?.Invoke(offerOp.Desc.sdp);
        }

        private IEnumerator SetRemoteDescriptionCoroutine(string sdp)
        {
            var desc = new RTCSessionDescription { type = RTCSdpType.Answer, sdp = sdp };
            var op   = _pc.SetRemoteDescription(ref desc);
            yield return op;

            if (op.IsError)
                UniPeekConstants.LogError($"[WebRTC] SetRemoteDescription error: {op.Error.message}");
            else
                UniPeekConstants.Log("[WebRTC] Remote answer accepted.");
        }

        // ── Peer-connection callbacks ─────────────────────────────────────────

        private void OnIceCandidate(RTCIceCandidate candidate)
        {
            if (candidate?.Candidate == null) return;
            IceCandidateReady?.Invoke(
                candidate.Candidate,
                candidate.SdpMid  ?? string.Empty,
                candidate.SdpMLineIndex ?? 0);
        }

        private void OnIceConnectionChange(RTCIceConnectionState state)
        {
            UniPeekConstants.Log($"[WebRTC] ICE state → {state}");
            switch (state)
            {
                case RTCIceConnectionState.Connected:
                case RTCIceConnectionState.Completed:
                    ApplyBitrateSettings();
                    Connected?.Invoke();
                    break;

                case RTCIceConnectionState.Disconnected:
                case RTCIceConnectionState.Failed:
                case RTCIceConnectionState.Closed:
                    Disconnected?.Invoke();
                    break;
            }
        }

        // Raise the video sender's bitrate cap so the stream quality is not
        // limited by WebRTC's conservative default (~600 kbps).
        // 10 Mbps max allows HD streaming over a local Wi-Fi link.
        private void ApplyBitrateSettings()
        {
            if (_pc == null) return;
            foreach (var sender in _pc.GetSenders())
            {
                var parameters = sender.GetParameters();
                if (parameters.encodings == null) continue;
                foreach (var enc in parameters.encodings)
                    enc.maxBitrate = 10_000_000; // 10 Mbps
                sender.SetParameters(parameters);
            }
            UniPeekConstants.Log("[WebRTC] Bitrate cap set to 10 Mbps.");
        }

        private void OnConnectionStateChange(RTCPeerConnectionState state)
            => UniPeekConstants.Log($"[WebRTC] PC state → {state}");

        private void OnRemoteDataChannel(RTCDataChannel channel)
        {
            // Flutter may open a data channel — accept and mirror messages.
            channel.OnMessage = bytes =>
                DataChannelMessage?.Invoke(Encoding.UTF8.GetString(bytes));
        }
    }
}

#endif // UNITY_WEBRTC
