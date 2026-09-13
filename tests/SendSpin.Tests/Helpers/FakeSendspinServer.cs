using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using MusicBeePlugin.SendSpin.Noise;
using Noise;
using NoiseKeyPair = Noise.KeyPair;
using Sendspin.SDK.Tests;
using Sendspin.SDK.Tests.Connection;

// In-process fake Sendspin SERVER (the Noise initiator side, mirroring aiosendspin) behind a real
// HttpListener WebSocket, so SourceConnection's full wire flow runs against actual sockets:
// handshake, hello exchange, pairing (pairing_psk + in-band re-handshakes), clock sync, commands.
//
// Single-reader rule: the background pump is the ONLY frame reader after the initial handshake
// (scripted handshake steps read directly before the pump exists). Re-handshakes are therefore
// also completed by the pump: the test sends msg1 via TriggerRehandshake, the pump decodes the
// client's deferred msg2 reply and completes the noise state.
internal sealed class FakeSendspinServer : IDisposable
{
    // Non-readonly: a failed Start() (port busy) leaves the managed HttpListener disposed on
    // Linux, so the retry recreates it instead of reusing the dead instance.
    private HttpListener _listener = new();
    private WebSocket? _ws;
    private readonly CancellationTokenSource _cts = new();
    private Task? _pumpTask;

    private readonly List<HttpListenerContext> _contexts = new();
    private TestNoiseServer? _noise;
    // Fixed static keys: the server_id stays stable across (re)handshakes, which the client's
    // misbinding check and the persisted pairing record depend on.
    private readonly NoiseKeyPair _fixedKeys = NoiseKeyPair.Generate();
    /// <summary>The fake server's stable server_id (base64url of its static public key).</summary>
    public string NoiseServerId { get; }

    /// <summary>PSK the server actually runs the handshake with (msg2 is validated against it).</summary>
    public byte[] ActualPsk { get; set; } = NoiseConstants.SentinelPsk.ToArray();
    /// <summary>PSK whose psk_id msg1 advertises (Sentinel-fallback tests advertise an unknown one).</summary>
    public byte[] AdvertisedPsk { get; set; } = NoiseConstants.SentinelPsk.ToArray();

    // All decrypted application messages received, in order.
    private readonly ConcurrentQueue<JObject> _received = new();
    private readonly ConcurrentDictionary<string, ConcurrentQueue<JObject>> _backlog = new();

    // Swapped per re-handshake: the pairing flow runs several in sequence.
    private volatile TaskCompletionSource<bool> _rehandshakeDone =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    // Distinct server-clock epoch so offset assertions are meaningful.
    private long _nowUs = 1_500_000_000_000;

    public Uri Url { get; }

    public FakeSendspinServer(int preferredPort)
    {
        Url = Bind(preferredPort);
        NoiseServerId = TestBase64Url.EncodeToString(_fixedKeys.PublicKey);
    }

    private Uri Bind(int port)
    {
        for (int attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                _listener.Prefixes.Add($"http://127.0.0.1:{port + attempt}/");
                _listener.Start();
                return new Uri($"ws://127.0.0.1:{port + attempt}/sendspin");
            }
            catch (HttpListenerException)
            {
                _listener.Close();
                _listener = new HttpListener();
            }
        }
        throw new InvalidOperationException("no free port for the fake server");
    }

    public void Start() => _ = Task.Run(AcceptLoopAsync);

    public void Dispose()
    {
        _cts.Cancel();
        try { _ws?.Dispose(); } catch { }
        try { _listener.Stop(); _listener.Close(); } catch { }
    }

    public long NowUs() => _nowUs;
    private long Tick() => _nowUs += 1_000;

    // ------------------------------------------------------------------
    // Waiting helpers
    // ------------------------------------------------------------------

    /// <summary>
    /// Waits for a recorded message of <paramref name="type"/> matching <paramref name="predicate"/>,
    /// scanning the backlog (removing the match) and re-scanning as new messages arrive. Entries
    /// that do not match stay queued, so unrelated messages of the same type never satisfy an
    /// unrelated wait.
    /// </summary>
    public async Task<JObject> WaitFor(string type, Func<JObject, bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (_backlog.TryGetValue(type, out var backlog))
            {
                // Scan: remove the first matching entry, keep the rest.
                JObject?[] snapshot = new JObject[backlog.Count];
                int n = 0;
                while (backlog.TryDequeue(out var item))
                {
                    if (n < snapshot.Length)
                        snapshot[n++] = item;
                }
                JObject? match = null;
                var requeue = new Queue<JObject>();
                for (int i = 0; i < n; i++)
                {
                    var item = snapshot[i];
                    if (item is null)
                        continue;
                    if (match is null && predicate(item))
                        match = item;
                    else
                        requeue.Enqueue(item);
                }
                foreach (var item in requeue)
                    backlog.Enqueue(item);
                if (match is not null)
                    return match;
            }
            await Task.Delay(25);
        }
        throw new TimeoutException($"fake server: no '{type}' matching the predicate within {timeout.TotalSeconds}s");
    }

    public Task<JObject> WaitFor(string type, TimeSpan timeout) =>
        WaitFor(type, _ => true, timeout);

    private void Record(JObject message)
    {
        string type = message["type"]?.Value<string>() ?? "";
        _received.Enqueue(message);
        _backlog.GetOrAdd(type, _ => new ConcurrentQueue<JObject>()).Enqueue(message);
    }

    public IReadOnlyList<JObject> Received => _received.ToArray();

    public JObject? LastReceived(string type) =>
        _received.ToArray().LastOrDefault(m => m["type"]?.Value<string>() == type);

    public void ClearReceived()
    {
        while (_received.TryDequeue(out _)) { }
        foreach (string key in _backlog.Keys.ToArray())
            while (_backlog[key].TryDequeue(out _)) { }
    }

    // ------------------------------------------------------------------
    // WebSocket accept + send plumbing
    // ------------------------------------------------------------------

    private async Task AcceptLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                HttpListenerContext ctx = await _listener.GetContextAsync();
                HttpListenerWebSocketContext wsCtx = await ctx.AcceptWebSocketAsync(null);
                WebSocket old = Interlocked.Exchange(ref _ws, wsCtx.WebSocket);
                old?.Dispose();
                // Keep the HttpListenerContext alive for the WebSocket's lifetime: if the
                // context becomes unreachable, GC finalization closes the connection.
                _contexts.Add(ctx);
            }
        }
        catch (Exception) when (_cts.IsCancellationRequested)
        {
            // shutting down
        }
    }

    private WebSocket Ws => _ws ?? throw new InvalidOperationException("no client connection yet");

    public async Task WaitForClientAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (_ws is null && DateTime.UtcNow < deadline)
            await Task.Delay(10);
        if (_ws is null)
            throw new TimeoutException("fake server: no incoming connection");
    }

    private async Task SendTextAsync(string text)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        await Ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, _cts.Token);
    }

    private async Task SendBinaryAsync(byte[] bytes)
    {
        await Ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Binary, true, _cts.Token);
    }

    private bool _transportReady;

    /// <summary>
    /// Sends a Sendspin message: a cleartext TEXT frame before transport mode (server/init etc.),
    /// an encrypted BINARY frame (type 0 JSON body) afterwards — matching the spec's framing.
    /// </summary>
    public async Task SendJsonAsync(string type, JObject payload)
    {
        string json = new JObject { ["type"] = type, ["payload"] = payload }.ToString(Newtonsoft.Json.Formatting.None);
        if (!_transportReady)
        {
            await SendTextAsync(json);
            return;
        }
        byte[] plain = new byte[1 + Encoding.UTF8.GetByteCount(json)];
        plain[0] = NoiseConstants.MessageTypeJsonBody;
        Encoding.UTF8.GetBytes(json).CopyTo(plain, 1);
        await SendBinaryAsync(_noise!.EncryptFrame(plain));
    }

    // ------------------------------------------------------------------
    // Initial handshake (server = Noise initiator, per the spec). Scripted reads — runs before
    // the pump starts, so no reader overlap.
    // ------------------------------------------------------------------

    public async Task RunInitialHandshake()
    {
        Console.WriteLine($"[{DateTime.UtcNow:HH:mm:ss.fff}][handshake] waiting for client/init");
        string clientInitText = await ReceiveTextRaw(TimeSpan.FromSeconds(10));
        var clientInit = JObject.Parse(clientInitText);
        byte[] clientPub = SendspinIdentity.DecodePeerId(
            clientInit["payload"]?["client_id"]?.Value<string>() ?? throw new InvalidOperationException("client/init missing client_id"));

        _noise = new TestNoiseServer(
            clientPub, ActualPsk,
            keys: _fixedKeys,
            suite: NoiseCipherSuite.ChaChaPoly,
            advertisedPskId: NoiseConstants.DerivePskId(AdvertisedPsk));

        var (serverInit, msg1) = _noise.Respond(clientInitText);
        await SendTextAsync(serverInit);
        await SendTextAsync(msg1);

        string msg2Text = await ReceiveTextRaw(TimeSpan.FromSeconds(10));
        _noise.CompleteHandshake(msg2Text);
        _transportReady = true;
    }

    /// <summary>Sends an in-band re-handshake msg1 for a new PSK; the pump completes it from the client's reply.</summary>
    public async Task TriggerRehandshake(byte[] newPsk)
    {
        byte[] msg1Enc = _noise!.StartRehandshake(newPsk);
        await SendBinaryAsync(msg1Enc);
        var done = _rehandshakeDone;
        var winner = await Task.WhenAny(done.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        if (winner != done.Task)
            throw new TimeoutException("fake server: re-handshake did not complete");
    }

    // ------------------------------------------------------------------
    // Receive helpers (pre-pump only)
    // ------------------------------------------------------------------

    private async Task<string> ReceiveTextRaw(TimeSpan timeout)
    {
        byte[] buf = new byte[64 * 1024];
        (WebSocketReceiveResult result, int count) = await ReceiveFullAsync(buf, timeout);
        if (result.MessageType != WebSocketMessageType.Text)
            throw new InvalidOperationException("expected a text frame");
        return Encoding.UTF8.GetString(buf, 0, count);
    }

    private async Task<(WebSocketReceiveResult Result, int Count)> ReceiveFullAsync(byte[] buf, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        var result = await Ws.ReceiveAsync(new ArraySegment<byte>(buf), cts.Token);
        int total = result.Count;
        while (!result.EndOfMessage)
        {
            result = await Ws.ReceiveAsync(new ArraySegment<byte>(buf, total, buf.Length - total), cts.Token);
            total += result.Count;
        }
        return (result, total);
    }

    // ------------------------------------------------------------------
    // Application-message pump: the only reader once started. Auto-answers client/time,
    // completes re-handshakes, records everything else.
    // ------------------------------------------------------------------

    public void StartPump()
    {
        _pumpTask = Task.Run(PumpAsync);
    }

    private async Task PumpAsync()
    {
        // NOTE: a pending ReceiveAsync must NOT be canceled — cancelling it aborts the managed
        // WebSocket and kills the connection. The pump therefore blocks on one long-lived read
        // and only exits when the connection closes or the server is disposed.
        byte[] buf = new byte[64 * 1024];
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                (WebSocketReceiveResult result, int count) r;
                try
                {
                    r = await ReceiveFullAsync(buf, Timeout.InfiniteTimeSpan);
                }
                catch (WebSocketException)
                {
                    return; // connection dropped (client closed); pump ends
                }

                if (r.result.MessageType == WebSocketMessageType.Close)
                    return;
                if (r.result.MessageType != WebSocketMessageType.Binary)
                    return; // unexpected text after transport: treat as end of session

                byte[] plain = _noise!.DecryptFrame(buf.AsSpan(0, r.count).ToArray());
                byte type = plain[0];

                if (type == 0)
                {
                    var doc = JObject.Parse(Encoding.UTF8.GetString(plain[1..]));
                    string? msgType = doc["type"]?.Value<string>();
                    if (msgType == "client/time")
                    {
                        long t1 = doc["payload"]?["client_transmitted"]?.Value<long>() ?? 0;
                        long now = Tick();
                        await SendJsonAsync("server/time", new JObject
                        {
                            ["client_transmitted"] = t1,
                            ["server_received"] = now,
                            ["server_transmitted"] = now,
                        });
                        Record(doc);
                    }
                    else if (msgType == "noise/handshake")
                    {
                        // `plain` is already AEAD-decrypted (and its leading type-0 byte verified):
                        // complete the re-handshake from the msg2 JSON that follows it. Passing the
                        // ciphertext here would decrypt twice and fail.
                        if (plain[0] != 0)
                            throw new InvalidOperationException("rehandshake type mismatch");
                        _noise.CompleteHandshake(Encoding.UTF8.GetString(plain[1..]));
                        var tcs = _rehandshakeDone;
                        _rehandshakeDone = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                        tcs.TrySetResult(true);
                    }
                    else
                    {
                        Record(doc);
                    }
                }
                else if (type is NoiseConstants.MessageTypeFragmentMore or NoiseConstants.MessageTypeFragmentEnd)
                {
                    throw new InvalidOperationException("fake server: fragments not implemented");
                }
                else
                {
                    // Binary message (e.g. source audio type 12): record raw as a stub doc.
                    Record(new JObject
                    {
                        ["type"] = "_binary",
                        ["payload"] = new JObject
                        {
                            ["message_type"] = type,
                            ["data"] = Convert.ToBase64String(plain[1..]),
                        },
                    });
                }
            }
        }
        catch (Exception)
        {
            // Connection torn down (client closed or server disposed) — pump simply ends.
        }
    }
}