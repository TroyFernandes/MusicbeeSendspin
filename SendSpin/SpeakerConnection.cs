using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace MusicBeePlugin.SendSpin
{
    /// <summary>
    /// Represents a connection to a SendSpin speaker/client.
    /// This is used for server-initiated connections where WE connect TO the speaker.
    ///
    /// Wire contract (verified against sendspin-go v1.8.2, the version echolocal runs):
    ///  - Server dials the speaker's WebSocket; speaker sends client/hello first.
    ///  - ALL server-domain timestamps (server/time t2/t3 + audio chunk timestamps)
    ///    are SERVER UPTIME MICROS (monotonic, since plugin start) — see ServerClock.
    ///    Client timestamps (client/time t1) are Unix epoch µs and echoed back as-is.
    ///  - Binary audio frame: [0x04][8-byte big-endian timestamp µs][raw audio].
    ///  - Each chunk must be sent ~500 ms before its timestamp (speaker drops chunks
    ///    arriving >50 ms late and buffers 25 chunks before first playback).
    ///  - All WS writes are serialized through a single writer (ClientWebSocket is
    ///    not thread-safe for concurrent sends).
    /// </summary>
    public class SpeakerConnection : IDisposable
    {
        private const long SendAheadUs = 500_000;   // send each chunk 500 ms before its play time
        private const long MaxQueuedChunks = 6000;  // ~2 min of 20 ms chunks — drop-oldest safety valve
        private const int AudioChunkMessageType = 0x04;

        private readonly DiscoveredSpeaker _speaker;
        private readonly Action<string> _logger;
        private ClientWebSocket? _webSocket;
        private CancellationTokenSource? _cts;
        private BlockingCollection<SendItem>? _sendQueue;
        private Task? _writerTask;
        private Timer? _pumpTimer;
        private readonly ConcurrentQueue<(long TsUs, byte[] Data)> _audioQueue = new();
        private long _anchorUs;            // set on stream start; 0 = not streaming
        private bool _disposed;
        private bool _handshakeComplete;
        private bool _disconnectFired;

        // Client capabilities discovered during handshake
        public string ClientId { get; private set; } = string.Empty;
        public string ClientName { get; private set; } = string.Empty;
        public int ClientVersion { get; private set; }
        public List<string> SupportedRoles { get; private set; } = new();
        public List<string> ActiveRoles { get; private set; } = new();
        public int BufferCapacityBytes { get; private set; }
        public List<(string Codec, int SampleRate, int Channels, int BitDepth)> SupportedFormats { get; private set; } = new();
        public List<string> SupportedCommands { get; private set; } = new();

        // Player state reported by the speaker (client/state)
        public string PlayerState { get; private set; } = string.Empty;
        public int PlayerVolume { get; private set; } = 100;
        public bool PlayerMuted { get; private set; }

        public bool IsConnected => _webSocket?.State == WebSocketState.Open && _handshakeComplete;
        public DiscoveredSpeaker Speaker => _speaker;
        /// <summary>Timestamp anchor (server-uptime µs) for the current stream, 0 if not streaming.</summary>
        public long AnchorUs => _anchorUs;

        /// <summary>
        /// Event raised when the connection is established and handshake complete.
        /// </summary>
        public event EventHandler? Connected;

        /// <summary>
        /// Event raised when the connection is closed.
        /// </summary>
        public event EventHandler? Disconnected;

        /// <summary>
        /// Event raised when a (still unhandled) message is received from the speaker.
        /// </summary>
        public event EventHandler<JObject>? MessageReceived;

        public SpeakerConnection(DiscoveredSpeaker speaker, Action<string> logger)
        {
            _speaker = speaker ?? throw new ArgumentNullException(nameof(speaker));
            _logger = logger ?? (_ => { });
        }

        /// <summary>
        /// Connect to the speaker and perform the SendSpin handshake.
        /// </summary>
        public async Task<bool> ConnectAsync(string serverId, string serverName, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(_speaker.WebSocketUrl))
            {
                _logger($"Cannot connect to {_speaker.Name}: No WebSocket URL");
                return false;
            }

            try
            {
                _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                _webSocket = new ClientWebSocket();
                _webSocket.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);

                var uri = new Uri(_speaker.WebSocketUrl);
                _logger($"Connecting to speaker: {_speaker.Name} at {uri}");

                await _webSocket.ConnectAsync(uri, _cts.Token);
                _logger($"WebSocket connected to {_speaker.Name}");

                // Single serialized writer for ALL outbound frames (JSON + binary).
                _sendQueue = new BlockingCollection<SendItem>(new ConcurrentQueue<SendItem>());
                _writerTask = Task.Run(() => WriterLoopAsync());

                // Start receiving messages
                _ = ReceiveLoopAsync();

                // Wait for client/hello (speaker sends it right after accept)
                var helloReceived = await WaitForClientHelloAsync(_cts.Token);

                if (!helloReceived)
                {
                    _logger($"Did not receive client/hello from {_speaker.Name}");
                    await DisconnectAsync("no_hello");
                    return false;
                }

                // Send server/hello (v1.8.2 keys: server_id, name, version, active_roles, connection_reason)
                await SendServerHelloAsync(serverId, serverName);

                _handshakeComplete = true;
                _logger($"Handshake complete with {_speaker.Name} (client: {ClientName}, formats: {SupportedFormats.Count}, buffer: {BufferCapacityBytes} B)");

                // Pacing pump: release queued audio chunks ~500 ms before their play time.
                _pumpTimer = new Timer(_ => PumpAudioOnce(), null, 20, 20);

                Connected?.Invoke(this, EventArgs.Empty);
                return true;
            }
            catch (Exception ex)
            {
                _logger($"Failed to connect to {_speaker.Name}: {ex.Message}");
                await DisconnectAsync("connect_failed");
                return false;
            }
        }

        /// <summary>
        /// Disconnect from the speaker.
        /// </summary>
        public async Task DisconnectAsync(string reason = "shutdown")
        {
            var ws = _webSocket;
            if (ws == null)
                return;

            try
            {
                if (ws.State == WebSocketState.Open)
                {
                    var goodbye = new JObject
                    {
                        ["type"] = "server/goodbye",
                        ["payload"] = new JObject { ["reason"] = reason }
                    };
                    EnqueueText(goodbye.ToString(Newtonsoft.Json.Formatting.None));
                    // Give the writer a moment to flush the goodbye before closing.
                    try { await Task.Delay(100); } catch { }
                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, reason, CancellationToken.None);
                }
            }
            catch (Exception ex)
            {
                _logger($"Error during disconnect from {_speaker.Name}: {ex.Message}");
            }
            finally
            {
                CleanupAfterClose();
            }
        }

        private void CleanupAfterClose()
        {
            _sendQueue?.CompleteAdding();
            _sendQueue = null;
            _pumpTimer?.Dispose();
            _pumpTimer = null;
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
            _webSocket?.Dispose();
            _webSocket = null;
            _handshakeComplete = false;
            while (_audioQueue.TryDequeue(out _)) { }
            _anchorUs = 0;

            if (!_disconnectFired)
            {
                _disconnectFired = true;
                Disconnected?.Invoke(this, EventArgs.Empty);
            }
        }

        // ------------------------------------------------------------------
        // Outbound: control messages
        // ------------------------------------------------------------------

        /// <summary>
        /// Send a JSON message to the speaker (serialized through the writer).
        /// </summary>
        public Task SendMessageAsync(JObject message)
        {
            EnqueueText(message.ToString(Newtonsoft.Json.Formatting.None));
            return Task.CompletedTask;
        }

        private void EnqueueText(string json)
        {
            if (_sendQueue == null) return;
            _sendQueue.TryAdd(new SendItem
            {
                Data = Encoding.UTF8.GetBytes(json),
                IsText = true
            });
        }

        /// <summary>
        /// Send stream/start with the given format. Call SetStreamAnchor first
        /// so queued chunks are scheduled in the right clock domain.
        /// </summary>
        public Task SendStreamStart(string codec, int sampleRate, int channels, int bitDepth)
        {
            while (_audioQueue.TryDequeue(out _)) { }

            var msg = new JObject
            {
                ["type"] = "stream/start",
                ["payload"] = new JObject
                {
                    ["player"] = new JObject
                    {
                        ["codec"] = codec,
                        ["sample_rate"] = sampleRate,
                        ["channels"] = channels,
                        ["bit_depth"] = bitDepth
                    }
                }
            };
            SendMessageAsync(msg);
            return Task.CompletedTask;
        }
        /// <summary>
        /// (Re)anchor a running stream — used when a speaker joins mid-playback.
        /// <paramref name="anchorUs"/> is the server-uptime µs at which the decoder's
        /// current first-in-flight timestamp (see _lastChunkRelTs) should play.
        /// </summary>
        public void SetStreamAnchor(long anchorUs)
        {
            _anchorUs = anchorUs;
            while (_audioQueue.TryDequeue(out _)) { }
        }

        public Task SendStreamEnd()
        {
            var msg = new JObject
            {
                ["type"] = "stream/end",
                ["payload"] = new JObject { }
            };
            SendMessageAsync(msg);
            return Task.CompletedTask;
        }

        /// <summary>
        /// stream/clear: speaker drops all buffered audio and re-enters buffering mode (used on seek).
        /// </summary>
        public Task SendStreamClear()
        {
            while (_audioQueue.TryDequeue(out _)) { }
            var msg = new JObject
            {
                ["type"] = "stream/clear",
                ["payload"] = new JObject { }
            };
            SendMessageAsync(msg);
            return Task.CompletedTask;
        }

        public Task SendGroupUpdate(string playbackState)
        {
            var msg = new JObject
            {
                ["type"] = "group/update",
                ["payload"] = new JObject
                {
                    ["playback_state"] = playbackState,
                    ["group_id"] = "musicbee"
                }
            };
            SendMessageAsync(msg);
            return Task.CompletedTask;
        }

        /// <summary>
        /// server/state metadata (track info).
        /// </summary>
        public Task SendMetadata(string? title, string? artist, string? album, string? artworkUrl)
        {
            var metadata = new JObject { ["timestamp"] = ServerClock.NowUs() };
            if (title != null) metadata["title"] = title;
            if (artist != null) metadata["artist"] = artist;
            if (album != null) metadata["album"] = album;
            if (artworkUrl != null) metadata["artwork_url"] = artworkUrl;

            var msg = new JObject
            {
                ["type"] = "server/state",
                ["payload"] = new JObject { ["metadata"] = metadata }
            };
            SendMessageAsync(msg);
            return Task.CompletedTask;
        }

        // ------------------------------------------------------------------
        // Outbound: audio (paced)
        // ------------------------------------------------------------------

        /// <summary>
        /// Queue one audio chunk for paced delivery.
        /// <paramref name="relativeTsUs"/> is microseconds from the start of the
        /// (decoder's) stream; the chunk's absolute play time is AnchorUs + relativeTsUs.
        /// Chunks are released on the wire ~500 ms before that time.
        /// </summary>
        public void EnqueueAudioChunk(long relativeTsUs, byte[] data)
        {
            if (_anchorUs == 0 || _sendQueue == null) return;

            _audioQueue.Enqueue((_anchorUs + relativeTsUs, data));

            if (_audioQueue.Count > MaxQueuedChunks)
            {
                // Far too much queued (stalled writer): drop oldest, keep latest so
                // playback stays current. The speaker will re-buffer after the gap.
                while (_audioQueue.Count > MaxQueuedChunks && _audioQueue.TryDequeue(out _)) { }
                _logger($"Audio queue overflow on {_speaker.Name}, dropped {MaxQueuedChunks}+ oldest chunks");
            }
        }

        private void PumpAudioOnce()
        {
            if (_sendQueue == null || _anchorUs == 0) return;

            long horizon = ServerClock.NowUs() + SendAheadUs;
            int released = 0;
            while (_audioQueue.TryPeek(out var next) && next.TsUs <= horizon)
            {
                _audioQueue.TryDequeue(out var chunk);
                _sendQueue.TryAdd(new SendItem
                {
                    Data = BuildAudioFrame(chunk.TsUs, chunk.Data),
                    IsText = false
                });
                released++;
            }

            if (released > 0 && released % 50 == 0)
            {
                _logger($"{_speaker.Name}: released {released} chunks, {(_audioQueue.Count / 50.0):F1}s queued");
            }
        }

        private static byte[] BuildAudioFrame(long timestampUs, byte[] data)
        {
            // [0x04][8-byte big-endian timestamp µs][audio] — protocol.BinaryMessageHeaderSize = 9
            var frame = new byte[9 + data.Length];
            frame[0] = AudioChunkMessageType;
            frame[1] = (byte)(timestampUs >> 56);
            frame[2] = (byte)(timestampUs >> 48);
            frame[3] = (byte)(timestampUs >> 40);
            frame[4] = (byte)(timestampUs >> 32);
            frame[5] = (byte)(timestampUs >> 24);
            frame[6] = (byte)(timestampUs >> 16);
            frame[7] = (byte)(timestampUs >> 8);
            frame[8] = (byte)timestampUs;
            Buffer.BlockCopy(data, 0, frame, 9, data.Length);
            return frame;
        }

        // ------------------------------------------------------------------
        // Writer loop (all outbound frames, serialized)
        // ------------------------------------------------------------------

        private sealed class SendItem
        {
            public byte[] Data = null!;
            public bool IsText;
        }

        private async Task WriterLoopAsync()
        {
            var ws = _webSocket;
            var queue = _sendQueue;
            var cts = _cts;
            if (ws == null || queue == null || cts == null) return;

            try
            {
                foreach (var item in queue.GetConsumingEnumerable(cts.Token))
                {
                    await ws.SendAsync(
                        new ArraySegment<byte>(item.Data),
                        item.IsText ? WebSocketMessageType.Text : WebSocketMessageType.Binary,
                        true,
                        cts.Token);
                }
            }
            catch (OperationCanceledException) { /* shutdown */ }
            catch (Exception ex)
            {
                _logger($"Write error to {_speaker.Name}: {ex.Message}");
            }
        }

        // ------------------------------------------------------------------
        // Inbound
        // ------------------------------------------------------------------

        private TaskCompletionSource<bool>? _helloTcs;

        private async Task<bool> WaitForClientHelloAsync(CancellationToken cancellationToken)
        {
            _helloTcs = new TaskCompletionSource<bool>();

            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

            linkedCts.Token.Register(() => _helloTcs.TrySetResult(false));

            return await _helloTcs.Task;
        }

        private async Task SendServerHelloAsync(string serverId, string serverName)
        {
            // Determine which roles to activate based on what the client supports
            var activatedRoles = new List<string>();

            if (SupportedRoles.Any(r => r.StartsWith("player@")))
                activatedRoles.Add(SupportedRoles.First(r => r.StartsWith("player@")));
            if (SupportedRoles.Any(r => r.StartsWith("artwork@")))
                activatedRoles.Add(SupportedRoles.First(r => r.StartsWith("artwork@")));
            if (SupportedRoles.Any(r => r.StartsWith("metadata@")))
                activatedRoles.Add(SupportedRoles.First(r => r.StartsWith("metadata@")));

            ActiveRoles = activatedRoles;

            // v1.8.2 ServerHello keys: server_id, name, version, active_roles, connection_reason
            var hello = new JObject
            {
                ["type"] = "server/hello",
                ["payload"] = new JObject
                {
                    ["server_id"] = serverId,
                    ["name"] = serverName,
                    ["version"] = 1,
                    ["connection_reason"] = "playback",
                    ["active_roles"] = new JArray(activatedRoles)
                }
            };

            await SendMessageAsync(hello);
            _logger($"Sent server/hello to {_speaker.Name}, activated roles: {string.Join(", ", activatedRoles)}");
        }

        private async Task ReceiveLoopAsync()
        {
            var buffer = new byte[65536];
            var messageBuffer = new List<byte>();

            try
            {
                while (_webSocket?.State == WebSocketState.Open && !(_cts?.Token.IsCancellationRequested ?? true))
                {
                    var result = await _webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), _cts?.Token ?? CancellationToken.None);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        _logger($"Speaker {_speaker.Name} closed connection");
                        break;
                    }

                    if (result.MessageType == WebSocketMessageType.Text)
                    {
                        messageBuffer.AddRange(buffer.Take(result.Count));

                        if (result.EndOfMessage)
                        {
                            var json = Encoding.UTF8.GetString(messageBuffer.ToArray());
                            messageBuffer.Clear();
                            ProcessMessage(json);
                        }
                    }
                    else if (result.MessageType == WebSocketMessageType.Binary)
                    {
                        // We are the server; binary from the speaker is not expected. Ignore.
                        // (Drain until end of message.)
                        if (result.EndOfMessage)
                            messageBuffer.Clear();
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Normal cancellation
            }
            catch (Exception ex)
            {
                _logger($"Receive error from {_speaker.Name}: {ex.Message}");
            }
            finally
            {
                if (_webSocket != null)
                    CleanupAfterClose();
            }
        }

        private void ProcessMessage(string json)
        {
            try
            {
                var message = JObject.Parse(json);
                var type = message["type"]?.ToString();

                _logger($"Received from {_speaker.Name}: {type}");

                switch (type)
                {
                    case "client/hello":
                        ProcessClientHello(message);
                        _helloTcs?.TrySetResult(true);
                        break;

                    case "client/goodbye":
                        var reason = message["payload"]?["reason"]?.ToString();
                        _logger($"Speaker {_speaker.Name} said goodbye: {reason}");
                        break;

                    case "client/time":
                        ProcessClientTime(message);
                        break;

                    case "client/state":
                        ProcessClientState(message);
                        break;

                    default:
                        MessageReceived?.Invoke(this, message);
                        break;
                }
            }
            catch (Exception ex)
            {
                _logger($"Error processing message from {_speaker.Name}: {ex.Message}");
            }
        }

        private void ProcessClientHello(JObject message)
        {
            var payload = message["payload"];
            if (payload == null) return;

            ClientId = payload["client_id"]?.ToString() ?? Guid.NewGuid().ToString();
            ClientName = payload["name"]?.ToString() ?? _speaker.Name;
            ClientVersion = payload["version"]?.Value<int>() ?? 0;

            var roles = payload["supported_roles"]?.ToObject<List<string>>();
            if (roles != null)
                SupportedRoles = roles;

            // v1.8.2: capabilities live under "player@v1_support"
            var support = payload["player@v1_support"] ?? payload["player_support"];
            if (support != null)
            {
                BufferCapacityBytes = support["buffer_capacity"]?.Value<int>() ?? 0;
                SupportedCommands = support["supported_commands"]?.ToObject<List<string>>() ?? new List<string>();

                SupportedFormats = new List<(string, int, int, int)>();
                foreach (var f in support["supported_formats"]?.ToArray() ?? Enumerable.Empty<JToken>())
                {
                    var codec = f["codec"]?.ToString();
                    if (string.IsNullOrEmpty(codec)) continue;
                    SupportedFormats.Add((
                        codec,
                        f["sample_rate"]?.Value<int>() ?? 48000,
                        f["channels"]?.Value<int>() ?? 2,
                        f["bit_depth"]?.Value<int>() ?? 16));
                }
            }

            _logger($"Client hello from {ClientName}: roles=[{string.Join(",", SupportedRoles)}], " +
                    $"formats=[{string.Join(";", SupportedFormats.Select(f => $"{f.Codec}{f.SampleRate}/{f.Channels}/{f.BitDepth}"))}], " +
                    $"buffer={BufferCapacityBytes}B, commands=[{string.Join(",", SupportedCommands)}]");
        }

        private void ProcessClientTime(JObject message)
        {
            // v1.8.2 server/time: client_transmitted (echo, client µs) +
            // server_received/server_transmitted in SERVER UPTIME µs (ServerClock domain).
            var payload = message["payload"];
            if (payload == null) return;

            var clientTransmitted = payload["client_transmitted"]?.Value<long>() ?? 0;
            var serverReceived = ServerClock.NowUs();

            var response = new JObject
            {
                ["type"] = "server/time",
                ["payload"] = new JObject
                {
                    ["client_transmitted"] = clientTransmitted,
                    ["server_received"] = serverReceived,
                    ["server_transmitted"] = ServerClock.NowUs()
                }
            };

            EnqueueText(response.ToString(Newtonsoft.Json.Formatting.None));
        }

        private void ProcessClientState(JObject message)
        {
            var payload = message["payload"];
            if (payload == null) return;

            // v1.8.2: payload.player.{state,volume,muted}
            var player = payload["player"];
            if (player == null) return;

            var state = player["state"]?.ToString();
            if (!string.IsNullOrEmpty(state) && state != PlayerState)
            {
                _logger($"Speaker {_speaker.Name} state: {PlayerState} -> {state}");
                PlayerState = state ?? string.Empty;
            }

            if (player["volume"] != null)
                PlayerVolume = player["volume"].Value<int>();
            if (player["muted"] != null)
                PlayerMuted = player["muted"].Value<bool>();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _ = DisconnectAsync();
        }
    }

    /// <summary>
    /// Manages multiple speaker connections for server-initiated mode.
    /// </summary>
    public class SpeakerConnectionManager : IDisposable
    {
        private readonly ConcurrentDictionary<string, SpeakerConnection> _connections = new();
        private readonly Action<string> _logger;
        private readonly string _serverId;
        private readonly string _serverName;
        private bool _disposed;

        /// <summary>
        /// Event raised when a speaker is connected and ready.
        /// </summary>
        public event EventHandler<SpeakerConnection>? SpeakerConnected;

        /// <summary>
        /// Event raised when a speaker disconnects.
        /// </summary>
        public event EventHandler<SpeakerConnection>? SpeakerDisconnected;

        public SpeakerConnectionManager(string serverId, string serverName, Action<string> logger)
        {
            _serverId = serverId;
            _serverName = serverName;
            _logger = logger ?? (_ => { });
        }

        /// <summary>
        /// Gets all active connections.
        /// </summary>
        public IReadOnlyList<SpeakerConnection> Connections => _connections.Values.Where(c => c.IsConnected).ToList();

        /// <summary>
        /// Connect to a discovered speaker.
        /// </summary>
        public async Task<bool> ConnectToSpeakerAsync(DiscoveredSpeaker speaker, CancellationToken cancellationToken = default)
        {
            if (_connections.ContainsKey(speaker.Id))
            {
                _logger($"Already connected to {speaker.Name}");
                return true;
            }

            var connection = new SpeakerConnection(speaker, _logger);
            connection.Disconnected += OnConnectionDisconnected;

            _connections[speaker.Id] = connection;

            var success = await connection.ConnectAsync(_serverId, _serverName, cancellationToken);

            if (success)
            {
                SpeakerConnected?.Invoke(this, connection);
                return true;
            }

            _connections.TryRemove(speaker.Id, out _);
            connection.Dispose();
            return false;
        }

        /// <summary>
        /// Disconnect from a speaker.
        /// </summary>
        public async Task DisconnectFromSpeakerAsync(string speakerId)
        {
            if (_connections.TryRemove(speakerId, out var connection))
            {
                await connection.DisconnectAsync();
                connection.Dispose();
            }
        }

        /// <summary>
        /// Send a message to all connected speakers.
        /// </summary>
        public Task BroadcastMessageAsync(JObject message)
        {
            foreach (var c in _connections.Values)
            {
                if (c.IsConnected)
                    c.SendMessageAsync(message);
            }
            return Task.CompletedTask;
        }

        public void BroadcastStreamStart(string codec, int sampleRate, int channels, int bitDepth)
        {
            foreach (var c in _connections.Values)
            {
                if (c.IsConnected)
                    c.SendStreamStart(codec, sampleRate, channels, bitDepth);
            }
        }

        public void BroadcastStreamEnd()
        {
            foreach (var c in _connections.Values)
            {
                if (c.IsConnected)
                    c.SendStreamEnd();
            }
        }

        public void BroadcastStreamClear()
        {
            foreach (var c in _connections.Values)
            {
                if (c.IsConnected)
                    c.SendStreamClear();
            }
        }

        public void BroadcastGroupUpdate(string playbackState)
        {
            foreach (var c in _connections.Values)
            {
                if (c.IsConnected)
                    c.SendGroupUpdate(playbackState);
            }
        }

        /// <summary>
        /// Disconnect all speakers.
        /// </summary>
        public async Task DisconnectAllAsync()
        {
            var tasks = _connections.Values.Select(c => c.DisconnectAsync());
            await Task.WhenAll(tasks);

            foreach (var connection in _connections.Values)
            {
                connection.Dispose();
            }

            _connections.Clear();
        }

        private void OnConnectionDisconnected(object? sender, EventArgs e)
        {
            if (sender is SpeakerConnection connection)
            {
                _connections.TryRemove(connection.Speaker.Id, out _);
                SpeakerDisconnected?.Invoke(this, connection);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _ = DisconnectAllAsync();
        }
    }
}
