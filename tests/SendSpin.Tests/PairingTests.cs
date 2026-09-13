using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MusicBeePlugin.SendSpin.Noise;
using Sendspin.SDK.Tests.Connection;
using Xunit;

namespace SendSpin.Tests
{
    // Pairing-primitive tests for the source role: the spec's pairing-token reference vector, the
    // token codec round-trip, the JSON-file-backed PairingStore (resolve / evict / persist /
    // rotate), and NoiseWireFraming's MatchedPskCategory exposure (Sentinel, Pairing, and the
    // in-band re-handshake promotion to LongTerm).
    public class PairingTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "sendspin-pairing-tests-" + Guid.NewGuid().ToString("N"));

        public PairingTests() => Directory.CreateDirectory(_dir);
        public void Dispose() => Directory.Delete(_dir, true);

        private static byte[] Sequence(byte start) => Enumerable.Range(0, 32).Select(i => (byte)(start + i)).ToArray();

        private static string JsonQuote(string s) => "\"" + s + "\"";

        // --- token codec ---------------------------------------------------

        [Fact]
        public void Token_MatchesSpecReferenceVector()
        {
            // pairing.md "Pairing Token": reference vector for client_key = 0x00..0x1f,
            // pairing_psk = 0xe0..0xff.
            byte[] clientKey = Sequence(0x00);
            byte[] psk = Sequence(0xE0);
            const string expected = "SP:0AAAQEAYEAUDAOCAJBIFQYDIOB4IBCEQTCQKRMFYYDENBWHA5DYP6BYPC4PSOLZXH5DU6V97M5XXO74HR6LZ7J5PW674PT6X37T6757Y";
            Assert.Equal(expected, PairingToken.Encode(clientKey, psk));
        }

        [Fact]
        public void Token_RoundTrip_AndRejectsUnknownVersion()
        {
            byte[] clientKey = Sequence(0x10);
            byte[] psk = Sequence(0x40);
            string token = PairingToken.Encode(clientKey, psk);

            Assert.Equal(107, token.Length);
            Assert.All(token, c => Assert.Contains(c, "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ:"));
            Assert.DoesNotContain("2", token.Substring(4)); // transliterated to '9'

            // Lenient decode: case + surrounding whitespace + missing prefix.
            var (gotKey, gotPsk) = PairingToken.Decode("  " + token.ToLowerInvariant() + "  ");
            Assert.Equal(clientKey, gotKey);
            Assert.Equal(psk, gotPsk);

            Assert.Throws<FormatException>(() => PairingToken.Decode("SP:5" + token.Substring(4)));
        }

        // --- store ----------------------------------------------------------

        [Fact]
        public void Store_ResolveBindReplaceRemove()
        {
            var store = new PairingStore(Path.Combine(_dir, "pairing.json"));
            byte[] psk = Sequence(0x20);
            const string serverId = "server-A";
            store.Upsert(new PairingRecord(psk, PskCategory.LongTerm, serverId));

            string pskId = NoiseConstants.DerivePskId(psk);
            NoisePsk? resolved = store.Resolve(pskId);
            Assert.NotNull(resolved);
            Assert.Equal(PskCategory.LongTerm, resolved!.Category);
            Assert.Equal(serverId, resolved.ServerId);

            Assert.Null(store.Resolve(NoiseConstants.DerivePskId(Sequence(0x99))));
            Assert.Null(store.Resolve(NoiseConstants.SentinelPskId));

            // Upsert by same psk_id replaces (no duplicate), re-persisted.
            byte[] psk2 = Sequence(0x21);
            string pskId2 = NoiseConstants.DerivePskId(psk2);
            store.Upsert(new PairingRecord(psk2, PskCategory.LongTerm, serverId));
            store.Upsert(new PairingRecord(psk2, PskCategory.LongTerm, "server-B"));
            Assert.Equal(1, store.List().Count(r => r.PskId == pskId2));
            Assert.Equal("server-B", store.List().Single(r => r.PskId == pskId2).ServerId);

            store.Remove(pskId2);
            Assert.Null(store.Resolve(pskId2));
        }

        [Fact]
        public void Store_EvictsOldestLongTerm_KeepsPairingPsk()
        {
            var store = new PairingStore(Path.Combine(_dir, "pairing.json"));
            byte[] pairingPsk = store.EnsurePairingPsk();

            // Fill to capacity, then one more: the OLDEST long-term record is evicted, the
            // pairing PSK survives.
            var psks = new List<byte[]>();
            for (int i = 0; i < PairingStore.MaxLongTermRecords + 1; i++)
            {
                byte[] psk = Enumerable.Range(0, 32).Select(j => (byte)(i * 3 + j)).ToArray();
                psks.Add(psk);
                store.Upsert(new PairingRecord(psk, PskCategory.LongTerm, "srv-" + i));
            }

            Assert.Null(store.Resolve(NoiseConstants.DerivePskId(psks[0])));
            Assert.NotNull(store.Resolve(NoiseConstants.DerivePskId(psks[1])));
            Assert.NotNull(store.Resolve(NoiseConstants.DerivePskId(psks[^1])));
            Assert.Equal(PairingStore.MaxLongTermRecords,
                store.List().Count(r => r.Category == PskCategory.LongTerm));
            Assert.Equal(PskCategory.Pairing,
                store.Resolve(NoiseConstants.DerivePskId(pairingPsk))!.Category);
        }

        [Fact]
        public void Store_Persists_AndRotatesPairingPsk()
        {
            string path = Path.Combine(_dir, "pairing.json");
            var store = new PairingStore(path);
            byte[] pairingPsk = store.EnsurePairingPsk();
            byte[] ltPsk = Sequence(0x55);
            store.Upsert(new PairingRecord(ltPsk, PskCategory.LongTerm, "srv-persist"));

            // Reopen: everything persists.
            var reopened = new PairingStore(path);
            Assert.Equal(PskCategory.Pairing,
                reopened.Resolve(NoiseConstants.DerivePskId(pairingPsk))!.Category);
            Assert.Equal(ltPsk, reopened.LongTermFor("srv-persist")!.Psk.Span.ToArray());

            // Ensure is stable; the token derives from the stored PSK.
            Assert.Equal(pairingPsk, reopened.EnsurePairingPsk());
            var identity = SendspinIdentity.Generate();
            string token1 = reopened.GetPairingToken(identity);
            string token2 = reopened.GetPairingToken(identity);
            Assert.Equal(token1, token2);
            var (tokKey, tokPsk) = PairingToken.Decode(token1);
            Assert.Equal(identity.PublicKey.ToArray(), tokKey);
            Assert.Equal(pairingPsk, tokPsk);

            // Rotate: fresh PSK, old one gone; the new token decodes to the rotated PSK.
            byte[] beforeRotate = reopened.EnsurePairingPsk();
            byte[] afterRotate = reopened.RotatePairingPsk();
            Assert.NotEqual(beforeRotate, afterRotate);
            Assert.Equal(afterRotate, reopened.EnsurePairingPsk());
            var (_, newPsk) = PairingToken.Decode(reopened.GetPairingToken(identity));
            Assert.Equal(afterRotate, newPsk);
            Assert.Null(reopened.Resolve(NoiseConstants.DerivePskId(beforeRotate)));
        }

        [Fact]
        public void Store_SkipsCorruptRecord_KeepsWellFormed()
        {
            // A file holding one corrupt record (psk does not derive its psk_id) plus one
            // well-formed record: load skips the corrupt one, keeps the good one.
            byte[] good = Sequence(0x77);
            string goodId = NoiseConstants.DerivePskId(good);
            File.WriteAllText(Path.Combine(_dir, "pairing.json"),
                "{\"records\":["
                + "{\"psk_id\":\"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA\",\"psk\":\"AAAA\",\"category\":\"lt\",\"server_id\":\"x\"},"
                + "{\"psk_id\":" + JsonQuote(goodId) + ",\"psk\":" + JsonQuote(Base64UrlText.Encode(good)) + ",\"category\":\"lt\",\"server_id\":\"srv-good\"}]}");

            var store = new PairingStore(Path.Combine(_dir, "pairing.json"));
            Assert.Equal(PskCategory.LongTerm, store.Resolve(goodId)!.Category);
            Assert.Single(store.List());
        }

        // --- MatchedPskCategory -------------------------------------------------

        private static TestNoiseServer CompleteHandshake(NoiseWireFraming framing, SendspinIdentity identity, byte[] psk)
        {
            var server = new TestNoiseServer(identity.PublicKey, psk);
            var clientInit = framing.Start().Single();
            var (serverInit, msg1) = server.Respond(clientInit.PayloadAsText());
            var initRes = framing.ProcessInbound(WireFrame.FromText(serverInit));
            if (initRes.FatalReason is not null)
                return server;
            var result = framing.ProcessInbound(WireFrame.FromText(msg1));
            if (result.FatalReason is not null || result.Replies is null)
                return server;
            server.CompleteHandshake(result.Replies.Single().PayloadAsText());
            return server;
        }

        private sealed class FixedResolver : INoisePskResolver
        {
            private readonly Dictionary<string, NoisePsk> _psks = new();
            public void Add(byte[] psk, PskCategory category, string? serverId) =>
                _psks[NoiseConstants.DerivePskId(psk)] = new NoisePsk(psk.ToArray(), category, serverId);
            public NoisePsk? Resolve(string pskId) => _psks.TryGetValue(pskId, out var p) ? p : null;
        }

        [Fact]
        public void SentinelHandshake_ReportsSentinelCategory()
        {
            var identity = SendspinIdentity.Generate();
            var framing = new NoiseWireFraming(NoiseCipherSuite.ChaChaPoly, SentinelPskResolver.Instance, identity);
            CompleteHandshake(framing, identity, NoiseConstants.SentinelPsk.ToArray());
            Assert.Equal(PskCategory.Sentinel, framing.MatchedPskCategory);
            Assert.True(framing.IsTransportReady);
        }

        [Fact]
        public void PairingPskHandshake_ReportsPairingCategory()
        {
            var identity = SendspinIdentity.Generate();
            byte[] pairingPsk = Sequence(0x66);
            var resolver = new FixedResolver();
            resolver.Add(pairingPsk, PskCategory.Pairing, null);
            var framing = new NoiseWireFraming(NoiseCipherSuite.ChaChaPoly, resolver, identity);
            CompleteHandshake(framing, identity, pairingPsk);
            Assert.Equal(PskCategory.Pairing, framing.MatchedPskCategory);
        }

        [Fact]
        public void Rehandshake_PromotesPairingToLongTerm()
        {
            var identity = SendspinIdentity.Generate();
            byte[] pairingPsk = Sequence(0x66);
            byte[] ltPsk = Sequence(0x88);
            var server = new TestNoiseServer(identity.PublicKey, pairingPsk);
            var resolver = new FixedResolver();
            resolver.Add(pairingPsk, PskCategory.Pairing, null);
            resolver.Add(ltPsk, PskCategory.LongTerm, server.ServerId);
            var framing = new NoiseWireFraming(NoiseCipherSuite.ChaChaPoly, resolver, identity);

            var clientInit = framing.Start().Single();
            var (serverInit, msg1) = server.Respond(clientInit.PayloadAsText());
            framing.ProcessInbound(WireFrame.FromText(serverInit));
            var res = framing.ProcessInbound(WireFrame.FromText(msg1));
            Assert.Null(res.FatalReason);
            server.CompleteHandshake(res.Replies!.Single().PayloadAsText());
            Assert.Equal(PskCategory.Pairing, framing.MatchedPskCategory);

            // In-band re-handshake to the long-term PSK: encrypted noise/handshake msg1 arrives,
            // the client defers, EncodeDeferredReply commits the swap -> category promotes to LongTerm.
            byte[] msg1Enc = server.StartRehandshake(ltPsk);
            var deferred = framing.ProcessInbound(WireFrame.FromBinary(msg1Enc));
            Assert.True(deferred.HasDeferredReply);
            Assert.Equal(PskCategory.Pairing, framing.MatchedPskCategory); // unchanged until the commit
            foreach (WireFrame reply in framing.EncodeDeferredReply())
                server.CompleteRehandshake(reply.Payload.ToArray());
            Assert.Equal(PskCategory.LongTerm, framing.MatchedPskCategory);
            Assert.True(framing.IsTransportReady);
        }

        // PairingTests.cs uses _dir for the per-test temp dir.
    }
}