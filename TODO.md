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

## 1. Component A — MusicBee render device (front-end)
- [ ] Verify exact render-device method names + signatures in `MusicBeeInterface.cs` / `Plugins/MusicBeePlugin.cs`
      (cross-check the HQPlayer plugin's `MusicBeeHQP.vb`).
- [ ] `string[] GetRenderingDevices()` → returns one device (name from settings, default "Music Assistant (Sendspin)").
- [ ] `bool SetActiveRenderingDevice(string name)` → on activate: (re)establish source-role connection; on deactivate: tear down.
- [ ] `bool PlayToDevice(string url, int streamHandle)` → drive audio capture + feed the source connection.
- [ ] Raise `MB_SendNotification(CallbackType.RenderingDevicesChanged)` on list change.
- [ ] Forward transport state: pause / resume / stop / track change / volume+mute (decide local vs forward; document).

## 2. Component B — Sendspin `source@v1` client (new)
- [ ] Add `Noise.NET` (+ `libsodium`) to `MusicBeeSendSpin.csproj` (verify net48 restore).
- [ ] Port `Connection/Noise/` (+ `Framing/`) wrapper into `SendSpin/Noise/` (NoiseWireFraming, NoiseConstants,
      NoiseCipherSuite, NoisePsk, SentinelPskResolver, Base64UrlText, SendspinIdentity, NoiseHandshakeJson, WireFrame, IWireFraming).
      Use reflection-based System.Text.Json (drop the source-gen `MessageSerializerContext`).
- [ ] `SendSpin/SourceConnection.cs` — WebSocket to the MA **server** (client role).
- [ ] Sentinel KKpsk2 handshake (client/responder) per `spec/connection.md`.
- [ ] `client/hello` — advertise `source@v1` + codecs actually sent.
- [ ] `client/time` / respond to `server/time` (server-clock sync).
- [ ] Handle `server/command {source:{command:"start"|"pause"|"stop"}}`.
- [ ] `client-stream/start` with current track's `player` metadata (from `NowPlaying_*` APIs).
- [ ] Binary audio chunks: type `0x0C` + 8-byte BE µs timestamp (server clock) + payload; **pace to real-time** (reuse pump pattern).
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
