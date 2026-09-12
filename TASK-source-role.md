# Task: Send MusicBee audio to Music Assistant as a Sendspin **source** (via a MusicBee render device)

## Goal (user-facing)

Today, to get MusicBee playing into Sendspin, the plugin sends audio to a Sendspin speaker and **mutes MusicBee's local output** (`Player_SetMute(true)`) to stop you hearing it twice. That mute hack is inelegant and fragile — it mutes the whole player and fights the normal play/pause/mute UX.

Add a **MusicBee render device** (e.g. **"Music Assistant (Sendspin)"**) the way the HQPlayer plugin does. When the user selects it as the MusicBee output (Preferences → Player → Output), MusicBee routes playback **to that device instead of the local output** — so there is **no local audio and no mute hack**. The plugin then captures MusicBee's already-processed audio and sends it to a running **Music Assistant** server using the **Sendspin `source@v1` role**. Music Assistant then plays it to whatever targets the user picks there (speakers, AirPlay, etc.).

Prerequisite: a Music Assistant server is already running and listening for Sendspin clients. That is a given for anyone using Sendspin.

## Why this design (read before coding)

Two jobs, two components:

1. **Render device (front-end, in MusicBee).** Makes MusicBee treat the plugin as the *output destination*. This is what kills the local-mute hack — MusicBee stops feeding the local device and hands audio to the render device. MusicBee has a **native plugin API** for render devices (the same one the HQPlayer plugin uses). You do **not** need UPnP/DLNA for the MusicBee side.

2. **Sendspin source role (transport).** Carries the audio to Music Assistant over a new WebSocket speaking the Sendspin *source* protocol. Here **Music Assistant is the Sendspin *server*** and the plugin is the **client**. This is a *different* role and handshake from the existing `SpeakerConnection` (where the plugin acts as the **server** dials a speaker). **Do not reuse `SpeakerConnection`** — the message direction, the hello, and the handshake are the inverse.

Audio capture **already exists** in this plugin (`AudioCaptureService` / `DirectDecodeService`). Reuse it — do not reinvent it.

## Reference material (READ BEFORE CODING)

- **Sendspin spec** (cloned at `/home/nixos/spec`) — authoritative protocol:
  - `spec/roles/source/v1.md` — the source role: connection, `client/hello` (advertise `source@v1`), `client/time`, waiting for `server/command {source:{command:"start"}}`, `client-stream/start {player:{...}}`, and the **binary audio chunk** format (type byte `0x0C`, then an 8-byte big-endian server-clock timestamp in µs, then the audio payload). Also pause/stop → `client-stream/end`.
  - `spec/connection.md` — the connection-level **Noise `KKpsk2` handshake** and the **Sentinel PSK** flow for unpaired clients (the client always uses the sentinel; the server issues a random nonce; the per-connection PSK is derived from `sentinel:nonce` per the spec — the client does **not** persist a long-term PSK).
  - `spec/messaging.md` — message framing / chunk types / envelope.

- **Sendspin .NET SDK** (cloned at `/home/nixos/sendspin-dotnet`, from https://github.com/Sendspin/sendspin-dotnet) — the authoritative **crypto + handshake** reference:
  - `src/Sendspin.SDK/Connection/Noise/` — Noise `KKpsk2` handshake, Sentinel PSK flow, `NoiseWireFraming`, `NoiseConstants`, `NoiseCipherSuite`, `NoisePsk`, `SendspinIdentity`, `Base64UrlText`, and the hand-rolled `Pairing/X25519.cs`.
  - Dependencies: NuGet `Noise.NET` 1.0.0 (Noise protocol), `libsodium` 1.0.22, `Concentus` 2.2.2 (Opus). Reuse these + the wrapper source — **do not hand-roll crypto.**
  - ⚠️ The SDK targets `net8.0;net10.0` — **not** a direct project-reference for the net48 plugin. Plan: add the NuGet primitives (`Noise.NET`, `libsodium`) to the net48 plugin (verify netstandard2.0 support) and port the thin Sendspin KKpsk2/sentinel wrapper from this repo's `Connection/Noise/` files into `SendSpin/`.
- **HQPlayer plugin** (`/home/nixos/MusicBee-HQPlayer`) — the model for the **render-device** half. Key files:
  - `MusicBeeHQP.vb` — the MusicBee render-device entry points: `GetRenderingDevices() As String()`, `SetActiveRenderingDevice(name As String) As Boolean`, `PlayToDevice(url As String, streamHandle As Integer) As Boolean`, and how transport-state changes are forwarded to the active device.
  - `ControlPointManager.vb` — how the render-device list is maintained and `mbApiInterface.MB_SendNotification(CallbackType.RenderingDevicesChanged)` is raised.
  - `MediaRendererDevice.vb` — a reference device object and its `PlayToDevice`.

  You are copying only the **render-device API surface** (the three public methods + the notification), **not** the UPnP framework. In C# expose equivalent methods on the plugin class so MusicBee reflects them.

- **This repo** (`/home/nixos/MusicbeeSendspin`):
  - `SendSpinPlugin.cs` — plugin entry point, settings, service wiring.
  - `MusicBeeInterface.cs` — the MusicBee API surface (find the render-device methods + `MB_SendNotification` here).
  - `Plugins/MusicBeePlugin.cs` — the class MusicBee loads; this is where render-device methods are exposed.
  - `SendSpin/SpeakerConnection.cs` — the EXISTING client (plugin-as-server role). Read it to reuse the WS + real-time pump pattern; note it does **not** speak the source role.
  - `SendSpin/AudioCaptureService.cs` — capture from `Player_OpenStreamHandle` (BASS) → resample → encode → `AudioDataAvailable`.
  - `SendSpin/DirectDecodeService.cs` — alternate: decode the current file directly with BASS.
  - `SendSpin/SpeakerDiscoveryService.cs` — mDNS discovery; you'll want an analog for finding the Music Assistant **server**.
  - `PluginSettings.cs` — settings.

## Component A — MusicBee render device

Expose a render device to MusicBee. Implement C# equivalents of the HQPlayer VB methods on the plugin class:

- `string[] GetRenderingDevices()` — return one device, e.g. `"Music Assistant (Sendspin)"` (name from settings).
- `bool SetActiveRenderingDevice(string name)` — MusicBee calls this when the user selects the device as the output. On activation: (re)establish the source-role connection to Music Assistant and get ready to stream. On deactivation: tear it down.
- `bool PlayToDevice(string url, int streamHandle)` — MusicBee calls this when playback starts for this device. Drive the existing audio-capture service (or direct-decode path) and feed the resulting encoded audio into the source connection.
- Raise `MB_SendNotification(CallbackType.RenderingDevicesChanged)` whenever the device list changes (startup, settings change, MA server found/lost).
- Forward transport state from MusicBee to the source role: pause → per-spec pause (`client-stream/end` or a pause command), resume → re-`start`, stop → `client-stream/end`, track change → end the old stream and re-`client-stream/start` with the new track's `player` metadata; volume/mute → forward per spec **or** apply locally (decide and document).

Confirm the exact render-device method names against this repo's `MusicBeeInterface.cs` / `MusicBeePlugin.cs` — MusicBee reflects specific signatures, so match what the HQPlayer plugin uses (adapted to C#) and what this repo's MB interface exposes.

## Component B — Sendspin `source@v1` client (new)

Create a new connection class (e.g. `SendSpin/SourceConnection.cs`) speaking the **source** role to a Music Assistant **server**:

- **Find the server:** prefer mDNS discovery of the Music Assistant Sendspin **server** (service type `_sendspin-server._tcp`, read the `ws` TXT record for the WebSocket URL) — mirror the existing `SpeakerDiscoveryService`. Also support a manual `host:port` in settings as a fallback.
- **Connect** over WebSocket to the discovered URL.
- **Handshake:** run the Noise `KKpsk2` handshake as the **client/initiator** using the **Sentinel PSK** flow per `spec/connection.md` (client always uses the sentinel; handle the server-issued nonce; derive the per-connection PSK; do not persist long-term PSKs). **Verify message order against the spec before implementing** — the client-side direction is the inverse of the existing `SpeakerConnection`. **Check whether `Sendspin.SDK` already exposes the client-side handshake primitives (it very likely does) and reuse them — do not hand-roll crypto.**
- **`client/hello`:** advertise `source@v1` and the audio codec(s) you'll actually send.
- **`client/time`** / respond to **`server/time`** as specified.
- **Wait for** `server/command {source:{command:"start"}}` before streaming (Music Assistant tells you to start). On `pause`/`stop`, respond per spec.
- **`client-stream/start`** with the current track's `player` metadata (title, artist, album, albumArtUrl, position, duration, …) from MusicBee's `NowPlaying_*` APIs.
- **Binary audio chunks:** per `spec/roles/source/v1.md` — type byte `0x0C`, 8-byte big-endian timestamp (µs, on the server clock synced during handshake), then the encoded audio payload. **Pace to real-time** (the existing pump pattern is a good reference) — do not buffer ahead unboundedly.
- **Robustness:** reconnect with backoff if the WS drops; on Music Assistant's `start` after a reconnect, resume cleanly (seek/position).

## Audio path

Reuse the existing capture:

- Preferred: `AudioCaptureService.Start(streamHandle)` — captures MusicBee's **processed** output (ReplayGain/EQ applied), which is what a downstream server wants to receive.
- Codec: send **Opus** (the plugin already encodes Opus 48 kHz stereo via Concentus) — simplest and already wired. Send **PCM** only if MA requires it. Declare whatever you send in `client/hello` and `client-stream/start`.
- Feed the `AudioDataAvailable` payload into the source connection's chunk queue.

## Settings to add

- Target: auto-discover (mDNS) and/or manual `host:port` for the Music Assistant Sendspin server.
- Render-device name (default `"Music Assistant (Sendspin)"`).
- Audio codec for the source stream (default Opus).
- Leave all existing speaker-mode settings untouched.

## File map (suggested)

New:
- `SendSpin/SourceConnection.cs` — source-role client (handshake + streaming).
- `SendSpin/SourceDiscoveryService.cs` (or fold into existing discovery) — mDNS for `_sendspin-server._tcp`.
- `SendSpin/RenderDevice.cs` — optional device object/state (or just methods on the plugin class).

Modified:
- `Plugins/MusicBeePlugin.cs` — expose the render-device methods + wire the new services; send `RenderingDevicesChanged`.
- `SendSpinPlugin.cs` — settings + lifecycle (start/stop the render device and source connection on activation).
- `PluginSettings.cs` — new settings fields.
- `MusicBeeSendSpin.csproj` — **no new deps expected**; first check whether `Sendspin.SDK` exposes the Noise/`KKpsk2` client handshake and reuse it.

Do **not** remove or break the existing server/speaker mode — this is additive.

## Acceptance criteria

- `dotnet build` succeeds (0 errors).
- A "Music Assistant (Sendspin)" render device appears in MusicBee → Preferences → Player → Output.
- Selecting it and playing a track: audio plays on Music Assistant's targets, and **MusicBee's local output is silent without the plugin calling `Player_SetMute`** (do not use the local-mute hack on this path).
- Pause/resume/stop and track changes behave correctly on the Music Assistant side.
- The existing speaker mode still works as before.

## Risks / verify-before-build

2. **Noise `KKpsk2` client handshake** — reuse the SDK primitives; do not hand-roll crypto.
3. **Render-device method names** must match MusicBee's expected signatures (copy from HQPlayer / this repo's `MusicBeeInterface`).
4. **Clock/pacing** — source chunks use the server clock; ensure the handshake time-sync is done and chunks are paced to real-time.
5. **Codec acceptance** — confirm MA accepts the codec you send (Opus is the safe default).

## Suggested first steps

1. Verify a target Music Assistant accepts an incoming `source@v1` client (talk to it / read its docs). If not, stop.
2. Confirm `Sendspin.SDK` exposes the client-side Noise handshake; if not, decide the crypto dependency.
3. Build Component A (render device) so the device shows up in MusicBee and `SetActiveRenderingDevice` fires.
4. Build Component B (source-role client) and wire it to the existing audio capture.
5. End-to-end: select the render device → play → confirm audio on Music Assistant, silent locally, no `Player_SetMute`.

# Additional Requirements

1. Write C# Code only
2. Create Tests as you go along
3. Create small git commits and try to test often
