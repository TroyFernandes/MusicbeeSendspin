using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace MusicBeePlugin.SendSpin
{
    /// <summary>
    /// Plugin settings for SendSpin. The plugin streams MusicBee's playback to Music Assistant
    /// over the Sendspin <c>source@v1</c> protocol (MusicBee is a Sendspin client / render
    /// device). The legacy speaker-mode settings were archived with the speaker mode itself
    /// (branch archive/speaker-mode); unknown keys in older settings files are ignored.
    /// </summary>
    public class PluginSettings
    {
        // Audio Settings (captured from MusicBee and streamed as-is)
        // PCM only, bit-perfect: the file's native format untouched. Compression/transcode to
        // other players is Music Assistant's job, so the plugin has no codec settings.

        // DSP Settings (MusicBee's own processing applied to the handed decode stream)
        // Note: MusicBee's DSP/ReplayGain are NOT applied to the streamed audio — the decode
        // stream is handed over raw so Music Assistant handles any processing downstream.

        // Source role (render device / Music Assistant) — MusicBee acts as a Sendspin client to MA
        public bool RenderDeviceEnabled { get; set; } = true; // Expose the Music Assistant render device
        public string RenderDeviceName { get; set; } = "Music Assistant (Sendspin)"; // Name shown in MusicBee
        public bool SourceAutoDiscover { get; set; } = true; // Find the MA Sendspin server via mDNS
        public string SourceServerHost { get; set; } = ""; // Manual host (used when discovery is off / fails)
        public int SourceServerPort { get; set; } = 0; // Manual port (0 = use the port reported by discovery)

        // Advanced Settings
        public bool LogDebugInfo { get; set; } = false;

        /// <summary>
        /// Load settings from file
        /// </summary>
        public static PluginSettings Load(string filePath)
        {
            try
            {
                if (File.Exists(filePath))
                {
                    var json = File.ReadAllText(filePath);
                    var settings = JsonConvert.DeserializeObject<PluginSettings>(json, new JsonSerializerSettings
                    {
                        Converters = { new StringEnumConverter() }
                    });

                    return settings ?? new PluginSettings();
                }
            }
            catch (Exception ex)
            {
                Plugin.LogError("PluginSettings.Load", ex);
            }

            return new PluginSettings();
        }

        /// <summary>
        /// Save settings to file
        /// </summary>
        public void Save(string filePath)
        {
            try
            {
                var json = JsonConvert.SerializeObject(this, Formatting.Indented, new JsonSerializerSettings
                {
                    Converters = { new StringEnumConverter() }
                });

                File.WriteAllText(filePath, json);
            }
            catch (Exception ex)
            {
                Plugin.LogError("PluginSettings.Save", ex);
            }
        }

        /// <summary>
        /// Create a copy of settings
        /// </summary>
        public PluginSettings Clone()
        {
            return new PluginSettings
            {
                RenderDeviceEnabled = RenderDeviceEnabled,
                RenderDeviceName = RenderDeviceName,
                SourceAutoDiscover = SourceAutoDiscover,
                SourceServerHost = SourceServerHost,
                SourceServerPort = SourceServerPort,
                LogDebugInfo = LogDebugInfo
            };
        }
    }
}