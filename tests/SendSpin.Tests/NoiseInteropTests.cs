using System;
using System.Linq;
using MusicBeePlugin.SendSpin.Noise;
using Sendspin.SDK.Tests.Connection;
using Xunit;

namespace SendSpin.Tests
{
    // Interop check: the MusicBee plugin's NoiseWireFraming (client/responder) driven against the
    // verbatim reference TestNoiseServer (server/initiator). The server's reassembly + the client's
    // reassembly are the known-good reference logic, so a pass here cross-checks the ported
    // fragmentation (headerLen 2/1, type 2/3, orig_type at [1]) AND the full Noise_KKpsk2 handshake.
    public class NoiseInteropTests
    {
        // Reference handshake drive (FragmentationConformanceTests.CompleteHandshake): client Start ->
        // server Respond -> client ProcessInbound(server/init, msg1) -> server CompleteHandshake(msg2).
        private static TestNoiseServer CompleteHandshake(NoiseWireFraming framing, SendspinIdentity identity)
        {
            var server = new TestNoiseServer(identity.PublicKey, NoiseConstants.SentinelPsk.ToArray());
            var clientInit = framing.Start().Single();
            var (serverInit, msg1) = server.Respond(clientInit.PayloadAsText());
            Assert.Null(framing.ProcessInbound(WireFrame.FromText(serverInit)).FatalReason);
            var result = framing.ProcessInbound(WireFrame.FromText(msg1));
            Assert.Null(result.FatalReason);
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

        [Fact]
        public void NoiseHandshake_AndFragmentation_RoundTripAgainstReference()
        {
            var identity = SendspinIdentity.Generate();
            var framing = new NoiseWireFraming(NoiseCipherSuite.ChaChaPoly, SentinelPskResolver.Instance, identity);

            var server = CompleteHandshake(framing, identity);
            Assert.True(framing.IsTransportReady, "transport ready after handshake");

            // --- small, both directions. S->C first: it surfaces the first app message, which flips
            // the client's reassembly bound from the 128KB pre-first-message DoS cap to 64MB. ---
            var small = MakeMessage(1, 100);
            var sFrame = framing.EncodeBinary(small).Single();
            Assert.Equal(small, server.DecryptAndReassemble(new[] { sFrame.Payload.ToArray() }));
            var sSmall = server.EncryptFragmented(small).Single();
            var sRes = framing.ProcessInbound(WireFrame.FromBinary(sSmall));
            Assert.Null(sRes.FatalReason);
            Assert.Equal(small, sRes.Binary!.Value.ToArray());

            // --- large, fragmented, C->S. The reference reassembler has no size bound, so the
            // client's outbound fragmentation is cross-checked here against it. ---
            var bigCs = MakeMessage(1, 200_000);
            var bigCsFrames = framing.EncodeBinary(bigCs).Select(f => f.Payload.ToArray()).ToList();
            Assert.True(bigCsFrames.Count > 1, $"large C->S split into {bigCsFrames.Count} frames (>1)");
            Assert.Equal(bigCs, server.DecryptAndReassemble(bigCsFrames));

            // --- large, fragmented, S->C. Now that a message surfaced (above), the client's bound is
            // 64MB, so this reassembles. Multi-fragment (FragmentMore xN + FragmentEnd). ---
            var bigSc = MakeMessage(1, 333_333);
            byte[]? gotSc = null;
            foreach (var frame in server.EncryptFragmented(bigSc))
            {
                var res = framing.ProcessInbound(WireFrame.FromBinary(frame));
                Assert.Null(res.FatalReason);
                if (res.Binary is not null)
                    gotSc = res.Binary.Value.ToArray();
            }
            Assert.Equal(bigSc, gotSc);
        }
    }
}