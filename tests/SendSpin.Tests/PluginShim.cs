using System;

namespace MusicBeePlugin
{
    /// <summary>
    /// Test shim for the partial <see cref="Plugin"/> class: provides ONLY the members the
    /// test-included plugin sources reference (PluginSettings.ReplayGainMode + its logging).
    /// The real partial class lives in MusicBeeInterface.cs + SendSpinPlugin.cs, which drag
    /// WinForms types and the MusicBee API and are not test-compilable.
    /// </summary>
    public partial class Plugin
    {
        public enum ReplayGainMode
        {
            Off = 0,
            Track = 1,
            Album = 2,
            Smart = 3
        }

        internal static void LogInfo(string source, string message) { }
        internal static void LogError(string source, Exception ex) { }
        internal static void LogError(string source, string message) { }
    }
}