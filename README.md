# MusicBee SendSpin Plugin

A MusicBee plugin that streams audio to SendSpin-compatible wireless speakers with synchronized multi-room playback.

## Features

- **SendSpin Protocol Server** - Acts as a SendSpin server, allowing compatible clients to connect and receive audio
- **Audio Capture** - Captures MusicBee's audio output including DSP effects and ReplayGain
- **Multi-Codec Support** - Opus (recommended), FLAC, and PCM audio encoding
- **Speaker Groups** - Organize speakers into groups for synchronized playback
- **Individual Volume Control** - Control volume per speaker and per group
- **Automatic Discovery** - mDNS support for automatic client discovery
- **Low Latency** - Microsecond-level synchronization for perfect multi-room audio

## Requirements

- MusicBee 3.4 or later
- Windows 7/8/10/11
- .NET Framework 4.8
- BASS audio library (included with MusicBee)

## Installation

1. Build the plugin or download the release
2. Copy `mb_SendSpin.dll` to your MusicBee Plugins folder:
   - Usually: `%APPDATA%\MusicBee\Plugins\`
3. Restart MusicBee
4. Enable the plugin in MusicBee: Edit → Preferences → Plugins

## Configuration

### Server Settings

- **Enable SendSpin Server** - Turn the server on/off
- **Server Name** - Name shown to clients
- **Server Port** - Default: 8927 (SendSpin standard port)
- **Enable mDNS Discovery** - Allow clients to automatically discover the server

### Audio Settings

- **Audio Codec**
  - **Opus** (Recommended) - Excellent quality at low bitrates, lowest latency
  - **FLAC** - Lossless compression, higher bandwidth
  - **PCM** - Uncompressed audio, highest bandwidth
- **Sample Rate** - 44100, 48000, or 96000 Hz
- **Channels** - Stereo or Mono
- **Bit Depth** - 16-bit or 24-bit
- **Opus Bitrate** - 32-512 kbps (higher = better quality)

### DSP Settings

- **Apply MusicBee DSP Effects** - Include equalizer, VST plugins, etc.
- **ReplayGain** - Off, Track, Album, or Smart mode

### Advanced Settings

- **Client Buffer Size** - Higher = more stable, lower = less latency
- **Debug Logging** - Enable for troubleshooting

## Usage

### Managing Speakers

1. Open `Tools → SendSpin Speakers`
2. Connected speakers appear in the "Speakers" list
3. Create groups to organize speakers
4. Move speakers between groups by selecting and clicking "Move to Group..."
5. Adjust volume per speaker or per group

### Playback

Simply play music in MusicBee - it will automatically stream to all connected SendSpin clients!

## SendSpin Protocol

This plugin implements the SendSpin protocol as a server. For protocol details, see the [SendSpin Protocol Specification](../spec/README.md).

### Supported Roles

- **player** - Audio playback with synchronized timestamps
- **controller** - Play/pause/volume control
- **metadata** - Track title, artist, album info
- **artwork** - Album artwork streaming

## Building from Source

### Prerequisites

- Visual Studio 2022 or later
- .NET Framework 4.8 SDK
- NuGet packages (automatically restored):
  - Sendspin.SDK
  - Concentus (Opus encoder)
  - Makaretu.Dns.Multicast (mDNS)

### Build Steps

1. Open `MusicBeeSendSpin.sln` in Visual Studio
2. Restore NuGet packages
3. Build in Release configuration
4. Copy output to MusicBee Plugins folder

### Debug Configuration

1. Set the output path to your MusicBee Plugins folder
2. Set MusicBee as the debug startup program
3. Build and run in Debug mode

## Troubleshooting

### No clients connecting

- Check Windows Firewall is allowing connections on port 8927
- Verify server is enabled in settings
- Check clients are on the same network

### Audio stuttering

- Increase client buffer size
- Check network stability
- Try a lower bitrate codec setting

### High latency

- Decrease client buffer size
- Use Opus codec (lowest latency)
- Ensure speakers support low-latency mode

### Debug Logs

Enable debug logging in settings and check:
- MusicBee's log output (View → Error Log)
- `%APPDATA%\MusicBee\SendSpinDebug.log`

## Architecture

```
┌─────────────────────────────────────────────────────────────┐
│                        MusicBee                              │
│  ┌─────────────────────────────────────────────────────┐   │
│  │                   Audio Engine                        │   │
│  │  (DSP, EQ, ReplayGain, etc.)                         │   │
│  └───────────────────────┬─────────────────────────────┘   │
│                          │                                   │
│                          ▼                                   │
│  ┌─────────────────────────────────────────────────────┐   │
│  │              SendSpin Plugin                          │   │
│  │  ┌─────────────────┐  ┌─────────────────────────┐   │   │
│  │  │ AudioCapture    │  │    SendSpinServer       │   │   │
│  │  │ Service         │──│                         │   │   │
│  │  │ (BASS + Encoder)│  │  ┌─────────────────┐   │   │   │
│  │  └─────────────────┘  │  │ WebSocket       │   │   │   │
│  │                       │  │ Connections     │   │   │   │
│  │  ┌─────────────────┐  │  └────────┬────────┘   │   │   │
│  │  │ GroupManager    │  │           │             │   │   │
│  │  │ (Speakers/      │  └───────────┼─────────────┘   │   │
│  │  │  Groups)        │              │                 │   │
│  │  └─────────────────┘              │                 │   │
│  └───────────────────────────────────┼─────────────────┘   │
│                                      │                       │
└──────────────────────────────────────┼───────────────────────┘
                                       │
                                       ▼
                    ┌──────────────────────────────────┐
                    │         Network (WebSocket)       │
                    └─────┬─────────┬─────────┬────────┘
                          │         │         │
                          ▼         ▼         ▼
                    ┌─────────┐ ┌─────────┐ ┌─────────┐
                    │ Speaker │ │ Speaker │ │ Speaker │
                    │   1     │ │   2     │ │   3     │
                    └─────────┘ └─────────┘ └─────────┘
```

## License

MIT License - See LICENSE file for details.

## Credits

- MusicBee Plugin API by Steven Mayall
- SendSpin Protocol by the SendSpin Community
- Opus codec by Xiph.org (Concentus implementation)

## Contributing

Contributions welcome! Please see the main repository's contributing guidelines.
