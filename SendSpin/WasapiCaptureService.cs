using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace MusicBeePlugin.SendSpin
{
    /// <summary>
    /// Audio capture service using WASAPI loopback to capture system audio output.
    /// This captures whatever audio MusicBee is playing through the default audio device.
    /// </summary>
    public class WasapiCaptureService : IDisposable
    {
        private PluginSettings _settings;
        private bool _isCapturing;
        private bool _disposed;

        private Thread? _captureThread;
        private CancellationTokenSource? _cancellationTokenSource;

        private readonly object _syncLock = new object();
        private readonly System.Diagnostics.Stopwatch _clock;

        // BASS WASAPI handle
        private bool _wasapiInitialized;

        // Encoder
        private IAudioEncoder? _encoder;

        // Buffer for accumulating samples before encoding
        private byte[] _pcmBuffer;
        private int _pcmBufferPos;
        private int _frameSizeBytes;

        public event EventHandler<AudioDataEventArgs>? AudioDataAvailable;

        public bool IsCapturing => _isCapturing;

        public WasapiCaptureService(PluginSettings settings)
        {
            _settings = settings;
            _clock = System.Diagnostics.Stopwatch.StartNew();
            
            // Frame size for Opus (20ms at target sample rate)
            var samplesPerFrame = _settings.SampleRate / 50; // 20ms frame
            _frameSizeBytes = samplesPerFrame * _settings.Channels * 2; // 16-bit samples
            _pcmBuffer = new byte[_frameSizeBytes];
            _pcmBufferPos = 0;
        }

        /// <summary>
        /// Start capturing audio using WASAPI loopback
        /// </summary>
        public void Start()
        {
            if (_isCapturing) return;

            lock (_syncLock)
            {
                try
                {
                    // Initialize BASS for the current thread (without output device)
                    if (!BassWasapi.BASS_Init(-1, 0, 0, IntPtr.Zero, IntPtr.Zero))
                    {
                        var error = BassWasapi.BASS_ErrorGetCode();
                        // Error 14 (BASS_ERROR_ALREADY) is OK - already initialized
                        if (error != 14)
                        {
                            Plugin.LogError("WasapiCaptureService.Start", new Exception($"BASS_Init failed: {error}"));
                            // Continue anyway - MusicBee may have already initialized BASS
                        }
                    }

                    // Initialize WASAPI in loopback mode
                    // Device -3 = default loopback device
                    // BASS_WASAPI_BUFFER flag = 2 = WASAPI buffer mode
                    var initResult = BassWasapi.BASS_WASAPI_Init(
                        -3, // Default loopback device
                        0,  // Sample rate (0 = device default)
                        0,  // Channels (0 = device default)
                        0x2, // BASS_WASAPI_BUFFER = 2
                        0.5f, // Buffer length in seconds
                        0.0f, // Period (0 = use buffer)
                        IntPtr.Zero, // No callback (we'll poll)
                        IntPtr.Zero  // User data
                    );

                    if (!initResult)
                    {
                        var error = BassWasapi.BASS_ErrorGetCode();
                        Plugin.LogError("WasapiCaptureService.Start", new Exception($"BASS_WASAPI_Init failed: {error}"));
                        return;
                    }

                    _wasapiInitialized = true;

                    // Get actual format
                    var info = new BASS_WASAPI_INFO();
                    if (BassWasapi.BASS_WASAPI_GetInfo(ref info))
                    {
                        Plugin.LogInfo("WasapiCaptureService", $"WASAPI format: {info.freq}Hz, {info.chans}ch, format={info.format}");
                    }

                    // Start WASAPI capture
                    if (!BassWasapi.BASS_WASAPI_Start())
                    {
                        var error = BassWasapi.BASS_ErrorGetCode();
                        Plugin.LogError("WasapiCaptureService.Start", new Exception($"BASS_WASAPI_Start failed: {error}"));
                        return;
                    }

                    // Initialize encoder
                    _encoder = CreateEncoder(_settings.AudioCodec);

                    // Start capture thread
                    _cancellationTokenSource = new CancellationTokenSource();
                    _captureThread = new Thread(CaptureLoop)
                    {
                        IsBackground = true,
                        Name = "SendSpin-WasapiCapture",
                        Priority = ThreadPriority.AboveNormal
                    };

                    _isCapturing = true;
                    _captureThread.Start(_cancellationTokenSource.Token);

                    Plugin.LogInfo("WasapiCaptureService", "Started WASAPI loopback capture");
                }
                catch (Exception ex)
                {
                    Plugin.LogError("WasapiCaptureService.Start", ex);
                    _isCapturing = false;
                }
            }
        }

        /// <summary>
        /// Stop capturing audio
        /// </summary>
        public void Stop()
        {
            if (!_isCapturing) return;

            lock (_syncLock)
            {
                try
                {
                    _isCapturing = false;
                    _cancellationTokenSource?.Cancel();

                    _captureThread?.Join(1000);

                    if (_wasapiInitialized)
                    {
                        BassWasapi.BASS_WASAPI_Stop(true);
                        BassWasapi.BASS_WASAPI_Free();
                        _wasapiInitialized = false;
                    }

                    _encoder?.Dispose();
                    _encoder = null;

                    _pcmBufferPos = 0;

                    Plugin.LogInfo("WasapiCaptureService", "Stopped WASAPI loopback capture");
                }
                catch (Exception ex)
                {
                    Plugin.LogError("WasapiCaptureService.Stop", ex);
                }
            }
        }

        /// <summary>
        /// Prepare for track change
        /// </summary>
        public void PrepareForTrackChange()
        {
            _encoder?.Reset();
            _pcmBufferPos = 0;
        }

        /// <summary>
        /// Apply new settings
        /// </summary>
        public void ApplySettings(PluginSettings settings)
        {
            var codecChanged = settings.AudioCodec != _settings.AudioCodec;
            _settings = settings;

            if (codecChanged && _isCapturing)
            {
                _encoder?.Dispose();
                _encoder = CreateEncoder(settings.AudioCodec);
            }

            // Update frame size
            var samplesPerFrame = _settings.SampleRate / 50;
            _frameSizeBytes = samplesPerFrame * _settings.Channels * 2;
            _pcmBuffer = new byte[_frameSizeBytes];
            _pcmBufferPos = 0;
        }

        private void CaptureLoop(object? parameter)
        {
            var cancellationToken = (CancellationToken)(parameter ?? CancellationToken.None);
            var tempBuffer = new float[48000 / 50 * 2]; // Max 48kHz stereo for 20ms
            var lastLogTime = DateTime.MinValue;
            var totalSamplesRead = 0L;

            while (!cancellationToken.IsCancellationRequested && _isCapturing)
            {
                try
                {
                    // Get available data
                    var available = BassWasapi.BASS_WASAPI_GetData(IntPtr.Zero, 0); // BASS_DATA_AVAILABLE
                    
                    if (available > 0)
                    {
                        // Read float samples
                        var toRead = Math.Min(available / 4, tempBuffer.Length);
                        var handle = GCHandle.Alloc(tempBuffer, GCHandleType.Pinned);
                        try
                        {
                            var read = BassWasapi.BASS_WASAPI_GetData(handle.AddrOfPinnedObject(), (int)(toRead * 4) | 0x40000000);
                            
                            if (read > 0)
                            {
                                var samplesRead = read / 4;
                                totalSamplesRead += samplesRead;

                                // Convert float to 16-bit PCM and accumulate
                                for (int i = 0; i < samplesRead; i++)
                                {
                                    var sample = tempBuffer[i];
                                    if (sample > 1.0f) sample = 1.0f;
                                    if (sample < -1.0f) sample = -1.0f;

                                    var pcm16 = (short)(sample * 32767f);
                                    
                                    if (_pcmBufferPos + 2 <= _pcmBuffer.Length)
                                    {
                                        _pcmBuffer[_pcmBufferPos++] = (byte)(pcm16 & 0xFF);
                                        _pcmBuffer[_pcmBufferPos++] = (byte)((pcm16 >> 8) & 0xFF);
                                    }

                                    // When buffer is full, encode and send
                                    if (_pcmBufferPos >= _frameSizeBytes)
                                    {
                                        EncodeAndSend();
                                    }
                                }
                            }
                        }
                        finally
                        {
                            handle.Free();
                        }
                    }
                    else
                    {
                        Thread.Sleep(5);
                    }

                    // Log periodically
                    if ((DateTime.Now - lastLogTime).TotalSeconds >= 5)
                    {
                        Plugin.LogInfo("CaptureLoop", $"Total samples: {totalSamplesRead}");
                        lastLogTime = DateTime.Now;
                    }
                }
                catch (Exception ex)
                {
                    Plugin.LogError("CaptureLoop", ex);
                    Thread.Sleep(50);
                }
            }

            Plugin.LogInfo("CaptureLoop", $"WASAPI capture ended. Total samples: {totalSamplesRead}");
        }

        private void EncodeAndSend()
        {
            var timestamp = GetTimestampMicroseconds();

            byte[] encodedData;
            if (_encoder != null)
            {
                encodedData = _encoder.Encode(_pcmBuffer, _pcmBufferPos);
            }
            else
            {
                encodedData = new byte[_pcmBufferPos];
                Array.Copy(_pcmBuffer, encodedData, _pcmBufferPos);
            }

            _pcmBufferPos = 0;

            if (encodedData.Length > 0)
            {
                AudioDataAvailable?.Invoke(this, new AudioDataEventArgs(
                    encodedData,
                    timestamp,
                    _settings.SampleRate,
                    _settings.Channels,
                    _settings.BitDepth
                ));
            }
        }

        private IAudioEncoder? CreateEncoder(string codec)
        {
            switch (codec.ToLowerInvariant())
            {
                case "opus":
                    return new OpusEncoder(_settings.SampleRate, _settings.Channels, _settings.OpusBitrate);

                case "flac":
                    return new FlacEncoder(_settings.SampleRate, _settings.Channels, _settings.BitDepth);

                case "pcm":
                default:
                    return null;
            }
        }

        private long GetTimestampMicroseconds()
        {
            return _clock.ElapsedTicks / (TimeSpan.TicksPerMillisecond / 1000);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            Stop();
            _cancellationTokenSource?.Dispose();
            _encoder?.Dispose();

            GC.SuppressFinalize(this);
        }
    }

    #region BASS WASAPI Interop

    [StructLayout(LayoutKind.Sequential)]
    internal struct BASS_WASAPI_INFO
    {
        public int initflags;
        public int freq;
        public int chans;
        public int format;
        public int buflen;
        public float volmax;
        public float volmin;
        public float volstep;
    }

    internal static class BassWasapi
    {
        [DllImport("bass.dll", CharSet = CharSet.Auto)]
        public static extern bool BASS_Init(int device, int freq, int flags, IntPtr win, IntPtr clsid);

        [DllImport("bass.dll", CharSet = CharSet.Auto)]
        public static extern int BASS_ErrorGetCode();

        [DllImport("basswasapi.dll", CharSet = CharSet.Auto)]
        public static extern bool BASS_WASAPI_Init(int device, int freq, int chans, int flags, float buffer, float period, IntPtr proc, IntPtr user);

        [DllImport("basswasapi.dll", CharSet = CharSet.Auto)]
        public static extern bool BASS_WASAPI_Start();

        [DllImport("basswasapi.dll", CharSet = CharSet.Auto)]
        public static extern bool BASS_WASAPI_Stop(bool reset);

        [DllImport("basswasapi.dll", CharSet = CharSet.Auto)]
        public static extern bool BASS_WASAPI_Free();

        [DllImport("basswasapi.dll", CharSet = CharSet.Auto)]
        public static extern bool BASS_WASAPI_GetInfo(ref BASS_WASAPI_INFO info);

        [DllImport("basswasapi.dll", CharSet = CharSet.Auto)]
        public static extern int BASS_WASAPI_GetData(IntPtr buffer, int length);
    }

    #endregion
}
