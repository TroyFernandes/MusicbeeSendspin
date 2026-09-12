using System;
using System.Linq;
using MusicBeePlugin.SendSpin.Noise;
using Sendspin.SDK.Tests.Connection;

// Interop check: the MusicBee plugin's NoiseWireFraming (client/responder) driven against the
// verbatim reference TestNoiseServer (server/initiator). The server's reassembly + the client's
// reassembly are the known-good reference logic, so a pass here cross-checks the ported
// fragmentation (headerLen 2/1, type 2/3, orig_type at [1]) AND the full Noise_KKpsk2 handshake.
internal static class Program
{
    private static int _failures;

    private static void Check(bool ok, string what)
    {
        Console.WriteLine((ok ? "PASS  " : "FAIL  ") + what);
        if (!ok)
            _failures++;
    }

    // Reference handshake drive (FragmentationConformanceTests.CompleteHandshake): client Start ->
    // server Respond -> client ProcessInbound(server/init, msg1) -> server CompleteHandshake(msg2).
    private static TestNoiseServer CompleteHandshake(NoiseWireFraming framing, SendspinIdentity identity)
    {
        var server = new TestNoiseServer(identity.PublicKey, NoiseConstants.SentinelPsk.ToArray());
        var clientInit = framing.Start().Single();
        var (serverInit, msg1) = server.Respond(clientInit.PayloadAsText());
        Check(framing.ProcessInbound(WireFrame.FromText(serverInit)).FatalReason is null,
            "client processes server/init");
        var result = framing.ProcessInbound(WireFrame.FromText(msg1));
        Check(result.FatalReason is null, "client completes noise msg1: " + result.FatalReason);
        if (result.FatalReason is not null)
            return server;
        var reply = result.Replies!.Single();
        server.CompleteHandshake(reply.PayloadAsText());
        return server;
    }

    private static byte[] MakeMessage(byte type, int payloadLen)
    {
        var m = new byte[1 + payloadLen];
        m[0] = type;
        var payload = m.AsSpan(1);
        for (int i = 0; i < payload.Length; i++)
            payload[i] = (byte)(i * 31 + 7);
        return m;
    }

    private static int Main()
    {
        var identity = SendspinIdentity.Generate();
        var framing = new NoiseWireFraming(NoiseCipherSuite.ChaChaPoly, SentinelPskResolver.Instance, identity);

        var server = CompleteHandshake(framing, identity);
        Check(framing.IsTransportReady, "transport ready after handshake");
        if (!framing.IsTransportReady)
            return 1;

        // --- small, both directions. S->C first: it surfaces the first app message, which flips
        // the client's reassembly bound from the 128KB pre-first-message DoS cap to 64MB. ---
        var small = MakeMessage(1, 100);
        var sFrame = framing.EncodeBinary(small).Single();
        Check(server.DecryptAndReassemble(new[] { sFrame.Payload.ToArray() }).SequenceEqual(small),
            "small C->S (client EncryptOutbound -> reference reassembly)");
        var sSmall = server.EncryptFragmented(small).Single();
        var sRes = framing.ProcessInbound(WireFrame.FromBinary(sSmall));
        Check(sRes.FatalReason is null, "small S->C accepted: " + sRes.FatalReason);
        Check(sRes.Binary is not null && sRes.Binary.Value.ToArray().SequenceEqual(small),
            "small S->C round-trips");

        // --- large, fragmented, C->S. The reference reassembler has no size bound, so the
        // client's outbound fragmentation is cross-checked here against it. ---
        var bigCs = MakeMessage(1, 200_000);
        var bigCsFrames = framing.EncodeBinary(bigCs).Select(f => f.Payload.ToArray()).ToList();
        Check(bigCsFrames.Count > 1, $"large C->S split into {bigCsFrames.Count} frames (>1)");
        Check(server.DecryptAndReassemble(bigCsFrames).SequenceEqual(bigCs),
            "large C->S reassembles (200KB)");

        // --- large, fragmented, S->C. Now that a message surfaced (above), the client's bound is
        // 64MB, so this reassembles. Multi-fragment (FragmentMore xN + FragmentEnd). ---
        var bigSc = MakeMessage(1, 333_333);
        byte[]? gotSc = null;
        foreach (var frame in server.EncryptFragmented(bigSc))
        {
            var res = framing.ProcessInbound(WireFrame.FromBinary(frame));
            if (res.FatalReason is not null)
            {
                Check(false, "large S->C frame accepted: " + res.FatalReason);
                break;
            }
            if (res.Binary is not null)
                gotSc = res.Binary.Value.ToArray();
        }
        Check(gotSc is not null && gotSc.SequenceEqual(bigSc),
            "large S->C reassembles (333KB)");

        Console.WriteLine(_failures == 0 ? "\nALL PASS" : $"\n{_failures} FAILURE(S)");
        return _failures == 0 ? 0 : 1;
    }
}
