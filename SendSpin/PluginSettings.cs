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
        // Audio Settings (captured from MusicBee and encoded for the source stream)
        public string AudioCodec { get; set; } = "pcm"; // pcm (bit-perfect, default), opus, or flac
        // Sample rate / channels / bit depth are NOT configurable: pcm sends the file's native
        // format untouched; opus/flac encode at the canonical 48 kHz stereo 16-bit.
        public int OpusBitrate { get; set; } = 128000; // 128 kbps

        // DSP Settings (MusicBee's own processing applied to the handed decode stream)
        public bool EnableDsp { get; set; } = true;

        [JsonConverter(typeof(StringEnumConverter))]
        public Plugin.ReplayGainMode ReplayGainMode { get; set; } = Plugin.ReplayGainMode.Smart;

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
                AudioCodec = AudioCodec,
                OpusBitrate = OpusBitrate,
                EnableDsp = EnableDsp,
                ReplayGainMode = ReplayGainMode,
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