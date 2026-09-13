using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using MusicBeePlugin.SendSpin;
using MusicBeePlugin.SendSpin.Noise;
using Xunit;

namespace SendSpin.Tests
{
    // Full-loop wire tests for SourceConnection against the in-process fake Sendspin server (real
    // WebSocket, real Noise): hello exchange shape, the pairing_psk pairing flow end to end, clock
    // sync (client/time burst → offset estimate), command authorization, streaming (lazy
    // client_stream/start, server-domain chunk stamps, pause/resume mirror), and the stale-server
    // (credential mismatch → no grant, reconnect keeps trying) case. All in-process — no external
    // servers.
    [Collection(nameof(WireTests))]
    public class SourceConnectionTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "sendspin-src-tests-" + Guid.NewGuid().ToString("N"));

        public SourceConnectionTests() => Directory.CreateDirectory(_dir);
        public void Dispose() => Directory.Delete(_dir, true);

        private static SourceConnection NewConnection(Uri url, string dir, SendspinIdentity? identity = null)
        {
            var store = new PairingStore(Path.Combine(dir, "pairing.json"), _ => { });
            return new SourceConnection(
                url, "Music Assistant (Sendspin)", "2.0.0",
                identity ?? SendspinIdentity.Generate(), store,
                NoiseCipherSuite.ChaChaPoly, _ => { });
        }

        private static async Task AssertClientHelloShape(FakeSendspinServer server, string expectedTrust)
        {
            JObject hello = await server.WaitFor("client/hello", TimeSpan.FromSeconds(10));
            var p = hello["payload"]!;
            Assert.Equal("Music Assistant (Sendspin)", p["name"]?.Value<string>());
            Assert.Equal(expectedTrust, p["trust_level"]?.Value<string>());
            Assert.Contains("source@v1", p["supported_roles"]?.ToObject<JArray>()?.Select(t => t.Value<string>()) ?? Array.Empty<string>());
            Assert.NotEmpty(p["supported_pair_methods"]?.ToObject<JArray>() ?? new JArray());
            Assert.True((bool?)p["unpaired_access"]?["enabled"] == true, "unpaired_access enabled advertised");
            Assert.Equal("MusicBee", p["device_info"]?["product_name"]?.Value<string>());
        }

        /// <summary>Brings a freshly started connection through handshake → hello → granted → Ready.</summary>
        private static async Task RunToReady(FakeSendspinServer server, SourceConnection conn, string trust = "none")
        {
            await server.WaitForClientAsync(TimeSpan.FromSeconds(10));
            await server.RunInitialHandshake();
            server.StartPump();
            await server.SendJsonAsync("server/hello", new JObject { ["name"] = "MA Server" });
            await server.WaitFor("client/hello", TimeSpan.FromSeconds(10));
            await server.SendJsonAsync("server/activate", new JObject
            {
                ["activities"] = new JArray { "playback" },
                ["active_roles"] = new JArray { "source@v1" },
            });
            await TestAwait.WaitFor(conn, s => s == SourceConnectionState.Ready, TimeSpan.FromSeconds(10));
        }

        // ------------------------------------------------------------------
        // 1. Unpaired idle: handshake OK, activate without roles → Unpaired, no clock sync.
        // ------------------------------------------------------------------
        [Fact]
        public async Task UnpairedConnection_Idles_WithoutClockSync()
        {
            using var server = new FakeSendspinServer(48100);
            server.Start();
            var conn = NewConnection(server.Url, _dir);

            conn.Start();
            await server.WaitForClientAsync(TimeSpan.FromSeconds(10));
            await server.RunInitialHandshake();
            server.StartPump();
            // The server speaks first after transport mode: server/hello, then the client answers.
            await server.SendJsonAsync("server/hello", new JObject { ["name"] = "MA Server" });
            await AssertClientHelloShape(server, expectedTrust: "none");

            // No pairing, no grant: server/activate with no roles.
            await server.SendJsonAsync("server/activate", new JObject
            {
                ["activities"] = new JArray(),
                ["active_roles"] = new JArray(),
            });

            var finalState = await TestAwait.WaitFor(conn,
                s => s is SourceConnectionState.Unpaired or SourceConnectionState.Ready, TimeSpan.FromSeconds(10));
            Assert.Equal(SourceConnectionState.Unpaired, finalState);
            Assert.False(conn.IsSourceGranted);

            // The client must NOT have started clock sync before the grant: no client/time burst.
            await Task.Delay(700);
            Assert.Null(server.LastReceived("client/time"));
            Assert.Null(server.LastReceived("client/state"));

            conn.Stop();
            conn.Dispose();
        }

        // ------------------------------------------------------------------
        // 2. Pairing flow: sentinel → re-handshake to pairing PSK → pair-finalize → persist →
        //    re-handshake to long-term → grant → Ready + clock sync + start/stop commands.
        // ------------------------------------------------------------------
        [Fact]
        public async Task PairingFlow_EndToEnd()
        {
            using var server = new FakeSendspinServer(48150);
            server.Start();
            var store = new PairingStore(Path.Combine(_dir, "pairing.json"), _ => { });
            var conn = new SourceConnection(server.Url, "Music Assistant (Sendspin)", "2.0.0",
                SendspinIdentity.Generate(), store, NoiseCipherSuite.ChaChaPoly, _ => { });

            var pairedTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            conn.PairingCompleted += (_, serverId) => pairedTcs.TrySetResult(serverId);

            conn.Start();
            await server.WaitForClientAsync(TimeSpan.FromSeconds(10));
            await server.RunInitialHandshake(); // sentinel
            server.StartPump();
            await server.SendJsonAsync("server/hello", new JObject { ["name"] = "MA Server" });
            await AssertClientHelloShape(server, expectedTrust: "none");

            // Operator pasted the token: server re-handshakes to the client's PAIRING PSK and runs
            // the pairing exchange (aiosendspin _rehandshake_for_pairing_if_needed → pairing activate).
            byte[] pairingPsk = store.EnsurePairingPsk();
            await server.TriggerRehandshake(pairingPsk);
            await server.SendJsonAsync("server/hello", new JObject { ["name"] = "MA Server" });
            await server.WaitFor("client/hello", TimeSpan.FromSeconds(10));

            await server.SendJsonAsync("server/activate", new JObject
            {
                ["activities"] = new JArray { "pairing" },
                ["active_roles"] = new JArray(),
                ["pairing"] = new JObject { ["method"] = "pairing_psk" },
            });

            // The client must finalize with a fresh 32-byte base64url long-term PSK.
            JObject finalize = await server.WaitFor("client/pair-finalize", TimeSpan.FromSeconds(10));
            string pskB64 = finalize["payload"]?["long_term_psk"]?.Value<string>() ?? "";
            Assert.Equal(43, pskB64.Length);
            byte[] deliveredPsk = Base64UrlText.Decode(pskB64);
            Assert.Equal(32, deliveredPsk.Length);
            Assert.NotEqual(pairingPsk, deliveredPsk);

            // Server persists, acknowledges; client persists its record on server/pair-finalize.
            await server.SendJsonAsync("server/pair-finalize", new JObject());
            await TestAwait.WaitEvent(pairedTcs.Task, TimeSpan.FromSeconds(10), "PairingCompleted");
            Assert.Equal(server.NoiseServerId, pairedTcs.Task.Result);

            var record = store.LongTermFor(server.NoiseServerId);
            Assert.NotNull(record);
            Assert.Equal(deliveredPsk, record!.Psk.Span.ToArray());
            Assert.Equal(PskCategory.LongTerm, record.Category);

            // Server re-handshakes to the new long-term PSK and re-runs hello + activation, now
            // granting source@v1 (the connection is long-term paired).
            await server.TriggerRehandshake(deliveredPsk);
            await server.SendJsonAsync("server/hello", new JObject { ["name"] = "MA Server" });
            JObject hello2 = await server.WaitFor("client/hello", TimeSpan.FromSeconds(10));
            Assert.Equal("user", hello2["payload"]?["trust_level"]?.Value<string>());

            await server.SendJsonAsync("server/activate", new JObject
            {
                ["activities"] = new JArray { "playback" },
                ["active_roles"] = new JArray { "source@v1" },
            });

            var state = await TestAwait.WaitFor(conn, s => s == SourceConnectionState.Ready, TimeSpan.FromSeconds(10));
            Assert.Equal(SourceConnectionState.Ready, state);
            Assert.True(conn.IsSourceGranted);

            // Clock sync: the burst fires client/time; the fake server answers; then client/state
            // must arrive with available=true + the source object.
            JObject stateMsg = await server.WaitFor("client/state", TimeSpan.FromSeconds(10));
            Assert.True(stateMsg["payload"]?["available"]?.Value<bool>() == true);
            Assert.NotNull(stateMsg["payload"]?["source"] as JObject);
            Assert.True(conn.IsClockConverged);

            // Offset sanity: server epoch is far from the client's; estimate should sit within a
            // few ms of (serverNow - clientNow) for the in-process pair.
            long expectedOffset = server.NowUs() - ServerClock.NowUs();
            Assert.True(Math.Abs(conn.ClockOffsetUs - expectedOffset) < 200_000,
                $"clock offset ≈ server-client delta (got {conn.ClockOffsetUs}, expected ~{expectedOffset})");

            // Command authorization.
            var startTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            conn.StreamStartRequested += (_, _) => startTcs.TrySetResult(true);
            await server.SendJsonAsync("server/command", new JObject
            {
                ["source"] = new JObject { ["command"] = "start" },
            });
            await TestAwait.WaitEvent(startTcs.Task, TimeSpan.FromSeconds(5), "StreamStartRequested");
            Assert.True(conn.StreamStartAuthorized);

            await server.SendJsonAsync("server/command", new JObject
            {
                ["source"] = new JObject { ["command"] = "stop" },
            });
            await Task.Delay(300);
            Assert.False(conn.StreamStartAuthorized);

            conn.Stop();
            conn.Dispose();
        }

        // ------------------------------------------------------------------
        // 3. Already paired: reconnect uses the stored long-term PSK directly + streaming flow.
        // ------------------------------------------------------------------
        [Fact]
        public async Task PairedReconnect_UsesStoredLongTermPsk_AndStreams()
        {
            using var server = new FakeSendspinServer(48200);
            server.Start();
            var store = new PairingStore(Path.Combine(_dir, "pairing.json"), _ => { });
            var identity = SendspinIdentity.Generate();

            // Pre-pair: the record a previous session persisted.
            byte[] ltPsk = new byte[32];
            using (var rng = System.Security.Cryptography.RandomNumberGenerator.Create())
                rng.GetBytes(ltPsk);
            store.Upsert(new PairingRecord(ltPsk, PskCategory.LongTerm, server.NoiseServerId));

            var conn = new SourceConnection(server.Url, "Music Assistant (Sendspin)", "2.0.0",
                identity, store, NoiseCipherSuite.ChaChaPoly, _ => { });

            var grantedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            conn.SourceRoleChanged += (_, e) => { if (e.Granted) grantedTcs.TrySetResult(true); };

            // Server admits the client with the stored long-term PSK (aiosendspin _psk_provider).
            server.ActualPsk = ltPsk;
            server.AdvertisedPsk = ltPsk;
            conn.Start();
            await server.WaitForClientAsync(TimeSpan.FromSeconds(10));
            await server.RunInitialHandshake();
            server.StartPump();

            // Server speaks first after transport mode.
            await server.SendJsonAsync("server/hello", new JObject { ["name"] = "MA Server" });
            JObject hello = await server.WaitFor("client/hello", TimeSpan.FromSeconds(10));
            Assert.Equal("user", hello["payload"]?["trust_level"]?.Value<string>());

            await server.SendJsonAsync("server/activate", new JObject
            {
                ["activities"] = new JArray { "playback" },
                ["active_roles"] = new JArray { "source@v1" },
            });
            var state = await TestAwait.WaitFor(conn, s => s == SourceConnectionState.Ready, TimeSpan.FromSeconds(10));
            Assert.Equal(SourceConnectionState.Ready, state);
            await TestAwait.WaitEvent(grantedTcs.Task, TimeSpan.FromSeconds(5), "SourceRoleChanged granted");

            JObject stateMsg = await server.WaitFor("client/state", TimeSpan.FromSeconds(10));
            Assert.True(stateMsg["payload"]?["available"]?.Value<bool>() == true);
            Assert.True(conn.IsClockConverged);

            await TestStreaming(server, conn);

            conn.Stop();
            conn.Dispose();
        }

        // ------------------------------------------------------------------
        // Streaming: start authorization → lazy client_stream/start on first packet → paced binary
        // chunks with server-domain stamps; oversized chunks still pass; stop → client_stream/end;
        // MA pause/resume mirror.
        // ------------------------------------------------------------------
        private static async Task TestStreaming(FakeSendspinServer server, SourceConnection conn)
        {
            // Before any server start authorization: packets are dropped, no stream opens.
            conn.EnqueueEncodedAudio(new byte[] { 1, 2, 3 }, ServerClock.NowUs());
            await Task.Delay(150);
            Assert.False(conn.IsStreamOpen);
            Assert.Null(server.LastReceived("_binary"));

            // Server authorizes.
            var startTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            conn.StreamStartRequested += (_, _) => startTcs.TrySetResult(true);
            await server.SendJsonAsync("server/command", new JObject
            {
                ["source"] = new JObject { ["command"] = "start" },
            });
            await TestAwait.WaitEvent(startTcs.Task, TimeSpan.FromSeconds(5), "source:start");
            Assert.True(conn.StreamStartAuthorized);
            await Task.Delay(150);
            Assert.False(conn.IsStreamOpen, "stream does not open on start alone (opens lazily on first packet)");

            // Feed three packets; the first opens the stream.
            long capture1 = ServerClock.NowUs();
            conn.EnqueueEncodedAudio(new byte[] { 10, 11, 12 }, capture1);
            long capture2 = ServerClock.NowUs();
            conn.EnqueueEncodedAudio(new byte[] { 20, 21, 22 }, capture2);
            long capture3 = ServerClock.NowUs();
            conn.EnqueueEncodedAudio(new byte[] { 30, 31, 32 }, capture3);

            JObject streamStart = await server.WaitFor("client_stream/start", TimeSpan.FromSeconds(10));
            var src = streamStart["payload"]?["source"] as JObject;
            Assert.Equal("opus", src?["codec"]?.Value<string>());
            Assert.Equal(2, src?["channels"]?.Value<int>());
            Assert.Equal(48000, src?["sample_rate"]?.Value<int>());
            Assert.Equal(16, src?["bit_depth"]?.Value<int>());
            Assert.Equal(SourceConnectionState.Streaming, conn.State);

            // Collect the binary audio frames.
            var audio = new List<(long Ts, byte[] Payload)>();
            for (int i = 0; i < 3; i++)
            {
                JObject bin = await server.WaitFor("_binary", TimeSpan.FromSeconds(10));
                Assert.Equal(12, bin["payload"]?["message_type"]?.Value<int>());
                // The fake strips the 0x0C type byte when recording, so `data` is [8-byte BE ts][payload].
                byte[] frame = Convert.FromBase64String(bin["payload"]?["data"]?.Value<string>() ?? "");
                long ts = 0;
                for (int b = 0; b < 8; b++)
                    ts = (ts << 8) | frame[b];
                audio.Add((ts, frame[8..]));
            }
            Assert.Equal(new byte[] { 10, 11, 12 }, audio[0].Payload);
            Assert.Equal(new byte[] { 20, 21, 22 }, audio[1].Payload);
            Assert.Equal(new byte[] { 30, 31, 32 }, audio[2].Payload);
            long offset = conn.ClockOffsetUs;
            Assert.True(Math.Abs(audio[0].Ts - (capture1 + offset)) < 100_000
                        && Math.Abs(audio[1].Ts - (capture2 + offset)) < 100_000
                        && Math.Abs(audio[2].Ts - (capture3 + offset)) < 100_000,
                "chunk timestamps map the capture clock into the server time domain (+offset)");

            // An oversized chunk (above the 150 ms cap) is logged but still sent — the server, not
            // the client, decides what to do with it.
            conn.EnqueueEncodedAudio(new byte[conn.StreamParams.MaxPacketBytes + 1], ServerClock.NowUs());
            JObject oversized = await server.WaitFor("_binary", TimeSpan.FromSeconds(10));
            Assert.Equal(conn.StreamParams.MaxPacketBytes + 1,
                Convert.FromBase64String(oversized["payload"]?["data"]?.Value<string>() ?? "").Length - 8);

            // MusicBee stops: the stream ends.
            conn.NotifyPlaybackStopped();
            await server.WaitFor("client_stream/end", TimeSpan.FromSeconds(10));
            Assert.False(conn.IsStreamOpen);
            Assert.True(conn.StreamStartAuthorized, "track end keeps authorization (next track resumes without a new MA start)");
            Assert.Equal(SourceConnectionState.Ready, conn.State);

            // A server stop while idle is ignored gracefully (idempotent).
            await server.SendJsonAsync("server/command", new JObject
            {
                ["source"] = new JObject { ["command"] = "stop" },
            });
            await Task.Delay(200);
            Assert.False(conn.IsStreamOpen);

            // --- MA pause/resume mirror: source.stop/start while fed → SourceShouldPause/Resume ---
            var pauseTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            conn.SourceShouldPause += (_, _) => pauseTcs.TrySetResult(true);

            // Re-authorize and feed so _streamWasFed is set.
            await server.SendJsonAsync("server/command", new JObject
            {
                ["source"] = new JObject { ["command"] = "start" },
            });
            await TestAwait.WaitUntil(() => conn.StreamStartAuthorized, TimeSpan.FromSeconds(5), "start authorization");
            conn.EnqueueEncodedAudio(new byte[] { 4, 4, 4 }, ServerClock.NowUs());
            await TestAwait.WaitUntil(() => conn.IsStreamOpen, TimeSpan.FromSeconds(5), "stream fed");

            await server.SendJsonAsync("server/command", new JObject
            {
                ["source"] = new JObject { ["command"] = "stop" },
            });
            await TestAwait.WaitEvent(pauseTcs.Task, TimeSpan.FromSeconds(5), "SourceShouldPause");
            await server.WaitFor("client_stream/end", TimeSpan.FromSeconds(10));

            // MA start → resume mirror.
            var resumeTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            conn.SourceShouldResume += (_, _) => resumeTcs.TrySetResult(true);
            await server.SendJsonAsync("server/command", new JObject
            {
                ["source"] = new JObject { ["command"] = "start" },
            });
            await TestAwait.WaitEvent(resumeTcs.Task, TimeSpan.FromSeconds(5), "SourceShouldResume");
        }

        // ------------------------------------------------------------------
        // 4. Stale server (references a PSK the client lacks): sentinel fallback, handshake fails,
        //    no grant, reconnect loop keeps trying.
        // ------------------------------------------------------------------
        [Fact]
        public async Task StaleServer_NeverGrants_KeepsReconnecting()
        {
            using var server = new FakeSendspinServer(48250);
            server.Start();
            var conn = NewConnection(server.Url, _dir);

            // Server references a random PSK the client has never stored. The client falls back to
            // Sentinel (initial-handshake miss), the fake validates msg2 against the random PSK and
            // fails — the session never becomes Ready.
            byte[] stalePsk = new byte[32];
            using (var rng = System.Security.Cryptography.RandomNumberGenerator.Create())
                rng.GetBytes(stalePsk);
            server.AdvertisedPsk = stalePsk;
            server.ActualPsk = stalePsk;

            var grantedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            conn.SourceRoleChanged += (_, e) => { if (e.Granted) grantedTcs.TrySetResult(true); };

            conn.Start();
            await server.WaitForClientAsync(TimeSpan.FromSeconds(10));
            try
            {
                // The fake validates msg2 against the stale PSK and fails — exactly what a
                // server whose pairing record the client lost does (silent failure, then close).
                await server.RunInitialHandshake();
            }
            catch (Exception ex)
            {
                // expected
                _ = ex.Message;
            }

            await Task.Delay(2000);
            Assert.NotEqual(TaskStatus.RanToCompletion, grantedTcs.Task.Status);
            Assert.NotEqual(SourceConnectionState.Ready, conn.State);
            Assert.True(conn.State is SourceConnectionState.Connecting or SourceConnectionState.Handshaking,
                "client is still reconnecting (state=" + conn.State + ")");

            conn.Stop();
            conn.Dispose();
        }

        // ------------------------------------------------------------------
        // 5. server/unpair: the pairing record is removed on both sides and the session closes.
        // ------------------------------------------------------------------
        [Fact]
        public async Task ServerUnpair_RemovesRecord_AndCloses()
        {
            using var server = new FakeSendspinServer(48350);
            server.Start();
            var store = new PairingStore(Path.Combine(_dir, "pairing.json"), _ => { });
            var identity = SendspinIdentity.Generate();
            byte[] ltPsk = new byte[32];
            using (var rng = System.Security.Cryptography.RandomNumberGenerator.Create())
                rng.GetBytes(ltPsk);
            store.Upsert(new PairingRecord(ltPsk, PskCategory.LongTerm, server.NoiseServerId));
            server.ActualPsk = ltPsk;
            server.AdvertisedPsk = ltPsk;

            var conn = new SourceConnection(server.Url, "Music Assistant (Sendspin)", "2.0.0",
                identity, store, NoiseCipherSuite.ChaChaPoly, _ => { });
            conn.Start();
            await server.WaitForClientAsync(TimeSpan.FromSeconds(10));
            await server.RunInitialHandshake();
            server.StartPump();
            await server.SendJsonAsync("server/hello", new JObject { ["name"] = "MA Server" });
            await server.WaitFor("client/hello", TimeSpan.FromSeconds(10));

            // The operator unpaired us in MA: the record goes on both sides, then goodbye+close.
            await server.SendJsonAsync("server/unpair", new JObject());
            await server.WaitFor("client/goodbye", TimeSpan.FromSeconds(10));
            Assert.Null(store.LongTermFor(server.NoiseServerId));

            conn.Stop();
            conn.Dispose();
        }
    }
}