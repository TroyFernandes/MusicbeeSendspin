using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace MusicBeePlugin.SendSpin
{
    /// <summary>
    /// Preloads native libraries from the plugin's Native/ folder BEFORE any P/Invoke that uses a
    /// bare library name. Noise.NET's <c>Noise.Libsodium</c> P/Invokes bare <c>libsodium</c>, which
    /// Windows resolves only from MusicBee's executable directory, the system directories, the
    /// current directory, and PATH — never from the plugin's <c>Plugins\Native\</c> folder. Loading
    /// the module explicitly by full path first works because Windows matches already-loaded
    /// modules by base name when the later bare DllImport resolves.
    /// </summary>
    internal static class NativeLibs
    {
        private static bool _attempted;

        /// <summary>
        /// Preloads libsodium for the running process bitness. Idempotent; safe to call on any
        /// platform (non-Windows skips — the OS-wide libsodium is used there). Failures are logged
        /// and the caller continues: Noise surfaces its own error, and the plugin degrades
        /// gracefully (render device off, speaker mode unaffected).
        /// </summary>
        public static void EnsureLoaded(Action<string>? log = null)
        {
            if (_attempted)
                return;
            _attempted = true;

            var platform = Environment.OSVersion.Platform;
            if (platform != PlatformID.Win32NT && platform != PlatformID.Win32Windows)
                return; // not Windows — the OS-wide libsodium applies (Linux test/dev runs)

            string? assemblyDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            if (string.IsNullOrEmpty(assemblyDir))
            {
                log?.Invoke("[NativeLibs] cannot locate the plugin assembly directory — skipping preload");
                return;
            }

            string arch = Environment.Is64BitProcess ? "x64" : "x86";
            log?.Invoke($"[NativeLibs] process is {arch}, plugin dir: {assemblyDir}");

            // Preferred: bitness-matched subfolder; legacy: single-dll layout (older deployments).
            string[] candidates =
            {
                Path.Combine(assemblyDir, "Native", arch, "libsodium.dll"),
                Path.Combine(assemblyDir, "Native", "libsodium.dll"),
                Path.Combine(assemblyDir, "libsodium.dll"),
            };

            foreach (string candidate in candidates)
            {
                if (!File.Exists(candidate))
                {
                    log?.Invoke($"[NativeLibs] not present: {candidate}");
                    continue;
                }

                IntPtr handle = LoadLibrary(candidate);
                if (handle != IntPtr.Zero)
                {
                    log?.Invoke($"[NativeLibs] preloaded libsodium: {candidate}");
                    return;
                }

                int error = Marshal.GetLastWin32Error();
                // 193 = ERROR_BAD_EXE_FORMAT — the classic bitness mismatch signature.
                log?.Invoke($"[NativeLibs] LoadLibrary FAILED ({error}{(error == 193 ? ", bad architecture for this process" : "")}): {candidate}");
            }
            // Normal on fresh installs: libsodium is embedded in mb_SendSpin.dll and Costura
            // extracts + LoadLibrary's it at module init. This preload is just a fallback for
            // deployments that still carry a Native\\ folder.
            log?.Invoke("[NativeLibs] no libsodium.dll beside the plugin — relying on the Costura-embedded copy");
        }

        [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibrary(string fileName);
    }
}