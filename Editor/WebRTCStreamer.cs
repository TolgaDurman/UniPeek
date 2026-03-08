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
        private EditorCoroutine _updateCoroutine;

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
                iceServers = new[]
                {
                    new RTCIceServer { urls = new[] { "stun:stun.l.google.com:19302" } },
                },
            };

            _pc = new RTCPeerConnection(ref config);
            _pc.OnIceCandidate          = OnIceCandidate;
            _pc.OnIceConnectionChange   = OnIceConnectionChange;
            _pc.OnConnectionStateChange = OnConnectionStateChange;
            _pc.OnDataChannel           = OnRemoteDataChannel;

            // ── Video track ───────────────────────────────────────────────────
            var cam = Camera.main;
            if (cam == null && Camera.allCameras.Length > 0)
                cam = Camera.allCameras[0];

            if (cam != null)
            {
                _videoTrack  = cam.CaptureStreamTrack(_width, _height);
                _mediaStream = new MediaStream();
                _mediaStream.AddTrack(_videoTrack);
                _pc.AddTrack(_videoTrack, _mediaStream);
                UniPeekConstants.Log($"[WebRTC] Video track attached from camera '{cam.name}'.");
            }
            else
            {
                UniPeekConstants.LogWarning("[WebRTC] No camera found — streaming without video track.");
            }

            // ── Data channel (input messages from Flutter) ────────────────────
            var dcInit = new RTCDataChannelInit { ordered = true };
            _dataChannel          = _pc.CreateDataChannel("input", dcInit);
            _dataChannel.OnMessage = bytes =>
                DataChannelMessage?.Invoke(Encoding.UTF8.GetString(bytes));

            // ── Start offer creation and drive WebRTC update loop ─────────────
            EditorCoroutineUtility.StartCoroutineOwnerless(CreateOfferCoroutine());
            _updateCoroutine = EditorCoroutineUtility.StartCoroutineOwnerless(UpdateLoop());
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

            if (_updateCoroutine != null)
            {
                EditorCoroutineUtility.StopCoroutine(_updateCoroutine);
                _updateCoroutine = null;
            }

            UniPeekConstants.Log("[WebRTC] Streamer disposed.");
        }

        // ── Coroutines ────────────────────────────────────────────────────────

        private IEnumerator UpdateLoop()
        {
            while (!_disposed)
                yield return WebRTC.Update();
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
                    Connected?.Invoke();
                    break;

                case RTCIceConnectionState.Disconnected:
                case RTCIceConnectionState.Failed:
                case RTCIceConnectionState.Closed:
                    Disconnected?.Invoke();
                    break;
            }
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
