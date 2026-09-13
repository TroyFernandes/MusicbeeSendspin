using System;

namespace Sendspin.SDK.Tests;

/// <summary>
/// base64url for the server-simulating test doubles, on APIs both shipped target frameworks
/// have.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately NOT the SDK's <c>Base64UrlText</c>. The doubles stand in for the server, so
/// encoding the wire with the same code under test would let a bug agree with itself (#108 was
/// three separate encoding disagreements, and a shared implementation would have hidden all of
/// them).
/// </para>
/// </remarks>
internal static class TestBase64Url
{
    internal static string EncodeToString(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    internal static byte[] DecodeFromChars(ReadOnlySpan<char> chars)
    {
        string padded = new string(chars).Replace('-', '+').Replace('_', '/');

        // Restore the padding Convert.FromBase64String requires and base64url omits.
        padded = padded.PadRight((padded.Length + 3) / 4 * 4, '=');

        return Convert.FromBase64String(padded);
    }
}
