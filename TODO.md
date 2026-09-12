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

> **Not 100% set in stone (might be wrong):** the wire contract below is inferred from the reference
> SDK (`sendspin-dotnet`) + `spec/`; only the *Noise handshake* is proven against a live MA (net8 probe).
> The app-level exchange *order* — and especially what `active_roles` MA grants an **unpaired/Sentinel**
> client — are best-guesses. Treat the step order as a working model, not a fact; correct it from the
> first live run's actual traffic.

- [x] Add `Noise.NET` (+ `libsodium`) to `MusicBeeSendSpin.csproj` (verify net48 restore).
- [x] Port `Connection/Noise/` (+ `Framing/`) wrapper into `SendSpin/Noise/` (NoiseWireFraming, NoiseConstants,
      NoiseCipherSuite, NoisePsk, SentinelPskResolver, Base64UrlText, SendspinIdentity, WireFrame, IWireFraming).
      JSON envelopes use Newtonsoft (net48 has no System.Text.Json) — same field order as the spec schemas.
- [x] **Noise KKpsk2 handshake proven against live MA** — the ported `NoiseWireFraming` + the probe's drive
      (`Start()` → send; receive → `ProcessInbound` → send `Replies`; `IsTransportReady` flips) completed the full
      handshake against 192.168.1.10:8927. Remaining work is *embedding that proven drive* below, not building it.
- [ ] **Build order (mirrors the net8 probe's Noise drive, NOT `SpeakerConnection` — that's the old plaintext protocol):**
  - [ ] `SendSpin/SourceConnection.cs` — dial the MA server with `ClientWebSocket`, run the proven handshake drive
        into a persistent connect + receive loop. Serialize sends with a `SemaphoreSlim` (`ClientWebSocket` is not
        thread-safe for concurrent writes).
  - [ ] After transport-ready, **receive `server/hello` → answer with `client/hello`** (we do *not* initiate it):
        `supported_roles:["source@v1"]`, `source@v1_support:{}`, `trust_level:"none"`, `unpaired_access:{enabled:true}`,
        `device_info{product_name,manufacturer,software_version}`.
  - [ ] **Read `server/activate` → log `active_roles`.** GATING CHECK: does MA grant `source@v1` to an unpaired/Sentinel
        client, or is pairing required? The reference SDK *refuses* source@v1 without `user` trust — if MA doesn't grant
        it, pairing is a prerequisite for everything downstream. **Resolve before building audio.**
  - [ ] **Clock sync** (permitted only after `server/activate`): burst `client/time {client_transmitted:T1}` (µs) →
        `server/time {server_received:T2, server_transmitted:T3}` + local T4; `offset=((T2-T1)+(T3-T4))/2`,
        `rtt=(T4-T1)-(T3-T2)`, discard `rtt<=0`, keep min-RTT. Start with best-of-burst offset (skip the full Kalman).
  - [ ] **Stream is server-initiated**: send audio only on `server/command {source:{command:"start"}}`.
  - [ ] `client_stream/start` advertising the stream (codec/channels/sample_rate/bit_depth) + current track's player
        metadata (from `NowPlaying_*` APIs), then binary chunks.
  - [ ] Binary audio chunk: type `0x0C` + 8-byte BE µs timestamp (server clock = local + offset) + payload;
        **pace to real-time** (reuse the `SpeakerConnection` pump pattern — proven fix for queue overflow).
  - [ ] Handle `server/command {source:{command:"start"|"pause"|"stop"}}` (pause = stop sending; stop = end stream).
  - [ ] Reconnect with backoff; on `start` after reconnect, resume cleanly (seek/position).
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
