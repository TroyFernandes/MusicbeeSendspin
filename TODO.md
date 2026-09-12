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
- [ ] Verify exact render-device method names + signatures in `MusicBeeInterface.cs` / `Plugins/MusicBeePlugin.cs`
      (cross-check the HQPlayer plugin's `MusicBeeHQP.vb`).
- [ ] `string[] GetRenderingDevices()` → returns one device (name from settings, default "Music Assistant (Sendspin)").
- [ ] `bool SetActiveRenderingDevice(string name)` → on activate: (re)establish source-role connection; on deactivate: tear down.
- [ ] `bool PlayToDevice(string url, int streamHandle)` → drive audio capture + feed the source connection.
- [ ] Raise `MB_SendNotification(CallbackType.RenderingDevicesChanged)` on list change.
- [ ] Forward transport state: pause / resume / stop / track change / volume+mute (decide local vs forward; document).

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
- [ ] **Pairing prerequisite (new — gating check resolved above):**
  - [ ] `SendSpin/Noise/PairingStore.cs` — JSON-file-backed pairing-record store (>=5 long-term records,
        thread-safe) implementing `INoisePskResolver` (psk_id → long-term PSK + pairing PSK; Sentinel
        fallback already lives in NoiseWireFraming). Holds the plugin's own persistent **pairing PSK**
        (CSPRNG, category `pr`, never consumed by pairing).
  - [ ] `PairingToken.Encode` port — `SP:0` + base32(client_pub || pairing_psk), 2→9 transliteration
        (test against the spec's reference vector). Exposed for the settings UI.
  - [ ] Expose the **matched-PSK category** from `NoiseWireFraming` (needed to gate `client/pair-finalize`:
        the connection's matched PSK must be the pairing PSK when `pairing.method == 'pairing_psk'`).
  - [ ] Pairing flow in `SourceConnection`: on `server/activate {activities:['pairing'], pairing.method:'pairing_psk'}`
        → verify matched PSK is Pairing → send `client/pair-finalize {long_term_psk}` (fresh CSPRNG 32B)
        → on `server/pair-finalize` persist the record {server_id, psk, lt} → server re-handshakes in-band to the
        new long-term PSK (NoiseWireFraming already handles re-handshake + deferred reply) → hello exchange
        repeats → activate now carries `source@v1`.
- [ ] **Build order (mirrors the net8 probe's Noise drive, NOT `SpeakerConnection` — that's the old plaintext protocol):**
  - [ ] `SendSpin/SourceConnection.cs` — dial the MA server with `ClientWebSocket`, run the proven handshake drive
        into a persistent connect + receive loop. ONE writer task consuming a send queue (serializes ALL
        `ClientWebSocket` writes; receive loop is the single producer of handshake/control replies, so ordering
        holds; `EncodeDeferredReply` runs inside the writer's send step per the framing contract).
  - [ ] After transport-ready, **receive `server/hello` → answer with `client/hello`** (we do *not* initiate it):
        `supported_roles:["source@v1"]`, `source@v1_support:{}`, `trust_level:"user"|"none"` (long-term-paired ? user : none,
        mirroring the SDK), `unpaired_access:{enabled:true}`,
        `device_info{product_name,manufacturer,software_version}`,
        `supported_pair_methods:[{method:"pairing_psk",locations:["operator"]}]` (list form — see above).
  - [ ] **Read `server/activate` → grant check.** With `source@v1` in `active_roles` (long-term paired): proceed.
        Without it (unpaired): stay idle/waiting — pairing is the path, never stream without the grant.
        Mirror the SDK's client-side gate: refuse `source@v1` granted without a long-term PSK.
  - [ ] **Clock sync** (permitted only after `server/activate`): burst `client/time {client_transmitted:T1}` (µs) →
        `server/time {server_received:T2, server_transmitted:T3}` + local T4; `offset=((T2-T1)+(T3-T4))/2`,
        `rtt=(T4-T1)-(T3-T2)`, discard `rtt<=0`, keep min-RTT. Start with best-of-burst offset (skip the full Kalman);
        re-burst periodically + after reconnect. Then `client/state {available:true, source:{}}` (this opens
        the server's acceptance of source binary data — aiosendspin requires the state before chunks).
  - [ ] **Stream is server-initiated**: send audio only on `server/command {source:{command:"start"}}`.
  - [ ] `client_stream/start` with the `source` object only (codec/channels/sample_rate/bit_depth; opus =
        no codec_header, bit_depth ignored — aiosendspin decodes opus at 16-bit canonical), then binary chunks.
  - [ ] Binary audio chunk: type `0x0C` + 8-byte BE µs timestamp (server clock = local + offset) + payload;
        **pace to real-time** — the capture is inherently 1x; bounded drop-oldest queue (spec: drop stale backlog,
        resume from live capture); chunks 20 ms (spec bounds 5–150 ms), one Opus packet per chunk.
  - [ ] Handle `server/command {source:{command:"start"|"stop"}}` (idempotent per spec). MusicBee pause/stop →
        `client_stream/end`; resume → fresh `client_stream/start` (no new server grant needed — verified in
        aiosendspin `SourceV1Role`).
  - [ ] Reconnect with backoff; streaming state is per-connection (spec): a server that still wants the stream
        re-sends `start` after reconnect — just report state and wait.
- [ ] `SendSpin/SourceDiscoveryService.cs` — mDNS for `_sendspin-server._tcp` (read `ws` TXT record) + manual `host:port` fallback.

## 3. Audio path
- [ ] Wire `AudioDataAvailable` (existing capture) → source connection chunk queue (Opus 48k stereo default).

## 4. Settings
- [ ] Target: auto-discover (mDNS) and/or manual `host:port`.
- [ ] Render-device name (default "Music Assistant (Sendspin)").
- [ ] Audio codec (default Opus).
- [ ] Leave existing speaker-mode settings untouched.

## 5. Wire-up
- [ ] `Plugins/MusicBeePlugin.cs` — expose render-device methods + wire new services; send `RenderingDevicesChanged`.
- [ ] `SendSpinPlugin.cs` — settings + lifecycle (start/stop render device + source connection on activation).
- [ ] `PluginSettings.cs` — new settings fields.
- [ ] `MusicBeeSendSpin.csproj` — new deps.

## 6. Verify
- [ ] `dotnet build` succeeds (0 errors).
- [ ] Tests written as I go (task requires "create tests as you go").
- [ ] On Windows + MusicBee + a running Music Assistant:
  - [ ] "Music Assistant (Sendspin)" render device appears in Preferences → Player → Output.
  - [ ] Selecting it + playing → audio on MA targets, **local output silent without `Player_SetMute`**.
  - [ ] pause/resume/stop + track changes behave on the MA side.
  - [ ] existing speaker mode still works (additive only).
