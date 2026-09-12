using System;
using System.Runtime.InteropServices;
using System.Security;
using System.Threading;
using System.Threading.Tasks;

namespace MusicBeePlugin.SendSpin
{
    /// <summary>
    /// Service for capturing audio from MusicBee using BASS audio library
    /// </summary>
    public class AudioCaptureService : IDisposable, IRenderAudioCapture
    {
        private PluginSettings _settings;
        private int _streamHandle;
        private bool _ownsStreamHandle = true;
        private int _mixerHandle;
        private bool _isCapturing;
        private bool _disposed;
        
        private Thread? _captureThread;
        private CancellationTokenSource? _cancellationTokenSource;
        
        private readonly object _syncLock = new object();
        private readonly System.Diagnostics.Stopwatch _clock;
        
        // Audio buffer settings
        private const int BufferSizeMs = 20; // 20ms chunks for low latency
        private byte[]? _audioBuffer;
        private int _bufferSize;
        
        // Encoder for Opus/FLAC encoding
        private IAudioEncoder? _encoder;

        public event EventHandler<AudioDataEventArgs>? AudioDataAvailable;

        public bool IsCapturing => _isCapturing;

        public AudioCaptureService(PluginSettings settings)
        {
            _settings = settings;
            _clock = System.Diagnostics.Stopwatch.StartNew();
            
            // Initialize BASS library
            InitializeBass();
        }

        /// <summary>
        /// Start capturing audio from the given stream handle.
        /// <paramref name="ownsStreamHandle"/> must be FALSE for a handle MusicBee owns (render
        /// device) — Stop() must not close a stream MusicBee is still using.
        /// </summary>
        public void Start(int streamHandle, bool ownsStreamHandle = true)
        {
            if (_isCapturing) return;
            
            lock (_syncLock)
            {
                try
                {
                    _streamHandle = streamHandle;
                    _ownsStreamHandle = ownsStreamHandle;
                    
                    // Get stream info
                    if (!Bass.TryGetStreamInformation(streamHandle, out var sampleRate, out var channels, out var codec))
                    {
                        Plugin.LogError("AudioCaptureService.Start", new Exception($"Failed to get stream information for handle {streamHandle}"));
                        return;
                    }
                    
                    // Get stream flags to check decode mode
                    var flags = Bass.GetChannelFlags(streamHandle);
                    Plugin.LogInfo("AudioCaptureService", $"Stream info: handle={streamHandle}, rate={sampleRate}, ch={channels}, codec={codec}, flags=0x{flags:X}");
                    
                    // Test read to see if stream has data
                    var testBuffer = new byte[1024];
                    var testRead = Bass.ReadStreamDataRaw(_streamHandle, testBuffer, 1024);
                    var testError = Bass.GetLastError();
                    Plugin.LogInfo("AudioCaptureService", $"Test read: bytes={testRead}, error={testError}");

                    // Create mixer stream for format conversion if needed
                    _mixerHandle = CreateMixerStream(streamHandle, _settings.SampleRate, _settings.Channels);
                    
                    if (_mixerHandle != 0 && _mixerHandle != streamHandle)
                    {
                        Plugin.LogInfo("AudioCaptureService", $"Created mixer stream: handle={_mixerHandle}");
                    }
                    
                    // Calculate buffer size
                    _bufferSize = CalculateBufferSize(_settings.SampleRate, _settings.Channels, _settings.BitDepth, BufferSizeMs);
                    _audioBuffer = new byte[_bufferSize];
                    
                    // Initialize encoder based on settings
                    _encoder = CreateEncoder(_settings.AudioCodec);
                    
                    // Start capture thread
                    _cancellationTokenSource = new CancellationTokenSource();
                    _captureThread = new Thread(CaptureLoop)
                    {
                        IsBackground = true,
                        Name = "SendSpin-AudioCapture",
                        Priority = ThreadPriority.AboveNormal
                    };
                    
                    _isCapturing = true;
                    _captureThread.Start(_cancellationTokenSource.Token);
                    
                    Plugin.LogInfo("AudioCaptureService", $"Started capturing: {sampleRate}Hz, {channels}ch -> {_settings.SampleRate}Hz, {_settings.Channels}ch, {_settings.AudioCodec}");
                }
                catch (Exception ex)
                {
                    Plugin.LogError("AudioCaptureService.Start", ex);
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
                    
                    // Wait for capture thread to finish
                    _captureThread?.Join(1000);
                    
                    // Close mixer stream
                    if (_mixerHandle != 0)
                    {
                        Bass.CloseStream(_mixerHandle);
                        _mixerHandle = 0;
                    }
                    
                    // Close the source stream ONLY when the plugin opened it. A render device's
                    // handle belongs to MusicBee (it drives playback through it).
                    if (_streamHandle != 0 && _ownsStreamHandle)
                    {
                        Bass.CloseStream(_streamHandle);
                    }
                    _streamHandle = 0;
                    
                    _encoder?.Dispose();
                    _encoder = null;
                    
                    Plugin.LogInfo("AudioCaptureService", "Stopped capturing");
                }
                catch (Exception ex)
                {
                    Plugin.LogError("AudioCaptureService.Stop", ex);
                }
            }
        }

        /// <summary>
        /// Prepare for track change (flush buffers, etc.)
        /// </summary>
        public void PrepareForTrackChange()
        {
            // Reset encoder state if needed
            _encoder?.Reset();
        }

        /// <summary>
        /// Apply new settings
        /// </summary>
        public void ApplySettings(PluginSettings settings)
        {
            var codecChanged = settings.AudioCodec != _settings.AudioCodec;
            var formatChanged = settings.SampleRate != _settings.SampleRate ||
                               settings.Channels != _settings.Channels ||
                               settings.BitDepth != _settings.BitDepth;
            
            _settings = settings;
            
            if ((codecChanged || formatChanged) && _isCapturing)
            {
                // Reinitialize encoder with new settings
                _encoder?.Dispose();
                _encoder = CreateEncoder(settings.AudioCodec);
                
                // Recalculate buffer size
                _bufferSize = CalculateBufferSize(_settings.SampleRate, _settings.Channels, _settings.BitDepth, BufferSizeMs);
                _audioBuffer = new byte[_bufferSize];
            }
        }

        #region Private Methods

        private void InitializeBass()
        {
            try
            {
                // BASS library is already initialized by MusicBee
                // We just need to ensure we can use it for encoding
                Plugin.LogInfo("AudioCaptureService", "BASS library ready for audio capture");
            }
            catch (Exception ex)
            {
                Plugin.LogError("InitializeBass", ex);
            }
        }

        private int CreateMixerStream(int sourceHandle, int targetSampleRate, int targetChannels)
        {
            // Get source stream info
            if (!Bass.TryGetStreamInformation(sourceHandle, out var sourceSampleRate, out var sourceChannels, out _))
            {
                return sourceHandle;
            }
            
            // If formats match, just use the source handle
            if (sourceSampleRate == targetSampleRate && sourceChannels == targetChannels)
            {
                return sourceHandle;
            }
            
            // Create mixer stream for format conversion
            var mixerHandle = Bass.CreateMixerStream(targetSampleRate, targetChannels);
            if (mixerHandle == 0)
            {
                Plugin.LogError("CreateMixerStream", new Exception("Failed to create mixer stream"));
                return sourceHandle;
            }
            
            // Add source to mixer
            Bass.MixerAddChannel(mixerHandle, sourceHandle, sourceChannels, targetChannels);
            
            return mixerHandle;
        }

        private void CaptureLoop(object? parameter)
        {
            var cancellationToken = (CancellationToken)(parameter ?? CancellationToken.None);
            var streamToRead = _mixerHandle != 0 ? _mixerHandle : _streamHandle;
            var lastLogTime = DateTime.MinValue;
            var totalBytesRead = 0L;
            var readAttempts = 0;
            var successfulReads = 0;
            
            Plugin.LogInfo("CaptureLoop", $"Starting capture loop. StreamHandle={_streamHandle}, MixerHandle={_mixerHandle}, BufferSize={_bufferSize}");
            
            while (!cancellationToken.IsCancellationRequested && _isCapturing)
            {
                try
                {
                    if (_audioBuffer == null) break;
                    
                    readAttempts++;
                    
                    // Read audio data from stream
                    var bytesRead = Bass.ReadStreamData(streamToRead, _audioBuffer, _bufferSize);
                    
                    // Log periodically
                    if ((DateTime.Now - lastLogTime).TotalSeconds >= 5)
                    {
                        var errorCode = Bass.GetLastError();
                        Plugin.LogInfo("CaptureLoop", $"Stats: attempts={readAttempts}, successful={successfulReads}, totalBytes={totalBytesRead}, lastRead={bytesRead}, bassError={errorCode}");
                        lastLogTime = DateTime.Now;
                    }
                    
                    if (bytesRead > 0)
                    {
                        successfulReads++;
                        totalBytesRead += bytesRead;
                        
                        // Get timestamp for this audio chunk
                        var timestamp = GetTimestampMicroseconds();
                        
                        // Encode audio data
                        byte[] encodedData;
                        if (_encoder != null)
                        {
                            encodedData = _encoder.Encode(_audioBuffer, bytesRead);
                        }
                        else
                        {
                            // Raw PCM
                            encodedData = new byte[bytesRead];
                            Array.Copy(_audioBuffer, encodedData, bytesRead);
                        }
                        
                        // Raise event with encoded audio data
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
                    else if (bytesRead == 0)
                    {
                        // No data available, wait a bit
                        Thread.Sleep(5);
                    }
                    else
                    {
                        // Error - bytesRead is -1
                        var errorCode = Bass.GetLastError();
                        if (readAttempts <= 5 || readAttempts % 100 == 0)
                        {
                            Plugin.LogInfo("CaptureLoop", $"Read returned {bytesRead}, BASS error code: {errorCode}");
                        }
                        Thread.Sleep(10);
                    }
                }
                catch (Exception ex)
                {
                    Plugin.LogError("CaptureLoop", ex);
                    Thread.Sleep(50);
                }
            }
            
            Plugin.LogInfo("CaptureLoop", $"Capture loop ended. Total bytes read: {totalBytesRead}, Successful reads: {successfulReads}");
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
                    return null; // No encoding for PCM
            }
        }

        private int CalculateBufferSize(int sampleRate, int channels, int bitDepth, int durationMs)
        {
            var bytesPerSample = bitDepth / 8;
            var samplesPerMs = sampleRate / 1000;
            return samplesPerMs * durationMs * channels * bytesPerSample;
        }

        private long GetTimestampMicroseconds()
        {
            return _clock.ElapsedTicks / (TimeSpan.TicksPerMillisecond / 1000);
        }

        #endregion

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
    /// Interface for audio encoders
    /// </summary>
    public interface IAudioEncoder : IDisposable
    {
        byte[] Encode(byte[] pcmData, int length);
        void Reset();
    }

    /// <summary>
    /// Opus encoder using Concentus library
    /// </summary>
    public class OpusEncoder : IAudioEncoder
    {
        private readonly Concentus.Structs.OpusEncoder _encoder;
        private readonly int _sampleRate;
        private readonly int _channels;
        private readonly int _frameSize;
        private readonly short[] _inputBuffer;
        private readonly byte[] _outputBuffer;

        public OpusEncoder(int sampleRate, int channels, int bitrate)
        {
            _sampleRate = sampleRate;
            _channels = channels;
            
            // Frame size: 20ms for good balance of latency and efficiency
            _frameSize = sampleRate / 50; // 20ms frames
            
            _encoder = new Concentus.Structs.OpusEncoder(sampleRate, channels, Concentus.Enums.OpusApplication.OPUS_APPLICATION_AUDIO);
            _encoder.Bitrate = bitrate;
            _encoder.Complexity = 10; // Max quality
            
            _inputBuffer = new short[_frameSize * channels];
            _outputBuffer = new byte[4000]; // Max Opus frame size
        }

        public byte[] Encode(byte[] pcmData, int length)
        {
            // Convert bytes to shorts (16-bit samples)
            var sampleCount = length / 2;
            if (sampleCount > _inputBuffer.Length)
            {
                sampleCount = _inputBuffer.Length;
            }
            
            Buffer.BlockCopy(pcmData, 0, _inputBuffer, 0, sampleCount * 2);
            
            // Encode
            var encodedLength = _encoder.Encode(_inputBuffer, 0, _frameSize, _outputBuffer, 0, _outputBuffer.Length);
            
            if (encodedLength > 0)
            {
                var result = new byte[encodedLength];
                Array.Copy(_outputBuffer, result, encodedLength);
                return result;
            }
            
            return Array.Empty<byte>();
        }

        public void Reset()
        {
            _encoder.ResetState();
        }

        public void Dispose()
        {
            // Concentus encoder doesn't need explicit disposal
        }
    }

    /// <summary>
    /// FLAC encoder (placeholder - would need native FLAC library)
    /// </summary>
    public class FlacEncoder : IAudioEncoder
    {
        private readonly int _sampleRate;
        private readonly int _channels;
        private readonly int _bitDepth;

        public FlacEncoder(int sampleRate, int channels, int bitDepth)
        {
            _sampleRate = sampleRate;
            _channels = channels;
            _bitDepth = bitDepth;
            
            // Note: Full FLAC encoding would require a native library like libFLAC
            Plugin.LogInfo("FlacEncoder", "FLAC encoder initialized (limited implementation - consider using Opus for best results)");
        }

        public byte[] Encode(byte[] pcmData, int length)
        {
            // For now, return PCM data
            // A full implementation would use libFLAC for real compression
            var result = new byte[length];
            Array.Copy(pcmData, result, length);
            return result;
        }

        public void Reset()
        {
            // Nothing to reset for this simple implementation
        }

        public void Dispose()
        {
            // Nothing to dispose
        }
    }

    /// <summary>
    /// BASS audio library wrapper
    /// </summary>
    [SuppressUnmanagedCodeSecurity]
    internal static class Bass
    {
        [Flags]
        public enum BASSFlag
        {
            BASS_DEFAULT = 0,
            BASS_SAMPLE_FLOAT = 0x100,
            BASS_STREAM_DECODE = 0x200000,
            BASS_MIXER_DOWNMIX = 0x400000,
            BASS_MIXER_NORAMPIN = 0x800000,
            BASS_MIXER_MATRIX = 0x10000,
            BASS_STREAM_AUTOFREE = 0x40000
        }

        [StructLayout(LayoutKind.Sequential)]
        private class BASS_CHANNELINFO
        {
            public int freq;
            public int chans;
            public BASSFlag flags;
            public int ctype;
            public int origres;
            public int plugin;
            public int sample;
            private IntPtr filenamePtr;
        }

        public static bool TryGetStreamInformation(int streamHandle, out int sampleRate, out int channels, out int codec)
        {
            sampleRate = 0;
            channels = 0;
            codec = 0;
            
            var info = new BASS_CHANNELINFO();
            if (!BASS_ChannelGetInfo(streamHandle, info))
            {
                return false;
            }
            
            sampleRate = info.freq;
            channels = info.chans;
            codec = info.ctype;
            return true;
        }

        public static int GetChannelFlags(int streamHandle)
        {
            var info = new BASS_CHANNELINFO();
            if (BASS_ChannelGetInfo(streamHandle, info))
            {
                return (int)info.flags;
            }
            return 0;
        }

        public static int CreateMixerStream(int sampleRate, int channels)
        {
            return BASS_Mixer_StreamCreate(sampleRate, channels, 
                BASSFlag.BASS_STREAM_DECODE | BASSFlag.BASS_SAMPLE_FLOAT);
        }

        public static bool MixerAddChannel(int mixerHandle, int sourceHandle, int sourceChannels, int targetChannels)
        {
            var flags = BASSFlag.BASS_STREAM_AUTOFREE | BASSFlag.BASS_MIXER_NORAMPIN;
            
            if (sourceChannels == 1 && targetChannels > 1)
            {
                flags |= BASSFlag.BASS_MIXER_MATRIX;
            }
            else if (sourceChannels > 2 && targetChannels == 2)
            {
                flags |= BASSFlag.BASS_MIXER_DOWNMIX;
            }
            
            return BASS_Mixer_StreamAddChannel(mixerHandle, sourceHandle, flags);
        }

        public static int ReadStreamData(int streamHandle, byte[] buffer, int length)
        {
            // Try reading with float flag (most decode streams output floats)
            // BASS_DATA_FLOAT = 0x40000000
            return BASS_ChannelGetData(streamHandle, buffer, length | 0x40000000);
        }

        public static int ReadStreamDataRaw(int streamHandle, byte[] buffer, int length)
        {
            // Read without any flags
            return BASS_ChannelGetData(streamHandle, buffer, length);
        }

        public static int GetLastError()
        {
            return BASS_ErrorGetCode();
        }

        public static void CloseStream(int streamHandle)
        {
            BASS_StreamFree(streamHandle);
        }

        public static long GetStreamPosition(int streamHandle)
        {
            return BASS_ChannelGetPosition(streamHandle, 0);
        }

        public static void SetStreamPosition(int streamHandle, long position)
        {
            BASS_ChannelSetPosition(streamHandle, position, 0);
        }

        #region P/Invoke

        [DllImport("bass.dll", CharSet = CharSet.Auto)]
        private static extern bool BASS_ChannelGetInfo(int handle, [Out] BASS_CHANNELINFO info);

        [DllImport("bass.dll", CharSet = CharSet.Auto)]
        private static extern int BASS_ChannelGetData(int handle, [In, Out] byte[] buffer, int length);

        [DllImport("bass.dll", CharSet = CharSet.Auto)]
        private static extern long BASS_ChannelGetPosition(int handle, int mode);

        [DllImport("bass.dll", CharSet = CharSet.Auto)]
        private static extern bool BASS_ChannelSetPosition(int handle, long pos, int mode);

        [DllImport("bass.dll", CharSet = CharSet.Auto)]
        private static extern bool BASS_StreamFree(int handle);

        [DllImport("bass.dll", CharSet = CharSet.Auto)]
        private static extern int BASS_ErrorGetCode();

        [DllImport("bassmix.dll", CharSet = CharSet.Auto)]
        private static extern int BASS_Mixer_StreamCreate(int freq, int chans, BASSFlag flags);

        [DllImport("bassmix.dll", CharSet = CharSet.Auto)]
        private static extern bool BASS_Mixer_StreamAddChannel(int handle, int channel, BASSFlag flags);

        #endregion
    }
}
