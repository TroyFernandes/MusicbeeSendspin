using System;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MusicBeePlugin.SendSpin.Noise;

// Live handshake probe: drives the plugin's NoiseWireFraming (the client/responder) against a REAL
// sendspin server over a WebSocket, to prove the ported Noise_KKpsk2 transport actually interoperates
// with aiosendspin. The in-process interop test cross-checks the framing against the reference
// reassembly; this proves the real wire handshake. Transport-ready == Noise works end to end.
//
// Usage: live-handshake-probe [ws://host:port/sendspin]
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.WriteLine("usage: live-handshake-probe ws://host:port/sendspin   (no default target — manual tool)");
            return 1;
        }
        var uri = args[0];
        var suite = NoiseCipherSuite.ChaChaPoly;
        var identity = SendspinIdentity.Generate();
        Console.WriteLine($"target : {uri}");
        Console.WriteLine($"suite  : {suite.ToWireName()}");
        Console.WriteLine($"client : {identity.PeerId}");

        var framing = new NoiseWireFraming(suite, SentinelPskResolver.Instance, identity);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var ws = new ClientWebSocket();
        ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);

        try
        {
            await ws.ConnectAsync(new Uri(uri), cts.Token);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FAIL connect: {ex.GetBaseException().Message}");
            return 2;
        }
        Console.WriteLine($"ws open: {ws.State}");

        // 1. Send client/init (what the framing emits right after the socket opens).
        foreach (var f in framing.Start())
            await SendFrameAsync(ws, f);
        Console.WriteLine("sent   : client/init");

        // 2. Receive until transport-ready / fatal / close.
        var packet = new byte[64 * 1024];
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline && ws.State == WebSocketState.Open)
        {
            WebSocketReceiveResult result;
            try
            {
                result = await ws.ReceiveAsync(new ArraySegment<byte>(packet), cts.Token);
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine("FAIL : timed out waiting for the server");
                return 3;
            }

            if (result.MessageType == WebSocketMessageType.Close)
            {
                Console.WriteLine($"FAIL : server closed (code={result.CloseStatus} {result.CloseStatusDescription})");
                return 4;
            }

            // Reassemble the full WS message (a message may span several receive packets).
            using var ms = new MemoryStream();
            var isText = result.MessageType == WebSocketMessageType.Text;
            ms.Write(packet, 0, result.Count);
            while (!result.EndOfMessage)
            {
                result = await ws.ReceiveAsync(new ArraySegment<byte>(packet), cts.Token);
                ms.Write(packet, 0, result.Count);
            }
            var bytes = ms.ToArray();
            var frame = isText
                ? WireFrame.FromText(Encoding.UTF8.GetString(bytes))
                : WireFrame.FromBinary(bytes);

            if (isText)
            {
                var t = frame.PayloadAsText();
                Console.WriteLine($"recv   : {(t.Length > 140 ? t.Substring(0, 140) + "…" : t)}");
            }
            else
            {
                Console.WriteLine($"recv   : binary ({bytes.Length} bytes)");
            }

            var res = framing.ProcessInbound(frame);
            if (res.FatalReason is not null)
            {
                Console.WriteLine($"FAIL : fatal — {res.FatalReason}");
                return 5;
            }
            if (res.Replies is not null)
                foreach (var r in res.Replies)
                    await SendFrameAsync(ws, r);
            if (res.Text is not null)
                Console.WriteLine($"app    : {res.Text}");

            if (framing.IsTransportReady)
            {
                Console.WriteLine();
                Console.WriteLine("OK : Noise_KKpsk2 transport ready — handshake completed against the live server.");
                try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "probe done", CancellationToken.None); }
                catch { /* close is best-effort */ }
                return 0;
            }
        }
        Console.WriteLine("FAIL : did not reach transport-ready before the deadline");
        return 6;
    }

    private static Task SendFrameAsync(ClientWebSocket ws, WireFrame frame)
    {
        if (frame.Kind == WireFrameKind.Text)
            return ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(frame.PayloadAsText())),
                                WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);
        return ws.SendAsync(frame.Payload,
                            WebSocketMessageType.Binary, endOfMessage: true, CancellationToken.None).AsTask();
    }
}
