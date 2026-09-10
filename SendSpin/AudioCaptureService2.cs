using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Security;
using System.Threading;

namespace MusicBeePlugin.SendSpin
{
    /// <summary>
    /// Alternative audio capture service using BASS DSP callbacks
    /// This hooks into the audio processing chain directly instead of reading from a decode stream.
    /// </summary>
    public class AudioCaptureService2 : IDisposable
    {
        private PluginSettings _settings;
        private int _streamHandle;
        private int _dspHandle;
        private bool _isCapturing;
        private bool _disposed;

        private readonly object _syncLock = new object();
        private readonly System.Diagnostics.Stopwatch _clock;

        // DSP callback delegate (must be stored to prevent garbage collection)
        private Bass2.DSPPROC? _dspCallback;
        private GCHandle _dspCallbackHandle;

        // Encoder for Opus/FLAC encoding
        private IAudioEncoder? _encoder;

        // Audio buffer for encoding
        private ConcurrentQueue<float[]> _audioQueue = new ConcurrentQueue<float[]>();
        private Thread? _encoderThread;
        private CancellationTokenSource? _cancellationTokenSource;

        public event EventHandler<AudioDataEventArgs>? AudioDataAvailable;

        public bool IsCapturing => _isCapturing;

        public AudioCaptureService2(PluginSettings settings)
        {
            _settings = settings;
            _clock = System.Diagnostics.Stopwatch.StartNew();
        }

        /// <summary>
        /// Start capturing audio from the given stream handle using DSP callback
        /// </summary>
        public void Start(int streamHandle)
        {
            if (_isCapturing) return;

            lock (_syncLock)
            {
                try
                {
                    _streamHandle = streamHandle;

                    // Get stream info
                    if (!Bass2.TryGetStreamInformation(streamHandle, out var sampleRate, out var channels, out var flags))
                    {
                        Plugin.LogError("AudioCaptureService2.Start", new Exception($"Failed to get stream information for handle {streamHandle}"));
                        return;
                    }

                    Plugin.LogInfo("AudioCaptureService2", $"Stream info: handle={streamHandle}, rate={sampleRate}, ch={channels}, flags=0x{flags:X}");

                    // Check if stream is in decode mode
                    bool isDecodeMode = (flags & 0x200000) != 0; // BASS_STREAM_DECODE
                    Plugin.LogInfo("AudioCaptureService2", $"Stream decode mode: {isDecodeMode}");

                    // Initialize encoder
                    _encoder = CreateEncoder(_settings.AudioCodec);

                    // Create DSP callback
                    _dspCallback = new Bass2.DSPPROC(DspCallback);
                    _dspCallbackHandle = GCHandle.Alloc(_dspCallback);

                    // Set DSP on the stream - priority 0 means it runs at the end of the chain
                    _dspHandle = Bass2.BASS_ChannelSetDSP(streamHandle, _dspCallback, IntPtr.Zero, 0);
                    
                    if (_dspHandle == 0)
                    {
                        var error = Bass2.BASS_ErrorGetCode();
                        Plugin.LogError("AudioCaptureService2.Start", new Exception($"Failed to set DSP, error: {error}"));
                        return;
                    }

                    Plugin.LogInfo("AudioCaptureService2", $"DSP handle created: {_dspHandle}");

                    // Start encoder thread
                    _cancellationTokenSource = new CancellationTokenSource();
                    _encoderThread = new Thread(EncoderLoop)
                    {
                        IsBackground = true,
                        Name = "SendSpin-Encoder",
                        Priority = ThreadPriority.AboveNormal
                    };

                    _isCapturing = true;
                    _encoderThread.Start(_cancellationTokenSource.Token);

                    Plugin.LogInfo("AudioCaptureService2", $"Started DSP capture: {sampleRate}Hz, {channels}ch -> {_settings.AudioCodec}");
                }
                catch (Exception ex)
                {
                    Plugin.LogError("AudioCaptureService2.Start", ex);
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

                    // Remove DSP from stream
                    if (_dspHandle != 0 && _streamHandle != 0)
                    {
                        Bass2.BASS_ChannelRemoveDSP(_streamHandle, _dspHandle);
                        _dspHandle = 0;
                    }

                    // Wait for encoder thread to finish
                    _encoderThread?.Join(1000);

                    // Free callback handle
                    if (_dspCallbackHandle.IsAllocated)
                    {
                        _dspCallbackHandle.Free();
                    }

                    // Close source stream
                    if (_streamHandle != 0)
                    {
                        Bass2.BASS_StreamFree(_streamHandle);
                        _streamHandle = 0;
                    }

                    _encoder?.Dispose();
                    _encoder = null;

                    // Clear queue
                    while (_audioQueue.TryDequeue(out _)) { }

                    Plugin.LogInfo("AudioCaptureService2", "Stopped DSP capture");
                }
                catch (Exception ex)
                {
                    Plugin.LogError("AudioCaptureService2.Stop", ex);
                }
            }
        }

        /// <summary>
        /// Prepare for track change (flush buffers, etc.)
        /// </summary>
        public void PrepareForTrackChange()
        {
            _encoder?.Reset();
            while (_audioQueue.TryDequeue(out _)) { }
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
        }

        /// <summary>
        /// DSP callback - receives float samples
        /// </summary>
        private void DspCallback(int handle, int channel, IntPtr buffer, int length, IntPtr user)
        {
            if (!_isCapturing || length <= 0) return;

            try
            {
                // Copy float samples from unmanaged memory
                var floatCount = length / 4; // length is in bytes, floats are 4 bytes each
                var samples = new float[floatCount];
                Marshal.Copy(buffer, samples, 0, floatCount);

                // Queue for encoding
                _audioQueue.Enqueue(samples);
            }
            catch (Exception ex)
            {
                Plugin.LogError("DspCallback", ex);
            }
        }

        /// <summary>
        /// Encoder thread - processes queued audio and encodes it
        /// </summary>
        private void EncoderLoop(object? parameter)
        {
            var cancellationToken = (CancellationToken)(parameter ?? CancellationToken.None);
            var lastLogTime = DateTime.MinValue;
            var processedFrames = 0;

            while (!cancellationToken.IsCancellationRequested && _isCapturing)
            {
                try
                {
                    if (_audioQueue.TryDequeue(out var samples))
                    {
                        processedFrames++;
                        var timestamp = GetTimestampMicroseconds();

                        // Convert float samples to PCM bytes for encoding
                        var pcmBytes = ConvertFloatToPcm16(samples);

                        // Encode
                        byte[] encodedData;
                        if (_encoder != null)
                        {
                            encodedData = _encoder.Encode(pcmBytes, pcmBytes.Length);
                        }
                        else
                        {
                            encodedData = pcmBytes;
                        }

                        // Raise event
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

                        // Log periodically
                        if ((DateTime.Now - lastLogTime).TotalSeconds >= 5)
                        {
                            Plugin.LogInfo("EncoderLoop", $"Processed {processedFrames} frames, queue depth: {_audioQueue.Count}");
                            lastLogTime = DateTime.Now;
                        }
                    }
                    else
                    {
                        Thread.Sleep(1);
                    }
                }
                catch (Exception ex)
                {
                    Plugin.LogError("EncoderLoop", ex);
                    Thread.Sleep(10);
                }
            }

            Plugin.LogInfo("EncoderLoop", $"Encoder loop ended. Total frames processed: {processedFrames}");
        }

        private byte[] ConvertFloatToPcm16(float[] floatSamples)
        {
            var pcm = new byte[floatSamples.Length * 2];
            for (int i = 0; i < floatSamples.Length; i++)
            {
                // Clamp to -1.0 to 1.0 range and convert to 16-bit
                var sample = floatSamples[i];
                if (sample > 1.0f) sample = 1.0f;
                if (sample < -1.0f) sample = -1.0f;
                
                var pcm16 = (short)(sample * 32767f);
                pcm[i * 2] = (byte)(pcm16 & 0xFF);
                pcm[i * 2 + 1] = (byte)((pcm16 >> 8) & 0xFF);
            }
            return pcm;
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

    /// <summary>
    /// BASS library wrapper for DSP functionality
    /// </summary>
    [SuppressUnmanagedCodeSecurity]
    internal static class Bass2
    {
        // DSP callback delegate
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void DSPPROC(int handle, int channel, IntPtr buffer, int length, IntPtr user);

        [StructLayout(LayoutKind.Sequential)]
        private class BASS_CHANNELINFO
        {
            public int freq;
            public int chans;
            public int flags;
            public int ctype;
            public int origres;
            public int plugin;
            public int sample;
            private IntPtr filenamePtr;
        }

        public static bool TryGetStreamInformation(int streamHandle, out int sampleRate, out int channels, out int flags)
        {
            sampleRate = 0;
            channels = 0;
            flags = 0;

            var info = new BASS_CHANNELINFO();
            if (!BASS_ChannelGetInfo(streamHandle, info))
            {
                return false;
            }

            sampleRate = info.freq;
            channels = info.chans;
            flags = info.flags;
            return true;
        }

        #region P/Invoke

        [DllImport("bass.dll", CharSet = CharSet.Auto)]
        private static extern bool BASS_ChannelGetInfo(int handle, [Out] BASS_CHANNELINFO info);

        [DllImport("bass.dll", CharSet = CharSet.Auto)]
        public static extern int BASS_ChannelSetDSP(int handle, DSPPROC proc, IntPtr user, int priority);

        [DllImport("bass.dll", CharSet = CharSet.Auto)]
        public static extern bool BASS_ChannelRemoveDSP(int handle, int dsp);

        [DllImport("bass.dll", CharSet = CharSet.Auto)]
        public static extern bool BASS_StreamFree(int handle);

        [DllImport("bass.dll", CharSet = CharSet.Auto)]
        public static extern int BASS_ErrorGetCode();

        #endregion
    }
}
