# PORT-MAPPING — plugin transport port ↔ sendspin-dotnet SDK

Reference map for the manually ported Sendspin transport layer. Read this **before**
touching `SendSpin/Noise/*` when the SDK, the spec, or aiosendspin changes, so updates can
be diffed and applied quickly instead of re-derived.

---

## 1. Why the port exists

The MusicBee plugin targets **.NET Framework 4.8** because MusicBee hosts plugins in-process
on the Framework CLR. The official [sendspin-dotnet SDK](https://github.com/Sendspin/sendspin-dotnet)
targets **net8.0/net10.0** (CoreCLR) — a net8 assembly cannot load inside a Framework process,
so the SDK cannot be referenced directly. The transport layer (Noise handshake + framing) was
therefore ported by hand, faithful to the SDK's semantics, with a small number of documented
adaptations for net48 (§5).

**The port boundary is exactly `SendSpin/Noise/*`.** Everything above it
(`SourceConnection.cs` — hello/activate/pairing orchestration, clock sync, streaming,
`SourceRenderDevice.cs`, `SourceDiscoveryService.cs`) is **not ported**: it was implemented
directly from the spec (`/home/nixos/spec`) and verified against the reference *server*
implementation (aiosendspin). When the SDK changes, only the `Noise/` layer needs diffing;
`SourceConnection.cs` follows the spec + aiosendspin instead.

## 2. Pinned references

| What | Where | Note |
| --- | --- | --- |
| SDK clone (pinned) | `~/sendspin-dotnet` | port taken at commit **`49277a7` (2026-09-11)** |
| SDK upstream | <https://github.com/Sendspin/sendspin-dotnet> | check `git log` there for transport changes |
| Reference **server** (behavioral truth) | `~/sendspin/aiosendspin` | aiosendspin — what Music Assistant actually bundles; the SDK is a peer client lib, not the server |
| Spec | `~/spec` | `connection.md` (Noise/PSK/prologue), `messaging.md` (framing/fragment IDs), `pairing.md` (token), `roles/source/v1.md` |
| SDK version of the crypto lib | Noise.NET **1.0.0** + libsodium 1.0.22 | plugin uses the same versions (csproj pinned — see note in SDK csproj about the libsodium floor) |

## 3. File mapping (ported → SDK)

| Plugin file | SDK counterpart (`src/Sendspin.SDK/…`) | Port notes |
| --- | --- | --- |
| `SendSpin/Noise/NoiseSupport.cs` | split across `Connection/Noise/NoiseConstants.cs`, `NoiseCipherSuite.cs`, `NoisePsk.cs`, `Base64UrlText.cs`, `SendspinIdentity.cs` (+ `INoiseSessionInfo.cs` bits) | consolidated into one file for plugin ergonomics |
| `SendSpin/Noise/WireFraming.cs` | `Connection/Framing/WireFrame.cs`, `InboundFrameResult.cs`, `IWireFraming.cs` | `WireFrame` collapses the SDK's separate text/binary frame types into one struct with a cached string |
| `SendSpin/Noise/NoiseWireFraming.cs` | `Connection/Noise/NoiseWireFraming.cs` | the main port; see §4 |
| `SendSpin/Noise/IsExternalInit.cs` | — | net48 polyfill for `record`/init accessors |
| `SendSpin/Noise/PairingToken.cs` | `Connection/Noise/Pairing/PairingToken.cs` + `Pairing/Base32.cs` | `Decode` uses `Array.Copy` instead of ranges (net48-friendly); encode path identical |
| `SendSpin/Noise/PairingStore.cs` | `Connection/Noise/PairingRecordStore.cs` + `Pairing/X25519.cs` (record concept), `Client/IPairingRecordStore` (interface concept) | **plugin-specific**: JSON-file store, capacity 8 long-term records (spec min 5), oldest-eviction, corrupt-entry skip, `EnsurePairingPsk`/`RotatePairingPsk`. Semantics (psk_id derivation, category, server binding) match |
| `SendSpin/Noise/PairingToken.cs` (`Base32`) | `Connection/Noise/Pairing/Base32.cs` | verbatim |
| **NOT ported** | `Client/SendSpinClient.cs` (4.9k lines), `Connection/SendSpinConnection.cs`, WebSocket layers, `Synchronization/`, `Audio/`, webserver/host services | that logic lives in `SourceConnection.cs` / `RenderDevice.cs`, written from the spec + aiosendspin |
| **NOT in the SDK** | `MatchedPskCategory`, `_pendingCategory` | plugin-specific: the source role's client-side trust gate needs the matched PSK category committed atomically with the key swap |

## 4. `NoiseWireFraming` component map (the file most likely to need updating)

| Component in port (`SendSpin/Noise/NoiseWireFraming.cs`) | SDK location | What to check when it changes |
| --- | --- | --- |
| `Start()` — cleartext `client/init`, captured byte-exact for the prologue | `NoiseWireFraming.Start()` (SDK serializes via `ClientInitJson` source-gen) | field order `client_id, version, suite` must stay exact — the prologue binds these bytes |
| `HandleServerInit` — parse + peer-id validation, capture byte-exact | same name in SDK | version exact-match (`=1`), `server_id` must be 32-byte base64url |
| `RunResponderExchange` — placeholder-PSK probe (read msg1 to learn `psk_id`), resolve, real exchange, msg2 `{}` payload | `RunResponderExchange` | PSK resolution order: stored PSK by `psk_id`; on initial-handshake miss → Sentinel fallback (only initial — a re-handshake miss fails). **Known divergence:** the SDK checks the declared `psk_category` against the resolved record; the port matches by `psk_id` only (§6.3) |
| `BuildPrologue` — `client/init ‖ server/init` exact wire bytes | `BuildPrologue` | must hash raw transmitted bytes, never re-serialized JSON |
| Transport dispatch — type `0`=JSON, `2/3`=fragments | `DispatchMessage` | message-ID table in `messaging.md` §Binary Message ID (JSON=0, fragment=1-as-bits, source audio=12/0x0C) |
| `HandleFragment` — opening fragment headerLen 2 with `orig_type` at `[1]`, continuations headerLen 1; 128 KB pre-first-message / 64 MB after reassembly caps | `HandleFragment` | fragment `orig_type` must never be 1/2/3 |
| `EncryptOutbound`/`EncryptFrame` — split at `MaxTransportPlaintext` (65519), `+16` AEAD tag | same | 65535 Noise message cap − 16 tag |
| `EncodeDeferredReply` — materialize msg2 under OLD keys, then commit swap (+ `MatchedPskCategory`) | `EncodeDeferredReply` (SDK also disposes the retired transport — port now does too) | reply ordering vs key swap is the critical invariant |
| `HandleRehandshakeMessage` — encrypted JSON `noise/handshake` inside transport, prologue = prior handshake hash | `HandleRehandshakeMessage` | only one re-handshake may be in flight |
| `MatchedPskCategory` / `_pendingCategory` | **no SDK counterpart** | set on initial handshake (`resolved.Category`), committed with the swap on re-handshake; read by `SourceConnection` to gate `client/pair-finalize` and to refuse `source@v1` without a long-term PSK |

## 5. Deliberate divergences (expected when diffing)

These differences are **intentional**. When diffing port ↔ SDK after an upstream change, skip
these; anything *new* in the diff is a real update to apply.

1. **JSON:** Newtonsoft instead of System.Text.Json (net48 has no usable STJ source-gen here).
   The SDK also keeps a reflection-free STJ path for PublishAot. Our envelopes in `Start()`
   and `HandlePairingActivate` are hand-built strings to pin field order; other messages are
   built with `JObject` (field order follows insertion order, which we keep spec-ordered).
2. **SHA-256:** `SHA256.Create().ComputeHash` (classic BCL) instead of `SHA256.HashData`.
3. **Key wiping:** local `ZeroClear` helper instead of the SDK's crypto helpers; our port does
   NOT rely on Noise.NET zeroing handed-in keys (each `protocol.Create` gets its own copy —
   mirrors the SDK's "load-bearing clone" discipline; see the comment on `_keys` in
   `tests/noise-interop/TestNoiseServer.cs`).
4. **`MatchedPskCategory`** — plugin-only addition (see §4 last row).
5. **`psk_category` ignored** — the port resolves PSKs by `psk_id` only; the SDK also enforces
   the declared category. Benign with current servers; flagged in §7.
6. **`PairingStore`** is plugin-specific (file-backed JSON under MusicBee persistent storage),
   replacing the SDK's `IPairingRecordStore` abstraction. Capacity/eviction/corrupt handling
   is our policy (spec requires ≥5 long-term records and no-fail pairing).
7. **Namespace/assembly:** `MusicBeePlugin.SendSpin.Noise` vs `Sendspin.SDK.Connection.Noise`;
   all SDK types are `internal`-visible in the port (compiled into the plugin assembly).
8. **`WireFrame`**: single struct with an optional cached string instead of the SDK's two
   frame representations.

## 6. Runbook — when the SDK / spec / aiosendspin changes

Ordered by likelihood-of-change × blast-radius. Commands assume the SDK clone at
`~/sendspin-dotnet` is updated first (`git -C ~/sendspin-dotnet pull`).

1. **Constants & derivations** (`NoiseConstants`) — check: message-type IDs (0/2/3),
   `MaxTransportPlaintext` (65519), reassembly caps (128 KB pre-first / 64 MB after),
   sentinel PSK = `SHA-256("sendspin-sentinel-psk-v1")`, `psk_id` label
   `"sendspin-psk-id-v1"`, `ProtocolVersion = 1`. These magic strings are spec-fixed;
   a change here is a protocol-version event (see 6).
2. **Handshake flow** (`NoiseWireFraming`) — diff `RunResponderExchange`/`BuildPrologue`/
   `HandleFragment`/`EncodeDeferredReply` against upstream. Apply semantic changes into the
   port, re-listing any new intentional divergence in §5.
3. **Pairing token** (`PairingToken.cs`) — derivation `base32(client_pub ‖ pairing_psk)`
   with 2→9 transliteration, prefix `SP:` + version `0`. The **spec reference
   vector** (`pairing.md`: client_key `0x00..0x1f`, psk `0xe0..0xff` →
   `SP:0AAAQEAYEAUDAOCAJBIFQYDIOB4…`) is asserted in `tests/pairing-tests` — if it passes,
   derivation is still canonical.
4. **Protocol version bump** (`client/init version`) — this is the "everything changes"
   scenario: re-port from whatever the SDK does with v2, expect spec-wide message changes,
   and confirm **Music Assistant's bundled aiosendspin** actually speaks it first (the
   ecosystem moves together; until MA updates, nothing changes for us).
5. **aiosendspin (server) changes** — usually role/message level, which lives in
   `SourceConnection.cs` (spec-implemented). Diff aiosendspin's `server/` against the clone;
   the source-role specifics live in `server/roles/source/v1.py` (stream start/end rules,
   `_start_requested` stays set after `client_stream/end`, etc.).

### Re-verification ladder (fast → slow, run in order)

| Test | What it proves | Time |
| --- | --- | --- |
| `dotnet build MusicBeeSendSpin.csproj` | compiles on net48 | seconds |
| `tests/noise-interop` | port ↔ SDK's own test server: handshake + fragmentation both directions | seconds |
| `tests/pairing-tests` | token codec (incl. spec reference vector), store, matched-category incl. re-handshake promotion | seconds |
| `tests/source-connection` | full app loop against the in-process fake MA server (real sockets + Noise): hello shape, pairing_psk flow, clock sync, streaming | ~1 min |
| `tests/live-handshake-probe -- ws://<MA>:8927/sendspin` | **the real thing**: full KKpsk2 handshake against the live Music Assistant | seconds |

If all pass, the port matches the SDK's behavior as far as anything can tell from outside.

## 7. Known spec divergences of the port (accepted, low risk)

1. `psk_category` (msg1 payload) is not enforced — resolution by `psk_id` alone (§5.5).
   Impact: a server declaring a category we hold differently would handshake instead of
   failing. No current server does this.
2. `Noise.Transport` disposal parity was added late (this file's §4 note); re-handshakes
   before that fix leaked the retired transport (managed state, GC-reclaimed — benign).
3. Clock sync in `SourceConnection` is best-of-burst min-RTT (offset only, no drift term) —
   the spec's time-filter is a 2-D Kalman. Fine at LAN RTTs (~1.5 ms observed); revisit if
   the MA server and MusicBee machine ever drift apart on clock rate.

## 8. Quick-reference: wire constants (where they live on both sides)

| Constant | Port | SDK | Spec |
| --- | --- | --- | --- |
| JSON message type | `NoiseConstants.MessageTypeJsonBody = 0` | same | `messaging.md` Binary ID table |
| Fragment More/End | `2 / 3` | same | `messaging.md` Fragmentation |
| Source audio chunk | `0x0C` (`SourceConnection`) | same | `roles/source/v1.md` |
| Noise message cap | 65535; plaintext `MaxTransportPlaintext = 65519` | same | Noise spec + tag |
| PSK/Key size | 32 | same | `connection.md` Identities |
| Sentinel PSK | `SHA-256("sendspin-sentinel-psk-v1")`; id `GFsV9tLa…` | same | `connection.md` Pre-Shared Key |
| Prologue | exact `client/init ‖ server/init` bytes | same | `connection.md` Prologue |
| Re-handshake prologue | prior handshake hash `h` | same | `connection.md` Re-handshake |
| Pairing token | `SP:` + version `0` + base32(pub‖psk), 2↔9 | same | `pairing.md` Pairing Token |
