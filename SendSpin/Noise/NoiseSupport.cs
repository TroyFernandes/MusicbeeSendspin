using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Noise;
using NoiseProtocol = Noise.Protocol;

namespace MusicBeePlugin.SendSpin.Noise
{
    /// <summary>Protocol-wide constants (wire protocol v1, Noise KKpsk2).</summary>
    internal static class NoiseConstants
    {
        public const int ProtocolVersion = 1;
        public const int KeySize = 32;
        public const int PskSize = 32;
        public const int MaxTransportPlaintext = 65535 - 16;
        public const byte MessageTypeJsonBody = 0;
		public const byte MessageTypeFragmentMore = 2;
		public const byte MessageTypeFragmentEnd = 3;
        public const int MaxReassembledMessageBytes = 64 * 1024 * 1024;
        public const int MaxReassembledMessageBytesBeforeFirstMessage = 128 * 1024;

        private static readonly byte[] SentinelPskBytes =
            Sha256(Encoding.ASCII.GetBytes("sendspin-sentinel-psk-v1"));

        public static ReadOnlySpan<byte> SentinelPsk => SentinelPskBytes;
        public static string SentinelPskId { get; } = DerivePskId(SentinelPskBytes);

        /// <summary>Derives the public PSK identifier as base64url(SHA-256("sendspin-psk-id-v1" || psk)).</summary>
        public static string DerivePskId(ReadOnlySpan<byte> psk)
        {
            byte[] prefix = Encoding.ASCII.GetBytes("sendspin-psk-id-v1");
            byte[] input = new byte[prefix.Length + psk.Length];
            prefix.CopyTo(input, 0);
            psk.CopyTo(input.AsSpan(prefix.Length));
            return Base64UrlText.Encode(Sha256(input));
        }

        // net48 has no SHA256.HashData — compute via the classic API.
        private static byte[] Sha256(byte[] data)
        {
            using (var sha = SHA256.Create())
                return sha.ComputeHash(data);
        }
    }

    /// <summary>The Noise cipher suite used for Sendspin encrypted transport.</summary>
    public enum NoiseCipherSuite
    {
        ChaChaPoly,
        AesGcm,
    }

    public static class NoiseCipherSuiteExtensions
    {
        // The libsodium native library ships with the plugin (Native/ + libsodium NuGet),
        // so both suites work on Windows. These probes exist to give an actionable error
        // naming the alternative if a future build ever lacks the native binary.
        private static readonly Lazy<bool> ChaChaPolyProbe = new Lazy<bool>(() => Probe(NoiseCipherSuite.ChaChaPoly));
        private static readonly Lazy<bool> AesGcmProbe = new Lazy<bool>(() => Probe(NoiseCipherSuite.AesGcm));

        public static string ToWireName(this NoiseCipherSuite suite) => suite switch
        {
            NoiseCipherSuite.ChaChaPoly => "25519_ChaChaPoly_SHA256",
            NoiseCipherSuite.AesGcm => "25519_AESGCM_SHA256",
            _ => throw new ArgumentOutOfRangeException(nameof(suite)),
        };

        public static string ToProtocolName(this NoiseCipherSuite suite) =>
            $"Noise_KKpsk2_{suite.ToWireName()}";

        public static bool IsSupported(this NoiseCipherSuite suite) => suite switch
        {
            NoiseCipherSuite.ChaChaPoly => ChaChaPolyProbe.Value,
            NoiseCipherSuite.AesGcm => AesGcmProbe.Value,
            _ => false,
        };

        public static NoiseCipherSuite SelectDefault() =>
            NoiseCipherSuite.ChaChaPoly.IsSupported()
                ? NoiseCipherSuite.ChaChaPoly
                : NoiseCipherSuite.AesGcm;

        public static void EnsureSupported(this NoiseCipherSuite suite)
        {
            if (suite.IsSupported())
                return;

            NoiseCipherSuite other = suite == NoiseCipherSuite.ChaChaPoly
                ? NoiseCipherSuite.AesGcm
                : NoiseCipherSuite.ChaChaPoly;

            throw new PlatformNotSupportedException(
                $"This platform cannot perform {suite.ToWireName()}. "
                + (other.IsSupported()
                    ? $"Set the suite to NoiseCipherSuite.{other} — servers support both."
                    : "Neither Sendspin cipher suite is available on this platform. Both run through "
                      + "libsodium, so this usually means no native libsodium binary shipped. "
                      + "It ships in the plugin's Native/ folder."));
        }

        private static bool Probe(NoiseCipherSuite suite)
        {
            try
            {
                using (var local = KeyPair.Generate())
                using (var remote = KeyPair.Generate())
                {
                    var protocol = NoiseProtocol.Parse(suite.ToProtocolName().AsSpan());
                    using (var state = protocol.Create(
                        initiator: true,
                        prologue: Array.Empty<byte>(),
                        s: (byte[])local.PrivateKey.Clone(),
                        rs: (byte[])remote.PublicKey.Clone(),
                        psks: new[] { new byte[NoiseConstants.PskSize] }))
                    {
                        state.WriteMessage(Array.Empty<byte>(), new byte[NoiseProtocol.MaxMessageLength]);
                        return true;
                    }
                }
            }
            catch (Exception ex) when (ex is DllNotFoundException
                or EntryPointNotFoundException
                or TypeInitializationException
                or BadImageFormatException
                or NotSupportedException
                or PlatformNotSupportedException)
            {
                return false;
            }
        }
    }

    /// <summary>The class of Noise PSK a connection uses.</summary>
    public enum PskCategory
    {
        LongTerm,
        Pairing,
        Sentinel,
    }

    /// <summary>A resolved Noise PSK with its category and the server it pairs with.</summary>
    internal sealed record NoisePsk(ReadOnlyMemory<byte> Key, PskCategory Category, string? ServerId = null);

    /// <summary>Resolves a PSK identifier from the connection's server/init to a usable PSK.</summary>
    internal interface INoisePskResolver
    {
        NoisePsk? Resolve(string pskId);
    }

    /// <summary>Resolves only the sentinel PSK — used by the source role.</summary>
    internal sealed class SentinelPskResolver : INoisePskResolver
    {
        public static SentinelPskResolver Instance { get; } = new SentinelPskResolver();

        public NoisePsk? Resolve(string pskId) =>
            pskId == NoiseConstants.SentinelPskId
                ? new NoisePsk(NoiseConstants.SentinelPsk.ToArray(), PskCategory.Sentinel)
                : null;
    }

    /// <summary>
    /// RFC 4648 base64url with no padding. This is hand-rolled (not the BCL) so every
    /// platform, including net48, agrees byte-for-byte with the spec's canonical examples.
    /// </summary>
    internal static class Base64UrlText
    {
        public static string Encode(ReadOnlySpan<byte> data)
        {
            byte[] arr = data.ToArray();
            return Convert.ToBase64String(arr).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        public static byte[] Decode(string encoded)
        {
            if (encoded == null) throw new ArgumentNullException(nameof(encoded));
            if (encoded.IndexOfAny(new[] { '+', '/' }) >= 0)
                throw new FormatException("Input is not a valid base64url string.");

            string b64 = new string(encoded.Where(c => c != ' ' && c != '\t' && c != '\r' && c != '\n').ToArray())
                .Replace('-', '+').Replace('_', '/');
            int padding = (4 - b64.Length % 4) % 4;
            return Convert.FromBase64String(b64.PadRight(b64.Length + padding, '='));
        }
    }

    /// <summary>The client's X25519 identity. The PeerId is base64url(Pub) — the wire client_id.</summary>
    internal sealed class SendspinIdentity
    {
        internal ReadOnlyMemory<byte> PrivateKey { get; }
        public ReadOnlyMemory<byte> PublicKey { get; }
        public string PeerId { get; }

        private SendspinIdentity(byte[] privateKey, byte[] publicKey)
        {
            if (privateKey.Length != NoiseConstants.KeySize)
                throw new ArgumentException($"private key must be {NoiseConstants.KeySize} bytes", nameof(privateKey));
            if (publicKey.Length != NoiseConstants.KeySize)
                throw new ArgumentException($"public key must be {NoiseConstants.KeySize} bytes", nameof(publicKey));
            PrivateKey = privateKey;
            PublicKey = publicKey;
            PeerId = Base64UrlText.Encode(publicKey);
        }

        public static SendspinIdentity Generate()
        {
            using (var keyPair = KeyPair.Generate())
            {
                return new SendspinIdentity(
                    (byte[])keyPair.PrivateKey.Clone(),
                    (byte[])keyPair.PublicKey.Clone());
            }
        }

        /// <summary>Decodes a base64url peer id back to its 32-byte key.</summary>
        public static byte[] DecodePeerId(string peerId)
        {
            byte[] bytes = Base64UrlText.Decode(peerId);
            if (bytes.Length != NoiseConstants.KeySize)
                throw new FormatException($"peer id must decode to {NoiseConstants.KeySize} bytes");
            return bytes;
        }

        /// <summary>Decodes and validates a base64url PSK string.</summary>
        internal static byte[] DecodePsk(string encoded)
        {
            byte[] psk = Base64UrlText.Decode(encoded);
            if (psk.Length != NoiseConstants.KeySize)
                throw new FormatException($"PSK must decode to {NoiseConstants.KeySize} bytes");
            return psk;
        }
    }
}
