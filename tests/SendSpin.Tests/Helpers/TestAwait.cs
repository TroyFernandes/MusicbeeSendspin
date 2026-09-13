using System;
using System.Threading.Tasks;
using MusicBeePlugin.SendSpin;

namespace SendSpin.Tests
{
    /// <summary>Shared helpers for the wire-level connection tests.</summary>
    internal static class TestAwait
    {
        /// <summary>Waits until the connection reaches a matching state; throws on timeout.</summary>
        public static async Task<SourceConnectionState> WaitFor(SourceConnection conn, Func<SourceConnectionState, bool> match, TimeSpan timeout)
        {
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            EventHandler<SourceConnectionState> handler = (_, s) => { if (match(s)) tcs.TrySetResult(true); };
            conn.StateChanged += handler;
            if (match(conn.State))
                tcs.TrySetResult(true);
            var winner = tcs.Task;
            var delay = Task.Delay(timeout);
            var first = await Task.WhenAny(winner, delay);
            conn.StateChanged -= handler;
            if (first == winner)
                return conn.State;
            throw new TimeoutException($"state condition not met within {timeout.TotalSeconds}s (state={conn.State})");
        }

        /// <summary>Waits for a TCS (event) or times out — Assert-style helper for event captures.</summary>
        public static async Task WaitEvent(Task task, TimeSpan timeout, string what)
        {
            var first = await Task.WhenAny(task, Task.Delay(timeout));
            if (first != task)
                throw new TimeoutException($"event did not fire within {timeout.TotalSeconds}s: {what}");
        }
    }
}