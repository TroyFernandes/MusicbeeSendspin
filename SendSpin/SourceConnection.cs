using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using MusicBeePlugin.SendSpin.Noise;

namespace MusicBeePlugin.SendSpin
{
    /// <summary>Lifecycle state of a <see cref="SourceConnection"/>.</summary>
    public enum SourceConnectionState
    {
        /// <summary>Not started, or stopped by the owner.</summary>
        Idle,
        /// <summary>Dialing the server.</summary>
        Connecting,
        /// <summary>Noise handshake in progress (client/init sent, awaiting msg1/msg2 completion).</summary>
        Handshaking,
        /// <summary>Encrypted session up, but the server has not granted <c>source@v1</c> (unpaired / waiting for pairing).</summary>
        Unpaired,
        /// <summary>Pairing exchange in progress (pairing activation received, finalizing).</summary>
        Pairing,
        /// <summary><c>source@v1</c> granted; clock synchronized and reported available. Accepts server start commands.</summary>
        Ready,
    }

    /// <summary>Args for the <see cref="SourceConnection.SourceRoleChanged"/> event.</summary>
    public sealed class SourceRoleEventArgs : EventArgs
    {
        /// <summary>True when <c>source@v1</c> was granted, false when revoked.</summary>
        public bool Granted { get; }
        public SourceRoleEventArgs(bool granted) { Granted = granted; }
    }

    /// <summary>
    /// Sendspin <b>source@v1</b> client connection: the plugin dials a Music Assistant
    /// <b>server</b> over WebSocket and speaks the encrypted Sendspin protocol as the Noise
    /// <b>responder</b> (the inverse of <see cref="SpeakerConnection"/>, which acts as a Sendspin
    /// server for speakers).
    /// </summary>
    /// <remarks>
    /// Wire drive (proven against a live MA server by tests/live-handshake-probe):
    ///   ws connect → <c>client/init</c> (cleartext) → receive <c>server/init</c> + <c>noise/handshake</c>
    ///   msg1 (cleartext) → send msg2 (cleartext) → transport ready → receive <c>server/hello</c> →
    ///   send <c>client/hello</c> → receive <c>server/activate</c>.
    ///
    /// The PSK resolution rides on the <see cref="PairingStore"/>: a stored long-term PSK pairs us
    /// straight back to a server we have already paired with; otherwise the handshake lands on the
    /// Sentinel PSK (fallback lives inside <see cref="NoiseWireFraming"/>) and the connection can
    /// only proceed through the Pairing PSK pairing flow.
    ///
    /// Threading: ONE receive loop (all inbound), ONE writer task consuming a send queue (all
    /// outbound — <c>ClientWebSocket</c> is not thread-safe for concurrent writes). Handshake
    /// replies are produced on the receive thread and enqueued in order, so ordering holds.
    /// <see cref="IWireFraming.EncodeDeferredReply"/> (re-handshake reply + key swap) runs inside
    /// the writer's send step, per the framing contract.
    /// </remarks>
    internal sealed class SourceConnection : IDisposable
    {
        private readonly Uri _serverUri;
        private readonly SendspinIdentity _identity;
        private readonly PairingStore _pairingStore;
        private readonly NoiseCipherSuite _suite;
        private readonly Action<string> _log;
        private readonly string _clientName;
        private readonly string _softwareVersion;

        private readonly CancellationTokenSource _cts = new();
        private readonly object _stateLock = new();
        // Small lock guarding the clock-burst timer state (burst timer thread vs receive thread).
        private readonly object _clockLock = new();

        private NoiseWireFraming? _framing;
        private ClientWebSocket? _ws;
        private Task? _receiveTask;
        private Task? _writerTask;
        private Task? _runTask;
        // Per-connection send queue (a fresh one each ConnectOnceAsync): the receive loop's
        // finally completes it, so reusing one instance across reconnects would dead-lock.
        private volatile BlockingCollection<OutItem>? _sendQueue;

        private volatile SourceConnectionState _state = SourceConnectionState.Idle;
        private bool _running;
        private bool _disposed;

        // Session state (per-connection; reset on every connect).
        private string? _serverName;
        private string? _serverId;
        private bool _sourceGranted;
        private byte[]? _pendingLongTermPsk;   // sent in client/pair-finalize; persisted on server/pair-finalize
        private bool _activateReceived;
        private string[] _activeRoles = Array.Empty<string>();

        // Clock sync: server time domain ≈ client (ServerClock) domain + offset. Best (min-RTT)
        // sample of the latest burst; 0 offset before any sample.
        private long _clockOffsetUs;
        private bool _clockConverged;
        private int _burstRemaining;
        private Timer? _burstTimer;
        private Timer? _resyncTimer;

        /// <summary>Raised on every state change (any thread).</summary>
        public event EventHandler<SourceConnectionState>? StateChanged;

        /// <summary>Raised when the server grants (true) or revokes (false) the source@v1 role.</summary>
        public event EventHandler<SourceRoleEventArgs>? SourceRoleChanged;

        /// <summary>Raised when a pairing completes and the long-term record is persisted.</summary>
        public event EventHandler<string>? PairingCompleted;

        public SourceConnection(
            Uri serverUri,
            string clientName,
            string softwareVersion,
            SendspinIdentity identity,
            PairingStore pairingStore,
            NoiseCipherSuite suite,
            Action<string>? logger)
        {
            _serverUri = serverUri;
            _clientName = string.IsNullOrWhiteSpace(clientName) ? "MusicBee (SendSpin)" : clientName;
            _softwareVersion = string.IsNullOrWhiteSpace(softwareVersion) ? "1.0" : softwareVersion;
            _identity = identity ?? throw new ArgumentNullException(nameof(identity));
            _pairingStore = pairingStore ?? throw new ArgumentNullException(nameof(pairingStore));
            _suite = suite;
            _log = logger ?? (_ => { });
        }

        public Uri ServerUri => _serverUri;
        public SourceConnectionState State => _state;
        /// <summary>The server's friendly name from the last server/hello (null before hello).</summary>
        public string? ServerName => _serverName;
        /// <summary>base64url server static key of the current session (null before server/init).</summary>
        public string? ServerId => _serverId;
        /// <summary>Whether source@v1 is currently granted on the session.</summary>
        public bool IsSourceGranted => _sourceGranted;
        /// <summary>Server − client clock offset (µs) from the best min-RTT sample.</summary>
        public long ClockOffsetUs => _clockOffsetUs;
        /// <summary>Whether at least one valid time sample has converged.</summary>
        public bool IsClockConverged => _clockConverged;

        /// <summary>The pairing token for this client ("SP:…"), for the operator to paste into the Music Assistant server.</summary>
        public string PairingToken => _pairingStore.GetPairingToken(_identity);

        // ------------------------------------------------------------------
        // Lifecycle
        // ------------------------------------------------------------------

        /// <summary>Starts the persistent connect loop (connects, reconnects with backoff until <see cref="Stop"/>).</summary>
        public void Start()
        {
            lock (_stateLock)
            {
                if (_running)
                    return;
                _running = true;
            }
            SetState(SourceConnectionState.Connecting);
            _runTask = Task.Run(() => RunForeverAsync(_cts.Token));
        }

        /// <summary>Stops the connection: says goodbye, closes the socket, ends the reconnect loop.</summary>
        public void Stop()
        {
            lock (_stateLock)
            {
                if (!_running)
                    return;
                _running = false;
            }

            // Best-effort goodbye + close; the run loop observes cancellation either way.
            try
            {
                var ws = _ws;
                if (ws is { State: WebSocketState.Open })
                {
                    EnqueueJson(BuildGoodbye("shutdown"));
                    Enqueue(OutItem.Close());
                }
            }
            catch (ObjectDisposedException) { /* racing close */ }

            _cts.Cancel();
            SetState(SourceConnectionState.Idle);
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            Stop();
            try { _cts.Dispose(); } catch { }
            _burstTimer?.Dispose();
            _resyncTimer?.Dispose();
            _sendQueue?.CompleteAdding();
        }

        private void SetState(SourceConnectionState s)
        {
            SourceConnectionState old;
            lock (_stateLock)
            {
                if (_state == s)
                    return;
                old = _state;
                _state = s;
            }
            if (old != s)
            {
                _log("[Source] state: " + old + " → " + s);
                StateChanged?.Invoke(this, s);
            }
        }

        // ------------------------------------------------------------------
        // Connect loop
        // ------------------------------------------------------------------

        private async Task RunForeverAsync(CancellationToken ct)
        {
            int backoffAttempt = 0;
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    SetState(SourceConnectionState.Connecting);
                    await ConnectOnceAsync(ct);
                    // A clean close resets the backoff only if the session got somewhere;
                    // ConnectOnceAsync throws on failures that count as attempts.
                    backoffAttempt = 0;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _log("[Source] session ended: " + ex.Message);
                }

                ResetSessionState();
                if (ct.IsCancellationRequested)
                    break;

                // Exponential backoff 1s, 2s, 4s … capped at 30s.
                int delayMs = Math.Min(30_000, 1000 * (1 << Math.Min(backoffAttempt, 5)));
                backoffAttempt++;
                _log($"[Source] reconnecting in {delayMs} ms");
                try
                {
                    await Task.Delay(delayMs, ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        private void ResetSessionState()
        {
            _serverName = null;
            _serverId = null;
            _sourceGranted = false;
            _pendingLongTermPsk = null;
            _activateReceived = false;
            _activeRoles = Array.Empty<string>();
            _clockOffsetUs = 0;
            _clockConverged = false;
            _bestRttUs = long.MaxValue;
            StreamStartAuthorized = false;
            _burstRemaining = 0;
            DisposeTimer(ref _burstTimer);
            DisposeTimer(ref _resyncTimer);
        }

        private static void DisposeTimer(ref Timer? timer)
        {
            Timer? t = Interlocked.Exchange(ref timer, null);
            t?.Dispose();
        }

        private async Task ConnectOnceAsync(CancellationToken ct)
        {
            var framing = new NoiseWireFraming(_suite, _pairingStore, _identity);
            _framing = framing;

            var ws = new ClientWebSocket();
            ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(30); // RFC6455 ping keeps liveness (spec-allowed)
            _ws = ws;

            SetState(SourceConnectionState.Handshaking);
            _log("[Source] connecting to " + _serverUri);
            await ws.ConnectAsync(_serverUri, ct);

            var queue = new BlockingCollection<OutItem>(new ConcurrentQueue<OutItem>());
            _sendQueue = queue;
            _writerTask = Task.Run(() => WriterLoopAsync(framing, ws, queue, ct));
            foreach (WireFrame f in framing.Start())
                Enqueue(OutItem.Raw(f));

            _receiveTask = ReceiveLoopAsync(framing, ws, queue, ct);
            await Task.WhenAny(_receiveTask, _writerTask);
            // Propagate the first failure (if any) to the run loop for backoff accounting.
            if (_receiveTask is { IsFaulted: true })
                throw _receiveTask.Exception!.GetBaseException();
            if (_writerTask is { IsFaulted: true })
                throw _writerTask.Exception!.GetBaseException();
        }

        // ------------------------------------------------------------------
        // Receive loop (single thread; owns all inbound processing)
        // ------------------------------------------------------------------

        private async Task ReceiveLoopAsync(NoiseWireFraming framing, ClientWebSocket ws, BlockingCollection<OutItem> queue, CancellationToken ct)
        {
            var packet = new byte[64 * 1024];
            try
            {
                while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
                {
                    WebSocketReceiveResult result = await ws.ReceiveAsync(new ArraySegment<byte>(packet), ct);
                    if (result.MessageType == WebSocketMessageType.Close)
                        break;

                    // Reassemble the full WS message (a message may span several receive packets).
                    using var ms = new MemoryStream();
                    bool isText = result.MessageType == WebSocketMessageType.Text;
                    ms.Write(packet, 0, result.Count);
                    while (!result.EndOfMessage)
                    {
                        result = await ws.ReceiveAsync(new ArraySegment<byte>(packet), ct);
                        ms.Write(packet, 0, result.Count);
                    }
                    byte[] bytes = ms.ToArray();

                    WireFrame frame = isText
                        ? WireFrame.FromText(Encoding.UTF8.GetString(bytes))
                        : WireFrame.FromBinary(bytes);

                    InboundFrameResult res = framing.ProcessInbound(frame);
                    // The Noise responder learns the server's static key from server/init; the
                    // connection mirrors it once the handshake produces it (pairing persistence
                    // and the trust gate both read it).
                    if (_serverId is null && framing.ServerId is not null)
                        _serverId = framing.ServerId;
                    if (res.FatalReason is not null)
                    {
                        _log("[Source] fatal protocol error: " + res.FatalReason + " — closing");
                        break; // silent failure: close without any further message (spec)
                    }
                    if (res.Replies is not null)
                    {
                        _log($"[Source] inbound produced {res.Replies.Count} reply frame(s)");
                        foreach (WireFrame reply in res.Replies)
                            Enqueue(OutItem.Raw(reply));
                    }
                    if (res.HasDeferredReply)
                    {
                        _log("[Source] inbound produced a deferred reply (re-handshake)");
                        Enqueue(OutItem.DeferredReply());
                    }
                    if (res.Text is not null)
                        HandleJsonMessage(res.Text);
                }
            }
            catch (OperationCanceledException) { /* stopping */ }
            catch (WebSocketException ex)
            {
                _log("[Source] receive error: " + ex.Message);
            }
            finally
            {
                // Signal the writer to drain and stop.
                queue.CompleteAdding();
            }
        }

        private void HandleJsonMessage(string json)
        {
            JObject doc;
            try { doc = JObject.Parse(json); }
            catch (Exception ex)
            {
                _log("[Source] unparsable server JSON: " + ex.Message);
                return;
            }

            string? type = doc["type"]?.Value<string>();
            JObject payload = doc["payload"] as JObject ?? new JObject();

            switch (type)
            {
                case "server/hello":
                    _serverName = payload["name"]?.Value<string>();
                    _log("[Source] server/hello: name=" + (_serverName ?? "?") + ", server_id=" + _serverId);
                    SendClientHello();
                    break;

                case "server/activate":
                    HandleServerActivate(payload);
                    break;

                case "server/pair-finalize":
                    HandleServerPairFinalize();
                    break;

                case "server/time":
                    HandleServerTime(payload);
                    break;

                case "server/command":
                    HandleServerCommand(payload);
                    break;

                case "pair/abort":
                    _log("[Source] pair/abort: " + (payload["reason"]?.Value<string>() ?? "?"));
                    _pendingLongTermPsk = null;
                    break;

                case "server/error":
                    _log("[Source] server/error: " + (payload["reason"]?.Value<string>() ?? "?"));
                    break;

                case "server/unpair":
                    // The server dropped its record: forget ours, say goodbye 'unpaired', close.
                    if (_serverId is not null)
                    {
                        var lt = _pairingStore.LongTermFor(_serverId);
                        if (lt is not null)
                            _pairingStore.Remove(lt.PskId);
                        _log("[Source] server/unpair: removed pairing record for " + _serverId);
                    }
                    EnqueueJson(BuildGoodbye("unpaired"));
                    Enqueue(OutItem.Close());
                    break;

                case "client/goodbye":
                    _log("[Source] server sent client/goodbye (unexpected): " + json);
                    break;

                default:
                    _log("[Source] ignoring message type: " + type);
                    break;
            }
        }

        // ------------------------------------------------------------------
        // client/hello
        // ------------------------------------------------------------------

        private void SendClientHello()
        {
            // Field order mirrors the reference SDK's ClientHelloPayload.
            var payload = new JObject
            {
                ["name"] = _clientName,
                ["trust_level"] = _framing!.MatchedPskCategory == PskCategory.LongTerm ? "user" : "none",
                ["supported_roles"] = new JArray { "source@v1" },
                // No features: the plugin reports no line-sense, so the support object stays empty.
                ["source@v1_support"] = new JObject(),
                // Pair methods use the LIST wire form both reference implementations parse.
                ["supported_pair_methods"] = new JArray
                {
                    new JObject { ["method"] = "pairing_psk", ["locations"] = new JArray { "operator" } },
                },
                ["unpaired_access"] = new JObject { ["enabled"] = true },
                ["device_info"] = new JObject
                {
                    ["product_name"] = "MusicBee",
                    ["manufacturer"] = "SendSpin MusicBee plugin",
                    ["software_version"] = _softwareVersion,
                },
            };
            EnqueueJson(new JObject { ["type"] = "client/hello", ["payload"] = payload });
            _log("[Source] sent client/hello (roles: source@v1, trust: " + payload["trust_level"] + ")");
        }

        // ------------------------------------------------------------------
        // server/activate
        // ------------------------------------------------------------------

        private void HandleServerActivate(JObject payload)
        {
            string[] activities = payload["activities"]?.ToObject<JArray>()?.Select(t => t.Value<string>() ?? "").ToArray()
                                  ?? Array.Empty<string>();
            string[]? roles = payload["active_roles"]?.ToObject<JArray>()?.Select(t => t.Value<string>() ?? "").ToArray();
            JObject? pairing = payload["pairing"] as JObject;

            _activateReceived = true;
            // active_roles persists across activates that omit it.
            if (roles is not null)
                _activeRoles = roles;

            string pairingMethod = pairing?["method"]?.Value<string>() ?? "";
            _log($"[Source] server/activate: activities=[{string.Join(",", activities)}], roles=[{string.Join(",", _activeRoles)}]"
                 + (pairing is not null ? ", pairing.method=" + pairingMethod : ""));

            if (pairing is not null && activities.Contains("pairing"))
            {
                HandlePairingActivate(pairingMethod);
                return;
            }

            bool granted = Array.Exists(_activeRoles, r => r.StartsWith("source@", StringComparison.Ordinal));
            if (granted)
            {
                // Client-side trust gate (mirrors the reference SDK): source@v1 streams captured
                // audio, so it may only run on a paired ('user'-trust) connection.
                if (_framing!.MatchedPskCategory != PskCategory.LongTerm)
                {
                    _log("[Source] server granted source@v1 without a long-term PSK — refusing, closing (unauthorized)");
                    EnqueueJson(BuildGoodbye("unauthorized"));
                    Enqueue(OutItem.Close());
                    return;
                }

                if (!_sourceGranted)
                {
                    _sourceGranted = true;
                    SetState(SourceConnectionState.Ready);
                    SourceRoleChanged?.Invoke(this, new SourceRoleEventArgs(true));
                }

                // The spec permits client/time only after activation. (Re)converge, then report
                // state — aiosendspin requires the client/state before it accepts source audio.
                StartClockBurst();
                _resyncTimer ??= new Timer(_ => { if (_sourceGranted) StartClockBurst(); },
                    null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
            }
            else if (_sourceGranted)
            {
                // Role revoked: stop streaming (the stream end goes out with the audio-path
                // handling) and drop any pending start authorization.
                _sourceGranted = false;
                SetState(SourceConnectionState.Unpaired);
                DisposeTimer(ref _resyncTimer);
                SourceRoleChanged?.Invoke(this, new SourceRoleEventArgs(false));
            }
            else
            {
                // Unpaired and not pairing: idle until the operator pairs us or approves access.
                SetState(SourceConnectionState.Unpaired);
            }
        }

        private void HandlePairingActivate(string method)
        {
            if (_framing!.MatchedPskCategory != PskCategory.Pairing)
            {
                // The connection is not on the pairing PSK, so finalizing would deliver the
                // long-term PSK over an unauthenticated channel: abort the attempt (attempt
                // stays alive for a re-handshaked retry from the server).
                _log("[Source] pairing activation (method=" + method + ") but matched PSK is "
                     + _framing.MatchedPskCategory + " — aborting attempt (method_not_supported)");
                EnqueueJson(new JObject
                {
                    ["type"] = "pair/abort",
                    ["payload"] = new JObject { ["reason"] = "method_not_supported" },
                });
                SetState(SourceConnectionState.Pairing);
                return;
            }

            if (method != "pairing_psk")
            {
                // The plugin only offers pairing_psk.
                _log("[Source] unsupported pairing method '" + method + "' — aborting attempt");
                EnqueueJson(new JObject
                {
                    ["type"] = "pair/abort",
                    ["payload"] = new JObject { ["reason"] = "method_not_supported" },
                });
                SetState(SourceConnectionState.Pairing);
                return;
            }

            // Fresh long-term PSK from the CSPRNG, delivered in the clear-of-PAKE but
            // PSK-authenticated channel (the pairing PSK in msg1 proves the server holds the
            // token's secret).
            byte[] psk = new byte[NoiseConstants.PskSize];
            using (var rng = RandomNumberGenerator.Create())
                rng.GetBytes(psk);
            _pendingLongTermPsk = psk;
            SetState(SourceConnectionState.Pairing);
            EnqueueJson(new JObject
            {
                ["type"] = "client/pair-finalize",
                ["payload"] = new JObject { ["long_term_psk"] = Base64UrlText.Encode(psk) },
            });
            _log("[Source] pairing: sent client/pair-finalize (delivering long-term PSK)");
        }

        // ------------------------------------------------------------------
        // server/pair-finalize is delivered as a JSON message (no payload fields).
        // ------------------------------------------------------------------

        private void HandleServerPairFinalize()
        {
            byte[]? psk = _pendingLongTermPsk;
            _pendingLongTermPsk = null;
            if (psk is null)
            {
                _log("[Source] server/pair-finalize with no pairing attempt in flight — ignoring");
                return;
            }
            if (_serverId is null)
            {
                _log("[Source] pairing complete but server_id unknown — record NOT persisted");
                return;
            }

            _pairingStore.Upsert(new PairingRecord(psk, PskCategory.LongTerm, _serverId));
            _log("[Source] pairing complete: long-term record persisted for server " + _serverId);
            PairingCompleted?.Invoke(this, _serverId);
            // The server now re-handshakes to the new PSK; the hello/activate exchange repeats
            // on the same connection (handled by the ordinary receive path).
        }

        // ------------------------------------------------------------------
        // Clock sync (client/time → server/time)
        // ------------------------------------------------------------------

        private void StartClockBurst()
        {
            lock (_clockLock)
            {
                if (_burstRemaining > 0)
                    return;
                _burstRemaining = 8;
                _burstTimer ??= new Timer(_ => SendTimeProbe(), null, TimeSpan.Zero, TimeSpan.FromMilliseconds(25));
            }
        }

        private void SendTimeProbe()
        {
            bool fire;
            lock (_clockLock)
            {
                if (_burstRemaining <= 0 || !_sourceGranted)
                    return;
                fire = true;
                _burstRemaining--;
                if (_burstRemaining == 0)
                {
                    var t = _burstTimer;
                    _burstTimer = null;
                    t?.Dispose();
                }
            }
            if (!fire)
                return;
            long t1 = ServerClock.NowUs();
            EnqueueJson(new JObject
            {
                ["type"] = "client/time",
                ["payload"] = new JObject { ["client_transmitted"] = t1 },
            });
        }

        private void HandleServerTime(JObject payload)
        {
            long t1 = payload["client_transmitted"]?.Value<long>() ?? 0;
            long t2 = payload["server_received"]?.Value<long>() ?? 0;
            long t3 = payload["server_transmitted"]?.Value<long>() ?? 0;
            long t4 = ServerClock.NowUs();

            long rtt = (t4 - t1) - (t3 - t2);
            if (rtt <= 0)
                return; // clock jumped between T1 and T4; discard the sample
            long offset = ((t2 - t1) + (t3 - t4)) / 2;

            // Best-of-burst: keep the min-RTT sample (smallest network noise → best offset).
            if (!_clockConverged || rtt < Math.Abs(_bestRttUs))
            {
                _bestRttUs = rtt;
                _clockOffsetUs = offset;
                bool wasConverged = _clockConverged;
                _clockConverged = true;
                if (!wasConverged)
                {
                    _log($"[Source] clock synced: offset={offset} µs, rtt={rtt} µs");
                    // First convergence on this connection: report state so the server may
                    // start the stream (aiosendspin requires client/state before chunks).
                    SendClientState(available: true);
                }
            }
        }

        private long _bestRttUs = long.MaxValue;

        private void SendClientState(bool available)
        {
            if (!_activateReceived)
                return;
            var payload = new JObject
            {
                ["available"] = available,
                // The source role object is required once the role is active; the plugin
                // supports no line-sense, so it carries no optional fields.
                ["source"] = new JObject(),
            };
            EnqueueJson(new JObject { ["type"] = "client/state", ["payload"] = payload });
            _log("[Source] sent client/state: available=" + available.ToString().ToLowerInvariant());
        }

        // ------------------------------------------------------------------
        // server/command (source)
        // ------------------------------------------------------------------

        private void HandleServerCommand(JObject payload)
        {
            JObject? source = payload["source"] as JObject;
            if (source is null)
                return; // player/other commands are not for us (we advertise source@v1 only)
            string? command = source["command"]?.Value<string>();
            _log("[Source] server/command: source." + command);

            // Streaming behavior lands with the audio path; for now authorization is tracked
            // so the audio path can be driven by tests.
            switch (command)
            {
                case "start":
                    if (!_sourceGranted)
                    {
                        _log("[Source] ignoring start: source@v1 not granted");
                        return;
                    }
                    StreamStartAuthorized = true;
                    StreamStartRequested?.Invoke(this, EventArgs.Empty);
                    break;

                case "stop":
                    StreamStartAuthorized = false;
                    StreamStopRequested?.Invoke(this, EventArgs.Empty);
                    break;
            }
        }

        /// <summary>Whether the server's latest start authorization stands on the current connection.</summary>
        public bool StreamStartAuthorized { get; private set; }

        /// <summary>Raised when the server commands this source to start streaming.</summary>
        public event EventHandler? StreamStartRequested;
        /// <summary>Raised when the server commands this source to stop streaming.</summary>
        public event EventHandler? StreamStopRequested;

        // ------------------------------------------------------------------
        // Outbound plumbing
        // ------------------------------------------------------------------

        private void EnqueueJson(JObject message) =>
            Enqueue(OutItem.Json(message.ToString(Newtonsoft.Json.Formatting.None)));

        private JObject BuildGoodbye(string reason) => new JObject
        {
            ["type"] = "client/goodbye",
            ["payload"] = new JObject { ["reason"] = reason },
        };

        private void Enqueue(OutItem item)
        {
            var queue = _sendQueue;
            if (queue is null || queue.IsAddingCompleted)
                return;
            try
            {
                queue.TryAdd(item);
            }
            catch (InvalidOperationException)
            {
                // CompleteAdding raced the add; the connection is going away anyway.
            }
        }

        private sealed class OutItem
        {
            public enum Kind { Raw, Json, Binary, DeferredReply, Close }

            public Kind ItemKind;
            public WireFrame Frame;
            public string JsonText = "";

            public static OutItem Raw(WireFrame frame) => new OutItem { ItemKind = Kind.Raw, Frame = frame };
            public static OutItem Json(string json) => new OutItem { ItemKind = Kind.Json, JsonText = json };
            public static OutItem Binary(ReadOnlyMemory<byte> data) => new OutItem { ItemKind = Kind.Binary, Frame = WireFrame.FromBinary(data) };
            public static OutItem DeferredReply() => new OutItem { ItemKind = Kind.DeferredReply };
            public static OutItem Close() => new OutItem { ItemKind = Kind.Close };
        }

        private async Task WriterLoopAsync(NoiseWireFraming framing, ClientWebSocket ws, BlockingCollection<OutItem> queue, CancellationToken ct)
        {
            try
            {
                foreach (OutItem item in queue.GetConsumingEnumerable(ct))
                {
                    if (item.ItemKind == OutItem.Kind.Close)
                    {
                        try
                        {
                            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "goodbye", ct);
                        }
                        catch { /* close is best-effort */ }
                        break;
                    }

                    // Raw = pre-transport handshake frames (or handshake replies), sent as-is.
                    // Json/Binary = application messages, encrypted here on the send path.
                    if (item.ItemKind == OutItem.Kind.Raw)
                        await SendWireFramesAsync(ws, new[] { item.Frame }, ct);
                    else if (item.ItemKind == OutItem.Kind.Json)
                        await SendWireFramesAsync(ws, framing.EncodeText(item.JsonText), ct);
                    else if (item.ItemKind == OutItem.Kind.Binary)
                        await SendWireFramesAsync(ws, framing.EncodeBinary(item.Frame.Payload), ct);
                    else // DeferredReply: encode + commit the pending key swap in one send step.
                        await SendWireFramesAsync(ws, framing.EncodeDeferredReply(), ct);
                }
            }
            catch (OperationCanceledException) { /* stopping */ }
            catch (Exception ex)
            {
                _log("[Source] write error: " + ex.Message);
            }
        }

        private static async Task SendWireFramesAsync(ClientWebSocket ws, IEnumerable<WireFrame> frames, CancellationToken ct)
        {
            foreach (WireFrame f in frames)
            {
                if (f.Kind == WireFrameKind.Text)
                {
                    byte[] bytes = Encoding.UTF8.GetBytes(f.PayloadAsText());
                    await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct);
                }
                else
                {
                    await ws.SendAsync(new ArraySegment<byte>(f.Payload.ToArray()), WebSocketMessageType.Binary, true, ct);
                }
            }
        }
    }
}