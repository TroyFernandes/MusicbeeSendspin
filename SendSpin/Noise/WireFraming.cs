using System;
using System.Collections.Generic;
using System.Text;

namespace MusicBeePlugin.SendSpin.Noise
{
    /// <summary>The WebSocket frame kind a wire frame travels in.</summary>
    public enum WireFrameKind
    {
        Text,
        Binary,
    }

    /// <summary>A single WebSocket frame as it appears on the wire.</summary>
    public readonly struct WireFrame
    {
        private readonly string? _text;

        public WireFrameKind Kind { get; }

        /// <summary>The raw frame payload bytes.</summary>
        public ReadOnlyMemory<byte> Payload { get; }

        public WireFrame(WireFrameKind kind, ReadOnlyMemory<byte> payload)
        {
            Kind = kind;
            Payload = payload;
            _text = null;
        }

        private WireFrame(string text)
        {
            Kind = WireFrameKind.Text;
            Payload = Encoding.UTF8.GetBytes(text);
            _text = text;
        }

        public static WireFrame FromText(string text) => new WireFrame(text);
        public static WireFrame FromBinary(ReadOnlyMemory<byte> payload) => new WireFrame(WireFrameKind.Binary, payload);

        /// <summary>The payload decoded as UTF-8 text.</summary>
        public string PayloadAsText() => _text ?? Encoding.UTF8.GetString(Payload.ToArray());
    }

    /// <summary>
    /// Outcome of feeding one received wire frame through <see cref="IWireFraming"/>:
    /// at most one application frame to surface, plus any wire frames the framing layer
    /// needs transmitted back immediately (handshake responses, re-handshake replies).
    /// </summary>
    public readonly struct InboundFrameResult
    {
        /// <summary>Application JSON message to surface, if any.</summary>
        public string? Text { get; init; }

        /// <summary>Application binary message to surface, if any.</summary>
        public ReadOnlyMemory<byte>? Binary { get; init; }

        /// <summary>Wire frames to transmit in response before processing further input, if any.</summary>
        public IReadOnlyList<WireFrame>? Replies { get; init; }

        /// <summary>
        /// When true, the framing holds a reply that must be produced on the connection's
        /// send path via <see cref="IWireFraming.EncodeDeferredReply"/> (re-handshake).
        /// </summary>
        public bool HasDeferredReply { get; init; }

        /// <summary>Set on an unrecoverable protocol/crypto failure; the connection must close.</summary>
        public string? FatalReason { get; init; }

        public static InboundFrameResult ForText(string text) => new InboundFrameResult { Text = text };
        public static InboundFrameResult ForBinary(ReadOnlyMemory<byte> data) => new InboundFrameResult { Binary = data };
        public static InboundFrameResult None => default;
        public static InboundFrameResult ForDeferredReply() => new InboundFrameResult { HasDeferredReply = true };
        public static InboundFrameResult Fatal(string reason) => new InboundFrameResult { FatalReason = reason };
    }

    /// <summary>
    /// Translates between application frames (JSON protocol messages, protocol binary
    /// messages) and the WebSocket wire frames that carry them. Implementations need not be
    /// thread-safe: the connection serializes encode calls under its send lock and processes
    /// inbound frames on a single receive path.
    /// </summary>
    public interface IWireFraming
    {
        /// <summary>Whether application frames may currently flow (false until the handshake completes).</summary>
        bool IsTransportReady { get; }

        /// <summary>Wire frames to transmit immediately after the socket opens.</summary>
        IReadOnlyList<WireFrame> Start();

        /// <summary>Encodes an application JSON message into one or more wire frames.</summary>
        IEnumerable<WireFrame> EncodeText(string json);

        /// <summary>Encodes an application binary message into one or more wire frames.</summary>
        IEnumerable<WireFrame> EncodeBinary(ReadOnlyMemory<byte> data);

        /// <summary>Processes one received wire frame.</summary>
        InboundFrameResult ProcessInbound(WireFrame frame);

        /// <summary>
        /// Encodes a prior deferred reply (signalled via <see cref="InboundFrameResult.HasDeferredReply"/>)
        /// and commits the framing's pending key swap, as one inseparable operation. Must be called on the
        /// connection's send path and the returned frames transmitted within the same send-lock acquisition.
        /// </summary>
        IReadOnlyList<WireFrame> EncodeDeferredReply();

        /// <summary>Resets all per-connection state. Called before each (re)connect.</summary>
        void Reset();
    }
}
