using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using MusicBeePlugin.SendSpin;
using MusicBeePlugin.SendSpin.Noise;
using Xunit;

namespace SendSpin.Tests
{
    // Render-device wiring tests (SourceRenderDevice): activation, PlayToDevice capture wiring,
    // play-state stream open/close, chunk re-stamping into the server time domain, seek plumbing,
    // track-end advancement, and the MA pause/resume mirror.
    [Collection(nameof(WireTests))]
    public class RenderDeviceTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "sendspin-render-tests-" + Guid.NewGuid().ToString("N"));

        public RenderDeviceTests() => Directory.CreateDirectory(_dir);
        public void Dispose() => Directory.Delete(_dir, true);

        private sealed class FakeCapture : IRenderAudioCapture
        {
            public event EventHandler<AudioDataEventArgs>? AudioDataAvailable;
            public event EventHandler? StreamEnded;
            public bool IsCapturing { get; private set; }
            public int Handle { get; private set; }
            public bool Owns { get; private set; }
            public int StartCount { get; private set; }
            public int StopCount { get; private set; }
            public long CapturedAudioUs { get; private set; }
            public double? SeekTargetSeconds { get; private set; }
            public int NativeSampleRate => 48000;
            public int NativeChannels => 2;
            public int OutputSampleRate => 48000;
            public string Codec => "pcm";
            public int BitDepth => 16;

            public void Start(int streamHandle, bool ownsStreamHandle)
            {
                Handle = streamHandle;
                Owns = ownsStreamHandle;
                IsCapturing = true;
                StartCount++;
            }

            public void Stop()
            {
                IsCapturing = false;
                StopCount++;
            }

            public void SeekTo(double seconds)
            {
                SeekTargetSeconds = seconds;
                CapturedAudioUs = 0; // SeekTo resets the capture position (mirrors production)
            }

            /// <summary>Fakes the end of the handed decode stream (track fully consumed).</summary>
            public void EndStream()
            {
                IsCapturing = false;
                StreamEnded?.Invoke(this, EventArgs.Empty);
            }

            public void Emit(byte[] data, long captureUs)
            {
                CapturedAudioUs = captureUs;
                AudioDataAvailable?.Invoke(this, new AudioDataEventArgs(data, captureUs, 48000, 2, 16));
            }

            public void Dispose() { }
        }

        /// <summary>Activated device with a paired, Ready connection against a live fake server.</summary>
        private static async Task<(SourceRenderDevice Device, FakeCapture Capture, FakeSendspinServer Server)> NewReadyDeviceAsync(string dir)
        {
            var server = new FakeSendspinServer(48300);
            server.Start();
            var store = new PairingStore(Path.Combine(dir, "pairing.json"), _ => { });
            var identity = SendspinIdentity.Generate();

            byte[] ltPsk = new byte[32];
            using (var rng = System.Security.Cryptography.RandomNumberGenerator.Create())
                rng.GetBytes(ltPsk);
            store.Upsert(new PairingRecord(ltPsk, PskCategory.LongTerm, server.NoiseServerId));
            server.ActualPsk = ltPsk;
            server.AdvertisedPsk = ltPsk;

            var fakeCapture = new FakeCapture();
            var device = new SourceRenderDevice(
                "Music Assistant (Sendspin)",
                new SourceStreamParams(),
                resolveServerUrl: () => server.Url,
                openStreamHandle: url => 77,
                identityProvider: () => identity,
                pairingStoreProvider: () => store,
                captureFactory: () => fakeCapture,
                logger: _ => { });

            Assert.True(device.Activate());
            await server.WaitForClientAsync(TimeSpan.FromSeconds(10));
            await server.RunInitialHandshake();
            server.StartPump();
            await server.SendJsonAsync("server/hello", new JObject { ["name"] = "MA Server" });
            await server.SendJsonAsync("server/activate", new JObject
            {
                ["activities"] = new JArray { "playback" },
                ["active_roles"] = new JArray { "source@v1" },
            });
            await server.WaitFor("client/state", TimeSpan.FromSeconds(10));
            return (device, fakeCapture, server);
        }

        [Fact]
        public async Task PlayToDevice_CaptureWiring_AndChunkRestamping()
        {
            var (device, fakeCapture, server) = await NewReadyDeviceAsync(_dir);
            try
            {
                // Deactivate before activate: no-ops are safe (fresh device above is active).
                Assert.True(device.IsActive);

                // PlayToDevice wires the capture to MusicBee's (unowned) handle.
                Assert.True(device.PlayToDevice("track.mp3", 42));
                Assert.True(fakeCapture.IsCapturing && fakeCapture.Handle == 42 && !fakeCapture.Owns,
                    "capture started on MusicBee's handle (not owned)");
                Assert.Null(server.LastReceived("client_stream/start"));

                // Server authorizes; the first emitted packet opens the stream.
                await server.SendJsonAsync("server/command", new JObject
                {
                    ["source"] = new JObject { ["command"] = "start" },
                });
                await Task.Delay(200);

                fakeCapture.Emit(new byte[] { 1, 1, 1 }, 1_000);
                JObject streamStart = await server.WaitFor("client_stream/start", TimeSpan.FromSeconds(10));
                Assert.Equal(fakeCapture.Codec, streamStart["payload"]?["source"]?["codec"]?.Value<string>());

                fakeCapture.Emit(new byte[] { 2, 2, 2 }, 21_000);
                // The first _binary is the stream-opening packet (1,1,1); the asserted one follows.
                await server.WaitFor("_binary", TimeSpan.FromSeconds(10));
                JObject bin2 = await server.WaitFor("_binary", TimeSpan.FromSeconds(10));
                byte[] frame = Convert.FromBase64String(bin2["payload"]?["data"]?.Value<string>() ?? "");
                long serverTs = 0;
                for (int b = 0; b < 8; b++)
                    serverTs = (serverTs << 8) | frame[b];
                // The controller stamps ServerClock-epoch + capture-µs; converted to the server domain by
                // the connection's offset. The epoch is the ServerClock value at capture creation —
                // reconstructed here as (stamped − offset − captureµs).
                long impliedEpoch = serverTs - (server.NowUs() - ServerClock.NowUs()) - 21_000;
                long nowEpoch = ServerClock.NowUs();
                Assert.True(Math.Abs(impliedEpoch - nowEpoch) < 2_000_000,
                    $"capture epoch mapped into the ServerClock domain (drift {Math.Abs(impliedEpoch - nowEpoch)} µs)");
                Assert.Equal(new byte[] { 2, 2, 2 }, frame[8..]);

                // Pause: capture stops and the stream ends.
                device.HandlePlayStateChanged(RenderPlayState.Paused, "track.mp3");
                await server.WaitFor("client_stream/end", TimeSpan.FromSeconds(10));
                Assert.False(fakeCapture.IsCapturing);
                Assert.Equal(42, fakeCapture.Handle); // pause does not clear the PlayToDevice handle

                // Resume without a server start: the controller reuses the PlayToDevice handle.
                device.HandlePlayStateChanged(RenderPlayState.Playing, "track.mp3");
                Assert.True(fakeCapture.IsCapturing && fakeCapture.Handle == 42 && !fakeCapture.Owns);

                // Stop: handle cleared; the next Playing opens a fresh plugin-owned stream.
                device.HandlePlayStateChanged(RenderPlayState.Stopped, "track.mp3");
                device.HandlePlayStateChanged(RenderPlayState.Playing, "next-track.mp3");
                Assert.True(fakeCapture.IsCapturing && fakeCapture.Handle == 77 && fakeCapture.Owns,
                    "playing after stop opens a fresh plugin-owned stream handle (77)");
            }
            finally
            {
                device.Deactivate();
                device.Dispose();
            }
        }

        [Fact]
        public async Task Seek_PlumbsToCapture_AndReportsPosition()
        {
            var (device, fakeCapture, server) = await NewReadyDeviceAsync(_dir);
            try
            {
                Assert.True(device.PlayToDevice("track.mp3", 42));
                fakeCapture.Emit(new byte[] { 1, 1, 1 }, 1_234_000); // position reports the capture clock

                Assert.Equal(1234, device.PlayPositionMs);

                device.SetPlayPosition(5_000);
                Assert.Equal(5.0, fakeCapture.SeekTargetSeconds);
                Assert.Equal(5_000, device.PlayPositionMs); // seek offset reported, not 0

                // Deactivate resets the position.
                device.Deactivate();
                Assert.Equal(0, device.PlayPositionMs);
            }
            finally
            {
                device.Dispose();
            }
            await Task.Delay(100);
            Assert.False(fakeCapture.IsCapturing);
        }

        [Fact]
        public async Task TrackEnded_FiresAdvancementEvent()
        {
            var (device, fakeCapture, server) = await NewReadyDeviceAsync(_dir);
            var endedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            device.TrackEnded += (_, _) => endedTcs.TrySetResult(true);
            try
            {
                Assert.True(device.PlayToDevice("track.mp3", 42));
                fakeCapture.EndStream(); // decode stream fully consumed
                await TestAwait.WaitEvent(endedTcs.Task, TimeSpan.FromSeconds(5), "TrackEnded");
                Assert.False(fakeCapture.IsCapturing);
            }
            finally
            {
                device.Dispose();
            }
        }

        [Fact]
        public async Task MaPauseResume_MirrorsThroughDeviceEvents()
        {
            var (device, fakeCapture, server) = await NewReadyDeviceAsync(_dir);
            var pauseTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var resumeTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            device.PlaybackShouldPause += (_, _) => pauseTcs.TrySetResult(true);
            device.PlaybackShouldResume += (_, _) => resumeTcs.TrySetResult(true);
            try
            {
                Assert.True(device.PlayToDevice("track.mp3", 42));

                // MA stops the input while MusicBee is feeding it → the plugin must pause MusicBee.
                await server.SendJsonAsync("server/command", new JObject
                {
                    ["source"] = new JObject { ["command"] = "start" },
                });
                await Task.Delay(150);
                fakeCapture.Emit(new byte[] { 1, 2, 3 }, ServerClock.NowUs());
                await Task.Delay(150);

                await server.SendJsonAsync("server/command", new JObject
                {
                    ["source"] = new JObject { ["command"] = "stop" },
                });
                await TestAwait.WaitEvent(pauseTcs.Task, TimeSpan.FromSeconds(5), "PlaybackShouldPause");

                await server.SendJsonAsync("server/command", new JObject
                {
                    ["source"] = new JObject { ["command"] = "start" },
                });
                await TestAwait.WaitEvent(resumeTcs.Task, TimeSpan.FromSeconds(5), "PlaybackShouldResume");
            }
            finally
            {
                device.Dispose();
            }
        }

        [Fact]
        public async Task PlayToDevice_WithoutConnection_Declines()
        {
            // Activate against a resolver that never resolves: the device is active but no
            // connection exists yet — PlayToDevice must decline so MusicBee reports the device
            // unavailable (instead of playing into a capture with nowhere to send).
            var store = new PairingStore(Path.Combine(_dir, "pairing.json"), _ => { });
            var identity = SendspinIdentity.Generate();
            var device = new SourceRenderDevice(
                "Music Assistant (Sendspin)",
                new SourceStreamParams(),
                resolveServerUrl: () => null,   // discovery never finds a server
                openStreamHandle: url => 77,
                identityProvider: () => identity,
                pairingStoreProvider: () => store,
                captureFactory: () => new FakeCapture(),
                logger: _ => { });

            Assert.True(device.Activate());
            Assert.True(device.IsActive);
            await Task.Delay(100); // give the resolve loop a beat — it will never connect
            Assert.False(device.PlayToDevice("track.mp3", 42), "no connection → PlayToDevice declines");
            device.Dispose();
        }
    }
}