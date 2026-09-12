using System;
using System.Collections.Generic;
using System.Text;

namespace MusicBeePlugin.SendSpin.Noise
{
    /// <summary>
    /// RFC 4648 base32 (alphabet <c>A-Z2-7</c>) helpers. Used only by <see cref="PairingToken"/>.
    /// </summary>
    internal static class Base32
    {
        /// <summary>Encodes bytes as base32 with no <c>=</c> padding.</summary>
        public static string Encode(ReadOnlySpan<byte> data)
        {
            if (data.Length == 0)
                return string.Empty;

            var result = new StringBuilder((data.Length * 8 + 4) / 5);
            int buffer = 0;
            int bitsInBuffer = 0;

            foreach (byte b in data)
            {
                buffer = (buffer << 8) | b;
                bitsInBuffer += 8;

                while (bitsInBuffer >= 5)
                {
                    bitsInBuffer -= 5;
                    result.Append(Alphabet[(buffer >> bitsInBuffer) & 0x1F]);
                }
            }

            if (bitsInBuffer > 0)
                result.Append(Alphabet[(buffer << (5 - bitsInBuffer)) & 0x1F]);

            return result.ToString();
        }

        /// <summary>Decodes a base32 string, case-insensitively and without requiring <c>=</c> padding.</summary>
        /// <exception cref="FormatException">The string contains a character outside the base32 alphabet.</exception>
        public static byte[] Decode(string encoded)
        {
            string upper = encoded.ToUpperInvariant().TrimEnd('=');

            var result = new List<byte>((upper.Length * 5) / 8);
            int buffer = 0;
            int bitsInBuffer = 0;

            foreach (char c in upper)
            {
                int value = Alphabet.IndexOf(c);
                if (value < 0)
                    throw new FormatException($"Character '{c}' is not part of the base32 alphabet.");

                buffer = (buffer << 5) | value;
                bitsInBuffer += 5;

                if (bitsInBuffer >= 8)
                {
                    bitsInBuffer -= 8;
                    result.Add((byte)((buffer >> bitsInBuffer) & 0xFF));
                }
            }

            return result.ToArray();
        }

        private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
    }

    /// <summary>
    /// The Sendspin pairing token: one string carrying the client's static public key and its
    /// Pairing PSK, for the operator to paste (or QR-scan) into the server. The plugin renders the
    /// string verbatim, with no URI wrapper.
    /// </summary>
    /// <remarks>
    /// Wire shape (spec pairing.md): token = "SP:" || version || body, where body is base32 of the
    /// 64-byte payload (client_key || pairing_psk) with every '2' transliterated to '9' so the
    /// token stays QR-alphanumeric. The spec's reference vector for client_key 0x00..0x1f and
    /// pairing_psk 0xe0..0xff is exercised in the interop test.
    /// </remarks>
    public static class PairingToken
    {
        /// <summary>Token version this plugin emits.</summary>
        public const int EmittedVersion = 0;

        private const string Prefix = "SP:";
        private const int KeySize = 32;
        private const int PayloadSize = KeySize * 2;

        /// <summary>
        /// Builds the token for a client key and Pairing PSK, both 32 bytes. The result is 107
        /// characters and contains only QR alphanumeric characters.
        /// </summary>
        public static string Encode(ReadOnlySpan<byte> clientKey, ReadOnlySpan<byte> pairingPsk)
        {
            if (clientKey.Length != KeySize)
                throw new ArgumentException($"clientKey must be {KeySize} bytes.", nameof(clientKey));
            if (pairingPsk.Length != KeySize)
                throw new ArgumentException($"pairingPsk must be {KeySize} bytes.", nameof(pairingPsk));

            Span<byte> payload = stackalloc byte[PayloadSize];
            clientKey.CopyTo(payload);
            pairingPsk.CopyTo(payload.Slice(KeySize));

            string body = Base32.Encode(payload).Replace('2', '9');
            return $"{Prefix}{EmittedVersion}{body}";
        }

        /// <summary>
        /// Parses a token, tolerating case, surrounding whitespace and a missing <c>SP:</c> prefix.
        /// Accepts versions 0 and 1, which carry an identical payload. (Used by tests; the plugin
        /// emits tokens, the server decodes them.)
        /// </summary>
        /// <exception cref="FormatException">The token is malformed, carries an unrecognised version, or does not decode to exactly 64 bytes.</exception>
        public static (byte[] ClientKey, byte[] PairingPsk) Decode(string token)
        {
            string trimmed = token.Trim().ToUpperInvariant();

            if (trimmed.StartsWith(Prefix, StringComparison.Ordinal))
                trimmed = trimmed.Substring(Prefix.Length);

            if (trimmed.Length == 0)
                throw new FormatException("Pairing token is empty.");

            char version = trimmed[0];
            if (version != '0' && version != '1')
                throw new FormatException($"Pairing token has unrecognised version '{version}'; expected 0 or 1.");

            string body = trimmed.Substring(1).Replace('9', '2');

            byte[] payload = Base32.Decode(body);
            if (payload.Length != PayloadSize)
                throw new FormatException($"Pairing token payload is {payload.Length} bytes; expected {PayloadSize}.");

            byte[] clientKey = new byte[KeySize];
            Array.Copy(payload, 0, clientKey, 0, KeySize);
            byte[] pairingPsk = new byte[KeySize];
            Array.Copy(payload, KeySize, pairingPsk, 0, KeySize);
            return (clientKey, pairingPsk);
        }
    }
}