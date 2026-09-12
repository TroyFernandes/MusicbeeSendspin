using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;

namespace MusicBeePlugin.SendSpin.Noise
{
    /// <summary>A persisted pairing credential: a PSK, its category, and the server it is bound to.</summary>
    /// <remarks>
    /// Long-term records ("lt", from completed pairings) are bound to a server_id; the plugin's own
    /// Pairing PSK ("pr", the bootstrap secret distributed via the pairing token) is unbound and is
    /// NOT consumed by a successful pairing — it can pair this client with any number of servers.
    /// </remarks>
    internal sealed class PairingRecord
    {
        public ReadOnlyMemory<byte> Psk { get; }
        public PskCategory Category { get; }
        public string? ServerId { get; }

        public PairingRecord(ReadOnlyMemory<byte> psk, PskCategory category, string? serverId = null)
        {
            Psk = psk;
            Category = category;
            ServerId = serverId;
        }

        /// <summary>The record's psk_id (base64url of SHA-256("sendspin-psk-id-v1" || psk)).</summary>
        public string PskId => NoiseConstants.DerivePskId(Psk.Span);

        internal static string CategoryToWire(PskCategory category) => category switch
        {
            PskCategory.LongTerm => "lt",
            PskCategory.Pairing => "pr",
            PskCategory.Sentinel => "sn",
            _ => throw new ArgumentOutOfRangeException(nameof(category)),
        };

        internal static PskCategory CategoryFromWire(string wire) => wire switch
        {
            "lt" => PskCategory.LongTerm,
            "pr" => PskCategory.Pairing,
            "sn" => PskCategory.Sentinel,
            _ => throw new FormatException("unknown psk_category: " + wire),
        };
    }

    /// <summary>
    /// JSON-file-backed pairing-record store, and the PSK resolver a <see cref="NoiseWireFraming"/>
    /// uses on this client. Thread-safe (the connection's receive thread resolves PSKs while the UI
    /// thread may rotate the pairing PSK).
    /// </summary>
    /// <remarks>
    /// Spec obligations implemented here:
    ///  - a client MUST store at least 5 long-term records; at capacity a pairing MUST NOT fail —
    ///    evict the oldest long-term record instead (never evict the pairing PSK).
    ///  - long-term records are bound to a server_id; <see cref="NoiseWireFraming"/> rejects a
    ///    psk_id whose record names a different server (misbinding, not a lookup miss).
    /// The Sentinel PSK is deliberately NOT stored here: NoiseWireFraming falls back to it on an
    /// initial-handshake lookup miss, and only there.
    /// </remarks>
    internal sealed class PairingStore : INoisePskResolver
    {
        /// <summary>Long-term-record capacity, comfortably above the spec's minimum of 5.</summary>
        public const int MaxLongTermRecords = 8;

        private readonly string _filePath;
        private readonly Action<string>? _logger;
        private readonly object _lock = new();
        private readonly List<PairingRecord> _records = new();

        public PairingStore(string filePath, Action<string>? logger = null)
        {
            _filePath = filePath;
            _logger = logger;
            Load();
        }

        /// <summary>All stored records (pairing PSK first is not guaranteed; order is insertion order).</summary>
        public IReadOnlyList<PairingRecord> List()
        {
            lock (_lock)
                return new List<PairingRecord>(_records);
        }

        /// <summary>
        /// Adds or replaces the record with the same psk_id. Evicts the oldest long-term record
        /// when a new long-term record would exceed <see cref="MaxLongTermRecords"/>.
        /// </summary>
        public bool Upsert(PairingRecord record)
        {
            lock (_lock)
            {
                for (int i = 0; i < _records.Count; i++)
                {
                    if (_records[i].PskId == record.PskId)
                    {
                        _records[i] = record;
                        Save();
                        return true;
                    }
                }

                if (record.Category == PskCategory.LongTerm)
                {
                    int longTermCount = 0;
                    foreach (PairingRecord r in _records)
                        if (r.Category == PskCategory.LongTerm)
                            longTermCount++;

                    if (longTermCount >= MaxLongTermRecords)
                    {
                        PairingRecord? evicted = null;
                        foreach (PairingRecord r in _records)
                            if (r.Category == PskCategory.LongTerm)
                            {
                                evicted = r;
                                break;
                            }
                        if (evicted is not null)
                        {
                            _records.Remove(evicted);
                            _logger?.Invoke("PairingStore: evicted oldest long-term record for server " + evicted.ServerId);
                        }
                    }
                }

                _records.Add(record);
                Save();
                return true;
            }
        }

        /// <summary>Removes the record with the given psk_id (no-op if absent).</summary>
        public void Remove(string pskId)
        {
            lock (_lock)
            {
                for (int i = _records.Count - 1; i >= 0; i--)
                {
                    if (_records[i].PskId == pskId)
                    {
                        _records.RemoveAt(i);
                        Save();
                    }
                }
            }
        }

        /// <summary>The long-term record bound to a server, if stored.</summary>
        public PairingRecord? LongTermFor(string serverId)
        {
            lock (_lock)
            {
                foreach (PairingRecord r in _records)
                    if (r.Category == PskCategory.LongTerm && r.ServerId == serverId)
                        return r;
                return null;
            }
        }

        /// <summary>INoisePskResolver: psk_id → PSK with its category and server binding.</summary>
        public NoisePsk? Resolve(string pskId)
        {
            lock (_lock)
            {
                foreach (PairingRecord r in _records)
                {
                    if (r.PskId != pskId)
                        continue;
                    // Sentinel category is never stored; lt/pr both map onto the wire categories
                    // the handshake's psk_category declares.
                    return new NoisePsk(r.Psk.ToArray(), r.Category, r.ServerId);
                }
                return null;
            }
        }

        /// <summary>
        /// The plugin's persistent Pairing PSK: returns it if stored, generating and persisting a
        /// fresh one otherwise. A pairing consumes a *long-term* PSK, never this one.
        /// </summary>
        public byte[] EnsurePairingPsk()
        {
            lock (_lock)
            {
                foreach (PairingRecord r in _records)
                    if (r.Category == PskCategory.Pairing)
                        return r.Psk.ToArray();

                byte[] psk = new byte[NoiseConstants.PskSize];
                using (var rng = RandomNumberGenerator.Create())
                    rng.GetBytes(psk);
                _records.Add(new PairingRecord(psk, PskCategory.Pairing, null));
                Save();
                return psk;
            }
        }

        /// <summary>Rotates the pairing PSK (e.g. the token leaked): discards all pairing records and returns the fresh PSK. Call <see cref="GetPairingToken"/> for the new token.</summary>
        public byte[] RotatePairingPsk()
        {
            lock (_lock)
            {
                for (int i = _records.Count - 1; i >= 0; i--)
                    if (_records[i].Category == PskCategory.Pairing)
                        _records.RemoveAt(i);

                byte[] psk = new byte[NoiseConstants.PskSize];
                using (var rng = RandomNumberGenerator.Create())
                    rng.GetBytes(psk);
                _records.Add(new PairingRecord(psk, PskCategory.Pairing, null));
                Save();
                return psk;
            }
        }

        /// <summary>The pairing token for the stored pairing PSK ("SP:0" + base32(pub || psk)); the operator pastes this into the server.</summary>
        public string GetPairingToken(SendspinIdentity identity)
        {
            byte[] psk = EnsurePairingPsk();
            try
            {
                return PairingToken.Encode(identity.PublicKey.Span, psk);
            }
            finally
            {
                Array.Clear(psk, 0, psk.Length);
            }
        }

        // --- persistence ---

        private void Load()
        {
            lock (_lock)
            {
                _records.Clear();
                try
                {
                    if (!File.Exists(_filePath))
                        return;
                    JObject root = JObject.Parse(File.ReadAllText(_filePath));
                    if (root["records"] is not JArray records)
                        return;
                    foreach (JToken item in records)
                    {
                        if (item is not JObject o)
                            continue;
                        string? pskId = o["psk_id"]?.Value<string>();
                        string? pskB64 = o["psk"]?.Value<string>();
                        string? category = o["category"]?.Value<string>();
                        string? serverId = o["server_id"]?.Value<string>();
                        if (pskId is null || pskB64 is null || category is null)
                            continue;

                        byte[] psk;
                        try { psk = Base64UrlText.Decode(pskB64); }
                        catch (FormatException) { continue; } // corrupt entry: skip it
                        if (psk.Length != NoiseConstants.PskSize || pskId != NoiseConstants.DerivePskId(psk))
                            continue; // tampered or corrupt: skip

                        PskCategory cat;
                        try { cat = PairingRecord.CategoryFromWire(category); }
                        catch (FormatException) { continue; }

                        _records.Add(new PairingRecord(psk, cat, serverId));
                    }
                }
                catch (Exception ex)
                {
                    // A corrupt store starts empty: the next pairing re-establishes credentials.
                    _logger?.Invoke("PairingStore: failed to load " + _filePath + ": " + ex.Message);
                    _records.Clear();
                }
            }
        }

        private void Save()
        {
            // Caller holds _lock. Atomic-ish: temp file + replace, so a crash mid-write
            // cannot leave a truncated store behind.
            try
            {
                var root = new JObject();
                var arr = new JArray();
                foreach (PairingRecord r in _records)
                {
                    arr.Add(new JObject
                    {
                        ["psk_id"] = r.PskId,
                        ["psk"] = Base64UrlText.Encode(r.Psk.Span),
                        ["category"] = PairingRecord.CategoryToWire(r.Category),
                        ["server_id"] = r.ServerId,
                    });
                }
                root["records"] = arr;

                string dir = Path.GetDirectoryName(Path.GetFullPath(_filePath)) ?? ".";
                if (!Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                string tmp = _filePath + ".tmp";
                File.WriteAllText(tmp, root.ToString(Formatting.None));
                if (File.Exists(_filePath))
                    File.Replace(tmp, _filePath, null);
                else
                    File.Move(tmp, _filePath);
            }
            catch (Exception ex)
            {
                // Persisted credentials are recoverable (re-pair); a save failure must not
                // corrupt the in-memory state that the current session runs on.
                _logger?.Invoke("PairingStore: failed to save " + _filePath + ": " + ex.Message);
            }
        }
    }
}