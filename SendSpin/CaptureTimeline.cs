using System;
using System.Diagnostics;

namespace MusicBeePlugin.SendSpin
{
    /// <summary>
    /// Content timeline for the capture loop: chunk timestamps AND 1× real-time pacing.
    ///
    /// Chunk timestamps are CONTENT-spaced (track start + audio delivered before the chunk),
    /// never emission wall-clock: a wall-clock stamp compresses on catch-up bursts after any
    /// hiccup, and MA's source bridge then drops chunks as out-of-order / inserts silence for
    /// the gaps / trims the resampler ratio — audible distortion (spec roles/source/v1.md: the
    /// sample stream must stay continuous; timestamps only anchor it in time).
    ///
    /// Pure and clock-injectable so tests can simulate stalls and bursts deterministically.
    /// </summary>
    internal sealed class CaptureTimeline
    {
        private readonly Func<long> _nowUs;
        private long _trackStartUs;
        private long _producedUs;

        public CaptureTimeline() : this(() => Stopwatch.GetTimestamp() * 1_000_000 / Stopwatch.Frequency) { }

        public CaptureTimeline(Func<long> nowUs)
        {
            _nowUs = nowUs;
            StartTrack();
        }

        /// <summary>New content timeline (track start or seek): the next chunk stamps from 'now'.</summary>
        public void StartTrack()
        {
            _trackStartUs = _nowUs();
            _producedUs = 0;
        }

        /// <summary>
        /// First-sample capture timestamp (µs on the owning clock) for a chunk of
        /// <paramref name="pcmBytes"/> at <paramref name="bytesPerSecond"/>, advancing the
        /// content position by the chunk's audio duration.
        /// </summary>
        public long StampChunk(long pcmBytes, long bytesPerSecond)
        {
            long firstSampleUs = _trackStartUs + _producedUs;
            _producedUs += pcmBytes * 1_000_000 / bytesPerSecond;
            return firstSampleUs;
        }

        /// <summary>
        /// Milliseconds to sleep to hold 1× real-time pacing (the plugin IS MusicBee's playback
        /// clock on the render-device path); 0 means behind — run flat out to catch up.
        /// </summary>
        public int PacingSleepMs(long bytesPerSecond)
        {
            long aheadUs = _producedUs - (_nowUs() - _trackStartUs);
            if (aheadUs <= 0)
                return 0;
            return (int)Math.Min(aheadUs / 1000, 250);
        }
    }
}