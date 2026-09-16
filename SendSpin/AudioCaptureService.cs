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
        // Output format: 16-bit stereo (the source's native rate — no resampling).
        private const int OutputChannels = 2;
        private const int OutputBitDepth = 16;
        private int _streamHandle;
        private int _sourceSampleRate = 48000;
        private int _sourceChannels = 2;
        private int _outputSampleRate = 48000;  // the source's native rate (known after Start)
        private long _totalBytesRead;
        private int _zeroReadStreak;
        private bool _ownsStreamHandle = true;
        private bool _isCapturing;
        private bool _disposed;
        
        private Thread? _captureThread;
        private CancellationTokenSource? _cancellationTokenSource;
        
        private readonly object _syncLock = new object();
        // Chunk timestamps + 1× real-time pacing (see CaptureTimeline: content-spaced stamps,
        // never emission wall-clock).
        private readonly CaptureTimeline _timeline;
        // Audio buffer settings
        private const int BufferSizeMs = 20; // 20ms chunks for low latency
        // Buffers: the stream is read as 32-bit FLOAT (decode sources are float; BASS converts
        // 16-bit sources when BASS_DATA_FLOAT is requested) and converted to the 16-bit LE PCM
        // that is streamed. _pcmBuffer holds the converted 16-bit data (20 ms).
        private byte[]? _floatBuffer;     // raw float bytes from BASS (2× the 16-bit size)
        private float[]? _floatSamples;   // float view of _floatBuffer
        private short[]? _pcmShorts;      // converted samples
        private byte[]? _pcmBuffer;       // 16-bit LE PCM streamed to the consumer (20 ms)
        private int _bufferSize;

        public event EventHandler<AudioDataEventArgs>? AudioDataAvailable;

        /// <summary>
        /// Raised when the decode stream reaches its end (BASS_ERROR_ENDED): the handed track has
        /// been fully consumed. For a render device this is TRACK END — the plugin must advance
        /// MusicBee's queue itself (MusicBee's own end-of-track clock is dead with output devices).
        /// </summary>
        public event EventHandler? StreamEnded;

        public bool IsCapturing => _isCapturing;

        /// <summary>The decode stream's native sample rate (known after Start).</summary>
        public int NativeSampleRate => _sourceSampleRate;
        /// <summary>The decode stream's native channel count.</summary>
        public int NativeChannels => _sourceChannels;
        /// <summary>The actual output sample rate (the source's native rate).</summary>
        /// <summary>Bit depth of the emitted stream (always 16 — s16le PCM).</summary>
        public int BitDepth => OutputBitDepth;
        public int OutputSampleRate => _outputSampleRate;

        /// <summary>
        /// Audio time consumed from the handed stream, in microseconds (16-bit output domain).
        /// This IS MusicBee's playback position: with a render device the plugin pulls the decode
        /// stream, so the decoded position is the only clock that exists.
        /// </summary>
        public long CapturedAudioUs
        {
            get
            {
                lock (_syncLock)
                {
                    long bytesPerSecond = _outputSampleRate * OutputChannels * OutputBitDepth / 8;
                    return _totalBytesRead * 1_000_000 / bytesPerSecond;
                }
            }
        }

        /// <summary>
        /// Seeks the handed decode stream. Timestamps are unaffected (capture time keeps running —
        /// a live input has no seek concept); only which audio plays next changes.
        /// </summary>
        public void SeekTo(double seconds)
        {
            lock (_syncLock)
            {
                if (!_isCapturing || _streamHandle == 0)
                    return;
                long pos = (long)(seconds * _sourceSampleRate * _sourceChannels * 4); // float bytes

                // Direct read from the decode stream: reposition and drain any internally
                // buffered data by reading a few bytes past the seek point.
                Bass.SetStreamPosition(_streamHandle, pos);
                var drain = new byte[256];
                Bass.ReadStreamDataRaw(_streamHandle, drain, drain.Length); // flush codec state

                _totalBytesRead = 0; // reset: the position counter tracks the NEW position
                // Seek = content jump: restart the timeline at 'now' so the next chunk's
                // timestamp continues seamlessly (no rewind, no gap).
                _timeline.StartTrack();
                Plugin.LogInfo("AudioCaptureService", $"Seek to {seconds:F1}s (byte pos {pos})");
            }
        }

        public AudioCaptureService(PluginSettings settings)
        {
            _settings = settings;
            _timeline = new CaptureTimeline();
            
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
                    _totalBytesRead = 0;
                    _zeroReadStreak = 0;
                    // New content timeline: chunk timestamps restart from 'now' on this track.
                    _timeline.StartTrack();
                    // Get stream info
                    if (!Bass.TryGetStreamInformation(streamHandle, out var sampleRate, out var channels, out var codec))
                    {
                        Plugin.LogError("AudioCaptureService.Start", new Exception($"Failed to get stream information for handle {streamHandle}"));
                        return;
                    }
                    _sourceSampleRate = sampleRate;
                    _sourceChannels = channels;
                    
                    // Get stream flags to check decode mode
                    var flags = Bass.GetChannelFlags(streamHandle);
                    Plugin.LogInfo("AudioCaptureService", $"Stream info: handle={streamHandle}, rate={sampleRate}, ch={channels}, codec={codec}, flags=0x{flags:X}");
                    
                    // Test read to see if stream has data
                    var testBuffer = new byte[1024];
                    var testRead = Bass.ReadStreamDataRaw(_streamHandle, testBuffer, 1024);
                    var testError = Bass.GetLastError();
                    Plugin.LogInfo("AudioCaptureService", $"Test read: bytes={testRead}, error={testError}");

                    // Bit-perfect: read the decode stream at its NATIVE rate (no mixer, no resampling).
                    _outputSampleRate = sampleRate;
                    Plugin.LogInfo("AudioCaptureService", $"PCM native: {sampleRate}Hz, {channels}ch (no resampling)");

                    // Calculate buffer size
                    // Allocate the conversion buffers (16-bit PCM) plus the float-side buffers:
                    // float bytes are 2× the 16-bit size (4 B vs 2 B per sample).
                    _bufferSize = CalculateBufferSize(sampleRate, OutputChannels, OutputBitDepth, BufferSizeMs);
                    _pcmBuffer = new byte[_bufferSize];
                    _floatBuffer = new byte[_bufferSize * 2];
                    _floatSamples = new float[_bufferSize / 2];
                    _pcmShorts = new short[_bufferSize / 2];

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
                    
                    Plugin.LogInfo("AudioCaptureService", $"Started capturing: {sampleRate}Hz, {channels}ch -> {_outputSampleRate}Hz, {OutputChannels}ch, PCM s{OutputBitDepth}le");
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
                    

                    // Close the source stream ONLY when the plugin opened it. A render device's
                    // handle belongs to MusicBee (it drives playback through it).
                    if (_streamHandle != 0 && _ownsStreamHandle)
                    {
                        Bass.CloseStream(_streamHandle);
                    }
                    _streamHandle = 0;

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
            // Nothing to flush: the PCM path has no encoder state.
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

        private void CaptureLoop(object? parameter)
        {
            var cancellationToken = (CancellationToken)(parameter ?? CancellationToken.None);
            var streamToRead = _streamHandle;
            var lastLogTime = DateTime.MinValue;
            var readAttempts = 0;
            var successfulReads = 0;
            
            Plugin.LogInfo("CaptureLoop", $"Starting capture loop. StreamHandle={_streamHandle}, BufferSize={_bufferSize}");
            
            while (!cancellationToken.IsCancellationRequested && _isCapturing)
            {
                try
                {
                    // Snapshot: nullable fields don't narrow, and the loop runs on its own thread.
                    var floatBuffer = _floatBuffer;
                    var floatSamples = _floatSamples;
                    var pcmShorts = _pcmShorts;
                    var pcmBuffer = _pcmBuffer;
                    if (floatBuffer is null || floatSamples is null || pcmShorts is null || pcmBuffer is null)
                        break;
                    
                    readAttempts++;
                    
                    // Read 32-BIT FLOAT data from the stream (the decode source is typically
                    // float too — BASS converts non-float streams when BASS_DATA_FLOAT is
                    // requested, so this is correct for every source). The bytes are NOT the
                    // 16-bit PCM we stream: they are converted below. Feeding float bytes
                    // straight in was the "extremely loud static" bug on the render-device path.
                    var bytesRead = Bass.ReadStreamData(streamToRead, floatBuffer, floatBuffer.Length);
                    
                    // Log periodically
                    if ((DateTime.Now - lastLogTime).TotalSeconds >= 5)
                    {
                        var errorCode = Bass.GetLastError();
                        Plugin.LogInfo("CaptureLoop", $"Stats: attempts={readAttempts}, successful={successfulReads}, totalBytes={_totalBytesRead}, lastRead={bytesRead}, bassError={errorCode}");
                        lastLogTime = DateTime.Now;
                    }
                    
                    if (bytesRead > 0)
                    {
                        _zeroReadStreak = 0;
                        successfulReads++;

                        // Convert float [-1, 1] to clamped 16-bit LE PCM.
                        int sampleCount = bytesRead / 4;
                        if (sampleCount > floatSamples.Length)
                            sampleCount = floatSamples.Length;
                        if (sampleCount == 0)
                        {
                            Thread.Sleep(5);
                            continue;
                        }
                        Buffer.BlockCopy(floatBuffer, 0, floatSamples, 0, sampleCount * 4);
                        for (int i = 0; i < sampleCount; i++)
                        {
                            float f = floatSamples[i];
                            if (f > 1f) f = 1f;
                            else if (f < -1f) f = -1f;
                            pcmShorts[i] = (short)(f * 32767f);
                        }
                        int pcmBytes = sampleCount * 2;
                        Buffer.BlockCopy(pcmShorts, 0, pcmBuffer, 0, pcmBytes);
                        _totalBytesRead += pcmBytes; // converted 16-bit bytes (drives pacing/stats)

                        long bytesPerSecond = _outputSampleRate * OutputChannels * OutputBitDepth / 8;
                        var timestamp = _timeline.StampChunk(pcmBytes, bytesPerSecond);

                        // Raise event with the converted 16-bit PCM chunk.
                        if (pcmBytes > 0)
                        {
                            AudioDataAvailable?.Invoke(this, new AudioDataEventArgs(
                                // Copy only the valid bytes: the buffer is reused by the next read.
                                SlicePcm(pcmBuffer, pcmBytes),
                                timestamp,
                                _outputSampleRate,
                                OutputChannels,
                                OutputBitDepth
                            ));
                        }

                        // Pace to 1× real-time (as in the plugin's decode loops;
                        // but here it ALSO drives MusicBee's render-device playback clock: MusicBee
                        // decodes as fast as we pull, so without pacing the track would race to its
                        // end and the capture would run dry seconds into playback).
                        int sleepMs = _timeline.PacingSleepMs(bytesPerSecond);
                        if (sleepMs > 0)
                        {
                            Thread.Sleep(sleepMs);
                        }
                    }
                    else if (bytesRead == 0)
                    {
                        // No data right now: check the source handle directly after a sustained
                        // silence — BASS_ACTIVE_ENDED means the handed track is fully consumed.
                        _zeroReadStreak++;
                        if (_zeroReadStreak >= 20) // ~100 ms of no data at 5 ms/read
                        {
                            int active = Bass.ChannelIsActive(_streamHandle);
                            if (active == 4) // BASS_ACTIVE_ENDED: the handed track is fully consumed
                            {
                                Plugin.LogInfo("CaptureLoop", "Source decode stream ended (BASS_ACTIVE_ENDED)");
                                _isCapturing = false;
                                StreamEnded?.Invoke(this, EventArgs.Empty);
                                break;
                            }
                        }
                        Thread.Sleep(5);
                    }
                    else
                    {
                        // Error - bytesRead is -1
                        var errorCode = Bass.GetLastError();
                        if (errorCode == 38 || errorCode == 45) // BASS_ERROR_ENDED(38) or BASS_ERROR_DECODEEND(45) — decode stream fully consumed: TRACK END
                        {
                            Plugin.LogInfo("CaptureLoop", $"Decode stream ended (error {errorCode})");
                            _isCapturing = false;
                            StreamEnded?.Invoke(this, EventArgs.Empty);
                            break;
                        }
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
            
            Plugin.LogInfo("CaptureLoop", $"Capture loop ended. Total bytes read: {_totalBytesRead}, Successful reads: {successfulReads}");
        }

        /// <summary>Copies the first <paramref name="length"/> bytes into a fresh array.</summary>
        private static byte[] SlicePcm(byte[] buffer, int length)
        {
            var result = new byte[length];
            Array.Copy(buffer, result, length);
            return result;
        }

        private int CalculateBufferSize(int sampleRate, int channels, int bitDepth, int durationMs)
        {
            var bytesPerSample = bitDepth / 8;
            var samplesPerMs = sampleRate / 1000;
            return samplesPerMs * durationMs * channels * bytesPerSample;
        }

        #endregion

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            Stop();
            _cancellationTokenSource?.Dispose();
            GC.SuppressFinalize(this);
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

        /// <summary>0=stopped, 1=playing, 2=paused, 3=stalled, 4=ended.</summary>
        public static int ChannelIsActive(int handle)
        {
            return BASS_ChannelIsActive(handle);
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

        [DllImport("bass.dll", CharSet = CharSet.Auto)]
        private static extern int BASS_ChannelIsActive(int handle);

        #endregion
    }
}
