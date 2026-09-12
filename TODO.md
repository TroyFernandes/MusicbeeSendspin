# TODO — MusicBee render device → Music Assistant (Sendspin `source@v1`)

Working checklist for `TASK-source-role.md`. Flip `[ ]` → `[x]` as each item lands.
Full spec lives in `TASK-source-role.md`; crypto reference is the cloned SDK at
`/home/nixos/sendspin-dotnet` (see the "Sendspin .NET SDK" note in the task file).

Convention: `- [ ]` = open, `- [x]` = done, `- [~]` = blocked/deferred (say why).

---

## 0. Setup & investigation

- [x] Clone `sendspin-dotnet` SDK to `~/sendspin-dotnet`; reference it in `TASK-source-role.md`.
- [x] Confirm crypto approach: `Noise.NET` (netstandard1.3 → net48-compatible) drives the KKpsk2
      state machine; port the SDK's `Connection/Noise/` wrapper into `SendSpin/`. **Do not hand-roll crypto.**
- [x] Key inversion noted: the plugin is the Sendspin **client** but the Noise **responder** — the server
      sends `noise/handshake` msg 1 (carrying `psk_id`), the client resolves the **Sentinel PSK**, sends msg 2.
- [x] **Windows:** `tests/noise-net48-smoke` → `noise-net48-smoke.exe` proves libsodium + the KKpsk2 handshake actually run on net48 (build-only can't show that). exe is self-sufficient — bundles `Native/libsodium.dll` (x64). **DONE: 2× PASS on Windows (2026-09-12).**
      - [x] Ran `noise-net48-smoke.exe` on a 64-bit Windows box — `PASS ChaChaPoly`, `PASS AesGcm`.
- [x] **Live handshake verified vs real MA server (2026-09-12):** net8 probe (`tests/live-handshake-probe`) completed the full KKpsk2 handshake against 192.168.1.10:8927 (`25519_ChaChaPoly_SHA256`, unpaired Sentinel PSK). The ported transport interoperates with aiosendspin — Component B (SourceConnection) de-risked.

## 1. Component A — MusicBee render device (front-end)

- [x] Method surface verified against HQPlayer's `MusicBeeHQP.vb` and implemented on the plugin's
      `partial class Plugin` (there is no `Plugins/MusicBeePlugin.cs` in this repo — `SendSpinPlugin.cs`
      IS the plugin class): `GetRenderingDevices()`, `GetRenderingSettings()` (continuous=0 so MusicBee
      calls PlayToDevice per track), `SetActiveRenderingDevice(name)`, `PlayToDevice(url, handle)`,
      `QueueNext(url)` (no-op — the next track arrives as a fresh PlayToDevice).
- [x] `GetRenderingDevices()` → one device from settings (default "Music Assistant (Sendspin)"); empty when disabled.
- [x] `SetActiveRenderingDevice(name)` → activate resolves the MA server URL (manual host first, then mDNS,
      retrying every 3s while nothing is found) and starts the SourceConnection; deactivate tears down
      capture + connection. Switching to another output device deactivates ours.
- [x] `PlayToDevice(url, streamHandle)` → `SourceRenderDevice` restarts the capture on MusicBee's decode
      stream (handle NOT owned — Stop must never close a MusicBee stream) and feeds chunks into the
      connection; the input stream opens lazily on the first packet while the server's start
      authorization stands. (commit 62d244b)
- [x] `RenderingDevicesChanged` raised at plugin startup and whenever the render-device settings change.
- [x] Transport state forwarding (documented decisions): pause/stop → capture stops + `client_stream/end`;
      resume → capture restarts on the remembered PlayToDevice handle (unowned) or a fresh plugin-owned
      `Player_OpenStreamHandle` stream, opening a fresh input stream; track change → continuous source
      stream (timestamps keep advancing; aiosendspin tolerates the small restart gap); **volume/mute are
      NOT forwarded** — the source role has no volume channel, Music Assistant applies its own target
      volume. Local playback is silent by MusicBee's render-device routing (no `Player_SetMute` hack).

## 2. Component B — Sendspin `source@v1` client (new)

> **Gating check RESOLVED (2026-09-12, from the `sendspin/aiosendspin` clone = current reference server):**
> aiosendspin registers `source@v1` with `requires_pairing=True` (`server/roles/source/__init__.py`), and
> `SendspinConnection._filter_pairing_roles` strips it from `active_roles` unless the connection is
> **long-term paired** (`_is_long_term_paired`). An unpaired/Sentinel client — even one with unpaired
> access + operator trust — NEVER gets `source@v1`. **=> Pairing (Pairing PSK method) is a hard
> prerequisite before any audio can flow.** Chosen method: **Pairing PSK** (the plugin shows an `SP:`
> pairing token; the operator pastes it into MA — no CPace PAKE, no display/speaker out-channel).
>
> **Wire-form corrections from the reference implementations (spec doc is stale on these):**
>
> - `supported_pair_methods` is a **LIST** of `{method, locations?}` (both sendspin-dotnet and aiosendspin's
>   `list[PairMethodDescriptor]`); the spec text's object-keyed form is not what servers parse.
> - The stream messages are `client_stream/start` / `client_stream/end` **with underscores** on the wire
>   (SDK `MessageTypes` + aiosendspin `Literal`; the spec doc's `client-stream/*` hyphen form would be
>   rejected as an unknown type).
> - `client_stream/start` carries ONLY the `source` object (codec/channels/sample_rate/bit_depth/
>   codec_header) — **no player metadata**. The source role has no metadata channel; the
>   `NowPlaying_*` metadata idea is dead for this path.
> - Source server command is only `start` | `stop` (no `pause`). MusicBee pause/stop maps to
>   `client_stream/end`; resume sends a fresh `client_stream/start` (aiosendspin keeps `_start_requested`
>   set after end, so no new server `start` command is needed).

- [x] Add `Noise.NET` (+ `libsodium`) to `MusicBeeSendSpin.csproj` (verify net48 restore).
- [x] Port `Connection/Noise/` (+ `Framing/`) wrapper into `SendSpin/Noise/` (NoiseWireFraming, NoiseConstants,
      NoiseCipherSuite, NoisePsk, SentinelPskResolver, Base64UrlText, SendspinIdentity, WireFrame, IWireFraming).
      JSON envelopes use Newtonsoft (net48 has no System.Text.Json) — same field order as the spec schemas.
- [x] **Noise KKpsk2 handshake proven against live MA** — the ported `NoiseWireFraming` + the probe's drive
      (`Start()` → send; receive → `ProcessInbound` → send `Replies`; `IsTransportReady` flips) completed the full
      handshake against 192.168.1.10:8927. Remaining work is *embedding that proven drive* below, not building it.
- [x] **Pairing prerequisite (gating check resolved above) — DONE (commit e241138):**
  - [x] `SendSpin/Noise/PairingStore.cs` — JSON-file-backed pairing-record store (8 long-term capacity,
        thread-safe) implementing `INoisePskResolver` (psk_id → long-term PSK + pairing PSK; Sentinel
        fallback lives in NoiseWireFraming). Holds the plugin's own persistent **pairing PSK**
        (CSPRNG, category `pr`, never consumed by pairing). Corrupt entries skipped on load.
  - [x] `PairingToken.Encode` port — `SP:0` + base32(client_pub || pairing_psk), 2→9 transliteration;
        verified against the spec's reference vector in tests/pairing-tests.
  - [x] `NoiseWireFraming.MatchedPskCategory` exposed (committed with the key swap on re-handshake);
        SourceConnection gates `client/pair-finalize` on it (pairing PSK required; aborts with
        `method_not_supported` otherwise).
  - [x] Pairing flow in `SourceConnection`: on `server/activate {activities:['pairing'], pairing.method:'pairing_psk'}`
        → verify matched PSK is Pairing → send `client/pair-finalize {long_term_psk}` (fresh CSPRNG 32B)
        → on `server/pair-finalize` persist the record {server_id, psk, lt} → server re-handshakes in-band to the
        new long-term PSK (NoiseWireFraming already handles re-handshake + deferred reply) → hello exchange
        repeats → activate now carries `source@v1`.
- [~] **Build order — connection/protocol + streaming DONE (commits c7f8ca6 + streaming commit); discovery still open:**
  - [x] `SendSpin/SourceConnection.cs` — dial the MA server with `ClientWebSocket`, run the proven handshake drive
        into a persistent connect + receive loop. ONE writer task consuming a send queue (serializes ALL
        `ClientWebSocket` writes; receive loop is the single producer of handshake/control replies, so ordering
        holds; `EncodeDeferredReply` runs inside the writer's send step per the framing contract).
  - [x] After transport-ready, **receive `server/hello` → answer with `client/hello`** (we do *not* initiate it):
        `supported_roles:["source@v1"]`, `source@v1_support:{}`, `trust_level:"user"|"none"` (long-term-paired ? user : none,
        mirroring the SDK), `unpaired_access:{enabled:true}`,
        `device_info{product_name,manufacturer,software_version}`,
        `supported_pair_methods:[{method:"pairing_psk",locations:["operator"]}]` (list form — see above).
  - [x] **Read `server/activate` → grant check.** With `source@v1` in `active_roles` (long-term paired): proceed.
        Without it (unpaired): stay idle/waiting — pairing is the path, never stream without the grant.
        Mirror the SDK's client-side gate: refuse `source@v1` granted without a long-term PSK.
  - [x] **Clock sync** (permitted only after `server/activate`): burst `client/time {client_transmitted:T1}` (µs) →
        `server/time {server_received:T2, server_transmitted:T3}` + local T4; `offset=((T2-T1)+(T3-T4))/2`,
        `rtt=(T4-T1)-(T3-T2)`, discard `rtt<=0`, keep min-RTT. Start with best-of-burst offset (skip the full Kalman);
        re-burst periodically + after reconnect. Then `client/state {available:true, source:{}}` (this opens
        the server's acceptance of source binary data — aiosendspin requires the state before chunks).
  - [x] **Stream is server-initiated** + opens lazily on the first audio packet while authorized (the render device streams exactly when MusicBee plays; a server start while MusicBee is idle opens nothing).
  - [x] `client_stream/start` with the `source` object only (codec/channels/sample_rate/bit_depth; opus =
        no codec_header, bit_depth ignored — aiosendspin decodes opus at 16-bit canonical), then binary chunks.
  - [x] Binary audio chunk: type `0x0C` + 8-byte BE µs timestamp (server clock = local + offset) + payload;
        **pace to real-time** — the capture is inherently 1x; bounded drop-oldest queue (spec: drop stale backlog,
        resume from live capture); chunks 20 ms (spec bounds 5–150 ms), one Opus packet per chunk.
  - [x] Handle `server/command {source:{command:"start"|"stop"}}` (idempotent per spec). MusicBee pause/stop →
        `client_stream/end`; resume → fresh `client_stream/start` (no new server grant needed — verified in
        aiosendspin `SourceV1Role`).
  - [x] Reconnect with backoff (1s..30s); streaming state is per-connection (reset on reconnect; the server
        re-sends `start` if it still wants the stream). Credential-mismatch reconnect covered by test.
- [x] `SendSpin/SourceDiscoveryService.cs` (commit c6c8e75) — mDNS for `_sendspin-server._tcp.local.`
      (SRV/TXT/A resolution; TXT `path` = WebSocket endpoint — **the task file's "ws" TXT key guess was
      wrong**: aiosendspin advertises `{name, path}`, `path` fixed to `/sendspin` — plus `name` = friendly
      name; stale servers dropped after 2 min) + `ManualServerUrl(host, port, discoveredPort, path)` fallback
      (manual host:port wins; port 0 uses the discovered/default 8927). Settings `SourceAutoDiscover` /
      `SourceServerHost` / `SourceServerPort` already exist (commit dbd59c7).
- [~] **Component B REMAINING: wire-up only** — the render-device / audio-path / plugin lifecycle pieces
      (sections 1, 3, 5) that START/STOP a `SourceConnection` and feed it encoded audio. The connection,
      pairing, clock sync and streaming protocol themselves are done and tested.

## 3. Audio path

- [x] Wired via `SourceRenderDevice`: `AudioCaptureService` (Opus 48k stereo default) events are
      re-stamped into the ServerClock domain (one Stopwatch epoch per activation → monotonic across
      per-track capture restarts) and fed to `SourceConnection.EnqueueEncodedAudio`. Tested end to end
      with a fake capture (real BASS path verifies on Windows in section 6).

## 4. Settings

- [x] Target fields exist and are used by the resolver: `SourceAutoDiscover` (mDNS) and manual
      `SourceServerHost`/`SourceServerPort` (host wins when set; port 0 → discovered/default 8927). (commit dbd59c7)
- [x] Render-device name setting `RenderDeviceName` (default "Music Assistant (Sendspin)"); feeds both
      the device list and the client/hello name.
- [x] Audio codec for the source stream reuses the existing `AudioCodec`/`SampleRate`/`Channels`/`BitDepth`
      settings (default opus/48k/2ch/16); `StreamParams` flows into `client_stream/start`.
- [x] Existing speaker-mode settings untouched (additive only).
- [x] **Settings dialog UI** — new "Music Assistant" tab: enable toggle, device name, auto-discover
      checkbox, manual host/port, and the **pairing token** (readonly textbox + copy button; the token
      is also logged at startup for headless setups).

## 5. Wire-up

- [x] Render-device methods exposed on the plugin class (`SendSpinPlugin.cs`, `partial class Plugin` —
      the task file's `Plugins/MusicBeePlugin.cs` does not exist in this repo) + `RenderingDevicesChanged`.
- [x] `SendSpinPlugin.cs` — lifecycle: render device created at startup (persistent identity +
      pairing store under `Setting_GetPersistentStoragePath()`), play-state forwarding, deactivation
      on close/shutdown, settings-change restart.
- [x] `PluginSettings.cs` — source-role fields (commit dbd59c7).
- [x] `MusicBeeSendSpin.csproj` — Noise.NET + libsodium (commit 5dff4b0); no further deps needed.

## 6. Verify

- [x] `dotnet build` succeeds (0 errors).
- [x] Tests written as I go: tests/pairing-tests (29 checks), tests/source-connection (60 checks — in-process fake Sendspin server incl. the full pairing + streaming flow), noise-interop + live-handshake-probe still green.
- [ ] On Windows + MusicBee + a running Music Assistant (⚠️ open risk: HQPlayer is `PluginType.DataStream`,
      this plugin is `General` — if the render device does not appear in Preferences → Player → Output,
      try switching `_about.Type` to `DataStream`):
  - [ ] "Music Assistant (Sendspin)" render device appears in Preferences → Player → Output.
  - [ ] Selecting it + playing → audio on MA targets, **local output silent without `Player_SetMute`**.
  - [ ] pause/resume/stop + track changes behave on the MA side.
  - [ ] existing speaker mode still works (additive only).
