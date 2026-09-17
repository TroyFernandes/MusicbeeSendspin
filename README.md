# MusicBee Sendspin Plugin

Experimental MusicBee plugin to send audio to Music Assistant as a Sendspin **source**.

## Requirements

- A running [Music Assistant](https://www.music-assistant.io/) server

## Installation

1. Build the plugin (`dotnet build MusicBeeSendSpin.csproj`) or grab a release
2. Copy `mb_SendSpin.dll` into MusicBee's Plugins folder
3. Add a firewall rule for windows (adjust port if you change it): ``New-NetFirewallRule -DisplayName "MusicBee SendSpin 8927" -Direction Inbound -Protocol TCP -LocalPort 8927 -Action Allow -Profile Domain,Private``
4. Restart MusicBee, enable the plugin (Preferences → Plugins)

## Setup

1. **MusicBee**: Tools → SendSpin Settings → *Music Assistant* tab — enable the render device,
   copy the **pairing token** (also logged at startup)
2. **Music Assistant**: Settings → Players → the new *Music Assistant (Sendspin)* player →
   **Setup** → paste the token
3. **Change output device in MusicBee**: either do:
    - Right Click the speaker icon in musicbee → Output To → Sendspin
    - Edit → Edit Preferences → Player → sound device → Sendspin
5. **Play**: start music in MusicBee first, then in MA select a target player → Browse →
   *Sendspin Source* → the MusicBee input → Play 
    - (note: wont show in the UI without audio playing)

## Settings (Tools → SendSpin Settings)

| Tab | What |
| --- | --- |
| **Advanced** | Debug logging |
| **Music Assistant** | Enable/disable, device name, mDNS or manual `host:port`, pairing token |

## Building from source

- .NET SDK that can build **net48** (VS 2022 or `dotnet build`)

## Troubleshooting

- **Firewall** - Usually is the main issue, you *must* create the rule yourself, the plugin won't. check that the rule is in place.
  - `Get-NetFirewallRule -Direction Inbound | Get-NetFirewallPortFilter | Where-Object { $_.LocalPort -like '*8927*' }`
  - `Get-NetFirewallRule -DisplayName "*MusicBee*"`
- **Device doesn't appear in Output** — the render device is disabled in settings, or the plugin
  failed at startup; check the log for `[ERROR] [InitializeSourceDevice]`
- **No audio in MA** — check order: MusicBee playing first, then start the Live Input in MA;
  make sure the source is paired (player shows paired, not "connected without pairing")
- **Logs** — Always check the logs!
  - In Musicbee → Help → Support → View Error Log

## Notes

- The Sendspin transport (Noise handshake, framing, pairing) is port of the
  [sendspin-dotnet SDK](https://github.com/Sendspin/sendspin-dotnet) because the SDK targets
  net8/net10 and MusicBee hosts net48 in-process. See [PORT-MAPPING.md](PORT-MAPPING.md)
- The legacy speaker mode created before the source@v1 role (MusicBee acting as a Sendspin server / dialing speakers) was
  removed; it lives on the `archive/speaker-mode` branch.

## References

- [Sendspin Spec](https://github.com/Sendspin/spec)
- [sendspin-dotnet](https://github.com/Sendspin/sendspin-dotnet)
- [MusicBee-HQPlayer](https://github.com/tracemouse/MusicBee-HQPlayer)
