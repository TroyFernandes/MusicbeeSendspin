using System;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using MusicBeePlugin.SendSpin;
using MusicBeePlugin.SendSpin.Noise;

namespace MusicBeePlugin
{
    /// <summary>
    /// MusicBee SendSpin Plugin — exposes MusicBee's playback to Music Assistant as a Sendspin
    /// <c>source@v1</c> render device ("Music Assistant (Sendspin)" in Preferences → Player →
    /// Output). Selecting it routes playback to Music Assistant (local output silent) without any
    /// local-mute hack.
    ///
    /// The legacy speaker mode (MusicBee acting as a Sendspin server, or connecting out to
    /// speakers) was archived to the archive/speaker-mode branch.
    /// </summary>
    public partial class Plugin
    {
        private static MusicBeeApiInterface _mbApiInterface;
        private static readonly PluginInfo _about = new PluginInfo();

        private static PluginSettings? _settings;

        // Source-role render device (Music Assistant) components
        private static SourceDiscoveryService? _sourceDiscovery;
        private static SendspinIdentity? _sourceIdentity;
        private static PairingStore? _sourcePairingStore;
        private static SourceRenderDevice? _sourceRenderDevice;
        private static string? _sourceIdentityPath;
        private static string? _sourcePairingPath;
        private static SourceConnection? _connectionEventsWiredFor;

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
        /// Plugin initialization - called when MusicBee loads the plugin
        /// </summary>
        public PluginInfo Initialise(IntPtr apiInterfacePtr)
        {
            _mbApiInterface = new MusicBeeApiInterface();
            _mbApiInterface.Initialise(apiInterfacePtr);

            _about.PluginInfoVersion = PluginInfoVersion;
            _about.Name = "SendSpin";
            _about.Description = "Send MusicBee's playback to Music Assistant over the Sendspin protocol (source role)";
            _about.Author = "SendSpin Community";
            _about.TargetApplication = "";
            _about.Type = PluginType.General;
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
                using (var dialog = new SettingsDialog(
                    _settings ?? new PluginSettings(),
                    pairingToken: _sourceRenderDevice?.GetPairingToken()))
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
        /// Save current settings
        /// </summary>
        public void SaveSettings()
        {
            try
            {
                var path = _settingsPath;
                if (_settings != null && path is { Length: > 0 })
                {
                    _settings.Save(path);
                    LogInfo("SaveSettings", "Settings saved");
                }
            }
            catch (Exception ex)
            {
                LogError("SaveSettings", ex);
            }
        }

        /// <summary>
        /// Close plugin - cleanup resources
        /// </summary>
        public void Close(PluginCloseReason reason)
        {
            try
            {
                LogInfo("Close", $"Closing plugin. Reason: {reason}");

                // Stop the render device on a background thread to avoid blocking
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
                    // Stop the Music Assistant render device
                    _sourceRenderDevice?.Deactivate();
                    _sourceRenderDevice?.Dispose();
                    _sourceRenderDevice = null;
                    _sourceDiscovery?.Stop();
                    _sourceDiscovery?.Dispose();
                    _sourceDiscovery = null;

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

                    case NotificationType.PlayStateChanged:
                        HandlePlayStateChanged();
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

                LogInfo("PluginStartup", "SendSpin plugin initialized successfully");
            }
            catch (Exception ex)
            {
                LogError("HandlePluginStartup", ex);
            }
        }

        private void HandlePlayStateChanged()
        {
            if (!_isInitialized) return;

            // Render device forwards play state: playing resumes capture, pause/stop end the
            // input stream. Volume/mute are NOT forwarded (the source role has no volume
            // channel; MA applies its own target volume) — documented decision, see TODO.md.
            if (_sourceRenderDevice is { IsActive: true })
            {
                try
                {
                    var playState = _mbApiInterface.Player_GetPlayState();
                    LogInfo("PlayStateChanged", $"State: {playState}");
                    _sourceRenderDevice.HandlePlayStateChanged(
                        ToRenderPlayState(playState),
                        _mbApiInterface.NowPlaying_GetFileUrl() ?? string.Empty);
                }
                catch (Exception ex)
                {
                    LogError("HandlePlayStateChanged(render)", ex);
                }
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

                    // Music Assistant render device (source role)
                    InitializeSourceDevice();

                    _isInitialized = true;
                }
            }
            catch (Exception ex)
            {
                LogError("InitializeComponents", ex);
                _isInitialized = false;
            }
        }

        #endregion

        #region Source render device (Music Assistant)

        /// <summary>
        /// Creates the Music Assistant render-device wiring: persistent identity + pairing store,
        /// mDNS server discovery, and the render device that feeds captured audio into the
        /// source-role connection.
        /// </summary>
        private void InitializeSourceDevice()
        {
            try
            {
                // MUST run before anything touches Noise: Noise.Libsodium P/Invokes bare
                // "libsodium", which Windows cannot resolve from Plugins\Native\ — preload it by
                // full path (x64/x86 per process bitness) so the later DllImport binds to it.
                NativeLibs.EnsureLoaded(LogSource);

                var storagePath = _mbApiInterface.Setting_GetPersistentStoragePath();
                _sourceIdentityPath = Path.Combine(storagePath, "SendSpinSourceIdentity.key");
                _sourcePairingPath = Path.Combine(storagePath, "SendSpinSourcePairing.json");

                _sourceIdentity = IdentityFile.LoadOrGenerate(_sourceIdentityPath, LogSource);
                _sourcePairingStore = new PairingStore(_sourcePairingPath, LogSource);

                if (_settings?.SourceAutoDiscover == true)
                {
                    _sourceDiscovery = new SourceDiscoveryService(LogSource);
                    _sourceDiscovery.Start();
                }

                var streamParams = new SourceStreamParams
                // Rate/channels/bit depth are always re-set from the capture at stream open;
                // only the codec is seeded here.
                {
                    Codec = _settings?.AudioCodec ?? "opus",
                };
                _sourceRenderDevice = new SourceRenderDevice(
                    _settings?.RenderDeviceName ?? "Music Assistant (Sendspin)",
                    streamParams,
                    ResolveSourceServerUrl,
                    OpenStreamHandleForSource,
                    () => _sourceIdentity!,
                    () => _sourcePairingStore!,
                    () => new AudioCaptureService(_settings ?? new PluginSettings()),
                    LogSource);
                _sourceRenderDevice.TrackEnded += OnSourceTrackEnded;
                _sourceRenderDevice.PlaybackShouldPause += OnMaSourceShouldPause;
                _sourceRenderDevice.PlaybackShouldResume += OnMaSourceShouldResume;

                LogInfo("SourceDevice", $"Render device '{_sourceRenderDevice.DeviceName}' ready; client_id={_sourceIdentity.PeerId}");
                // The operator needs the token to pair: it lives in the settings dialog, but the
                // log copy helps headless setups.
                LogInfo("SourceDevice", "Pairing token: " + _sourceRenderDevice.GetPairingToken());

                // Announce the device so MusicBee (re)reads GetRenderingDevices.
                if (_settings?.RenderDeviceEnabled == true)
                {
                    _mbApiInterface.MB_SendNotification(CallbackType.RenderingDevicesChanged);
                }
            }
            catch (Exception ex)
            {
                // ex.ToString() walks the inner-exception chain (e.g. the native-load failure
                // inside TypeInitializationException) — the message alone hides the cause.
                LogError("InitializeSourceDevice", new Exception(ex.ToString(), ex));
            }
        }

        private void OnMaSourceShouldPause(object? sender, EventArgs e)
        {
            try
            {
                // MA stopped the Live Input while MusicBee was playing into it: pause MusicBee,
                // otherwise it would play silently and skip through the queue.
                if (_mbApiInterface.Player_GetPlayState() == PlayState.Playing)
                {
                    LogInfo("MaMirror", "MA stopped the input — pausing MusicBee");
                    _mbApiInterface.Player_PlayPause();
                }
            }
            catch (Exception ex)
            {
                LogError("OnMaSourceShouldPause", ex);
            }
        }

        private void OnMaSourceShouldResume(object? sender, EventArgs e)
        {
            try
            {
                // MA started the Live Input again: resume the MusicBee playback we paused.
                if (_mbApiInterface.Player_GetPlayState() == PlayState.Paused)
                {
                    LogInfo("MaMirror", "MA started the input — resuming MusicBee");
                    _mbApiInterface.Player_PlayPause();
                }
            }
            catch (Exception ex)
            {
                LogError("OnMaSourceShouldResume", ex);
            }
        }

        private void OnSourceTrackEnded(object? sender, EventArgs e)
        {
            try
            {
                // The handed decode stream was fully consumed: MusicBee cannot see this end
                // itself (its own clock is dead with a render device) — advance the queue.
                LogInfo("SourceTrackEnded", "Track fully streamed — advancing to the next track");
                _mbApiInterface.Player_PlayNextTrack();
            }
            catch (Exception ex)
            {
                LogError("OnSourceTrackEnded", ex);
            }
        }

        private static void LogSource(string message) => LogInfo("Source", message);

        /// <summary>
        /// Server URL for the source connection: an explicit manual host wins, then mDNS
        /// discovery, then nothing (the render device keeps retrying while it waits).
        /// </summary>
        private static Uri? ResolveSourceServerUrl()
        {
            var settings = _settings;
            if (settings == null)
                return null;

            if (!string.IsNullOrWhiteSpace(settings.SourceServerHost))
            {
                return SourceDiscoveryService.ManualServerUrl(
                    settings.SourceServerHost, settings.SourceServerPort, 0, "/sendspin");
            }

            if (settings.SourceAutoDiscover)
            {
                var found = _sourceDiscovery?.GetFirstReadyServer();
                if (found != null && !string.IsNullOrEmpty(found.WebSocketUrl))
                {
                    try { return new Uri(found.WebSocketUrl); }
                    catch (UriFormatException ex)
                    {
                        LogError("ResolveSourceServerUrl", ex);
                    }
                }
            }

            return null;
        }

        private static RenderPlayState ToRenderPlayState(PlayState playState) => playState switch
        {
            PlayState.Playing => RenderPlayState.Playing,
            PlayState.Paused => RenderPlayState.Paused,
            _ => RenderPlayState.Stopped,
        };

        /// <summary>Opens a decode stream for the render device's resume path (plugin-owned handle).</summary>
        private static int OpenStreamHandleForSource(string url)
        {
            return _mbApiInterface.Player_OpenStreamHandle(
                url,
                useMusicBeeSettings: true,
                enableDsp: _settings?.EnableDsp ?? true,
                gainType: _settings?.ReplayGainMode ?? ReplayGainMode.Smart);
        }

        // --- MusicBee render-device reflection surface (mirrors the HQPlayer plugin) ---

        /// <summary>MusicBee asks for the plugin's output devices.</summary>
        public string[] GetRenderingDevices()
        {
            if (_settings?.RenderDeviceEnabled != true || _sourceRenderDevice == null)
                return Array.Empty<string>();
            return new[] { _sourceRenderDevice.DeviceName };
        }

        /// <summary>
        /// {continuousOutput, sampleRate, channels, bitDepth} — continuous output off, so MusicBee
        /// calls PlayToDevice per track (the capture restarts on the new decode stream).
        /// </summary>
        public int[] GetRenderingSettings()
        {
            return new[] { 0, 48000, 2, 16 };
        }

        /// <summary>
        /// MusicBee selected (or deselected) this device as the output. On activation the source
        /// connection comes up; on deactivation everything tears down — MusicBee stops feeding us
        /// and returns to the local output on its own (no local-mute hack on this path).
        /// </summary>
        public bool SetActiveRenderingDevice(string name)
        {
            var device = _sourceRenderDevice;
            if (device == null || _settings?.RenderDeviceEnabled != true)
            {
                LogInfo("SetActiveRenderingDevice", $"device not available (name={name ?? "null"})");
                return string.IsNullOrEmpty(name);
            }

            try
            {
                if (string.IsNullOrEmpty(name))
                {
                    device.Deactivate();
                    LogInfo("SetActiveRenderingDevice", "deactivated (no active device)");
                    return true;
                }

                if (string.Equals(name, device.DeviceName, StringComparison.Ordinal))
                {
                    bool ok = device.Activate();
                    LogInfo("SetActiveRenderingDevice", $"activate '{name}': {ok}");
                    return ok;
                }

                // A different output device was selected: ours steps aside.
                device.Deactivate();
                LogInfo("SetActiveRenderingDevice", $"deactivated (switched to '{name}')");
                return true;
            }
            catch (Exception ex)
            {
                LogError("SetActiveRenderingDevice", ex);
                return false;
            }
        }

        /// <summary>
        /// MusicBee polls this for the progress bar. With a render device the plugin pulls the
        /// decode stream, so the plugin IS the playback clock — without this the bar stays at 0:00
        /// and MusicBee never advances the queue.
        /// </summary>
        public int GetPlayPosition()
        {
            var device = _sourceRenderDevice;
            if (device == null || !device.IsActive)
                return 0;
            return (int)Math.Min(device.PlayPositionMs, int.MaxValue);
        }

        /// <summary>MusicBee seeked (progress bar drag): reposition the handed decode stream.</summary>
        public void SetPlayPosition(int ms)
        {
            _sourceRenderDevice?.SetPlayPosition(ms);
        }

        /// <summary>MusicBee starts playback on this device with a decode stream for us to read.</summary>
        public bool PlayToDevice(string url, int streamHandle)
        {
            var device = _sourceRenderDevice;
            if (device == null || !device.IsActive)
            {
                LogInfo("PlayToDevice", $"no active device (url={url}, handle={streamHandle})");
                return false;
            }

            return device.PlayToDevice(url, streamHandle);
        }

        /// <summary>
        /// Gapless next-track hook: a no-op for the source role — the next track arrives as a
        /// fresh PlayToDevice call and the input stream continues across it.
        /// </summary>
        public bool QueueNext(string url)
        {
            LogInfo("QueueNext", $"queue next: {url} (no-op for the source role)");
            return _sourceRenderDevice is { IsActive: true };
        }

        /// <summary>
        /// Settings changed: restart the render device so StreamParams/URL/enable state are picked
        /// up, and tell MusicBee to re-read the device list.
        /// </summary>
        private void ApplySettings()
        {
            try
            {
                lock (_syncLock)
                {
                    if (_settings == null) return;

                    // Render device: react to enable/disable and codec/server changes.
                    if (_sourceRenderDevice != null)
                    {
                        var wasActive = _sourceRenderDevice.IsActive;
                        _sourceRenderDevice.Deactivate(); // pick up new StreamParams + URL
                        if (_settings?.RenderDeviceEnabled == true && wasActive)
                        {
                            _sourceRenderDevice.Activate();
                        }
                        _mbApiInterface.MB_SendNotification(CallbackType.RenderingDevicesChanged);
                    }
                }
            }
            catch (Exception ex)
            {
                LogError("ApplySettings", ex);
            }
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