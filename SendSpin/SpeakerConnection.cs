using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MusicBeePlugin.SendSpin
{
    /// <summary>
    /// Represents a connection to a SendSpin speaker/client.
    /// This is used for server-initiated connections where WE connect TO the speaker.
    /// </summary>
    public class SpeakerConnection : IDisposable
    {
        private readonly DiscoveredSpeaker _speaker;
        private readonly Action<string> _logger;
        private ClientWebSocket? _webSocket;
        private CancellationTokenSource? _cts;
        private bool _disposed;
        private bool _handshakeComplete;
        
        // Client capabilities discovered during handshake
        public string ClientId { get; private set; } = string.Empty;
        public string ClientName { get; private set; } = string.Empty;
        public List<string> SupportedRoles { get; private set; } = new();
        public List<string> ActiveRoles { get; private set; } = new();
        public int BufferDurationMs { get; private set; } = 2000;
        
        public bool IsConnected => _webSocket?.State == WebSocketState.Open && _handshakeComplete;
        public DiscoveredSpeaker Speaker => _speaker;

        /// <summary>
        /// Event raised when the connection is established and handshake complete.
        /// </summary>
        public event EventHandler? Connected;
        
        /// <summary>
        /// Event raised when the connection is closed.
        /// </summary>
        public event EventHandler? Disconnected;
        
        /// <summary>
        /// Event raised when a message is received from the speaker.
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
                
                var uri = new Uri(_speaker.WebSocketUrl);
                _logger($"Connecting to speaker: {_speaker.Name} at {uri}");
                
                await _webSocket.ConnectAsync(uri, _cts.Token);
                _logger($"WebSocket connected to {_speaker.Name}");
                
                // Start receiving messages
                _ = ReceiveLoopAsync();
                
                // Wait for client/hello
                var helloReceived = await WaitForClientHelloAsync(_cts.Token);
                
                if (!helloReceived)
                {
                    _logger($"Did not receive client/hello from {_speaker.Name}");
                    await DisconnectAsync();
                    return false;
                }
                
                // Send server/hello
                await SendServerHelloAsync(serverId, serverName);
                
                _handshakeComplete = true;
                _logger($"Handshake complete with {_speaker.Name} (client: {ClientName})");
                
                Connected?.Invoke(this, EventArgs.Empty);
                return true;
            }
            catch (Exception ex)
            {
                _logger($"Failed to connect to {_speaker.Name}: {ex.Message}");
                await DisconnectAsync();
                return false;
            }
        }

        /// <summary>
        /// Disconnect from the speaker.
        /// </summary>
        public async Task DisconnectAsync(string reason = "shutdown")
        {
            if (_webSocket == null)
                return;

            try
            {
                if (_webSocket.State == WebSocketState.Open)
                {
                    // Send server/goodbye
                    var goodbye = new JObject
                    {
                        ["type"] = "server/goodbye",
                        ["payload"] = new JObject
                        {
                            ["reason"] = reason
                        }
                    };
                    await SendMessageAsync(goodbye);
                    
                    await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, reason, CancellationToken.None);
                }
            }
            catch (Exception ex)
            {
                _logger($"Error during disconnect from {_speaker.Name}: {ex.Message}");
            }
            finally
            {
                _cts?.Cancel();
                _webSocket?.Dispose();
                _webSocket = null;
                _handshakeComplete = false;
                Disconnected?.Invoke(this, EventArgs.Empty);
            }
        }

        /// <summary>
        /// Send a JSON message to the speaker.
        /// </summary>
        public async Task SendMessageAsync(JObject message)
        {
            if (_webSocket?.State != WebSocketState.Open)
                return;

            try
            {
                var json = message.ToString(Formatting.None);
                var bytes = Encoding.UTF8.GetBytes(json);
                await _webSocket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, _cts?.Token ?? CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger($"Error sending message to {_speaker.Name}: {ex.Message}");
            }
        }

        /// <summary>
        /// Send binary data to the speaker.
        /// </summary>
        public async Task SendBinaryAsync(byte[] data)
        {
            if (_webSocket?.State != WebSocketState.Open)
                return;

            try
            {
                await _webSocket.SendAsync(new ArraySegment<byte>(data), WebSocketMessageType.Binary, true, _cts?.Token ?? CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger($"Error sending binary to {_speaker.Name}: {ex.Message}");
            }
        }

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
            
            var hello = new JObject
            {
                ["type"] = "server/hello",
                ["payload"] = new JObject
                {
                    ["server_id"] = serverId,
                    ["server_name"] = serverName,
                    ["protocol_version"] = 1,
                    ["connection_reason"] = "playback",
                    ["active_roles"] = new JArray(activatedRoles),
                    ["group"] = new JObject
                    {
                        ["group_id"] = "default",
                        ["members"] = new JArray(ClientId)
                    }
                }
            };
            
            await SendMessageAsync(hello);
            _logger($"Sent server/hello to {_speaker.Name}, activated roles: {string.Join(", ", activatedRoles)}");
        }

        private async Task ReceiveLoopAsync()
        {
            var buffer = new byte[8192];
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
                    
                    messageBuffer.AddRange(buffer.Take(result.Count));
                    
                    if (result.EndOfMessage)
                    {
                        if (result.MessageType == WebSocketMessageType.Text)
                        {
                            var json = Encoding.UTF8.GetString(messageBuffer.ToArray());
                            ProcessMessage(json);
                        }
                        
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
                if (_handshakeComplete)
                {
                    Disconnected?.Invoke(this, EventArgs.Empty);
                }
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
                        _logger($"Speaker {_speaker.Name} said goodbye: {message["payload"]?["reason"]}");
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
            ClientName = payload["client_name"]?.ToString() ?? _speaker.Name;
            
            var roles = payload["supported_roles"]?.ToObject<List<string>>();
            if (roles != null)
                SupportedRoles = roles;
            
            var player = payload["player"];
            if (player != null)
            {
                BufferDurationMs = player["buffer_duration_ms"]?.Value<int>() ?? 2000;
            }
            
            _logger($"Client hello from {ClientName}: roles={string.Join(",", SupportedRoles)}, buffer={BufferDurationMs}ms");
        }

        private void ProcessClientTime(JObject message)
        {
            // Respond with server/time
            var payload = message["payload"];
            if (payload == null) return;
            
            var clientTime = payload["client_time"]?.Value<long>() ?? 0;
            var serverTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000; // Microseconds
            
            var response = new JObject
            {
                ["type"] = "server/time",
                ["payload"] = new JObject
                {
                    ["client_time"] = clientTime,
                    ["server_time"] = serverTime
                }
            };
            
            _ = SendMessageAsync(response);
        }

        private void ProcessClientState(JObject message)
        {
            var payload = message["payload"];
            if (payload == null) return;
            
            var state = payload["state"]?.ToString();
            _logger($"Speaker {_speaker.Name} state: {state}");
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
        public async Task BroadcastMessageAsync(JObject message)
        {
            var tasks = _connections.Values
                .Where(c => c.IsConnected)
                .Select(c => c.SendMessageAsync(message));
            
            await Task.WhenAll(tasks);
        }

        /// <summary>
        /// Send binary data to all connected speakers.
        /// </summary>
        public async Task BroadcastBinaryAsync(byte[] data)
        {
            var tasks = _connections.Values
                .Where(c => c.IsConnected)
                .Select(c => c.SendBinaryAsync(data));
            
            await Task.WhenAll(tasks);
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
