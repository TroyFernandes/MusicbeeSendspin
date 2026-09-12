using System;

namespace MusicBeePlugin.SendSpin
{
    /// <summary>
    /// SendSpin "server" clock, verified against sendspin-go v1.8.2
    /// (pkg/sync/clock.go + pkg/protocol/messages.go, the version echolocal runs):
    ///
    ///  - server/time replies (server_received, server_transmitted) and binary
    ///    audio chunk timestamps must be in the SAME domain the speaker's
    ///    ServerToLocalTime maps: monotonic microseconds since the server's
    ///    clock epoch (Go: time.Since(c.clock) in µs — i.e. SERVER UPTIME).
    ///    The reference server re-anchors on every stream: the chunk's play
    ///    time is computed from the server's wall clock at send time, so the
    ///    speaker's linear map converges to wall≈server-uptime+const within
    ///    one stream session.
    ///  - client/time t1 and the echoed client_transmitted are UNIX epoch µs
    ///    (the speaker's monotonic clock is epoch-based; GetTimeOffset is
    ///    never used by v1.8.2 players).
    ///
    /// Using one uptime-µs clock for all server-domain fields (instead of the
    /// old mix of DateTime.UtcNow ticks and DateTime.Now ticks) removes the
    /// epoch mismatch and the wall/monotonic domain jump that made the
    /// speaker's clock sync produce a meaningless offset and drop audio.
    /// </summary>
    public static class ServerClock
    {
        private static readonly DateTime Epoch = DateTime.UtcNow;

        /// <summary>
        /// Server uptime in microseconds (monotonic within a session, like Go's
        /// time.Since). This is the domain for server_received,
        /// server_transmitted and audio chunk timestamps.
        /// </summary>
        public static long NowUs() => (long)(DateTime.UtcNow.Subtract(Epoch).TotalSeconds * 1_000_000);
    }
}
