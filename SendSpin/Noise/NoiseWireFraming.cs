using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Noise;
using NoiseProtocol = Noise.Protocol;

namespace MusicBeePlugin.SendSpin.Noise
{
    /// <summary>
    /// Encrypted transport framing that performs the Noise_KKpsk2 handshake as the
    /// CLIENT / Noise RESPONDER (the Sendspin source role: Music Assistant is the
    /// server / Noise initiator), then encrypts/decrypts every subsequent frame.
    ///
    /// Faithful port of the Sendspin .NET SDK's NoiseWireFraming with the net48 adaptations:
    ///   - JSON envelopes are built/parsed with Newtonsoft (no System.Text.Json on net48),
    ///     emitting the same field order the spec's message schemas declare.
    ///   - SHA256 / key wiping use the classic BCL + a local ZeroClear helper.
    ///   - Noise.NET 1.0.0's WriteMessage/ReadMessage return a (int, byte[], Transport) tuple.
    /// Everything else (Noise KKpsk2, prologue binding, fragment reassembly, key rotation)
    /// is unchanged from the SDK.
    /// </summary>
    internal sealed class NoiseWireFraming : IWireFraming
    {
        private readonly NoiseCipherSuite _suite;
        private readonly INoisePskResolver _pskResolver;
        private readonly SendspinIdentity _identity;
        private readonly object _fragmentGate = new object();

        // Cleartext init exchange (bound into the prologue).
        private byte[]? _clientInitBytes;
        private byte[]? _serverInitBytes;
        private string? _serverId;

        // Live Noise state.
        private Transport? _transport;
        private byte[]? _handshakeHash; // prior handshake hash (re-handshake prologue)
        private bool _transportReady;

        // Re-handshake deferred reply (committed on the send path via EncodeDeferredReply).
        private byte[]? _pendingReplyJson;   // the noise/handshake msg2 JSON (base64 data)
        private Transport? _pendingTransport;
        private byte[]? _pendingHash;

        // Fragment reassembly.
        private MemoryStream? _reassemblyBuffer;
        private byte? _reassemblyOriginalType;
        private int _reassemblyTotal;
        private bool _surfacedApplicationMessage;

        public NoiseWireFraming(NoiseCipherSuite suite, INoisePskResolver pskResolver, SendspinIdentity identity)
        {
            _suite = suite;
            _pskResolver = pskResolver ?? throw new ArgumentNullException(nameof(pskResolver));
            _identity = identity ?? throw new ArgumentNullException(nameof(identity));
        }

        public NoiseCipherSuite Suite => _suite;
        public string? ServerId => _serverId;
        public bool IsTransportReady => _transportReady;

        // --- Lifecycle ---

        public IReadOnlyList<WireFrame> Start()
        {
            _suite.EnsureSupported();

            // client/init is cleartext, sent before any Noise state exists. The exact bytes we
            // transmit are bound into the handshake prologue, so capture them as sent.
            string clientInitText =
                "{\"type\":\"client/init\",\"payload\":{" +
                "\"client_id\":\"" + _identity.PeerId + "\"," +
                "\"version\":" + NoiseConstants.ProtocolVersion + "," +
                "\"suite\":\"" + _suite.ToWireName() + "\"}}";
            _clientInitBytes = Encoding.UTF8.GetBytes(clientInitText);

            return new List<WireFrame> { WireFrame.FromText(clientInitText) };
        }

        public IEnumerable<WireFrame> EncodeText(string json)
        {
            EnsureTransportReady();
            return EncryptOutbound(Encoding.UTF8.GetBytes(json).AsMemory());
        }

        public IEnumerable<WireFrame> EncodeBinary(ReadOnlyMemory<byte> data)
        {
            EnsureTransportReady();
            if (data.Length == 0)
                throw new ArgumentException("Empty binary frame", nameof(data));
            return EncryptOutbound(data);
        }

        public InboundFrameResult ProcessInbound(WireFrame frame)
        {
            if (frame.Kind != WireFrameKind.Text)
                return HandleTransportFrame(frame);

            if (_clientInitBytes is null)
                return Fail("Unexpected frame: client/init not sent");
            if (_serverInitBytes is null)
                return HandleServerInit(frame);
            if (_transportReady)
                return HandleCleartextInTransport(frame.PayloadAsText());
            return HandleNoiseMessage1(frame);
        }

        public void Reset()
        {
            _clientInitBytes = null;
            _serverInitBytes = null;
            _serverId = null;
            _transport = null;
            _handshakeHash = null;
            _transportReady = false;
            _pendingReplyJson = null;
            _pendingTransport = null;
            _pendingHash = null;

            lock (_fragmentGate)
            {
                _reassemblyBuffer?.Dispose();
                _reassemblyBuffer = null;
                _reassemblyOriginalType = null;
                _reassemblyTotal = 0;
                _surfacedApplicationMessage = false;
            }
        }

        // --- Handshake (client = Noise responder) ---

        private InboundFrameResult HandleServerInit(WireFrame frame)
        {
            JObject doc;
            try { doc = JObject.Parse(frame.PayloadAsText()); }
            catch (JsonReaderException ex)
            {
                return Fail("Malformed JSON in server/init: " + ex.Message);
            }

            if (doc["type"]?.Value<string>() != "server/init")
                return Fail("Expected server/init before noise/handshake");

            JObject payload;
            try { payload = (JObject)doc["payload"]!; }
            catch (InvalidCastException)
            {
                return Fail("Missing payload in server/init");
            }

            int version = payload["version"]?.Value<int>() ?? 0;
            string? serverId = payload["server_id"]?.Value<string>();
            if (serverId is null)
                return Fail("Missing server_id in server/init");

            if (version != NoiseConstants.ProtocolVersion)
                return Fail($"Unsupported protocol version {version}");

            try { SendspinIdentity.DecodePeerId(serverId); }
            catch (FormatException ex)
            {
                return Fail("server_id is not a valid peer id: " + ex.Message);
            }

            _serverId = serverId;
            _serverInitBytes = Encoding.UTF8.GetBytes(frame.PayloadAsText());
            // No reply: the server now sends noise/handshake msg1.
            return InboundFrameResult.None;
        }

        private InboundFrameResult HandleNoiseMessage1(WireFrame frame)
        {
            JObject doc;
            try { doc = JObject.Parse(frame.PayloadAsText()); }
            catch (JsonReaderException ex)
            {
                return Fail("Malformed JSON in noise/handshake: " + ex.Message);
            }

            if (doc["type"]?.Value<string>() != "noise/handshake")
                return Fail("Expected noise/handshake before transport mode");

            byte[] msg1;
            try { msg1 = Base64UrlText.Decode(doc["payload"]?["data"]?.Value<string>() ?? string.Empty); }
            catch (FormatException ex)
            {
                return Fail("noise/handshake data is not valid base64url: " + ex.Message);
            }

            return RunResponderExchange(msg1, BuildPrologue(), isInitialHandshake: true);
        }

        private byte[] BuildPrologue()
        {
            // Prologue = the exact client/init bytes followed by the exact server/init bytes,
            // as transmitted on the wire (the JSON UTF-8 bodies, no WebSocket framing).
            byte[] prologue = new byte[_clientInitBytes!.Length + _serverInitBytes!.Length];
            _clientInitBytes.CopyTo(prologue, 0);
            _serverInitBytes.CopyTo(prologue, _clientInitBytes.Length);
            return prologue;
        }

        private InboundFrameResult RunResponderExchange(byte[] msg1, byte[] prologue, bool isInitialHandshake)
        {
            byte[] serverPub = SendspinIdentity.DecodePeerId(_serverId!);
            Protocol protocol = NoiseProtocol.Parse(_suite.ToProtocolName().AsSpan());

            // Noise.NET takes ownership of the arrays it is handed, so each Create gets its own
            // copy of the private key, cleared in a finally on every path out.
            byte[] privProbe = _identity.PrivateKey.ToArray();
            byte[] priv = _identity.PrivateKey.ToArray();
            try
            {
                // First pass: read msg1 with a placeholder PSK to learn psk_id from its payload
                // (the PSK is mixed in only at the end of msg2, so msg1 decrypts without it).
                string pskId;
                {
                    using (var probe = protocol.Create(
                               initiator: false, prologue: prologue,
                               s: privProbe, rs: serverPub,
                               psks: new[] { new byte[NoiseConstants.PskSize] }))
                    {
                        byte[] probeBuf = new byte[NoiseProtocol.MaxMessageLength];
                        var (probeLen, _, _) = probe.ReadMessage(msg1, probeBuf);
                        JObject payload;
                        try { payload = JObject.Parse(Encoding.UTF8.GetString(probeBuf, 0, probeLen)); }
                        catch (JsonReaderException ex)
                        {
                            return Fail("Malformed JSON in noise/handshake msg1 payload: " + ex.Message);
                        }

                        pskId = payload["psk_id"]?.Value<string>()
                            ?? throw new FormatException("psk_id missing");
                    }
                }

                // Resolve the PSK; on the initial handshake a lookup miss falls back to the
                // published Sentinel PSK (the pre-pairing path for this source role).
                NoisePsk? resolved = _pskResolver.Resolve(pskId);
                if (resolved is null)
                {
                    if (!isInitialHandshake)
                        return Fail($"no PSK matches psk_id {pskId}");
                    resolved = SentinelPskResolver.Instance.Resolve(NoiseConstants.SentinelPskId)!;
                }

                // A misbinding, not a miss: the psk_id matched a record bound to another server.
                if (resolved.ServerId is not null && resolved.ServerId != _serverId)
                    return Fail("PSK is bound to a different server_id");

                byte[] pskCopy = resolved.Key.ToArray();
                try
                {
                    // Real exchange: read msg1 (advance state), then write msg2 under the resolved PSK.
                    using (var state = protocol.Create(
                               initiator: false, prologue: prologue,
                               s: priv, rs: serverPub,
                               psks: new[] { pskCopy }))
                    {
                        byte[] buf = new byte[NoiseProtocol.MaxMessageLength];
                        state.ReadMessage(msg1, buf);
                        var (msg2Len, handshakeHash, transport) =
                            state.WriteMessage(Encoding.UTF8.GetBytes("{}"), buf);
                        if (transport is null)
                            return Fail("handshake did not complete after message 2");

                        string replyJson =
                            "{\"type\":\"noise/handshake\",\"payload\":{" +
                            "\"data\":\"" + Base64UrlText.Encode(buf.AsSpan(0, msg2Len)) + "\"}}";

                        if (isInitialHandshake)
                        {
                            // Initial handshake: install the keys immediately and reply as a
                            // cleartext text frame (no application traffic is in flight yet).
                            _transport = transport;
                            _handshakeHash = handshakeHash;
                            _transportReady = true;
                            return new InboundFrameResult
                            {
                                Replies = new List<WireFrame> { WireFrame.FromText(replyJson) },
                            };
                        }

                        // Re-handshake: the reply must travel under the OLD keys, so hold it and
                        // let EncodeDeferredReply (on the send path) commit the swap.
                        _pendingReplyJson = Encoding.UTF8.GetBytes(replyJson);
                        _pendingTransport = transport;
                        _pendingHash = handshakeHash;
                        return InboundFrameResult.ForDeferredReply();
                    }
                }
                finally
                {
                    ZeroClear(pskCopy);
                }
            }
            finally
            {
                ZeroClear(privProbe);
                ZeroClear(priv);
            }
        }

        public IReadOnlyList<WireFrame> EncodeDeferredReply()
        {
            if (_pendingReplyJson is null)
                throw new InvalidOperationException("no deferred reply is pending");

            // Materialize the reply (a JSON body under the OLD transport) BEFORE committing the
            // swap, so it is encrypted with the current keys.
            byte[] replyMsg = new byte[1 + _pendingReplyJson.Length];
            replyMsg[0] = NoiseConstants.MessageTypeJsonBody;
            _pendingReplyJson.CopyTo(replyMsg.AsMemory(1));
            var frames = new List<WireFrame>();
            foreach (WireFrame f in EncryptOutbound(replyMsg.AsMemory()))
                frames.Add(f);

            // Commit the key swap.
            if (_pendingTransport is not null)
            {
                _transport = _pendingTransport;
                _handshakeHash = _pendingHash;
            }
            _pendingReplyJson = null;
            _pendingTransport = null;
            _pendingHash = null;

            return frames;
        }

        // --- Transport mode ---

        // A cleartext text frame received after the initial handshake is only valid if it is the
        // re-handshake's noise/handshake msg1 -- but the spec carries re-handshake messages as
        // encrypted BINARY frames, so a cleartext text frame here is a protocol violation.
        private InboundFrameResult HandleCleartextInTransport(string text)
        {
            return Fail("cleartext frame received in transport mode");
        }

        private InboundFrameResult HandleTransportFrame(WireFrame frame)
        {
            if (frame.Kind != WireFrameKind.Binary)
                return Fail("text frame received in transport mode");

            if (_transport is null)
                return Fail("transport frame before handshake complete");

            byte[] plainBuf = new byte[frame.Payload.Length];
            int plainLen;
            try
            {
                plainLen = _transport.ReadMessage(frame.Payload.Span, plainBuf);
            }
            catch (Exception ex)
            {
                return Fail("Transport decryption failed: " + ex.Message);
            }

            if (plainLen == 0)
                return Fail("empty transport message");

            return DispatchMessage(plainBuf.AsMemory(0, plainLen));
        }

        private InboundFrameResult DispatchMessage(ReadOnlyMemory<byte> payload)
        {
            if (payload.Length == 0)
                return Fail("empty dispatch payload");

            byte msgType = payload.Span[0];
            var body = payload.Slice(1);

            switch (msgType)
            {
                case NoiseConstants.MessageTypeJsonBody:
                    string json = Encoding.UTF8.GetString(body.ToArray());
                    try
                    {
                        JObject doc = JObject.Parse(json);
                        if (doc["type"]?.Value<string>() == "noise/handshake")
                            return HandleRehandshakeMessage(doc);
                    }
                    catch (JsonReaderException)
                    {
                        // Not JSON after all; surface as-is.
                    }
                    _surfacedApplicationMessage = true;
                    return InboundFrameResult.ForText(json);

                case NoiseConstants.MessageTypeFragmentMore:
                case NoiseConstants.MessageTypeFragmentEnd:
                    return HandleFragment(payload, msgType == NoiseConstants.MessageTypeFragmentEnd);

                default:
                    if (_reassemblyBuffer is not null)
                        return Fail("non-fragment frame received while a fragmented message is in flight");
                    // Protocol binary message type (e.g. audio): reframe as a full binary frame.
                    _surfacedApplicationMessage = true;
                    var full = new byte[payload.Length];
                    full[0] = msgType;
                    body.CopyTo(full.AsMemory(1));
                    return InboundFrameResult.ForBinary(full);
            }
        }

        // Re-handshake msg1: carried as an encrypted frame that decrypts to this JSON. Runs the
        // responder exchange with isInitialHandshake=false, deferring the reply + key swap.
        private InboundFrameResult HandleRehandshakeMessage(JObject doc)
        {
            if (_pendingReplyJson is not null)
                return Fail("a re-handshake is already in progress");

            byte[] msg1;
            try { msg1 = Base64UrlText.Decode(doc["payload"]?["data"]?.Value<string>() ?? string.Empty); }
            catch (FormatException ex)
            {
                return Fail("re-handshake data is not valid base64url: " + ex.Message);
            }

            // The re-handshake's prologue is the prior handshake's hash.
            byte[] prologue = _handshakeHash ?? throw new InvalidOperationException("re-handshake before initial handshake");
            return RunResponderExchange(msg1, prologue, isInitialHandshake: false);
        }

        private InboundFrameResult HandleFragment(ReadOnlyMemory<byte> plaintext, bool last)
        {
            // plaintext includes the leading fragment-type byte (2 or 3); the opening fragment
            // carries orig_type at plaintext[1]. Mirrors the SDK reference layout.
            lock (_fragmentGate)
            {
                ReadOnlyMemory<byte> data;
                if (_reassemblyBuffer is null)
                {
                    if (plaintext.Length < 2)
                        return Fail("opening fragment missing orig_type");
                    byte origType = plaintext.Span[1];
                    if (origType is NoiseConstants.MessageTypeFragmentMore or NoiseConstants.MessageTypeFragmentEnd)
                        return Fail("orig_type of 2 or 3");
                    _reassemblyBuffer = new MemoryStream();
                    _reassemblyOriginalType = origType;
                    data = plaintext.Slice(2);
                }
                else
                {
                    data = plaintext.Slice(1);
                }

                int maxReassembled = _surfacedApplicationMessage
                    ? NoiseConstants.MaxReassembledMessageBytes
                    : NoiseConstants.MaxReassembledMessageBytesBeforeFirstMessage;
                if (_reassemblyTotal + data.Length > maxReassembled)
                    return Fail("reassembled message exceeds size bound");
                _reassemblyTotal += data.Length;
                byte[] dataArr = data.ToArray();
                _reassemblyBuffer.Write(dataArr, 0, dataArr.Length);

                if (!last)
                    return InboundFrameResult.None;

                byte fullType = _reassemblyOriginalType!.Value;
                byte[] reassembled = _reassemblyBuffer.ToArray();
                _reassemblyBuffer.Dispose();
                _reassemblyBuffer = null;
                _reassemblyOriginalType = null;
                _reassemblyTotal = 0;

                // Re-dispatch the reassembled message: JSON re-checks for a re-handshake; binary reframes.
                var full = new byte[1 + reassembled.Length];
                full[0] = fullType;
                reassembled.CopyTo(full.AsMemory(1));
                return DispatchMessage(full.AsMemory());
            }
        }

        // --- Outbound encryption + fragmentation ---

        private List<WireFrame> EncryptOutbound(ReadOnlyMemory<byte> plaintext)
        {
            if (plaintext.Length <= NoiseConstants.MaxTransportPlaintext)
                return new List<WireFrame> { EncryptFrame(plaintext.Span) };

            byte origType = plaintext.Span[0];
            var remaining = plaintext.Slice(1);
            bool first = true;
            var frames = new List<WireFrame>();
            while (remaining.Length > 0)
            {
                int headerLen = first ? 2 : 1;
                int chunkLen = Math.Min(remaining.Length, NoiseConstants.MaxTransportPlaintext - headerLen);
                bool isLast = chunkLen >= remaining.Length;

                byte[] fragment = new byte[headerLen + chunkLen];
                fragment[0] = isLast ? NoiseConstants.MessageTypeFragmentEnd : NoiseConstants.MessageTypeFragmentMore;
                if (first) fragment[1] = origType;
                remaining.Slice(0, chunkLen).CopyTo(fragment.AsMemory(headerLen));

                frames.Add(EncryptFrame(fragment.AsSpan()));
                remaining = remaining.Slice(chunkLen);
                first = false;
            }
            return frames;
        }

        private WireFrame EncryptFrame(ReadOnlySpan<byte> plaintext)
        {
            if (_transport is null)
                throw new InvalidOperationException("Transport not ready");

            byte[] ciphertext = new byte[plaintext.Length + 16];
            int written = _transport.WriteMessage(plaintext, ciphertext);
            return new WireFrame(WireFrameKind.Binary, ciphertext.AsMemory(0, written));
        }

        private void EnsureTransportReady()
        {
            if (!_transportReady)
                throw new InvalidOperationException("Cannot encode application frames before the Noise handshake completes");
        }

        private static void ZeroClear(byte[]? a)
        {
            if (a == null) return;
            for (int i = 0; i < a.Length; i++) a[i] = 0;
        }

        private InboundFrameResult Fail(string reason) => InboundFrameResult.Fatal(reason);
    }
}
