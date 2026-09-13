# MusicBee SendSpin Plugin

A MusicBee plugin that exposes playback as a **Music Assistant (Sendspin) render device**: select
it in Preferences → Player → Output and everything MusicBee plays is streamed to Music Assistant
as a Sendspin **source** — MusicBee's local output stays silent, with no mute hacks.

## How it works

```text
MusicBee playback (DSP, EQ, ReplayGain applied)
  → captured from MusicBee's decode stream (BASS)
  → encoded (Opus 48 kHz stereo by default)
  → Sendspin source@v1 protocol over WebSocket + Noise_KKpsk2 encryption
  → Music Assistant → whatever targets you pick there (speakers, AirPlay, web players, …)
```

- **Render device**: appears as "Music Assistant (Sendspin)" in MusicBee's output list; the
  plugin feeds MusicBee's decode stream straight into the Sendspin connection.
- **Music Assistant drives the stream**: audio flows when MA starts the input (Sendspin Source
  Live Input); pause/stop in MusicBee ends it.
- **Encrypted + paired**: full Sendspin security — Noise KKpsk2, pairing via a token you paste
  into Music Assistant (Settings → Music Assistant tab → *Copy pairing token*). Pairing is
  remembered; it survives restarts.
- **mDNS discovery**: finds the Music Assistant server automatically, or set a manual
  `host:port`.

## Requirements

- MusicBee 3.4+ (32- or 64-bit), Windows
- .NET Framework 4.8 (bundled with Windows 10/11)
- A running [Music Assistant](https://www.music-assistant.io/) server with Sendspin enabled
  (built-in), **and** the bundled **Sendspin Source** plugin in MA to expose the input

## Installation

1. Build the plugin (`dotnet build MusicBeeSendSpin.csproj`) or grab a release
2. Copy `mb_SendSpin.dll` **and the `Native/` folder** into MusicBee's Plugins folder
   (keep the `Native/x64|` + `Native/x86/` subfolders together — they carry libsodium)
3. Restart MusicBee, enable the plugin (Preferences → Plugins)

## Setup

1. **MusicBee**: Tools → SendSpin Settings → *Music Assistant* tab — enable the render device,
   copy the **pairing token** (also logged at startup)
2. **Music Assistant**: Settings → Players → the new *Music Assistant (Sendspin)* player →
   **Setup** → paste the token
3. **Play**: start music in MusicBee first, then in MA select a target player → Browse →
   *Sendspin Source* → the MusicBee input → Play

Order matters: MA waits ~5 s for audio after starting an input, so start MusicBee playback first.

## Settings (Tools → SendSpin Settings)

| Tab | What |
| --- | --- |
| **Audio** | Codec (Opus recommended), sample rate, channels, bit depth, Opus bitrate, MusicBee DSP/ReplayGain |
| **Advanced** | Debug logging |
| **Music Assistant** | Enable/disable, device name, mDNS or manual `host:port`, pairing token |

## Building from source

- .NET SDK that can build **net48** (VS 2022 or `dotnet build`)
- NuGet restores automatically: Noise.NET, libsodium, Concentus (Opus), Makaretu.Dns, Newtonsoft.Json
- Debug build output goes straight to the MusicBee Plugins folder (see `.csproj`)

## Troubleshooting

- **Device doesn't appear in Output** — the render device is disabled in settings, or the plugin
  failed at startup; check the log for `[ERROR] [InitializeSourceDevice]`
- **No audio in MA** — check order: MusicBee playing first, then start the Live Input in MA;
  make sure the source is paired (player shows paired, not "connected without pairing")
- **Native load errors** (`Noise.Libsodium` type-init) — the `Native/x86` or `Native/x64`
  folder is missing next to the plugin DLL; the log names the exact path probed
- **Logs** — MusicBee's log (View → Error Log) carries every `[SendSpin]` line; connection,
  pairing, and clock-sync states are all logged under `[Source]`

## Notes for maintainers

- The Sendspin transport (Noise handshake, framing, pairing) is a hand port of the
  [sendspin-dotnet SDK](https://github.com/Sendspin/sendspin-dotnet) because the SDK targets
  net8/net10 and MusicBee hosts net48 in-process. **See [PORT-MAPPING.md](PORT-MAPPING.md)
  before changing `SendSpin/Noise/*`** — it maps every component to its SDK counterpart,
  lists the deliberate divergences, and has the update runbook + re-verification ladder.
- The legacy speaker mode (MusicBee acting as a Sendspin server / dialing speakers) was
  removed; it lives on the `archive/speaker-mode` branch.
- Tests: `tests/` — transport interop against the SDK's own test server, pairing/token
  (spec reference vectors), and a full source-role loop against an in-process fake MA server.

## License

MIT License — see LICENSE file for details.

## Credits

- MusicBee Plugin API by Steven Mayall
- SendSpin protocol by the Open Home Foundation (spec, aiosendspin, sendspin-dotnet)
- Opus codec via Concentus (Xiph.org)
