using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using MusicBeePlugin.SendSpin;

namespace MusicBeePlugin
{
    /// <summary>
    /// MusicBee SendSpin Plugin - Streams audio to SendSpin-compatible speakers
    /// </summary>
    public partial class Plugin
    {
        private static MusicBeeApiInterface _mbApiInterface;
        private static readonly PluginInfo _about = new PluginInfo();
        
        private static SendSpinServer? _server;
        private static AudioCaptureService? _audioCaptureService;
        private static DirectDecodeService? _directDecodeService;
        private static GroupManager? _groupManager;
        private static PluginSettings? _settings;
        
        // Server-initiated connection components
        private static SpeakerDiscoveryService? _discoveryService;
        private static SpeakerConnectionManager? _connectionManager;
        // Relative timestamp (µs from stream start) of the last decoded chunk in the current session
        private static long _lastChunkRelTs;
        
        private static string? _settingsPath;
        private static bool _isInitialized;
        
        // Synchronization
        private static readonly object _syncLock = new object();
        
        /// <summary>
        /// MusicBee API interface accessor for other classes
        /// </summary>
        public static MusicBeeApiInterface MbApiInterface => _mbApiInterface;
        
        /// <summary>
        /// Current plugin settings
        /// </summary>
        public static PluginSettings? Settings => _settings;
        
        /// <summary>
        /// SendSpin server instance
        /// </summary>
        public static SendSpinServer? Server => _server;
        
        /// <summary>
        /// Group manager instance
        /// </summary>
        public static GroupManager? GroupManager => _groupManager;

        /// <summary>
        /// Plugin initialization - called when MusicBee loads the plugin
        /// </summary>
        public PluginInfo Initialise(IntPtr apiInterfacePtr)
        {
            _mbApiInterface = new MusicBeeApiInterface();
            _mbApiInterface.Initialise(apiInterfacePtr);
            
            _about.PluginInfoVersion = PluginInfoVersion;
            _about.Name = "SendSpin";
            _about.Description = "Stream audio to SendSpin-compatible wireless speakers with synchronized multi-room playback";
            _about.Author = "SendSpin Community";
            _about.TargetApplication = "";
            _about.Type = PluginType.General; // Use General type for audio streaming
            _about.VersionMajor = 1;
            _about.VersionMinor = 0;
            _about.Revision = 0;
            _about.MinInterfaceVersion = MinInterfaceVersion;
            _about.MinApiRevision = MinApiRevision;
            _about.ReceiveNotifications = ReceiveNotificationFlags.PlayerEvents;
            _about.ConfigurationPanelHeight = 0; // Use separate settings dialog
            
            return _about;
        }

        /// <summary>
        /// Configure plugin - opens settings dialog
        /// </summary>
        public bool Configure(IntPtr panelHandle)
        {
            try
            {
                // Ensure discovery service is running for the settings dialog
                EnsureDiscoveryServiceRunning();
                
                using (var dialog = new SettingsDialog(_settings ?? new PluginSettings(), _discoveryService))
                {
                    var result = dialog.ShowDialog(Form.FromHandle(_mbApiInterface.MB_GetWindowHandle()));
                    if (result == DialogResult.OK)
                    {
                        _settings = dialog.Settings;
                        SaveSettings();
                        
                        // Apply settings changes
                        ApplySettings();
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                LogError("Configure", ex);
            }
            
            return false;
        }
        
        /// <summary>
        /// Logger adapter that wraps our two-parameter LogInfo for single-parameter Action.
        /// </summary>
        private static void LogDiscovery(string message) => LogInfo("Discovery", message);
        
        /// <summary>
        /// Ensure the speaker discovery service is running.
        /// </summary>
        private static void EnsureDiscoveryServiceRunning()
        {
            if (_discoveryService == null)
            {
                _discoveryService = new SpeakerDiscoveryService(LogDiscovery);
                _discoveryService.Start();
            }
        }

        /// <summary>
        /// Save current settings
        /// </summary>
        public void SaveSettings()
        {
            try
            {
                if (_settings != null && _settingsPath != null)
                {
                    _settings.Save(_settingsPath);
                }
            }
            catch (Exception ex)
            {
                LogError("SaveSettings", ex);
            }
        }

        /// <summary>
        /// Plugin close handler
        /// </summary>
        public void Close(PluginCloseReason reason)
        {
            try
            {
                LogInfo("Close", $"Closing plugin. Reason: {reason}");
                
                // Stop the server on a background thread to avoid blocking
                var closeThread = new Thread(ExecuteClose)
                {
                    IsBackground = true,
                    Name = "SendSpin-Close"
                };
                closeThread.Start();
            }
            catch (Exception ex)
            {
                LogError("Close", ex);
            }
        }

        private void ExecuteClose()
        {
            try
            {
                lock (_syncLock)
                {
                    _audioCaptureService?.Stop();
                    _audioCaptureService?.Dispose();
                    _audioCaptureService = null;
                    
                    _directDecodeService?.Stop();
                    _directDecodeService?.Dispose();
                    _directDecodeService = null;
                    
                    // Stop server-initiated connections
                    _connectionManager?.DisconnectAllAsync().GetAwaiter().GetResult();
                    _connectionManager?.Dispose();
                    _connectionManager = null;
                    
                    // Stop discovery
                    _discoveryService?.Stop();
                    _discoveryService?.Dispose();
                    _discoveryService = null;
                    
                    _server?.StopAsync().GetAwaiter().GetResult();
                    _server?.Dispose();
                    _server = null;
                    
                    _groupManager = null;
                    _isInitialized = false;
                }
            }
            catch (Exception ex)
            {
                LogError("ExecuteClose", ex);
            }
        }

        /// <summary>
        /// Uninstall handler
        /// </summary>
        public void Uninstall()
        {
            try
            {
                // Clean up settings file if desired
                if (!string.IsNullOrEmpty(_settingsPath) && File.Exists(_settingsPath))
                {
                    // Optionally delete settings on uninstall
                    // File.Delete(_settingsPath);
                }
            }
            catch (Exception ex)
            {
                LogError("Uninstall", ex);
            }
        }

        /// <summary>
        /// Receive notifications from MusicBee
        /// </summary>
        public void ReceiveNotification(string sourceFileUrl, NotificationType type)
        {
            try
            {
                switch (type)
                {
                    case NotificationType.PluginStartup:
                        HandlePluginStartup();
                        break;
                        
                    case NotificationType.TrackChanged:
                        HandleTrackChanged(sourceFileUrl);
                        break;
                        
                    case NotificationType.PlayStateChanged:
                        HandlePlayStateChanged();
                        break;
                        
                    case NotificationType.VolumeLevelChanged:
                        HandleVolumeChanged();
                        break;
                        
                    case NotificationType.VolumeMuteChanged:
                        HandleMuteChanged();
                        break;
                        
                    case NotificationType.TrackChanging:
                        HandleTrackChanging(sourceFileUrl);
                        break;
                        
                    case NotificationType.ShutdownStarted:
                        HandleShutdown();
                        break;
                }
            }
            catch (Exception ex)
            {
                LogError($"ReceiveNotification({type})", ex);
            }
        }

        #region Event Handlers

        private void HandlePluginStartup()
        {
            try
            {
                LogInfo("PluginStartup", "Initializing SendSpin plugin...");
                
                // Get settings path
                _settingsPath = Path.Combine(
                    _mbApiInterface.Setting_GetPersistentStoragePath(),
                    "SendSpinSettings.json"
                );
                
                // Load settings
                _settings = PluginSettings.Load(_settingsPath);
                
                // Initialize components
                InitializeComponents();
                
                // Add menu items
                _mbApiInterface.MB_AddMenuItem(
                    "mnuTools/SendSpin Settings",
                    "SendSpin: Open Settings",
                    (sender, args) => Configure(IntPtr.Zero)
                );
                
                _mbApiInterface.MB_AddMenuItem(
                    "mnuTools/SendSpin Speakers",
                    "SendSpin: Manage Speakers",
                    (sender, args) => ShowSpeakerManager()
                );
                
                LogInfo("PluginStartup", "SendSpin plugin initialized successfully");
            }
            catch (Exception ex)
            {
                LogError("HandlePluginStartup", ex);
            }
        }

        private void HandleTrackChanged(string sourceFileUrl)
        {
            if (!_isInitialized || _server == null) return;
            
            try
            {
                var trackInfo = GetCurrentTrackInfo();
                _server.UpdateMetadata(trackInfo);
                
                // Update artwork
                var artwork = _mbApiInterface.NowPlaying_GetArtwork();
                if (!string.IsNullOrEmpty(artwork))
                {
                    _server.UpdateArtwork(artwork);
                }
                
                LogInfo("TrackChanged", $"Now playing: {trackInfo.Title} - {trackInfo.Artist}");
                
                // If currently playing, restart audio capture for the new track
                var playState = _mbApiInterface.Player_GetPlayState();
                if (playState == PlayState.Playing)
                {
                    // Small delay to let MusicBee settle on the new track
                    Thread.Sleep(50);
                    StartAudioCapture();
                    
                    // Server-initiated: re-anchor + stream/start (TrackChanging sent stream/clear)
                    if (_connectionManager != null && _settings?.ConnectionMode == ConnectionMode.ServerInitiated)
                    {
                        BroadcastStreamStartToSpeakers();
                    }
                }
            }
            catch (Exception ex)
            {
                LogError("HandleTrackChanged", ex);
            }
        }

        private void HandleTrackChanging(string sourceFileUrl)
        {
            if (!_isInitialized) return;
            
            try
            {
                LogInfo("TrackChanging", $"Track changing to: {sourceFileUrl}");
                
                // Stop current decode (but keep connection alive)
                _directDecodeService?.Stop();
                _audioCaptureService?.PrepareForTrackChange();
                _lastChunkRelTs = 0;
                
                // Tell server to prepare for track change (sends stream/clear)
                _server?.PrepareForTrackChange();
                
                // Server-initiated: drop buffered audio on all speakers (seek/track change)
                if (_connectionManager != null && _settings?.ConnectionMode == ConnectionMode.ServerInitiated)
                {
                    _connectionManager.BroadcastStreamClear();
                }
            }
            catch (Exception ex)
            {
                LogError("HandleTrackChanging", ex);
            }
        }

        private void HandlePlayStateChanged()
        {
            if (!_isInitialized) return;
            
            // Need either server (client-initiated) or connection manager (server-initiated)
            var hasClientInitiated = _server != null && _settings?.ConnectionMode == ConnectionMode.ClientInitiated;
            var hasServerInitiated = _connectionManager != null && _settings?.ConnectionMode == ConnectionMode.ServerInitiated;
            
            if (!hasClientInitiated && !hasServerInitiated) return;
            
            try
            {
                var playState = _mbApiInterface.Player_GetPlayState();
                
                switch (playState)
                {
                    case PlayState.Playing:
                        // Start audio capture (works for both modes)
                        StartAudioCapture();
                        
                        // Client-initiated: notify server
                        if (hasClientInitiated && _server != null)
                        {
                            _server.SetPlaybackState(SendSpinPlaybackState.Playing);
                        }
                        
                        // Server-initiated: send stream/start to all connected speakers
                        if (hasServerInitiated && _connectionManager != null)
                        {
                            BroadcastStreamStartToSpeakers();
                        }
                        break;
                        
                    case PlayState.Paused:
                        StopAudioCapture();
                        if (hasClientInitiated && _server != null)
                        {
                            _server.SetPlaybackState(SendSpinPlaybackState.Paused);
                        }
                        if (hasServerInitiated && _connectionManager != null)
                        {
                            BroadcastStreamEndToSpeakers();
                        }
                        break;
                        
                    case PlayState.Stopped:
                        StopAudioCapture();
                        _lastChunkRelTs = 0;
                        if (hasClientInitiated && _server != null)
                        {
                            _server.SetPlaybackState(SendSpinPlaybackState.Stopped);
                        }
                        if (hasServerInitiated && _connectionManager != null)
                        {
                            BroadcastStreamEndToSpeakers();
                        }
                        break;
                }
                
                LogInfo("PlayStateChanged", $"State: {playState}");
            }
            catch (Exception ex)
            {
                LogError("HandlePlayStateChanged", ex);
            }
        }

        private void HandleVolumeChanged()
        {
            if (!_isInitialized || _server == null) return;
            
            try
            {
                var volume = _mbApiInterface.Player_GetVolume();
                _server.SetVolume(volume);
            }
            catch (Exception ex)
            {
                LogError("HandleVolumeChanged", ex);
            }
        }

        private void HandleMuteChanged()
        {
            if (!_isInitialized || _server == null) return;
            
            try
            {
                var muted = _mbApiInterface.Player_GetMute();
                _server.SetMute(muted);
            }
            catch (Exception ex)
            {
                LogError("HandleMuteChanged", ex);
            }
        }

        private void HandleShutdown()
        {
            try
            {
                LogInfo("Shutdown", "MusicBee shutting down...");
                Close(PluginCloseReason.MusicBeeClosing);
            }
            catch (Exception ex)
            {
                LogError("HandleShutdown", ex);
            }
        }

        #endregion

        #region Private Methods

        private void InitializeComponents()
        {
            try
            {
                lock (_syncLock)
                {
                    if (_settings == null)
                    {
                        _settings = new PluginSettings();
                    }
                    
                    // Initialize Group Manager
                    _groupManager = new GroupManager();
                    
                    // Initialize SendSpin Server
                    _server = new SendSpinServer(_settings);
                    _server.ClientConnected += OnClientConnected;
                    _server.ClientDisconnected += OnClientDisconnected;
                    
                    // Initialize Audio Capture Service (original approach - kept for reference)
                    _audioCaptureService = new AudioCaptureService(_settings);
                    _audioCaptureService.AudioDataAvailable += OnAudioDataAvailable;
                    
                    // Initialize Direct Decode Service (alternative approach - decodes files directly)
                    _directDecodeService = new DirectDecodeService(_settings);
                    _directDecodeService.AudioDataAvailable += OnAudioDataAvailable;
                    
                    // Start the server if enabled
                    if (_settings.EnableServer)
                    {
                        _server.StartAsync().GetAwaiter().GetResult();
                        LogInfo("InitializeComponents", $"SendSpin server started on port {_settings.ServerPort}");
                    }
                    
                    _isInitialized = true;
                }
            }
            catch (Exception ex)
            {
                LogError("InitializeComponents", ex);
                _isInitialized = false;
            }
        }

        private void ApplySettings()
        {
            try
            {
                lock (_syncLock)
                {
                    if (_settings == null) return;
                    
                    // Handle connection mode changes
                    if (_settings.ConnectionMode == ConnectionMode.ServerInitiated)
                    {
                        // Stop the client-initiated server if running (do this on a background thread to avoid deadlock)
                        if (_server != null && _server.IsRunning)
                        {
                            LogInfo("ApplySettings", "Switching to server-initiated mode, stopping server");
                            // Use Task.Run to avoid deadlock when called from UI thread
                            Task.Run(async () => await _server.StopAsync()).Wait(TimeSpan.FromSeconds(5));
                        }
                        
                        // Start discovery and connect to selected speakers
                        EnsureDiscoveryServiceRunning();
                        ConnectToSelectedSpeakers();
                    }
                    else
                    {
                        // Client-initiated mode
                        // Stop any server-initiated connections (do this on a background thread to avoid deadlock)
                        if (_connectionManager != null)
                        {
                            LogInfo("ApplySettings", "Switching to client-initiated mode, disconnecting speakers");
                            // Use Task.Run to avoid deadlock when called from UI thread
                            Task.Run(async () => await _connectionManager.DisconnectAllAsync()).Wait(TimeSpan.FromSeconds(5));
                        }
                        
                        // Restart server if settings changed
                        if (_server != null)
                        {
                            _server.ApplySettings(_settings);
                        }
                    }
                    
                    if (_audioCaptureService != null)
                    {
                        _audioCaptureService.ApplySettings(_settings);
                    }
                }
            }
            catch (Exception ex)
            {
                LogError("ApplySettings", ex);
            }
        }
        
        /// <summary>
        /// Logger adapter for connection manager.
        /// </summary>
        private static void LogConnection(string message) => LogInfo("Connection", message);
        
        /// <summary>
        /// Connect to speakers selected in settings (for server-initiated mode).
        /// </summary>
        private static void ConnectToSelectedSpeakers()
        {
            if (_settings == null || _discoveryService == null)
                return;
            
            if (_settings.ConnectionMode != ConnectionMode.ServerInitiated)
                return;
            
            // Create connection manager if needed
            if (_connectionManager == null)
            {
                var serverId = Guid.NewGuid().ToString();
                _connectionManager = new SpeakerConnectionManager(serverId, _settings.ServerName, LogConnection);
                
                // Wire up events for audio streaming
                _connectionManager.SpeakerConnected += OnSpeakerConnected;
                _connectionManager.SpeakerDisconnected += OnSpeakerDisconnected;
            }
            
            // Connect to selected speakers
            Task.Run(async () =>
            {
                foreach (var speakerId in _settings.SelectedSpeakerIds)
                {
                    var speaker = _discoveryService.GetSpeaker(speakerId);
                    if (speaker != null && !_connectionManager.Connections.Any(c => c.Speaker.Id == speakerId))
                    {
                        LogInfo("ConnectToSelectedSpeakers", $"Connecting to {speaker.Name}");
                        var success = await _connectionManager.ConnectToSpeakerAsync(speaker);
                        LogInfo("ConnectToSelectedSpeakers", $"Connection to {speaker.Name}: {(success ? "success" : "failed")}");
                    }
                }
            });
        }
        
        private static void OnSpeakerConnected(object? sender, SpeakerConnection connection)
        {
            LogInfo("SpeakerConnected", $"Speaker connected: {connection.Speaker.Name}");
            _discoveryService?.SetSpeakerConnected(connection.Speaker.Id, true);
            
            // If music is playing, send stream/start to this speaker
            if (_mbApiInterface.Player_GetPlayState() == PlayState.Playing)
            {
                var fileUrl = _mbApiInterface.NowPlaying_GetFileUrl();
                var position = string.IsNullOrEmpty(fileUrl) ? 0.0 : _mbApiInterface.Player_GetPosition() / 1000.0;
                var decoderRunning = _directDecodeService != null && _directDecodeService.IsCapturing;
                
                long anchorUs;
                if (decoderRunning && _lastChunkRelTs > 0)
                {
                    // Mid-join: map the decoder's current in-flight timestamp
                    // to ~300 ms from now so this speaker is in sync with the
                    // audio the others are already hearing.
                    anchorUs = ServerClock.NowUs() + 300_000 - _lastChunkRelTs;
                }
                else
                {
                    // Decoder will start (fresh) at `position`; its first chunk
                    // is timestamped position*1e6 µs — map that to ~600 ms from now.
                    anchorUs = ServerClock.NowUs() + 600_000 - (long)(position * 1_000_000);
                }
                
                SendStreamStartToSpeaker(connection, anchorUs);
                
                // Make sure audio capture is running
                // Use Task.Run to avoid blocking the connection thread
                Task.Run(() =>
                {
                    if (_directDecodeService != null && !_directDecodeService.IsCapturing)
                    {
                        if (!string.IsNullOrEmpty(fileUrl))
                        {
                            LogInfo("OnSpeakerConnected", $"Starting audio capture for {fileUrl}");
                            _directDecodeService.Start(fileUrl, position);
                        }
                    }
                });
            }
        }
        
        private static void OnSpeakerDisconnected(object? sender, SpeakerConnection connection)
        {
            LogInfo("SpeakerDisconnected", $"Speaker disconnected: {connection.Speaker.Name}");
            _discoveryService?.SetSpeakerConnected(connection.Speaker.Id, false);
        }
        
        private static void SendStreamStartToSpeaker(SpeakerConnection connection, long anchorUs)
        {
            // SetStreamAnchor clears any stale queued chunks and (re)schedules
            // the decoder's relative timestamps in the server clock domain.
            connection.SetStreamAnchor(anchorUs);
            connection.SendStreamStart("opus", 48000, 2, 16);
            
            LogInfo("SendStreamStart", $"Sending stream/start (anchor {anchorUs}) to {connection.Speaker.Name}");
        }
        
        private static void BroadcastStreamStartToSpeakers()
        {
            if (_connectionManager == null) return;
            
            // The decoder (re)starts at the current playback position; its first
            // chunk is timestamped position*1e6 µs. Anchor the stream so that
            // timestamp maps to ~600 ms from now.
            var positionSeconds = _mbApiInterface.Player_GetPosition() / 1000.0;
            long anchor = ServerClock.NowUs() + 600_000 - (long)(positionSeconds * 1_000_000);
            
            LogInfo("BroadcastStreamStart", $"Re-anchoring stream (pos {positionSeconds:F2}s) to {_connectionManager.Connections.Count} speakers");
            foreach (var conn in _connectionManager.Connections)
            {
                conn.SetStreamAnchor(anchor);
                conn.SendStreamStart("opus", 48000, 2, 16);
            }
        }
        
        private static void BroadcastPlaybackStateToSpeakers(string state)
        {
            if (_connectionManager == null) return;
            
            // "playback/state" is not a valid SendSpin message — stream/end
            // tells the speakers to stop (valid for pause).
            _connectionManager.BroadcastStreamEnd();
        }
        
        private static void BroadcastStreamEndToSpeakers()
        {
            if (_connectionManager == null) return;
            
            var streamEnd = new Newtonsoft.Json.Linq.JObject
            {
                ["type"] = "stream/end",
                ["payload"] = new Newtonsoft.Json.Linq.JObject { }
            };
            
            LogInfo("BroadcastStreamEnd", "Broadcasting stream/end to speakers");
            _connectionManager.BroadcastMessageAsync(streamEnd).ConfigureAwait(false);
        }

        private void StartAudioCapture()
        {
            try
            {
                if (!_isInitialized) return;
                
                var fileUrl = _mbApiInterface.NowPlaying_GetFileUrl();
                if (string.IsNullOrEmpty(fileUrl))
                {
                    LogInfo("StartAudioCapture", "No file URL available");
                    return;
                }
                
                LogInfo("StartAudioCapture", $"File URL: {fileUrl}");
                
                // Get current playback position for sync
                var position = _mbApiInterface.Player_GetPosition();
                var positionSeconds = position / 1000.0;
                
                // Try direct decode approach first (decodes file directly using BASS)
                if (_directDecodeService != null && _settings?.UseDirectDecode == true)
                {
                    LogInfo("StartAudioCapture", $"Using direct decode at position {positionSeconds}s");
                    _directDecodeService.Start(fileUrl, positionSeconds);
                    return;
                }
                
                // Fall back to Player_OpenStreamHandle approach
                if (_audioCaptureService != null)
                {
                    LogInfo("StartAudioCapture", "Using Player_OpenStreamHandle approach");
                    
                    var streamHandle = _mbApiInterface.Player_OpenStreamHandle(
                        fileUrl,
                        useMusicBeeSettings: true,
                        enableDsp: _settings?.EnableDsp ?? true,
                        gainType: _settings?.ReplayGainMode ?? ReplayGainMode.Smart
                    );
                    
                    LogInfo("StartAudioCapture", $"Got stream handle: {streamHandle}");
                    
                    if (streamHandle != 0)
                    {
                        _audioCaptureService.Start(streamHandle);
                    }
                    else
                    {
                        LogInfo("StartAudioCapture", "Player_OpenStreamHandle returned 0 - stream not available");
                    }
                }
            }
            catch (Exception ex)
            {
                LogError("StartAudioCapture", ex);
            }
        }

        private void StopAudioCapture()
        {
            try
            {
                _audioCaptureService?.Stop();
                _directDecodeService?.Stop();
            }
            catch (Exception ex)
            {
                LogError("StopAudioCapture", ex);
            }
        }

        private void OnAudioDataAvailable(object? sender, AudioDataEventArgs e)
        {
            try
            {
                // Send to client-initiated server
                _server?.SendAudioData(e.Data, e.Timestamp, e.SampleRate, e.Channels, e.BitDepth);
                
                // Also send to server-initiated connections (paced delivery —
                // each connection releases a chunk ~500 ms before its play time,
                // so the speaker always has its 25-chunk playback buffer).
                if (_connectionManager != null && _settings?.ConnectionMode == ConnectionMode.ServerInitiated)
                {
                    _lastChunkRelTs = e.Timestamp;
                    foreach (var conn in _connectionManager.Connections)
                    {
                        conn.EnqueueAudioChunk(e.Timestamp, e.Data);
                    }
                }
            }
            catch (Exception ex)
            {
                LogError("OnAudioDataAvailable", ex);
            }
        }
        

        private bool _wasLocallyMuted = false;
        private bool _localMuteApplied = false;

        private void OnClientConnected(object? sender, ClientEventArgs e)
        {
            LogInfo("ClientConnected", $"Client connected: {e.ClientName} ({e.ClientId})");
            _groupManager?.AddClient(e.ClientId, e.ClientName);
            
            // Check if this is a player client and mute local playback if enabled
            if (_settings?.MuteLocalPlayback == true && HasPlayerRole(e.Roles))
            {
                if (!_localMuteApplied)
                {
                    // Save current mute state before we override it
                    _wasLocallyMuted = _mbApiInterface.Player_GetMute();
                    
                    if (!_wasLocallyMuted)
                    {
                        _mbApiInterface.Player_SetMute(true);
                        _localMuteApplied = true;
                        LogInfo("ClientConnected", "Muted local playback - audio streaming to speakers");
                    }
                }
            }
        }

        private void OnClientDisconnected(object? sender, ClientEventArgs e)
        {
            LogInfo("ClientDisconnected", $"Client disconnected: {e.ClientName} ({e.ClientId})");
            _groupManager?.RemoveClient(e.ClientId);
            
            // If no more player clients, restore local playback
            if (_settings?.MuteLocalPlayback == true && _localMuteApplied)
            {
                // Check if any remaining clients have player role
                if (_server != null && !HasAnyPlayerClients())
                {
                    // Restore original mute state
                    _mbApiInterface.Player_SetMute(_wasLocallyMuted);
                    _localMuteApplied = false;
                    LogInfo("ClientDisconnected", "Restored local playback - no speakers connected");
                }
            }
        }

        private bool HasPlayerRole(string[] roles)
        {
            if (roles == null) return false;
            foreach (var role in roles)
            {
                if (role.StartsWith("player@") || role == "player")
                {
                    return true;
                }
            }
            return false;
        }

        private bool HasAnyPlayerClients()
        {
            // Check the server's client count - if 0, no players
            return _server != null && _server.ClientCount > 0;
        }

        private void ShowSpeakerManager()
        {
            try
            {
                using (var dialog = new SpeakerManagerDialog(_server, _groupManager))
                {
                    dialog.ShowDialog(Form.FromHandle(_mbApiInterface.MB_GetWindowHandle()));
                }
            }
            catch (Exception ex)
            {
                LogError("ShowSpeakerManager", ex);
            }
        }

        private TrackInfo GetCurrentTrackInfo()
        {
            return new TrackInfo
            {
                Title = _mbApiInterface.NowPlaying_GetFileTag(MetaDataType.TrackTitle),
                Artist = _mbApiInterface.NowPlaying_GetFileTag(MetaDataType.Artist),
                Album = _mbApiInterface.NowPlaying_GetFileTag(MetaDataType.Album),
                AlbumArtist = _mbApiInterface.NowPlaying_GetFileTag(MetaDataType.AlbumArtist),
                Duration = _mbApiInterface.NowPlaying_GetDuration(),
                Genre = _mbApiInterface.NowPlaying_GetFileTag(MetaDataType.Genre),
                Year = _mbApiInterface.NowPlaying_GetFileTag(MetaDataType.Year),
                TrackNumber = _mbApiInterface.NowPlaying_GetFileTag(MetaDataType.TrackNo)
            };
        }

        #endregion

        #region Logging

        internal static void LogInfo(string source, string message)
        {
            try
            {
                var logMessage = $"[SendSpin] [{DateTime.Now:HH:mm:ss.fff}] [{source}] {message}";
                _mbApiInterface.MB_Trace(logMessage);
                
#if DEBUG
                System.Diagnostics.Debug.WriteLine(logMessage);
#endif
            }
            catch
            {
                // Ignore logging errors
            }
        }

        internal static void LogError(string source, Exception ex)
        {
            try
            {
                var logMessage = $"[SendSpin] [{DateTime.Now:HH:mm:ss.fff}] [ERROR] [{source}] {ex.Message}\n{ex.StackTrace}";
                _mbApiInterface.MB_Trace(logMessage);
                
#if DEBUG
                System.Diagnostics.Debug.WriteLine(logMessage);
#endif
            }
            catch
            {
                // Ignore logging errors
            }
        }

        #endregion
    }
}
