using System;
using MusicBeePlugin.SendSpin.Noise;

// net48 runtime smoke for TODO #8: proves Noise.NET + the native libsodium binary actually
// LOAD AND RUN on net48/Windows — a build alone cannot show that. Reuses the plugin's own
// NoiseCipherSuite.IsSupported(), which performs a real in-process KKpsk2 handshake
// (KeyPair.Generate x2 + protocol.Create + WriteMessage) and catches the native-load
// failure modes (DllNotFound / BadImageFormat / missing symbol).
// Run on Windows:  noise-net48-smoke.exe   (net48 is always present — no .NET 8 SDK needed)
internal static class Program
{
    private static int Main()
    {
        int failures = 0;
        foreach (var suite in new[] { NoiseCipherSuite.ChaChaPoly, NoiseCipherSuite.AesGcm })
        {
            bool ok = suite.IsSupported();
            Console.WriteLine((ok ? "PASS  " : "FAIL  ") + suite.ToProtocolName());
            if (!ok)
                failures++;
        }

        Console.WriteLine();
        if (failures == 0)
            Console.WriteLine("OK: libsodium + Noise.NET handshake run on net48/Windows.");
        else
            Console.WriteLine(failures + " suite(s) unavailable: check the libsodium native binary shipped in "
                + "Native/ (correct architecture, and next to the .exe).");
        return failures == 0 ? 0 : 1;
    }
}
