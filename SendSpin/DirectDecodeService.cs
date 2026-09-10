using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Threading;

namespace MusicBeePlugin.SendSpin
{
    /// <summary>
    /// Audio capture service that decodes audio files directly using BASS.
    /// This creates its own decode stream from the source file rather than 
    /// trying to tap into MusicBee's playback stream.
    /// </summary>
    public class DirectDecodeService : IDisposable
    {
        private PluginSettings _settings;
        private int _streamHandle;
        private bool _isCapturing;
        private bool _disposed;
        private static bool _bassInitialized;

        private Thread? _captureThread;
        private CancellationTokenSource? _cancellationTokenSource;

        private readonly object _syncLock = new object();
        private System.Diagnostics.Stopwatch? _streamClock; // Reset when stream starts

        // Encoder
        private IAudioEncoder? _encoder;

        // Resampler (for 44.1kHz → 48kHz conversion)
        private LinearResampler? _resampler;
        private int _outputSampleRate; // Rate after resampling (encoder input rate)

        // Current file position tracking for sync
        private string? _currentFile;
        private double _startPosition;
        
        // Presentation timestamp tracking (microseconds into the stream)
        private long _presentationTimestamp;
        private int _sourceSampleRate;

        public event EventHandler<AudioDataEventArgs>? AudioDataAvailable;

        public bool IsCapturing => _isCapturing;

        public DirectDecodeService(PluginSettings settings)
        {
            _settings = settings;
            
            // Initialize BASS for decoding (no output device)
            InitializeBass();
        }

        private static void InitializeBass()
        {
            if (_bassInitialized) return;
            
            try
            {
                // Initialize BASS with device -1 (no sound output - decode only)
                // This allows us to create decode streams without affecting MusicBee's audio
                if (!BassLib.BASS_Init(-1, 44100, 0, IntPtr.Zero, IntPtr.Zero))
                {
                    var error = BassLib.BASS_ErrorGetCode();
                    // Error 14 = BASS_ERROR_ALREADY - already initialized, which is fine
                    if (error != 14)
                    {
                        Plugin.LogError("DirectDecodeService.InitializeBass", new Exception($"BASS_Init failed: {error}"));
                        return;
                    }
                }
                
                // Load FLAC plugin if available
                var pluginPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "bassflac.dll");
                if (File.Exists(pluginPath))
                {
                    BassLib.BASS_PluginLoad(pluginPath, 0);
                    Plugin.LogInfo("DirectDecodeService", "Loaded bassflac.dll");
                }
                
                _bassInitialized = true;
                Plugin.LogInfo("DirectDecodeService", "BASS initialized for decoding");
            }
            catch (Exception ex)
            {
                Plugin.LogError("DirectDecodeService.InitializeBass", ex);
            }
        }

        /// <summary>
        /// Start capturing audio from a file
        /// </summary>
        public void Start(string filePath, double startPositionSeconds = 0)
        {
            if (_isCapturing) Stop();

            lock (_syncLock)
            {
                try
                {
                    _currentFile = filePath;
                    _startPosition = startPositionSeconds;

                    Plugin.LogInfo("DirectDecodeService", $"Starting decode of: {filePath}");

                    // Create decode stream from file
                    // BASS_STREAM_DECODE = 0x200000
                    // BASS_SAMPLE_FLOAT = 0x100
                    // BASS_UNICODE = 0x80000000 (for Unicode file paths)
                    _streamHandle = BassLib.BASS_StreamCreateFile(
                        false,
                        filePath,
                        0, 0,
                        unchecked((int)(0x200000 | 0x100 | 0x80000000)) // DECODE | FLOAT | UNICODE
                    );

                    if (_streamHandle == 0)
                    {
                        var error = BassLib.BASS_ErrorGetCode();
                        Plugin.LogError("DirectDecodeService.Start", new Exception($"BASS_StreamCreateFile failed: {error}"));
                        return;
                    }

                    // Get stream info
                    var info = new BASS_CHANNELINFO();
                    if (!BassLib.BASS_ChannelGetInfo(_streamHandle, ref info))
                    {
                        Plugin.LogError("DirectDecodeService.Start", new Exception("Failed to get channel info"));
                        BassLib.BASS_StreamFree(_streamHandle);
                        _streamHandle = 0;
                        return;
                    }

                    Plugin.LogInfo("DirectDecodeService", $"Stream created: handle={_streamHandle}, rate={info.freq}, ch={info.chans}");
                    
                    // Store source sample rate for timestamp calculation
                    _sourceSampleRate = info.freq;
                    
                    // Determine output sample rate (for Opus, we need to resample to 48000)
                    _outputSampleRate = GetTargetSampleRate(_settings.AudioCodec, _sourceSampleRate);
                    
                    // Create resampler if needed
                    if (_sourceSampleRate != _outputSampleRate)
                    {
                        _resampler = new LinearResampler(_sourceSampleRate, _outputSampleRate, info.chans);
                        Plugin.LogInfo("DirectDecodeService", $"Resampling: {_sourceSampleRate}Hz → {_outputSampleRate}Hz");
                    }
                    else
                    {
                        _resampler = null;
                    }
                    
                    // Reset presentation timestamp
                    _presentationTimestamp = 0;
                    _streamClock = System.Diagnostics.Stopwatch.StartNew();

                    // Seek to start position if needed
                    if (startPositionSeconds > 0)
                    {
                        var bytePos = BassLib.BASS_ChannelSeconds2Bytes(_streamHandle, startPositionSeconds);
                        BassLib.BASS_ChannelSetPosition(_streamHandle, bytePos, 0);
                        // Adjust presentation timestamp for seek position
                        _presentationTimestamp = (long)(startPositionSeconds * 1000000);
                    }

                    // Initialize encoder with OUTPUT sample rate (after resampling)
                    _encoder = CreateEncoder(_settings.AudioCodec, _outputSampleRate, info.chans);

                    // Start capture thread
                    _cancellationTokenSource = new CancellationTokenSource();
                    _captureThread = new Thread(DecodeLoop)
                    {
                        IsBackground = true,
                        Name = "SendSpin-DirectDecode",
                        Priority = ThreadPriority.AboveNormal
                    };

                    _isCapturing = true;
                    _captureThread.Start(_cancellationTokenSource.Token);

                    Plugin.LogInfo("DirectDecodeService", $"Started decoding: {info.freq}Hz, {info.chans}ch");
                }
                catch (Exception ex)
                {
                    Plugin.LogError("DirectDecodeService.Start", ex);
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

                    if (_streamHandle != 0)
                    {
                        BassLib.BASS_StreamFree(_streamHandle);
                        _streamHandle = 0;
                    }

                    _encoder?.Dispose();
                    _encoder = null;
                    _resampler = null;

                    Plugin.LogInfo("DirectDecodeService", "Stopped decoding");
                }
                catch (Exception ex)
                {
                    Plugin.LogError("DirectDecodeService.Stop", ex);
                }
            }
        }

        /// <summary>
        /// Sync position with MusicBee playback
        /// </summary>
        public void SyncPosition(double positionSeconds)
        {
            if (!_isCapturing || _streamHandle == 0) return;

            lock (_syncLock)
            {
                var bytePos = BassLib.BASS_ChannelSeconds2Bytes(_streamHandle, positionSeconds);
                BassLib.BASS_ChannelSetPosition(_streamHandle, bytePos, 0);
                _resampler?.Reset();
            }
        }

        /// <summary>
        /// Prepare for track change
        /// </summary>
        public void PrepareForTrackChange()
        {
            _encoder?.Reset();
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
                // Re-create encoder with current output rate
                _encoder = CreateEncoder(settings.AudioCodec, _outputSampleRate, 2);
            }
        }

        private void DecodeLoop(object? parameter)
        {
            var cancellationToken = (CancellationToken)(parameter ?? CancellationToken.None);
            
            // Opus needs exactly 960 samples per channel (20ms at 48kHz)
            const int opusFrameSamples = 960;
            const int channels = 2;
            
            // Calculate how many source samples we need to read to get 960 output samples after resampling
            // If source is 44100Hz and output is 48000Hz: 960 * 44100 / 48000 = 882 samples
            int sourceSamplesNeeded;
            if (_resampler != null)
            {
                sourceSamplesNeeded = (int)Math.Ceiling((double)opusFrameSamples * _sourceSampleRate / _outputSampleRate);
            }
            else
            {
                sourceSamplesNeeded = opusFrameSamples;
            }
            
            // Buffer for source samples (stereo)
            var sourceFloatCount = sourceSamplesNeeded * channels;
            var buffer = new float[sourceFloatCount + channels * 10]; // Small extra for safety
            var bufferHandle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
            
            // Buffer to accumulate resampled output for exact Opus frame sizes
            var resampleBuffer = new float[opusFrameSamples * channels * 2]; // Double size for accumulation
            var resampleBufferOffset = 0;
            
            var lastLogTime = DateTime.MinValue;
            var totalSamplesRead = 0L;
            var totalFramesSent = 0;

            try
            {
                while (!cancellationToken.IsCancellationRequested && _isCapturing)
                {
                    try
                    {
                        // Read float samples from decode stream
                        // BASS_DATA_FLOAT = 0x40000000
                        var bytesRead = BassLib.BASS_ChannelGetData(
                            _streamHandle,
                            bufferHandle.AddrOfPinnedObject(),
                            sourceFloatCount * 4 | 0x40000000
                        );

                        if (bytesRead > 0)
                        {
                            var floatsRead = bytesRead / 4;
                            totalSamplesRead += floatsRead;

                            // Resample if needed (e.g., 44100 → 48000 for Opus)
                            float[] resampledAudio;
                            int resampledFloatCount;
                            if (_resampler != null)
                            {
                                var inputSamplesPerChannel = floatsRead / channels;
                                resampledAudio = _resampler.Resample(buffer, inputSamplesPerChannel);
                                resampledFloatCount = resampledAudio.Length;
                            }
                            else
                            {
                                resampledAudio = buffer;
                                resampledFloatCount = floatsRead;
                            }

                            // Copy resampled audio to accumulation buffer
                            Array.Copy(resampledAudio, 0, resampleBuffer, resampleBufferOffset, resampledFloatCount);
                            resampleBufferOffset += resampledFloatCount;

                            // Process complete Opus frames (960 samples per channel = 1920 floats for stereo)
                            var opusFrameFloats = opusFrameSamples * channels;
                            while (resampleBufferOffset >= opusFrameFloats)
                            {
                                // Extract exactly one Opus frame worth of samples
                                var frameFloats = new float[opusFrameFloats];
                                Array.Copy(resampleBuffer, 0, frameFloats, 0, opusFrameFloats);

                                // Shift remaining data to start of buffer
                                var remaining = resampleBufferOffset - opusFrameFloats;
                                if (remaining > 0)
                                {
                                    Array.Copy(resampleBuffer, opusFrameFloats, resampleBuffer, 0, remaining);
                                }
                                resampleBufferOffset = remaining;

                                // Convert float to 16-bit PCM
                                var pcmBytes = ConvertFloatToPcm16(frameFloats, opusFrameFloats);

                                // Calculate presentation timestamp
                                var timestamp = _presentationTimestamp;
                                
                                // Advance presentation timestamp by exactly one Opus frame (20ms)
                                _presentationTimestamp += (opusFrameSamples * 1000000L) / _outputSampleRate;

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

                                // Send only valid frames (Opus frames should be > 3 bytes typically)
                                if (encodedData.Length > 10)
                                {
                                    totalFramesSent++;
                                    AudioDataAvailable?.Invoke(this, new AudioDataEventArgs(
                                        encodedData,
                                        timestamp,
                                        _outputSampleRate,
                                        channels,
                                        16
                                    ));
                                }
                            }
                        }
                        else if (bytesRead == -1)
                        {
                            // End of stream or error
                            var error = BassLib.BASS_ErrorGetCode();
                            if (error == 38) // BASS_ERROR_ENDED
                            {
                                Plugin.LogInfo("DecodeLoop", "End of file reached");
                                break;
                            }
                            Thread.Sleep(10);
                        }
                        else
                        {
                            // No data available yet
                            Thread.Sleep(5);
                        }

                        // Log periodically
                        if ((DateTime.Now - lastLogTime).TotalSeconds >= 5)
                        {
                            Plugin.LogInfo("DecodeLoop", $"Samples: {totalSamplesRead}, Frames sent: {totalFramesSent}");
                            lastLogTime = DateTime.Now;
                        }
                    }
                    catch (Exception ex)
                    {
                        Plugin.LogError("DecodeLoop", ex);
                        Thread.Sleep(50);
                    }
                }
            }
            finally
            {
                bufferHandle.Free();
            }

            Plugin.LogInfo("DecodeLoop", $"Decode loop ended. Total samples: {totalSamplesRead}, frames: {totalFramesSent}");
        }

        private byte[] ConvertFloatToPcm16(float[] floatSamples, int count)
        {
            var pcm = new byte[count * 2];
            for (int i = 0; i < count; i++)
            {
                var sample = floatSamples[i];
                if (sample > 1.0f) sample = 1.0f;
                if (sample < -1.0f) sample = -1.0f;

                var pcm16 = (short)(sample * 32767f);
                pcm[i * 2] = (byte)(pcm16 & 0xFF);
                pcm[i * 2 + 1] = (byte)((pcm16 >> 8) & 0xFF);
            }
            return pcm;
        }

        /// <summary>
        /// Get the target sample rate for encoding based on codec requirements.
        /// Opus only supports: 8000, 12000, 16000, 24000, 48000 Hz
        /// </summary>
        private int GetTargetSampleRate(string codec, int sourceSampleRate)
        {
            if (codec.Equals("opus", StringComparison.OrdinalIgnoreCase))
            {
                // Map common sample rates to valid Opus rates
                if (sourceSampleRate == 44100) return 48000;
                if (sourceSampleRate == 22050) return 24000;
                if (sourceSampleRate == 11025) return 12000;
                
                // Already a valid Opus rate?
                int[] validRates = { 8000, 12000, 16000, 24000, 48000 };
                foreach (var rate in validRates)
                {
                    if (sourceSampleRate == rate) return rate;
                }
                
                // Default to 48000 for anything else
                return 48000;
            }
            
            // For other codecs, keep original rate
            return sourceSampleRate;
        }

        private IAudioEncoder? CreateEncoder(string codec, int sampleRate, int channels)
        {
            switch (codec.ToLowerInvariant())
            {
                case "opus":
                    // sampleRate should already be a valid Opus rate (from GetTargetSampleRate)
                    Plugin.LogInfo("CreateEncoder", $"Opus encoder: {sampleRate}Hz, {channels}ch, {_settings.OpusBitrate}bps");
                    return new OpusEncoder(sampleRate, channels, _settings.OpusBitrate);

                case "flac":
                    return new FlacEncoder(sampleRate, channels, _settings.BitDepth);

                case "pcm":
                default:
                    return null;
            }
        }

        private long GetTimestampMicroseconds()
        {
            // This is now only used for wall-clock timing if needed
            return _streamClock?.ElapsedTicks / (TimeSpan.TicksPerMillisecond / 1000) ?? 0;
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

    #region Audio Resampler

    /// <summary>
    /// Linear interpolation resampler for converting between sample rates.
    /// Used primarily for 44100Hz → 48000Hz conversion for Opus encoding.
    /// Maintains state between calls for seamless audio.
    /// </summary>
    internal class LinearResampler
    {
        private readonly double _ratio;
        private readonly int _channels;
        private double _fractionalPosition;
        private float[] _previousSamples; // Last few samples from previous buffer for interpolation

        public int OutputSampleRate { get; }

        public LinearResampler(int inputRate, int outputRate, int channels)
        {
            _ratio = (double)inputRate / outputRate;
            _channels = channels;
            OutputSampleRate = outputRate;
            _fractionalPosition = 0;
            _previousSamples = new float[channels * 2]; // Keep 2 samples per channel for interpolation
        }

        public void Reset()
        {
            _fractionalPosition = 0;
            Array.Clear(_previousSamples, 0, _previousSamples.Length);
        }

        /// <summary>
        /// Resample float audio data from input rate to output rate.
        /// </summary>
        /// <param name="input">Input samples (interleaved if stereo)</param>
        /// <param name="inputSampleCount">Number of samples per channel in input</param>
        /// <returns>Resampled output (interleaved if stereo)</returns>
        public float[] Resample(float[] input, int inputSampleCount)
        {
            if (_ratio == 1.0)
            {
                var copy = new float[inputSampleCount * _channels];
                Array.Copy(input, copy, copy.Length);
                return copy;
            }

            // Calculate output sample count based on input and ratio
            int outputSampleCount = (int)Math.Floor((inputSampleCount - _fractionalPosition) / _ratio) + 1;
            var output = new float[outputSampleCount * _channels];
            
            int outputIdx = 0;
            double pos = _fractionalPosition;

            while (outputIdx < outputSampleCount * _channels && pos < inputSampleCount)
            {
                int idx0 = (int)Math.Floor(pos);
                int idx1 = idx0 + 1;
                double frac = pos - idx0;

                for (int ch = 0; ch < _channels; ch++)
                {
                    float s0, s1;

                    // Get sample at idx0
                    if (idx0 < 0)
                    {
                        // Use previous buffer's sample
                        s0 = _previousSamples[(_channels + idx0) * _channels + ch];
                    }
                    else if (idx0 < inputSampleCount)
                    {
                        s0 = input[idx0 * _channels + ch];
                    }
                    else
                    {
                        s0 = 0;
                    }

                    // Get sample at idx1
                    if (idx1 < 0)
                    {
                        s1 = _previousSamples[(_channels + idx1) * _channels + ch];
                    }
                    else if (idx1 < inputSampleCount)
                    {
                        s1 = input[idx1 * _channels + ch];
                    }
                    else
                    {
                        s1 = s0; // Extend last sample
                    }

                    // Linear interpolation
                    output[outputIdx + ch] = (float)(s0 + (s1 - s0) * frac);
                }

                outputIdx += _channels;
                pos += _ratio;
            }

            // Store last samples for next chunk's interpolation
            if (inputSampleCount >= 2)
            {
                for (int ch = 0; ch < _channels; ch++)
                {
                    _previousSamples[ch] = input[(inputSampleCount - 2) * _channels + ch];
                    _previousSamples[_channels + ch] = input[(inputSampleCount - 1) * _channels + ch];
                }
            }
            else if (inputSampleCount == 1)
            {
                for (int ch = 0; ch < _channels; ch++)
                {
                    _previousSamples[ch] = _previousSamples[_channels + ch];
                    _previousSamples[_channels + ch] = input[ch];
                }
            }

            // Save fractional position for next call (adjusted for consumed input)
            _fractionalPosition = pos - inputSampleCount;

            // Trim output to actual size produced
            if (outputIdx < output.Length)
            {
                var trimmed = new float[outputIdx];
                Array.Copy(output, trimmed, outputIdx);
                return trimmed;
            }

            return output;
        }
    }

    #endregion

    #region BASS Library Interop

    [StructLayout(LayoutKind.Sequential)]
    internal struct BASS_CHANNELINFO
    {
        public int freq;
        public int chans;
        public int flags;
        public int ctype;
        public int origres;
        public int plugin;
        public int sample;
        public IntPtr filename;
    }

    [SuppressUnmanagedCodeSecurity]
    internal static class BassLib
    {
        [DllImport("bass.dll", CharSet = CharSet.Auto)]
        public static extern bool BASS_Init(int device, int freq, int flags, IntPtr win, IntPtr clsid);

        [DllImport("bass.dll", CharSet = CharSet.Unicode)]
        public static extern int BASS_PluginLoad([MarshalAs(UnmanagedType.LPWStr)] string file, int flags);

        [DllImport("bass.dll", CharSet = CharSet.Unicode)]
        public static extern int BASS_StreamCreateFile(
            bool mem,
            [MarshalAs(UnmanagedType.LPWStr)] string file,
            long offset,
            long length,
            int flags
        );

        [DllImport("bass.dll", CharSet = CharSet.Auto)]
        public static extern bool BASS_ChannelGetInfo(int handle, ref BASS_CHANNELINFO info);

        [DllImport("bass.dll", CharSet = CharSet.Auto)]
        public static extern int BASS_ChannelGetData(int handle, IntPtr buffer, int length);

        [DllImport("bass.dll", CharSet = CharSet.Auto)]
        public static extern long BASS_ChannelSeconds2Bytes(int handle, double pos);

        [DllImport("bass.dll", CharSet = CharSet.Auto)]
        public static extern bool BASS_ChannelSetPosition(int handle, long pos, int mode);

        [DllImport("bass.dll", CharSet = CharSet.Auto)]
        public static extern bool BASS_StreamFree(int handle);

        [DllImport("bass.dll", CharSet = CharSet.Auto)]
        public static extern int BASS_ErrorGetCode();
    }

    #endregion
}
