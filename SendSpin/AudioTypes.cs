using System;

namespace MusicBeePlugin.SendSpin
{
    /// <summary>
    /// Encoded audio packet raised by a capture service (BASS decode → encode). Timestamp is µs
    /// on the raising service's OWN monotonic clock (Stopwatch since its construction) — consumers
    /// map it into their domain (see SourceRenderDevice._captureEpochUs).
    /// </summary>
    public class AudioDataEventArgs : EventArgs
    {
        public byte[] Data { get; }
        public long Timestamp { get; }
        public int SampleRate { get; }
        public int Channels { get; }
        public int BitDepth { get; }

        public AudioDataEventArgs(byte[] data, long timestamp, int sampleRate, int channels, int bitDepth)
        {
            Data = data;
            Timestamp = timestamp;
            SampleRate = sampleRate;
            Channels = channels;
            BitDepth = bitDepth;
        }
    }
}
