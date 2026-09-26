# MusicBee Sendspin Plugin

Experimental MusicBee plugin to send audio to Music Assistant as a Sendspin **source**.

## Requirements

- A running [Music Assistant](https://www.music-assistant.io/) server

## Installation

1. Build the plugin (`dotnet build MusicBeeSendSpin.csproj`). Or grab a release
2. Copy `mb_SendSpin.dll` into MusicBee's Plugins folder.
3. Add a firewall rule for Windows (adjust the port if you change it).
   ```powershell
   New-NetFirewallRule -DisplayName "MusicBee SendSpin 8927" -Direction Inbound -Protocol TCP -LocalPort 8927 -Action Allow -Profile Domain,Private
   ```
4. Restart MusicBee. Then enable the plugin (Preferences → Plugins).

## Setup

1. **MusicBee**: open Tools → SendSpin Settings → *Music Assistant* section. Enable the render device. Copy the **pairing token** (also logged at startup).
2. **Music Assistant**: open Settings → Players → the new *Music Assistant (Sendspin)* player. Click **Setup** and paste the token.
3. **Change the output device in MusicBee**, either:
   - Right Click the speaker icon in musicbee → Output To → Sendspin.
   - Edit → Edit Preferences → Player → sound device → Sendspin.
4. **Play**: in MA select a target player → Browse → *Sendspin Source* → the MusicBee input → Play.
5. Note: start music in MusicBee first. The source will not show in the UI without audio playing.

## Settings (Tools → SendSpin Settings — a single page)

| Section | What |
| --- | --- |
| **Music Assistant** | Enable/disable, device name, mDNS or manual `host:port`, pairing token |
| **Advanced** | Debug logging |

> **Server port** = the *Sendspin endpoint* port (default **8927**), not the Music Assistant web-UI port (e.g. 8097). Only used in manual mode — ignored when auto-discover (mDNS) is on.

## Building from source

- .NET SDK that can build **net48** (VS 2022 or `dotnet build`)

## Troubleshooting

- **Firewall** — usually the main issue. The plugin will not create the rule. You must create it yourself. Check that the rule is in place.
  - `Get-NetFirewallRule -Direction Inbound | Get-NetFirewallPortFilter | Where-Object { $_.LocalPort -like '*8927*' }`
  - `Get-NetFirewallRule -DisplayName "*MusicBee*"`
- **Device does not appear in Output** — the render device is disabled in settings, or the plugin failed at startup. Check the log for `[ERROR] [InitializeSourceDevice]`
- **No audio in MA** — check the order. Play music in MusicBee first, then start the Live Input in MA. The player should show paired, not "connected without pairing".
- **Logs** — Always check the logs!
  - In Musicbee → Help → Support → View Error Log

## Notes

- The Sendspin transport (Noise handshake, framing, pairing) is a port of the
  [sendspin-dotnet SDK](https://github.com/Sendspin/sendspin-dotnet). The SDK targets net8/net10,
  but MusicBee hosts net48 in-process. See [PORT-MAPPING.md](PORT-MAPPING.md)
- The legacy speaker mode created before the source@v1 role (MusicBee acting as a Sendspin server / dialing speakers)
  no longer exists. It lives on the `archive/speaker-mode` branch.

## References

- [Sendspin Spec](https://github.com/Sendspin/spec)
- [sendspin-dotnet](https://github.com/Sendspin/sendspin-dotnet)
- [MusicBee-HQPlayer](https://github.com/tracemouse/MusicBee-HQPlayer)
