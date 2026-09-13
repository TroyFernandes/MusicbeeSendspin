using System;
using MusicBeePlugin.SendSpin;
using Xunit;

namespace SendSpin.Tests
{
    /// <summary>
    /// The capture loop's chunk timeline (content-spaced timestamps + 1× real-time pacing).
    /// These guard the hi-res distortion fix: MA's source bridge drops chunks whose timestamps
    /// compress on catch-up bursts, so stamps must follow CONTENT time, never emission wall time.
    /// </summary>
    public class CaptureTimelineTests
    {
        private const long Bps = 96000L * 2 * 2;      // 96 kHz stereo 16-bit
        private const long ChunkBytes = Bps / 50;     // a 20 ms chunk

        /// <summary>Fake clock advanced manually by the test (µs).</summary>
        private sealed class FakeClock
        {
            public long Now;
        }

        private static CaptureTimeline NewTimeline(FakeClock clock)
        {
            return new CaptureTimeline(() => clock.Now);
        }

        [Fact]
        public void FirstChunkStampsFromTrackStart()
        {
            var clock = new FakeClock { Now = 1_000_000 };
            var t = NewTimeline(clock);

            Assert.Equal(1_000_000, t.StampChunk(ChunkBytes, Bps));
        }

        [Fact]
        public void ConsecutiveChunksAreContentSpaced_EvenWhenWallTimeJumps()
        {
            // The distortion bug: a hiccup (GC, decode stall) advances wall time; the NEXT
            // chunks must still be spaced by their audio duration, not by emission time.
            var clock = new FakeClock { Now = 1_000_000 };
            var t = NewTimeline(clock);

            long ts1 = t.StampChunk(ChunkBytes, Bps);   // content 0-20ms
            clock.Now += 100_000;                       // hiccup: wall jumps 100 ms
            long ts2 = t.StampChunk(ChunkBytes, Bps);   // content 20-40ms
            long ts3 = t.StampChunk(ChunkBytes, Bps);   // content 40-60ms

            Assert.Equal(ts1 + 20_000, ts2);
            Assert.Equal(ts2 + 20_000, ts3);
        }

        [Fact]
        public void ContentTimeAnchorsToWallTime_AcrossAStall()
        {
            // After a stall the content timeline must lag wall time by exactly the stall
            // (not run ahead of it, and not rewind).
            var clock = new FakeClock { Now = 1_000_000 };
            var t = NewTimeline(clock);

            t.StampChunk(ChunkBytes, Bps);              // on-time
            clock.Now += 50_000;                        // 50 ms stall, no chunks produced
            long ts2 = t.StampChunk(ChunkBytes, Bps);   // content 20-40 ms

            // ts2 sits 50 ms behind the wall clock (1_050_000 + 20_000 - 50_000 = 1_020_000)…
            Assert.Equal(1_020_000, ts2);
            // …and the next chunk restores real-time spacing.
            long ts3 = t.StampChunk(ChunkBytes, Bps);
            Assert.Equal(ts2 + 20_000, ts3);
        }

        [Fact]
        public void StartTrack_ReanchorsToNow_AndNeverRewinds()
        {
            // New track (or seek): timestamps restart from 'now', i.e. never earlier than the
            // last stamp of the previous track (MA treats backward jumps as broken streams).
            var clock = new FakeClock { Now = 1_000_000 };
            var t = NewTimeline(clock);

            long last = 0;
            for (int i = 0; i < 100; i++)               // ~2 s of audio
                last = t.StampChunk(ChunkBytes, Bps);
            clock.Now = last + 5_000;                   // track change a little after the last chunk
            t.StartTrack();

            long first = t.StampChunk(ChunkBytes, Bps);
            Assert.Equal(last + 5_000, first);
            Assert.True(first > last, "new track stamps advance past the previous track's last stamp");
        }

        [Fact]
        public void PacingSleeps_WhenAheadOfRealTime()
        {
            var clock = new FakeClock { Now = 1_000_000 };
            var t = NewTimeline(clock);

            // One 20 ms chunk produced instantly: 20 ms ahead → sleep ~20 ms.
            t.StampChunk(ChunkBytes, Bps);
            int sleep = t.PacingSleepMs(Bps);
            Assert.InRange(sleep, 19, 21);

            // Two chunks without wall time advancing: ~40 ms ahead.
            t.StampChunk(ChunkBytes, Bps);
            Assert.InRange(t.PacingSleepMs(Bps), 39, 41);
        }

        [Fact]
        public void PacingRunsFlatOut_WhenBehind()
        {
            var clock = new FakeClock { Now = 1_000_000 };
            var t = NewTimeline(clock);

            t.StampChunk(ChunkBytes, Bps);
            clock.Now += 100_000;                       // wall raced 100 ms ahead of content (hiccup)
            Assert.Equal(0, t.PacingSleepMs(Bps));      // catch up, no sleep
        }

        [Fact]
        public void PacingSleepIsCapped_At250Ms()
        {
            var clock = new FakeClock { Now = 1_000_000 };
            var t = NewTimeline(clock);

            // 5 s of content produced in one burst → far ahead, but sleep must cap at 250 ms.
            for (int i = 0; i < 250; i++)
                t.StampChunk(ChunkBytes, Bps);
            Assert.Equal(250, t.PacingSleepMs(Bps));
        }
    }
}