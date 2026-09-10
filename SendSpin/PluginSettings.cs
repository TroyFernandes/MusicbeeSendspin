using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace MusicBeePlugin.SendSpin
{
    /// <summary>
    /// Connection mode for SendSpin
    /// </summary>
    public enum ConnectionMode
    {
        /// <summary>
        /// Speakers connect to MusicBee server (classic mode)
        /// </summary>
        ClientInitiated,
        
        /// <summary>
        /// MusicBee discovers and connects to speakers (recommended)
        /// </summary>
        ServerInitiated
    }
    
    /// <summary>
    /// Plugin settings for SendSpin
    /// </summary>
    public class PluginSettings
    {
        // Connection Mode
        [JsonConverter(typeof(StringEnumConverter))]
        public ConnectionMode ConnectionMode { get; set; } = ConnectionMode.ClientInitiated;
        
        // Selected speakers (for server-initiated mode)
        public List<string> SelectedSpeakerIds { get; set; } = new();
        
        // Server Settings (for client-initiated mode)
        public bool EnableServer { get; set; } = true;
        public string ServerName { get; set; } = "MusicBee SendSpin";
        public int ServerPort { get; set; } = 8927; // Default SendSpin server port
        public bool EnableMdns { get; set; } = true;
        
        // Audio Settings
        public string AudioCodec { get; set; } = "opus"; // opus, flac, or pcm
        public int SampleRate { get; set; } = 48000;
        public int Channels { get; set; } = 2;
        public int BitDepth { get; set; } = 16;
        public int OpusBitrate { get; set; } = 128000; // 128 kbps
        
        // DSP Settings
        public bool EnableDsp { get; set; } = true;
        
        [JsonConverter(typeof(StringEnumConverter))]
        public Plugin.ReplayGainMode ReplayGainMode { get; set; } = Plugin.ReplayGainMode.Smart;
        
        // Buffer Settings
        public int BufferSizeMs { get; set; } = 100; // Buffer size for clients
        
        // Playback Settings
        public bool MuteLocalPlayback { get; set; } = true; // Mute local output when streaming to speakers
        
        // Audio Capture Mode
        public bool UseDirectDecode { get; set; } = true; // Use direct file decode instead of Player_OpenStreamHandle
        
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
                ConnectionMode = ConnectionMode,
                SelectedSpeakerIds = new List<string>(SelectedSpeakerIds),
                EnableServer = EnableServer,
                ServerName = ServerName,
                ServerPort = ServerPort,
                EnableMdns = EnableMdns,
                AudioCodec = AudioCodec,
                SampleRate = SampleRate,
                Channels = Channels,
                BitDepth = BitDepth,
                OpusBitrate = OpusBitrate,
                EnableDsp = EnableDsp,
                ReplayGainMode = ReplayGainMode,
                BufferSizeMs = BufferSizeMs,
                MuteLocalPlayback = MuteLocalPlayback,
                UseDirectDecode = UseDirectDecode,
                LogDebugInfo = LogDebugInfo
            };
        }
    }

    /// <summary>
    /// Track information for metadata
    /// </summary>
    public class TrackInfo
    {
        public string? Title { get; set; }
        public string? Artist { get; set; }
        public string? Album { get; set; }
        public string? AlbumArtist { get; set; }
        public int Duration { get; set; }
        public string? Genre { get; set; }
        public string? Year { get; set; }
        public string? TrackNumber { get; set; }
    }
}
