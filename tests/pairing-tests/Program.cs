using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MusicBeePlugin.SendSpin.Noise;
using Sendspin.SDK.Tests.Connection;

// Pairing-primitive tests for the source role: the spec's pairing-token reference vector, the
// token codec round-trip, the JSON-file-backed PairingStore (resolve / evict / persist /
// rotate), and NoiseWireFraming's MatchedPskCategory exposure (Sentinel, Pairing, and the
// in-band re-handshake promotion to LongTerm).
internal static class Program
{
    private static int _failures;

    private static void Check(bool ok, string what)
    {
        Console.WriteLine((ok ? "PASS  " : "FAIL  ") + what);
        if (!ok)
            _failures++;
    }

    private static string TempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "sendspin-pairing-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
    private static byte[] Sequence(byte start) => Enumerable.Range(0, 32).Select(i => (byte)(start + i)).ToArray();

    private static string JsonQuote(string s) => "\"" + s + "\"";

    // --- token codec ---------------------------------------------------

    private static void TestTokenReferenceVector()
    {
        // pairing.md "Pairing Token": reference vector for client_key = 0x00..0x1f,
        // pairing_psk = 0xe0..0xff.
        byte[] clientKey = Sequence(0x00);
        byte[] psk = Sequence(0xE0);
        const string expected = "SP:0AAAQEAYEAUDAOCAJBIFQYDIOB4IBCEQTCQKRMFYYDENBWHA5DYP6BYPC4PSOLZXH5DU6V97M5XXO74HR6LZ7J5PW674PT6X37T6757Y";
        Check(PairingToken.Encode(clientKey, psk) == expected, "token matches the spec reference vector");
    }

    private static void TestTokenRoundTrip()
    {
        byte[] clientKey = Sequence(0x10);
        byte[] psk = Sequence(0x40);
        string token = PairingToken.Encode(clientKey, psk);

        Check(token.Length == 107, $"token is 107 chars (got {token.Length})");
        Check(token.All(c => "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ:".Contains(c)), "token is QR-alphanumeric only");
        Check(!token.Substring(4).Contains('2'), "token body has no '2' (transliterated to '9')");

        // Lenient decode: case + surrounding whitespace + missing prefix.
        var (gotKey, gotPsk) = PairingToken.Decode("  " + token.ToLowerInvariant() + "  ");
        Check(gotKey.SequenceEqual(clientKey), "decoded client key matches");
        Check(gotPsk.SequenceEqual(psk), "decoded pairing psk matches");

        try
        {
            PairingToken.Decode("SP:5" + token.Substring(4));
            Check(false, "unknown token version rejected");
        }
        catch (FormatException) { Check(true, "unknown token version rejected"); }
        catch (Exception ex) { Check(false, "unknown token version rejected (wrong exception: " + ex.GetType().Name + ")"); }
    }

    // --- store ----------------------------------------------------------

    private static void TestStoreResolveAndBind()
    {
        string dir = TempDir();
        try
        {
            var store = new PairingStore(Path.Combine(dir, "pairing.json"));
            byte[] psk = Sequence(0x20);
            const string serverId = "server-A";
            store.Upsert(new PairingRecord(psk, PskCategory.LongTerm, serverId));

            string pskId = NoiseConstants.DerivePskId(psk);
            NoisePsk? resolved = store.Resolve(pskId);
            Check(resolved is not null, "resolve finds the stored record");
            Check(resolved!.Category == PskCategory.LongTerm, "resolved category is LongTerm");
            Check(resolved.ServerId == serverId, "resolved record carries the server binding");

            Check(store.Resolve(NoiseConstants.DerivePskId(Sequence(0x99))) is null, "unknown psk_id resolves null");
            Check(store.Resolve(NoiseConstants.SentinelPskId) is null, "sentinel psk_id is never stored");

            // Upsert by same psk_id replaces (no duplicate), re-persisted.
            byte[] psk2 = Sequence(0x21);
            string pskId2 = NoiseConstants.DerivePskId(psk2);
            store.Upsert(new PairingRecord(psk2, PskCategory.LongTerm, serverId));
            store.Upsert(new PairingRecord(psk2, PskCategory.LongTerm, "server-B"));
            Check(store.List().Count(r => r.PskId == pskId2) == 1, "upsert with same psk_id replaces");
            Check(store.List().Single(r => r.PskId == pskId2).ServerId == "server-B", "replacement carries the new binding");

            store.Remove(pskId2);
            Check(store.Resolve(pskId2) is null, "Remove deletes the record");
        }
        finally { Directory.Delete(dir, true); }
    }

    private static void TestStoreEviction()
    {
        string dir = TempDir();
        try
        {
            var store = new PairingStore(Path.Combine(dir, "pairing.json"));
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

            Check(store.Resolve(NoiseConstants.DerivePskId(psks[0])) is null, "oldest long-term record evicted");
            Check(store.Resolve(NoiseConstants.DerivePskId(psks[1])) is not null, "second-oldest record kept");
            Check(store.Resolve(NoiseConstants.DerivePskId(psks[^1])) is not null, "newest record kept");
            Check(store.List().Count(r => r.Category == PskCategory.LongTerm) == PairingStore.MaxLongTermRecords,
                "long-term record count stays at capacity");
            Check(store.Resolve(NoiseConstants.DerivePskId(pairingPsk))!.Category == PskCategory.Pairing,
                "pairing PSK survives eviction");
        }
        finally { Directory.Delete(dir, true); }
    }

    private static void TestStorePersistenceAndRotation()
    {
        string dir = TempDir();
        try
        {
            string path = Path.Combine(dir, "pairing.json");
            var store = new PairingStore(path);
            byte[] pairingPsk = store.EnsurePairingPsk();
            byte[] ltPsk = Sequence(0x55);
            store.Upsert(new PairingRecord(ltPsk, PskCategory.LongTerm, "srv-persist"));

            // Reopen: everything persists.
            var reopened = new PairingStore(path);
            Check(reopened.Resolve(NoiseConstants.DerivePskId(pairingPsk))!.Category == PskCategory.Pairing,
                "pairing PSK persists across reopen");
            Check(reopened.LongTermFor("srv-persist")!.Psk.Span.SequenceEqual(ltPsk), "long-term record persists across reopen");

            // Ensure is stable; the token derives from the stored PSK.
            Check(reopened.EnsurePairingPsk().SequenceEqual(pairingPsk), "EnsurePairingPsk is stable");
            var identity = SendspinIdentity.Generate();
            string token1 = reopened.GetPairingToken(identity);
            string token2 = reopened.GetPairingToken(identity);
            Check(token1 == token2, "token is deterministic for the same identity + PSK");
            var (tokKey, tokPsk) = PairingToken.Decode(token1);
            Check(tokKey.SequenceEqual(identity.PublicKey.ToArray()), "token carries the client public key");
            Check(tokPsk.SequenceEqual(pairingPsk), "token carries the stored pairing PSK");

            // Rotate: fresh PSK, old one gone; the new token decodes to the rotated PSK.
            byte[] beforeRotate = reopened.EnsurePairingPsk();
            byte[] afterRotate = reopened.RotatePairingPsk();
            Check(!afterRotate.SequenceEqual(beforeRotate), "rotate mints a fresh pairing PSK");
            Check(reopened.EnsurePairingPsk().SequenceEqual(afterRotate), "EnsurePairingPsk is stable after rotate");
            string newToken = reopened.GetPairingToken(identity);
            var (_, newPsk) = PairingToken.Decode(newToken);
            Check(newPsk.SequenceEqual(afterRotate), "rotated token decodes to the new PSK");
            Check(reopened.Resolve(NoiseConstants.DerivePskId(beforeRotate)) is null, "old pairing PSK removed on rotate");
        }
        finally { Directory.Delete(dir, true); }
    }

    private static void TestStoreCorruptEntrySkipped()
    {
        string dir = TempDir();
        try
        {
            // A file holding one corrupt record (psk does not derive its psk_id) plus one
            // well-formed record: load skips the corrupt one, keeps the good one.
            byte[] good = Sequence(0x77);
            string goodId = NoiseConstants.DerivePskId(good);
            File.WriteAllText(Path.Combine(dir, "pairing.json"),
                "{\"records\":["
                + "{\"psk_id\":\"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA\",\"psk\":\"AAAA\",\"category\":\"lt\",\"server_id\":\"x\"},"
                + "{\"psk_id\":" + JsonQuote(goodId) + ",\"psk\":" + JsonQuote(Base64UrlText.Encode(good)) + ",\"category\":\"lt\",\"server_id\":\"srv-good\"}]}");

            var store = new PairingStore(Path.Combine(dir, "pairing.json"));
            Check(store.Resolve(goodId)!.Category == PskCategory.LongTerm,
                "well-formed record loads alongside a corrupt one");
            Check(store.List().Count == 1, "corrupt record skipped (does not derive its psk_id)");
        }
        finally { Directory.Delete(dir, true); }
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

    private static void TestMatchedCategorySentinel()
    {
        var identity = SendspinIdentity.Generate();
        var framing = new NoiseWireFraming(NoiseCipherSuite.ChaChaPoly, SentinelPskResolver.Instance, identity);
        CompleteHandshake(framing, identity, NoiseConstants.SentinelPsk.ToArray());
        Check(framing.MatchedPskCategory == PskCategory.Sentinel, "sentinel handshake reports PskCategory.Sentinel");
        Check(framing.MatchedPskCategory != null && framing.IsTransportReady, "transport ready with category set");
    }

    private static void TestMatchedCategoryPairing()
    {
        var identity = SendspinIdentity.Generate();
        byte[] pairingPsk = Sequence(0x66);
        var resolver = new FixedResolver();
        resolver.Add(pairingPsk, PskCategory.Pairing, null);
        var framing = new NoiseWireFraming(NoiseCipherSuite.ChaChaPoly, resolver, identity);
        CompleteHandshake(framing, identity, pairingPsk);
        Check(framing.MatchedPskCategory == PskCategory.Pairing, "pairing-psk handshake reports PskCategory.Pairing");
    }

    private static void TestMatchedCategoryLongTerm()
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
        if (res.FatalReason is not null || res.Replies is null)
        {
            Check(false, "pairing handshake completed: " + res.FatalReason);
            return;
        }
        server.CompleteHandshake(res.Replies.Single().PayloadAsText());
        Check(framing.MatchedPskCategory == PskCategory.Pairing, "initial session is Pairing");

        // In-band re-handshake to the long-term PSK: encrypted noise/handshake msg1 arrives,
        // the client defers, EncodeDeferredReply commits the swap -> category promotes to LongTerm.
        byte[] msg1Enc = server.StartRehandshake(ltPsk);
        var deferred = framing.ProcessInbound(WireFrame.FromBinary(msg1Enc));
        Check(deferred.HasDeferredReply, "re-handshake msg1 defers the reply");
        Check(framing.MatchedPskCategory == PskCategory.Pairing, "category unchanged until the commit");
        foreach (WireFrame reply in framing.EncodeDeferredReply())
            server.CompleteRehandshake(reply.Payload.ToArray());
        Check(framing.MatchedPskCategory == PskCategory.LongTerm, "re-handshake commit promotes to LongTerm");
        Check(framing.IsTransportReady, "transport still ready after re-handshake");
    }

    private static int Main()
    {
        TestTokenReferenceVector();
        TestTokenRoundTrip();
        TestStoreResolveAndBind();
        TestStoreEviction();
        TestStorePersistenceAndRotation();
        TestStoreCorruptEntrySkipped();
        TestMatchedCategorySentinel();
        TestMatchedCategoryPairing();
        TestMatchedCategoryLongTerm();

        Console.WriteLine(_failures == 0 ? "\nALL PASS" : $"\n{_failures} FAILURE(S)");
        return _failures == 0 ? 0 : 1;
    }
}