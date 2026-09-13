using System;
using System.Threading;
using System.Threading.Tasks;
using MusicBeePlugin.SendSpin.Noise;

namespace MusicBeePlugin.SendSpin
{
    /// <summary>
    /// The audio-capture seam the render device drives. Implemented by
    /// <see cref="AudioCaptureService"/> in production (BASS decode → resample → Opus encode,
    /// 20 ms chunks); tests inject a fake. <see cref="AudioDataEventArgs.Timestamp"/> is µs on the
    /// implementation's OWN monotonic clock (Stopwatch since service construction).
    /// </summary>
    public interface IRenderAudioCapture : IDisposable
    {
        event EventHandler<AudioDataEventArgs>? AudioDataAvailable;
        /// <summary>Raised when the handed decode stream is fully consumed (track end).</summary>
        event EventHandler? StreamEnded;
        bool IsCapturing { get; }
        /// <summary>Audio time consumed from the stream, µs — this is MusicBee's playback position.</summary>
        long CapturedAudioUs { get; }
        /// <summary>The decode stream's native sample rate (known after Start).</summary>
        int NativeSampleRate { get; }
        /// <summary>The decode stream's native channel count.</summary>
        int NativeChannels { get; }
        /// <summary>The actual output sample rate (native for PCM, mixer target for Opus).</summary>
        int OutputSampleRate { get; }
        /// <summary>Seeks the handed decode stream (capture timestamps unaffected).</summary>
        void SeekTo(double seconds);
        /// <param name="ownsStreamHandle">
        /// False for a MusicBee-owned handle (render device): Stop() must not close it.
        /// </param>
        void Start(int streamHandle, bool ownsStreamHandle);
        void Stop();
    }

    /// <summary>Play state the render device reacts to (MusicBee-free so tests can run anywhere).</summary>
    public enum RenderPlayState
    {
        Playing,
        Paused,
        Stopped,
    }

    /// <summary>
    /// The "Music Assistant (Sendspin)" <b>render device</b>: MusicBee routes playback here
    /// instead of the local output (no local-mute hack), and this class forwards the captured
    /// audio to a Music Assistant server over the Sendspin <c>source@v1</c> protocol via
    /// <see cref="SourceConnection"/>.
    /// </summary>
    /// <remarks>
    /// Wiring model:
    ///  - <see cref="Activate"/> resolves the server URL (manual host first, then mDNS discovery)
    ///    and starts the persistent <see cref="SourceConnection"/> (reconnects with backoff on its
    ///    own; pairing runs over the connection when the operator pastes the token into MA).
    ///  - <see cref="PlayToDevice"/> (called by MusicBee per track) starts the capture on the
    ///    decode stream MusicBee hands us. The input stream opens lazily on the first packet while
    ///    the server's start authorization stands, so a track played while Music Assistant is not
    ///    asking for audio opens nothing.
    ///  - Play state: pause/stop end the input stream (<c>client_stream/end</c>); the next packet
    ///    (resume/new track) opens a fresh one. The source stream itself is CONTINUOUS across
    ///    track changes — capture timestamps keep advancing (one capture service per activation,
    ///    Stopwatch epoch mapped into the <see cref="ServerClock"/> domain once).
    ///  - Volume/mute are NOT forwarded: the source role has no volume channel; Music Assistant
    ///    applies its own volume to its targets (documented decision — see TODO.md section 1).
    /// </remarks>
    internal sealed class SourceRenderDevice : IDisposable
    {
        private readonly string _deviceName;
        private readonly SourceStreamParams _streamParams;
        private readonly Func<Uri?> _resolveServerUrl;
        private readonly Func<string, int> _openStreamHandle;   // Player_OpenStreamHandle wrapper
        private readonly Func<SendspinIdentity> _identityProvider;
        private readonly Func<PairingStore> _pairingStoreProvider;
        private readonly Func<IRenderAudioCapture> _captureFactory;
        private readonly Action<string> _log;
        private readonly object _lock = new();

        private SourceConnection? _connection;
        private IRenderAudioCapture? _capture;
        private long _captureEpochUs;          // ServerClock value when the capture clock was ~0
        private int _lastPlayHandle;           // handle from the latest PlayToDevice (resume use)
        private long _frozenPositionMs;        // playback position while not capturing
        private long _seekOffsetUs;            // cumulative seek offset (SeekTo resets CapturedAudioUs)
        private bool _active;
        private bool _disposed;
        private CancellationTokenSource? _resolveLoopCts;

        /// <summary>Raised at natural track end (decode stream fully consumed): the plugin must advance MusicBee's queue.</summary>
        public event EventHandler? TrackEnded;
        /// <summary>Raised when MA stops the input while MusicBee is playing: the plugin should pause MusicBee.</summary>
        public event EventHandler? PlaybackShouldPause;
        /// <summary>Raised when MA starts the input while MusicBee was paused by MA: the plugin should resume MusicBee.</summary>
        public event EventHandler? PlaybackShouldResume;

        public SourceRenderDevice(
            string deviceName,
            SourceStreamParams streamParams,
            Func<Uri?> resolveServerUrl,
            Func<string, int> openStreamHandle,
            Func<SendspinIdentity> identityProvider,
            Func<PairingStore> pairingStoreProvider,
            Func<IRenderAudioCapture> captureFactory,
            Action<string>? logger)
        {
            _deviceName = string.IsNullOrWhiteSpace(deviceName) ? "Music Assistant (Sendspin)" : deviceName;
            _streamParams = streamParams;
            _resolveServerUrl = resolveServerUrl;
            _openStreamHandle = openStreamHandle;
            _identityProvider = identityProvider;
            _pairingStoreProvider = pairingStoreProvider;
            _captureFactory = captureFactory;
            _log = logger ?? (_ => { });
        }

        /// <summary>Whether the render device is currently selected in MusicBee.</summary>
        public bool IsActive => _active;

        /// <summary>The active connection (null before activation) — for the pairing-token UI.</summary>
        public SourceConnection? Connection => _connection;

        /// <summary>The device name MusicBee shows in Preferences → Player → Output.</summary>
        public string DeviceName => _deviceName;

        /// <summary>
        /// MusicBee's playback position (ms) — polled via <c>GetPlayPosition()</c>. With a render
        /// device the plugin pulls the decode stream, so THIS is the only progress clock.
        /// </summary>
        public long PlayPositionMs
        {
            get
            {
                lock (_lock)
                {
                    if (_capture is { IsCapturing: true })
                        return (_seekOffsetUs + _capture.CapturedAudioUs) / 1000;
                    return _frozenPositionMs;
                }
            }
        }

        /// <summary>MusicBee dragged the progress bar: seek the handed decode stream.</summary>
        public void SetPlayPosition(long ms)
        {
            lock (_lock)
            {
                _frozenPositionMs = ms;
                if (_capture is { IsCapturing: true })
                {
                    // SeekTo resets CapturedAudioUs to 0; _seekOffsetUs makes PlayPositionMs
                    // report the target position (not 0) after the seek.
                    _seekOffsetUs = ms * 1000;
                    _capture.SeekTo(ms / 1000.0);
                    _log($"[RenderDevice] seek to {ms} ms (offset {_seekOffsetUs} µs)");
                }
            }
        }

        // ------------------------------------------------------------------
        // Activation
        // ------------------------------------------------------------------

        /// <summary>MusicBee selected the device as the output. Idempotent.</summary>
        public bool Activate()
        {
            lock (_lock)
            {
                if (_disposed)
                    return false;
                if (_active)
                    return true;
                _active = true;
            }

            try
            {
                StartConnectionWhenServerResolved();
                _log("[RenderDevice] activated: " + DeviceName);
                return true;
            }
            catch (Exception ex)
            {
                _log("[RenderDevice] activation failed: " + ex.Message);
                lock (_lock) { _active = false; }
                return false;
            }
        }

        /// <summary>MusicBee deselected the device (or the plugin shuts down). Idempotent.</summary>
        public void Deactivate()
        {
            lock (_lock)
            {
                if (!_active)
                    return;
                _active = false;
            }

            _resolveLoopCts?.Cancel();
            StopCapture();
            _connection?.Stop();
            _frozenPositionMs = 0;
            _seekOffsetUs = 0;
            _log("[RenderDevice] deactivated");
        }

        /// <summary>
        /// Resolves the server URL and starts the connection as soon as one is known, retrying
        /// every few seconds while discovery is still empty. Music Assistant's connection itself
        /// handles reconnects once started.
        /// </summary>
        private void StartConnectionWhenServerResolved()
        {
            _resolveLoopCts = new CancellationTokenSource();
            var ct = _resolveLoopCts.Token;
            Task.Run(async () =>
            {
                int attempt = 0;
                while (!ct.IsCancellationRequested && _active && _connection is null)
                {
                    Uri? url = null;
                    try
                    {
                        url = _resolveServerUrl();
                    }
                    catch (Exception ex)
                    {
                        _log("[RenderDevice] URL resolution failed: " + ex.Message);
                    }

                    if (url is null)
                    {
                        if (attempt == 0 || attempt % 5 == 0)
                            _log($"[RenderDevice] no Music Assistant server yet (attempt {attempt})");
                        attempt++;
                        try { await Task.Delay(TimeSpan.FromSeconds(3), ct); }
                        catch (OperationCanceledException) { break; }
                        continue;
                    }

                    try
                    {
                        var conn = new SourceConnection(
                            url, DeviceName, PluginVersion,
                            _identityProvider(), _pairingStoreProvider(),
                            NoiseCipherSuiteExtensions.SelectDefault(), m => _log(m));
                        lock (_lock)
                        {
                            if (!_active)
                                return; // deactivated while resolving
                            _connection = conn;
                            // Forward the MA pause/resume mirror to the plugin: connections are
                            // recreated on every activation, so the plugin subscribes only to us.
                            conn.SourceShouldPause += (_, e) => PlaybackShouldPause?.Invoke(this, e);
                            conn.SourceShouldResume += (_, e) => PlaybackShouldResume?.Invoke(this, e);
                        }
                        conn.Start();
                        _log("[RenderDevice] connection started: " + url);
                    }
                    catch (Exception ex)
                    {
                        _log("[RenderDevice] failed to start connection: " + ex.Message);
                        try { await Task.Delay(TimeSpan.FromSeconds(3), ct); }
                        catch (OperationCanceledException) { break; }
                    }
                }
            }, ct);
        }

        private static string PluginVersion => "1.0";

        // ------------------------------------------------------------------
        // Playback (MusicBee → capture → source connection)
        // ------------------------------------------------------------------

        /// <summary>MusicBee starts a track on this device. Restarts the capture on the new decode stream.</summary>
        public bool PlayToDevice(string url, int streamHandle)
        {
            if (!_active)
                return false;
            if (streamHandle == 0)
            {
                _log("[RenderDevice] PlayToDevice: stream handle is 0 — cannot capture");
                return false;
            }

            _lastPlayHandle = streamHandle;
            _seekOffsetUs = 0; // new track: position restarts at 0
            StartCapture(streamHandle, ownsHandle: false);
            _log($"[RenderDevice] PlayToDevice: url={url}, handle={streamHandle}");
            return true;
        }

        /// <summary>
        /// Play-state hook from MusicBee. Playing resumes capture (a fresh handle via
        /// <paramref name="openStreamHandle"/> when the PlayToDevice one is gone); pause/stop end
        /// the input stream. Volume/mute are deliberately NOT forwarded (source role has no
        /// volume channel; Music Assistant applies its own target volume).
        /// </summary>
        public void HandlePlayStateChanged(RenderPlayState state, string nowPlayingUrl)
        {
            if (!_active)
                return;

            switch (state)
            {
                case RenderPlayState.Playing:
                    {
                        lock (_lock)
                        {
                            if (_capture is { IsCapturing: true })
                                return; // PlayToDevice already started this track's capture
                        }

                        // The remembered PlayToDevice handle belongs to MUSICBEE — never owned.
                        // Ownership (and closing) applies only to streams we open ourselves.
                        int handle = _lastPlayHandle;
                        bool owns = false;
                        if (handle == 0 && !string.IsNullOrEmpty(nowPlayingUrl))
                        {
                            // Resume/new session without a PlayToDevice handle: open our own decode
                            // stream (musicbee settings = what the user configured for playback).
                            handle = _openStreamHandle(nowPlayingUrl);
                            owns = handle != 0;
                            if (!owns)
                                _log("[RenderDevice] Playing: Player_OpenStreamHandle returned 0");
                        }

                        if (handle != 0)
                        {
                            _log($"[RenderDevice] Playing: capture from handle {handle} (owned={owns})");
                            StartCapture(handle, owns);
                        }
                        break;
                    }

                case RenderPlayState.Paused:
                case RenderPlayState.Stopped:
                    StopCapture();
                    if (state == RenderPlayState.Stopped)
                        _lastPlayHandle = 0;
                    break;
            }
        }

        private void StartCapture(int streamHandle, bool ownsHandle)
        {
            var connection = _connection;
            if (connection is null)
            {
                _log("[RenderDevice] capture start skipped: no connection");
                return;
            }

            try
            {
                lock (_lock)
                {
                    if (_capture is null)
                    {
                        _capture = _captureFactory();
                        // Map the capture service's own Stopwatch epoch (≈0 at construction) into
                        // the ServerClock domain ONCE; timestamps then advance monotonically across
                        // per-track capture restarts.
                        _captureEpochUs = ServerClock.NowUs();
                        _capture.AudioDataAvailable += OnCaptureAudio;
                        _capture.StreamEnded += OnCaptureStreamEnded;
                    }

                    if (_capture.IsCapturing)
                        _capture.Stop(); // new decode stream for the new track

                    _capture.Start(streamHandle, ownsHandle);

                    // For PCM native, the decode stream's rate IS the output rate. Update the
                    // connection's stream params so client_stream/start announces the native
                    // format (MA accepts any rate and resamples on its end).
                    if (_connection is not null)
                    {
                        _connection.StreamParams.SampleRate = _capture.OutputSampleRate;
                        _connection.StreamParams.Channels = _capture.NativeChannels;
                    }
                }
            }
            catch (Exception ex)
            {
                _log("[RenderDevice] capture start failed: " + ex.Message);
            }
        }

        private void StopCapture()
        {
            try
            {
                lock (_lock)
                {
                    if (_capture is { IsCapturing: true })
                    {
                        _frozenPositionMs = _capture.CapturedAudioUs / 1000;
                        _capture.Stop();
                    }
                }

                // End the input stream only once nothing is feeding it, so no chunks trail the
                // client_stream/end (the server tolerates stragglers, but this is cleaner).
                _connection?.NotifyPlaybackStopped();
            }
            catch (Exception ex)
            {
                _log("[RenderDevice] capture stop failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Natural end of the handed track. The input stream is ended (MA holds the Live Input
        /// open for ~30 s of silence, so the next track's stream resumes it seamlessly) and the
        /// plugin advances MusicBee's queue — MusicBee cannot detect the end itself.
        /// </summary>
        private void OnCaptureStreamEnded(object? sender, EventArgs e)
        {
            _log("[RenderDevice] track end (decode stream consumed)");
            lock (_lock)
            {
                if (_capture is { IsCapturing: true })
                {
                    _frozenPositionMs = (_seekOffsetUs + _capture.CapturedAudioUs) / 1000;
                    _capture.Stop();
                }
            }
            _connection?.NotifyPlaybackStopped();
            TrackEnded?.Invoke(this, EventArgs.Empty);
        }

        private void OnCaptureAudio(object? sender, AudioDataEventArgs e)
        {
            var connection = _connection;
            if (connection is null)
                return;

            // The capture's Timestamp is µs on ITS Stopwatch (epoch = service construction, mapped
            // to _captureEpochUs in the ServerClock domain). EnqueueEncodedAudio converts into the
            // server time domain with the latest clock offset at wire time.
            try
            {
                connection.EnqueueEncodedAudio(e.Data, _captureEpochUs + e.Timestamp);
            }
            catch (Exception ex)
            {
                _log("[RenderDevice] enqueue audio failed: " + ex.Message);
            }
        }

        /// <summary>The pairing token for the settings dialog (paste into Music Assistant).</summary>
        public string GetPairingToken() => _pairingStoreProvider().GetPairingToken(_identityProvider());

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            Deactivate();
            _resolveLoopCts?.Dispose();
            lock (_lock)
            {
                if (_capture is not null)
                {
                    _capture.AudioDataAvailable -= OnCaptureAudio;
                    _capture.Dispose();
                    _capture = null;
                }
            }
            _connection?.Dispose();
        }
    }
}