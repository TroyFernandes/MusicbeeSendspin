# SendSpin v2 — Implementation TODO

Goal: make MusicBee a sendspin-**v1.8.2-compatible** server so it works with
echolocal (Echo Dot @ 192.168.1.123) and any sendspin-go player.

## Protocol ground truth (verified against sendspin-go v1.8.2 source — the exact version on the speaker)

Reference: `/home/nixos/go/pkg/mod/github.com/!sendspin/sendspin-go@v1.8.2/`
Client side (what the SPEAKER runs): `pkg/protocol/client.go`, `pkg/sendspin/{receiver,scheduler,server_stream}.go`, `pkg/sync/clock.go`

**Direction.** The player LISTENS (`_sendspin._tcp.local`, TXT `path=/sendspin,name=...`,
echolocal default port 8928); the **server dials in**. Plaintext WebSocket, no Noise.

**Handshake.**
1. Server dials `ws://<ip>:<port><path>`.
2. Speaker sends `client/hello` (JSON text). Keys: `client_id, name, version,
   supported_roles[], device_info, player@v1_support{supported_formats[], buffer_capacity(bytes),
   supported_commands[]}`.
3. Server replies `server/hello`: `server_id, name, version:1, active_roles[],
   connection_reason:"playback"`. (No `group` field; no `protocol_version` — it's `version`.)
4. Speaker starts `client/time` sync bursts (periodic, ~8 rounds of 500ms each).
5. Server sends `stream/start` + binary chunks when music plays.

**Time domain (CRITICAL).** Two different clocks:
- Client timestamps (t1, t4 in `client/time`) = Unix epoch µs.
- Server timestamps (t2, t3 in `server/time`, and ALL audio chunk timestamps) =
  **server uptime µs** (monotonic, since server start — see `getClockMicros = time.Since(startTime)`).
The speaker runs an NTP-style Kalman filter (`ProcessSyncResponse(t1,t2,t3,t4)`) to map
server-uptime µs → local time, then `PlayAt = ServerToLocalTime(chunkTs) + staticDelay`.
If sync hasn't converged, `ServerToLocalTime(ts)` = `time.Unix(0, ts*1000)` → **1970** →
every chunk is >50 ms late → **every chunk is dropped**.
→ `server/time` payload MUST be:
  `{client_transmitted: <echo t1>, server_received: <uptime µs at recv>, server_transmitted: <uptime µs at send>}`

**Scheduler on speaker** (`scheduler.go`):
- Buffering mode until `bufferTarget = BufferMs/20` chunks queued (echolocal default BufferMs=500 → 25 chunks).
- 100 Hz tick; plays chunk when `PlayAt - now <= 50ms`; **drops chunk if >50 ms late**;
  waits if >50 ms early.
- `stream/clear` → Clear() → back into buffering mode.
→ Server must send each chunk ≥ ~500 ms before its PlayAt (reference: `ts = now + 500ms`
at decode time, i.e. real-time pacing). Burst-sending an entire track = all chunks late = silence.

**Binary audio frame** (BOTH reference server and v1.8.2 client agree — the C# code here is ALREADY correct):
`[0x04][8-byte big-endian timestamp µs, server-uptime domain][raw audio bytes]` (9-byte header).
Constants: `AudioChunkMessageType = 0x04` (protocol/client.go BinaryMessageHeaderSize = 9).

**Negotiated format.** echolocal hardware = 48 kHz/2ch/16bit → offers flac 48/2/16, opus 48/2/16,
pcm 48/2/16. Pick from intersection of `supported_formats`; Opus 48k stereo is what
DirectDecodeService already produces. `stream/start` payload: `player:{codec, sample_rate, channels, bit_depth}`.

## Current bugs (file → problem)

### Discovery (why speaker is "not in MusicBee")
- [ ] D1. **No manual target**: mDNS browse via Makaretu may find nothing on some Windows
      NICs/subnets, and there is no fallback. Add settings for manual `ip:port/path` targets
      (user knows the speaker is 192.168.1.123). Default port 8928.
- [ ] D2. `SpeakerDiscoveryService.ProcessAddressRecord` assigns **any** A record to the first
      speaker lacking an address — must match the SRV target name (`name == srv.Target`).
      Also store `SrvTarget` on the speaker when processing SRV.
- [ ] D3. `NotifySpeakerUpdate` "isNew" check is always true (LastSeen just updated) → duplicate
      SpeakerDiscovered events. Track a `Notified` flag.
- [ ] D4. Verify user is looking at Settings → Speakers tab (mDNS list), NOT the Speaker
      Manager dialog (which lists clients of the client-initiated server).

### Protocol (why no audio even when connected)
- [x] P1 (critical). **Timestamp domain** — DONE: `ServerClock.NowUs()` (uptime µs,
      `SendSpin/ServerClock.cs`); `BroadcastStreamStartToSpeakers` sets
      `anchor = ServerClock.NowUs() + 600_000` at stream start; `EnqueueAudioChunk(relTs, data)`
      stores `anchor + relTs`; mid-join anchor = `now + 300_000 - _lastChunkRelTs`.
- [x] P2 (critical). **`server/time` reply** — DONE: `client_transmitted` (echo),
      `server_received`/`server_transmitted` in uptime µs (v1.8.2 keys).
- [x] P3 (critical). **Pacing** — DONE: per-connection `ConcurrentQueue` + 20 ms
      `Timer` pump releases chunks with `ts <= now + 500ms`; 6000-chunk cap drops oldest.
- [x] P4 (major). **Hello field names** — DONE: server/hello `server_id,name,version,active_roles,connection_reason;
      client/hello parsed incl. `player@v1_support.{buffer_capacity,supported_formats}`.
- [x] P5 (major). **Concurrent WS writes** — DONE: single writer loop over
      `BlockingCollection<SendItem>` (Channel<> unavailable on .NET Framework).
- [x] P6 (moderate). **`client/state`** parse — DONE: `payload.player.{state,volume,muted}`.
- [ ] P7 (moderate). **Playback transitions** — DONE: pause/stop → `stream/end`,
      resume → `stream/start`+anchor, track change → `stream/clear` (HandleTrackChanging)
      + re-`stream/start` (HandleTrackChanged); `_lastChunkRelTs` reset on stop/track.
      OPEN: `group/update` on state changes + `server/state` metadata on track change
      (SpeakerConnection has SendGroupUpdate/SendMetadata; wire them from the plugin).
- [ ] P8 (minor). `client/goodbye` reason "restart" → redial. Optional: 30 s pings.

## Build order

1. **ServerClock + P2 + P4** in SpeakerConnection (small, unblocks handshake + sync).
2. **P5 writer** serialization in SpeakerConnection.
3. **P1 + P3** streamer: per-speaker pump with anchor; wire `stream/start` with
   negotiated format (P4's supported_formats).
4. **P7** transitions in SendSpinPlugin (pause/seek/track/stop).
5. **D1–D3** discovery: manual targets in settings + A-record fix.
6. **P6/P8** polish.
7. Verify against echolocal @ 192.168.1.123: state off → waiting → joined → playing; audio out.

## Known echolocal behaviors
- Only ONE server at a time; first to dial holds the room (409 otherwise).
- State sensor: off → waiting → joined (server connected) → playing.
- `buffer_capacity` = 30 s of audio in bytes (~8.6 MB for 48k stereo 16-bit PCM).
- Resyncs clock every 10 s in bursts of 8 rounds (500 ms timeout each).
- Hardware: 48 kHz / 2 ch / 16 bit → negotiates Opus 48k (preferred) or PCM 48k.
