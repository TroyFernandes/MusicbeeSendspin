using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MusicBeePlugin.SendSpin
{
    /// <summary>
    /// SendSpin server that manages WebSocket connections to clients
    /// and streams audio data using the SendSpin protocol
    /// </summary>
    public class SendSpinServer : IDisposable
    {
        private HttpListener? _httpListener;
        private CancellationTokenSource? _cancellationTokenSource;
        private Task? _listenerTask;
        private readonly ConcurrentDictionary<string, ConnectedClient> _clients;
        private readonly object _syncLock = new object();
        
        private PluginSettings _settings;
        private bool _isRunning;
        private bool _disposed;
        
        // Server info
        private readonly string _serverId;
        private readonly string _serverName;
        
        // Current state
        private SendSpinPlaybackState _playbackState = SendSpinPlaybackState.Stopped;
        private float _volume = 1.0f;
        private bool _muted;
        private TrackInfo? _currentTrack;
        private string? _currentArtworkPath;
        
        // Timing
        private readonly System.Diagnostics.Stopwatch _serverClock;
        private long _playbackStartTime; // Server time when playback started (for timestamp conversion)
        private const long BufferDelayMicroseconds = 500000; // 500ms buffer delay for speakers to prepare
        
        public event EventHandler<ClientEventArgs>? ClientConnected;
        public event EventHandler<ClientEventArgs>? ClientDisconnected;

        public bool IsRunning => _isRunning;
        public int ClientCount => _clients.Count;

        public SendSpinServer(PluginSettings settings)
        {
            _settings = settings;
            _clients = new ConcurrentDictionary<string, ConnectedClient>();
            _serverId = Guid.NewGuid().ToString();
            _serverName = settings.ServerName ?? "MusicBee SendSpin";
            _serverClock = System.Diagnostics.Stopwatch.StartNew();
        }

        /// <summary>
        /// Start the SendSpin server
        /// </summary>
        public async Task StartAsync()
        {
            if (_isRunning) return;
            
            try
            {
                _cancellationTokenSource = new CancellationTokenSource();
                
                // Start HTTP/WebSocket listener
                _httpListener = new HttpListener();
                _httpListener.Prefixes.Add($"http://+:{_settings.ServerPort}/");
                _httpListener.Start();
                
                _isRunning = true;
                
                // Start listener task
                _listenerTask = Task.Run(() => ListenForConnectionsAsync(_cancellationTokenSource.Token));
                
                // Start mDNS advertisement if enabled
                if (_settings.EnableMdns)
                {
                    await StartMdnsAdvertisementAsync();
                }
                
                Plugin.LogInfo("SendSpinServer", $"Server started on port {_settings.ServerPort}");
            }
            catch (Exception ex)
            {
                Plugin.LogError("SendSpinServer.Start", ex);
                _isRunning = false;
                throw;
            }
        }

        /// <summary>
        /// Stop the SendSpin server
        /// </summary>
        public async Task StopAsync()
        {
            if (!_isRunning) return;
            
            try
            {
                // Send stream/end to all clients before shutting down
                var endMessage = new
                {
                    type = "stream/end",
                    payload = new { }
                };
                BroadcastJsonMessage(endMessage);
                
                // Small delay to let messages send
                await Task.Delay(100);
                
                _cancellationTokenSource?.Cancel();
                
                // Disconnect all clients gracefully
                foreach (var client in _clients.Values)
                {
                    try
                    {
                        await client.DisconnectAsync("server_shutdown");
                    }
                    catch
                    {
                        // Ignore disconnection errors
                    }
                }
                _clients.Clear();
                
                _httpListener?.Stop();
                _httpListener?.Close();
                
                if (_listenerTask != null)
                {
                    try
                    {
                        await _listenerTask;
                    }
                    catch (OperationCanceledException)
                    {
                        // Expected
                    }
                }
                
                _isRunning = false;
                
                Plugin.LogInfo("SendSpinServer", "Server stopped");
            }
            catch (Exception ex)
            {
                Plugin.LogError("SendSpinServer.Stop", ex);
            }
        }

        /// <summary>
        /// Apply new settings
        /// </summary>
        public void ApplySettings(PluginSettings settings)
        {
            var portChanged = settings.ServerPort != _settings.ServerPort;
            _settings = settings;
            
            if (portChanged && _isRunning)
            {
                // Restart server with new port
                Task.Run(async () =>
                {
                    await StopAsync();
                    await StartAsync();
                });
            }
        }

        /// <summary>
        /// Send audio data to all connected clients
        /// </summary>
        public void SendAudioData(byte[] audioData, long presentationTimestamp, int sampleRate, int channels, int bitDepth)
        {
            if (!_isRunning || _clients.IsEmpty) return;
            
            // Convert presentation timestamp (relative to stream start) to server timestamp
            // presentationTimestamp is microseconds into the current track
            // Server timestamp is when this audio should be played in server's time domain
            var serverTimestamp = _playbackStartTime + presentationTimestamp;
            
            // Create binary message for player role (Type 4)
            var message = CreateAudioBinaryMessage(audioData, serverTimestamp, sampleRate, channels, bitDepth);
            
            var playerCount = 0;
            foreach (var client in _clients.Values)
            {
                try
                {
                    if (client.HasRole("player"))
                    {
                        playerCount++;
                        client.SendBinaryAsync(message).ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    Plugin.LogError($"SendAudioData to {client.Name}", ex);
                }
            }
            
            // Log periodically (every ~1 second at 48kHz with 960 samples per frame = ~50 calls/sec)
            if (_audioPacketCount++ % 50 == 0 && playerCount > 0)
            {
                Plugin.LogInfo("SendAudioData", $"Sent {audioData.Length} bytes to {playerCount} player(s), serverTs: {serverTimestamp}, presTs: {presentationTimestamp}");
            }
        }
        
        private int _audioPacketCount = 0;

        /// <summary>
        /// Update track metadata for all clients
        /// </summary>
        public void UpdateMetadata(TrackInfo trackInfo)
        {
            _currentTrack = trackInfo;
            
            var message = new
            {
                type = "server/state",
                payload = new
                {
                    metadata = new
                    {
                        title = trackInfo.Title,
                        artist = trackInfo.Artist,
                        album = trackInfo.Album,
                        duration = trackInfo.Duration
                    }
                }
            };
            
            BroadcastJsonMessage(message);
        }

        /// <summary>
        /// Update artwork for all clients
        /// </summary>
        public void UpdateArtwork(string artworkPath)
        {
            _currentArtworkPath = artworkPath;
            
            // Send artwork to artwork role clients
            if (!string.IsNullOrEmpty(artworkPath) && System.IO.File.Exists(artworkPath))
            {
                try
                {
                    var imageData = System.IO.File.ReadAllBytes(artworkPath);
                    var message = CreateArtworkBinaryMessage(imageData, "album");
                    
                    foreach (var client in _clients.Values)
                    {
                        if (client.HasRole("artwork"))
                        {
                            client.SendBinaryAsync(message).ConfigureAwait(false);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Plugin.LogError("UpdateArtwork", ex);
                }
            }
        }

        /// <summary>
        /// Set playback state
        /// </summary>
        public void SetPlaybackState(SendSpinPlaybackState state)
        {
            _playbackState = state;
            
            var stateString = state switch
            {
                SendSpinPlaybackState.Playing => "playing",
                SendSpinPlaybackState.Paused => "paused",
                SendSpinPlaybackState.Stopped => "stopped",
                _ => "stopped"
            };
            
            var message = new
            {
                type = "group/update",
                payload = new
                {
                    playback_state = stateString
                }
            };
            
            BroadcastJsonMessage(message);
            
            // If playing, send stream/start to all player clients
            if (state == SendSpinPlaybackState.Playing)
            {
                // Record when playback started in server time, plus buffer delay
                // This gives speakers time to buffer before the first audio needs to play
                _playbackStartTime = GetServerTimestamp() + BufferDelayMicroseconds;
                Plugin.LogInfo("SetPlaybackState", $"Playback starting at server time: {_playbackStartTime} (current: {GetServerTimestamp()}, buffer: {BufferDelayMicroseconds}us)");
                
                var streamStart = new
                {
                    type = "stream/start",
                    payload = new
                    {
                        player = new
                        {
                            codec = _settings.AudioCodec.ToLowerInvariant(),
                            sample_rate = _settings.SampleRate,
                            channels = _settings.Channels,
                            bit_depth = _settings.BitDepth
                        }
                    }
                };
                BroadcastJsonMessage(streamStart);
                Plugin.LogInfo("SetPlaybackState", $"Sent stream/start: {_settings.AudioCodec}, {_settings.SampleRate}Hz, {_settings.Channels}ch, {_settings.BitDepth}bit");
            }
            
            // If stopped, send stream/end
            if (state == SendSpinPlaybackState.Stopped)
            {
                var endMessage = new
                {
                    type = "stream/end",
                    payload = new { }
                };
                BroadcastJsonMessage(endMessage);
            }
        }

        /// <summary>
        /// Send stream/clear to tell clients to clear their audio buffers (used for seek and track changes)
        /// </summary>
        public void SendStreamClear()
        {
            var clearMessage = new
            {
                type = "stream/clear",
                payload = new
                {
                    roles = new[] { "player" }
                }
            };
            BroadcastJsonMessage(clearMessage);
            Plugin.LogInfo("SendStreamClear", "Sent stream/clear to all clients");
        }

        /// <summary>
        /// Prepare for track change - sends stream/clear and resets playback timing
        /// </summary>
        public void PrepareForTrackChange()
        {
            // Clear client buffers
            SendStreamClear();
            
            // Reset playback start time for the new track
            _playbackStartTime = GetServerTimestamp() + BufferDelayMicroseconds;
            Plugin.LogInfo("PrepareForTrackChange", $"Reset playback start time to: {_playbackStartTime}");
        }

        /// <summary>
        /// Set volume for group
        /// </summary>
        public void SetVolume(float volume)
        {
            _volume = volume;
            
            var message = new
            {
                type = "group/update",
                payload = new
                {
                    volume = volume
                }
            };
            
            BroadcastJsonMessage(message);
        }

        /// <summary>
        /// Set mute state for group
        /// </summary>
        public void SetMute(bool muted)
        {
            _muted = muted;
            
            var message = new
            {
                type = "group/update",
                payload = new
                {
                    muted = muted
                }
            };
            
            BroadcastJsonMessage(message);
        }

        #region Private Methods

        private async Task ListenForConnectionsAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    var context = await _httpListener!.GetContextAsync();
                    
                    if (context.Request.IsWebSocketRequest)
                    {
                        _ = HandleWebSocketConnectionAsync(context, cancellationToken);
                    }
                    else
                    {
                        // Return basic info for HTTP requests
                        var response = context.Response;
                        response.ContentType = "application/json";
                        var info = JsonConvert.SerializeObject(new
                        {
                            server = "MusicBee SendSpin",
                            version = "1.0",
                            protocol = "sendspin/1"
                        });
                        var buffer = Encoding.UTF8.GetBytes(info);
                        response.ContentLength64 = buffer.Length;
                        await response.OutputStream.WriteAsync(buffer, 0, buffer.Length, cancellationToken);
                        response.Close();
                    }
                }
                catch (HttpListenerException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    // Stop() disposed the listener while GetContextAsync was pending
                    break;
                }
                catch (Exception ex)
                {
                    Plugin.LogError("ListenForConnections", ex);
                }
            }
        }

        private async Task HandleWebSocketConnectionAsync(HttpListenerContext context, CancellationToken cancellationToken)
        {
            WebSocketContext? wsContext = null;
            
            try
            {
                wsContext = await context.AcceptWebSocketAsync(subProtocol: null);
                var webSocket = wsContext.WebSocket;
                
                // Wait for client/hello message
                var helloMessage = await ReceiveJsonMessageAsync(webSocket, cancellationToken);
                if (helloMessage == null || helloMessage["type"]?.ToString() != "client/hello")
                {
                    Plugin.LogInfo("WebSocket", "Invalid handshake - expected client/hello");
                    return;
                }
                
                var payload = helloMessage["payload"] as JObject;
                var clientId = payload?["client_id"]?.ToString() ?? Guid.NewGuid().ToString();
                var clientName = payload?["name"]?.ToString() ?? "Unknown Client";
                var supportedRoles = GetStringArray(payload, "supported_roles");
                
                // Create client wrapper
                var client = new ConnectedClient(clientId, clientName, webSocket, supportedRoles);
                
                // Send server/hello response
                await SendServerHelloAsync(client);
                
                // Add to connected clients
                _clients.TryAdd(clientId, client);
                
                // Raise connected event
                ClientConnected?.Invoke(this, new ClientEventArgs(clientId, clientName, supportedRoles));
                
                // Send initial state
                await SendInitialStateAsync(client);
                
                // Handle messages until disconnection
                await HandleClientMessagesAsync(client, cancellationToken);
            }
            catch (WebSocketException)
            {
                // Client disconnected
            }
            catch (Exception ex)
            {
                Plugin.LogError("HandleWebSocketConnection", ex);
            }
        }

        private async Task SendServerHelloAsync(ConnectedClient client)
        {
            var activeRoles = DetermineActiveRoles(client.SupportedRoles);
            client.SetActiveRoles(activeRoles);
            
            var message = new
            {
                type = "server/hello",
                payload = new
                {
                    server_id = _serverId,
                    name = _serverName,
                    version = 1,
                    active_roles = activeRoles,
                    connection_reason = "discovery"
                }
            };
            
            await client.SendJsonAsync(message);
        }

        private async Task SendInitialStateAsync(ConnectedClient client)
        {
            // Send group/update with current state
            var groupUpdate = new
            {
                type = "group/update",
                payload = new
                {
                    group_id = "default",
                    group_name = _serverName,
                    playback_state = _playbackState.ToString().ToLowerInvariant(),
                    volume = _volume,
                    muted = _muted
                }
            };
            await client.SendJsonAsync(groupUpdate);
            
            // Send stream/start if playing
            if (_playbackState == SendSpinPlaybackState.Playing && client.HasRole("player"))
            {
                var streamStart = new
                {
                    type = "stream/start",
                    payload = new
                    {
                        player = new
                        {
                            codec = _settings.AudioCodec.ToLowerInvariant(),
                            sample_rate = _settings.SampleRate,
                            channels = _settings.Channels,
                            bit_depth = _settings.BitDepth
                        }
                    }
                };
                await client.SendJsonAsync(streamStart);
            }
            
            // Send metadata if available
            if (_currentTrack != null && client.HasRole("metadata"))
            {
                var metadata = new
                {
                    type = "server/state",
                    payload = new
                    {
                        metadata = new
                        {
                            title = _currentTrack.Title,
                            artist = _currentTrack.Artist,
                            album = _currentTrack.Album,
                            duration = _currentTrack.Duration
                        }
                    }
                };
                await client.SendJsonAsync(metadata);
            }
        }

        private async Task HandleClientMessagesAsync(ConnectedClient client, CancellationToken cancellationToken)
        {
            try
            {
                while (client.WebSocket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
                {
                    var message = await ReceiveJsonMessageAsync(client.WebSocket, cancellationToken);
                    if (message == null) break;
                    
                    var type = message["type"]?.ToString();
                    
                    switch (type)
                    {
                        case "client/time":
                            await HandleClientTimeAsync(client, message);
                            break;
                            
                        case "client/state":
                            HandleClientState(client, message);
                            break;
                            
                        case "client/command":
                            await HandleClientCommandAsync(client, message);
                            break;
                            
                        case "client/goodbye":
                            await HandleClientGoodbyeAsync(client, message);
                            return;
                            
                        case "stream/request-format":
                            await HandleStreamRequestFormatAsync(client, message);
                            break;
                    }
                }
            }
            finally
            {
                // Remove client and raise disconnected event
                if (_clients.TryRemove(client.Id, out _))
                {
                    ClientDisconnected?.Invoke(this, new ClientEventArgs(client.Id, client.Name, client.SupportedRoles));
                }
            }
        }

        private async Task HandleClientTimeAsync(ConnectedClient client, JObject message)
        {
            var payload = message["payload"] as JObject;
            var clientTransmitted = payload?["client_transmitted"]?.Value<long>() ?? 0;
            
            var serverReceived = GetServerTimestamp();
            
            var response = new
            {
                type = "server/time",
                payload = new
                {
                    client_transmitted = clientTransmitted,
                    server_received = serverReceived,
                    server_transmitted = GetServerTimestamp()
                }
            };
            
            await client.SendJsonAsync(response);
        }

        private void HandleClientState(ConnectedClient client, JObject message)
        {
            var payload = message["payload"] as JObject;
            
            var state = payload?["state"]?.ToString();
            if (state != null)
            {
                client.SetSyncState(state);
            }
            
            var playerProperty = payload?["player"] as JObject;
            if (playerProperty != null)
            {
                var volume = playerProperty["volume"];
                if (volume != null)
                {
                    client.Volume = volume.Value<float>();
                }
                var muted = playerProperty["muted"];
                if (muted != null)
                {
                    client.Muted = muted.Value<bool>();
                }
            }
        }

        private async Task HandleClientCommandAsync(ConnectedClient client, JObject message)
        {
            var payload = message["payload"] as JObject;
            var controllerProperty = payload?["controller"] as JObject;
            
            if (controllerProperty != null)
            {
                var command = controllerProperty["command"]?.ToString();
                
                if (command != null)
                {
                    switch (command)
                    {
                        case "play":
                            Plugin.MbApiInterface.Player_PlayPause();
                            break;
                        case "pause":
                            Plugin.MbApiInterface.Player_PlayPause();
                            break;
                        case "stop":
                            Plugin.MbApiInterface.Player_Stop();
                            break;
                        case "next":
                            Plugin.MbApiInterface.Player_PlayNextTrack();
                            break;
                        case "previous":
                            Plugin.MbApiInterface.Player_PlayPreviousTrack();
                            break;
                    }
                }
                
                var volume = controllerProperty["volume"];
                if (volume != null)
                {
                    Plugin.MbApiInterface.Player_SetVolume(volume.Value<float>());
                }
                
                var muted = controllerProperty["muted"];
                if (muted != null)
                {
                    Plugin.MbApiInterface.Player_SetMute(muted.Value<bool>());
                }
                
                var seek = controllerProperty["seek"];
                if (seek != null)
                {
                    Plugin.MbApiInterface.Player_SetPosition(seek.Value<int>());
                }
            }
        }

        private async Task HandleClientGoodbyeAsync(ConnectedClient client, JObject message)
        {
            var payload = message["payload"] as JObject;
            var reason = payload?["reason"]?.ToString() ?? "unknown";
            
            Plugin.LogInfo("ClientGoodbye", $"Client {client.Name} disconnecting: {reason}");
            
            await client.WebSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Goodbye acknowledged", CancellationToken.None);
        }

        private async Task HandleStreamRequestFormatAsync(ConnectedClient client, JObject message)
        {
            // Client is requesting a different format - acknowledge with stream/start
            var streamStart = new
            {
                type = "stream/start",
                payload = new
                {
                    player = new
                    {
                        codec = _settings.AudioCodec.ToLowerInvariant(),
                        sample_rate = _settings.SampleRate,
                        channels = _settings.Channels,
                        bit_depth = _settings.BitDepth
                    }
                }
            };
            
            await client.SendJsonAsync(streamStart);
        }

        private void BroadcastJsonMessage(object message)
        {
            var json = JsonConvert.SerializeObject(message);
            var buffer = Encoding.UTF8.GetBytes(json);
            
            foreach (var client in _clients.Values)
            {
                try
                {
                    _ = client.WebSocket.SendAsync(
                        new ArraySegment<byte>(buffer),
                        WebSocketMessageType.Text,
                        true,
                        CancellationToken.None
                    );
                }
                catch
                {
                    // Client may have disconnected
                }
            }
        }

        private byte[] CreateAudioBinaryMessage(byte[] audioData, long timestamp, int sampleRate, int channels, int bitDepth)
        {
            // Binary message format for player role (Type 4)
            // Byte 0: Message type (4 for audio)
            // Bytes 1-8: Timestamp (big-endian int64 microseconds)
            // Bytes 9+: Audio data
            
            var message = new byte[1 + 8 + audioData.Length];
            message[0] = 4; // Player audio message type
            
            // Write timestamp as big-endian
            var timestampBytes = BitConverter.GetBytes(timestamp);
            if (BitConverter.IsLittleEndian)
            {
                Array.Reverse(timestampBytes);
            }
            Array.Copy(timestampBytes, 0, message, 1, 8);
            
            Array.Copy(audioData, 0, message, 9, audioData.Length);
            
            return message;
        }

        private byte[] CreateArtworkBinaryMessage(byte[] imageData, string source)
        {
            // Binary message format for artwork role (Type 8 for channel 0)
            // Byte 0: Message type (8 for artwork channel 0)
            // Bytes 1+: Image data
            
            var message = new byte[1 + imageData.Length];
            message[0] = 8; // Artwork channel 0
            
            Array.Copy(imageData, 0, message, 1, imageData.Length);
            
            return message;
        }

        private async Task<JObject?> ReceiveJsonMessageAsync(WebSocket webSocket, CancellationToken cancellationToken)
        {
            var buffer = new byte[8192];
            var messageBuffer = new System.IO.MemoryStream();
            
            try
            {
                WebSocketReceiveResult result;
                do
                {
                    result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
                    
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        return null;
                    }
                    
                    if (result.MessageType == WebSocketMessageType.Text)
                    {
                        await messageBuffer.WriteAsync(buffer, 0, result.Count, cancellationToken);
                    }
                }
                while (!result.EndOfMessage);
                
                messageBuffer.Position = 0;
                using (var reader = new System.IO.StreamReader(messageBuffer))
                {
                    var json = await reader.ReadToEndAsync();
                    return JObject.Parse(json);
                }
            }
            catch
            {
                return null;
            }
        }

        private string[] DetermineActiveRoles(string[] supportedRoles)
        {
            var activeRoles = new System.Collections.Generic.List<string>();
            
            foreach (var role in supportedRoles)
            {
                // Accept all standard roles
                if (role.StartsWith("player@") || role.StartsWith("controller@") ||
                    role.StartsWith("metadata@") || role.StartsWith("artwork@") ||
                    role.StartsWith("visualizer@"))
                {
                    activeRoles.Add(role);
                }
            }
            
            return activeRoles.ToArray();
        }

        private string[] GetStringArray(JObject? element, string propertyName)
        {
            if (element == null) return Array.Empty<string>();
            
            var property = element[propertyName] as JArray;
            if (property == null)
            {
                return Array.Empty<string>();
            }
            
            var list = new System.Collections.Generic.List<string>();
            foreach (var item in property)
            {
                var value = item?.ToString();
                if (value != null)
                {
                    list.Add(value);
                }
            }
            return list.ToArray();
        }

        private long GetServerTimestamp()
        {
            return _serverClock.ElapsedTicks / (TimeSpan.TicksPerMillisecond / 1000); // Convert to microseconds
        }

        private async Task StartMdnsAdvertisementAsync()
        {
            try
            {
                // mDNS advertisement for server-initiated connections
                // Service type: _sendspin-server._tcp.local.
                // This allows clients to discover and connect to the server
                
                // Note: Full mDNS implementation would use Makaretu.Dns.Multicast
                // For now, we just log that it would be enabled
                Plugin.LogInfo("mDNS", "mDNS advertisement enabled (implementation pending full SDK integration)");
            }
            catch (Exception ex)
            {
                Plugin.LogError("StartMdnsAdvertisement", ex);
            }
        }

        #endregion

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            
            StopAsync().GetAwaiter().GetResult();
            _cancellationTokenSource?.Dispose();
            
            GC.SuppressFinalize(this);
        }
    }

    /// <summary>
    /// Represents a connected SendSpin client
    /// </summary>
    internal class ConnectedClient
    {
        public string Id { get; }
        public string Name { get; }
        public WebSocket WebSocket { get; }
        public string[] SupportedRoles { get; }
        public string[] ActiveRoles { get; private set; }
        public string SyncState { get; private set; }
        public float Volume { get; set; } = 1.0f;
        public bool Muted { get; set; }
        
        private readonly SemaphoreSlim _sendLock = new SemaphoreSlim(1, 1);

        public ConnectedClient(string id, string name, WebSocket webSocket, string[] supportedRoles)
        {
            Id = id;
            Name = name;
            WebSocket = webSocket;
            SupportedRoles = supportedRoles;
            ActiveRoles = Array.Empty<string>();
            SyncState = "synchronized";
        }

        public void SetActiveRoles(string[] roles) => ActiveRoles = roles;
        public void SetSyncState(string? state) => SyncState = state ?? "synchronized";

        public bool HasRole(string rolePrefix)
        {
            foreach (var role in ActiveRoles)
            {
                if (role.StartsWith(rolePrefix + "@") || role == rolePrefix)
                {
                    return true;
                }
            }
            return false;
        }

        public async Task SendJsonAsync(object message)
        {
            var json = JsonConvert.SerializeObject(message);
            var buffer = Encoding.UTF8.GetBytes(json);
            
            await _sendLock.WaitAsync();
            try
            {
                if (WebSocket.State == WebSocketState.Open)
                {
                    await WebSocket.SendAsync(
                        new ArraySegment<byte>(buffer),
                        WebSocketMessageType.Text,
                        true,
                        CancellationToken.None
                    );
                }
            }
            finally
            {
                _sendLock.Release();
            }
        }

        public async Task SendBinaryAsync(byte[] data)
        {
            await _sendLock.WaitAsync();
            try
            {
                if (WebSocket.State == WebSocketState.Open)
                {
                    await WebSocket.SendAsync(
                        new ArraySegment<byte>(data),
                        WebSocketMessageType.Binary,
                        true,
                        CancellationToken.None
                    );
                }
            }
            finally
            {
                _sendLock.Release();
            }
        }

        public async Task DisconnectAsync(string reason)
        {
            if (WebSocket.State == WebSocketState.Open)
            {
                await WebSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, reason, CancellationToken.None);
            }
        }
    }

    public class ClientEventArgs : EventArgs
    {
        public string ClientId { get; }
        public string ClientName { get; }
        public string[] Roles { get; }

        public ClientEventArgs(string clientId, string clientName, string[] roles)
        {
            ClientId = clientId;
            ClientName = clientName;
            Roles = roles;
        }
    }

    public enum SendSpinPlaybackState
    {
        Stopped,
        Playing,
        Paused
    }
}
