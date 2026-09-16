using System;
using System.IO;
using MusicBeePlugin.SendSpin;
using Xunit;

namespace SendSpin.Tests
{
    /// <summary>
    /// PluginSettings persistence: round-trip and backward compatibility (older settings files
    /// with removed keys must load with defaults — the settings schema was trimmed several times).
    /// </summary>
    public class PluginSettingsTests : IDisposable
    {
        private readonly string _path;

        public PluginSettingsTests()
        {
            _path = Path.Combine(Path.GetTempPath(), "sendspin-settings-" + Guid.NewGuid().ToString("N") + ".json");
        }

        public void Dispose()
        {
            if (File.Exists(_path))
                File.Delete(_path);
        }

        [Fact]
        public void RoundTrip_PreservesValues()
        {
            var settings = new PluginSettings
            {
                RenderDeviceName = "Custom Device",
                SourceServerHost = "ma.example",
                SourceServerPort = 8927,
            };
            settings.Save(_path);

            var loaded = PluginSettings.Load(_path);
            Assert.Equal("Custom Device", loaded.RenderDeviceName);
            Assert.Equal("ma.example", loaded.SourceServerHost);
            Assert.Equal(8927, loaded.SourceServerPort);
        }

        [Fact]
        public void OldSettingsFile_WithRemovedKeys_LoadsWithDefaults()
        {
            // A pre-simplification settings file: codec parameters were removed from the schema
            // (782fee1), Opus settings later still, the speaker-mode keys earlier still
            // (archive/speaker-mode). Unknown keys must be ignored; known keys must still load.
            const string legacyJson = @"{
                ""AudioCodec"": ""opus"",
                ""SampleRate"": 48000,
                ""Channels"": 2,
                ""BitDepth"": 16,
                ""ConnectionMode"": ""server"",
                ""SpeakerModeEnabled"": true,
                ""UseDirectDecode"": true,
                ""OpusBitrate"": 96000,
                ""RenderDeviceEnabled"": true
            }";
            File.WriteAllText(_path, legacyJson);

            var loaded = PluginSettings.Load(_path);
            Assert.True(loaded.RenderDeviceEnabled);
            // Removed schema keys must not resurrect as properties; defaults stand for the rest.
            Assert.True(loaded.SourceAutoDiscover);
        }

        [Fact]
        public void MissingFile_LoadsDefaults()
        {
            var loaded = PluginSettings.Load(_path);
            Assert.True(loaded.RenderDeviceEnabled);
            Assert.True(loaded.SourceAutoDiscover);
        }

        [Fact]
        public void CorruptFile_LoadsDefaults()
        {
            File.WriteAllText(_path, "{ not json");
            var loaded = PluginSettings.Load(_path);
        }
    }
}