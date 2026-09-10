using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace MusicBeePlugin.SendSpin
{
    /// <summary>
    /// Settings dialog for SendSpin plugin
    /// </summary>
    public class SettingsDialog : Form
    {
        private PluginSettings _settings;
        private readonly SpeakerDiscoveryService? _discoveryService;
        
        // Controls - initialized in InitializeComponents
        private TabControl _tabControl = null!;
        private TabPage _speakersTab = null!;
        private TabPage _serverTab = null!;
        private TabPage _audioTab = null!;
        private TabPage _advancedTab = null!;
        
        // Speaker selection
        private CheckedListBox _speakerListBox = null!;
        private Button _refreshSpeakersButton = null!;
        private Label _speakerStatusLabel = null!;
        private RadioButton _modeClientInitiatedRadio = null!;
        private RadioButton _modeServerInitiatedRadio = null!;
        
        // Server settings
        private CheckBox _enableServerCheckbox = null!;
        private TextBox _serverNameTextbox = null!;
        private NumericUpDown _serverPortNumeric = null!;
        private CheckBox _enableMdnsCheckbox = null!;
        
        // Audio settings
        private ComboBox _codecCombobox = null!;
        private ComboBox _sampleRateCombobox = null!;
        private ComboBox _channelsCombobox = null!;
        private ComboBox _bitDepthCombobox = null!;
        private NumericUpDown _opusBitrateNumeric = null!;
        private Label _opusBitrateLabel = null!;
        
        // DSP settings
        private CheckBox _enableDspCheckbox = null!;
        private ComboBox _replayGainCombobox = null!;
        
        // Playback settings
        private CheckBox _muteLocalPlaybackCheckbox = null!;
        
        // Advanced settings
        private NumericUpDown _bufferSizeNumeric = null!;
        private CheckBox _logDebugCheckbox = null!;
        
        // Buttons
        private Button _okButton = null!;
        private Button _cancelButton = null!;

        public PluginSettings Settings => _settings;
        
        /// <summary>
        /// List of speakers that were selected in the dialog.
        /// </summary>
        public List<string> SelectedSpeakerIds { get; private set; } = new();

        public SettingsDialog(PluginSettings settings, SpeakerDiscoveryService? discoveryService = null)
        {
            _settings = settings.Clone();
            _discoveryService = discoveryService;
            InitializeComponents();
            LoadSettings();
            
            // Subscribe to discovery events
            if (_discoveryService != null)
            {
                _discoveryService.SpeakersChanged += OnSpeakersChanged;
            }
        }
        
        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            if (_discoveryService != null)
            {
                _discoveryService.SpeakersChanged -= OnSpeakersChanged;
            }
            base.OnFormClosed(e);
        }

        private void InitializeComponents()
        {
            Text = "SendSpin Settings";
            Size = new Size(450, 450);
            MinimumSize = new Size(400, 400);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            
            // Tab control
            _tabControl = new TabControl
            {
                Dock = DockStyle.Top,
                Height = 350
            };
            
            // Create tabs
            CreateSpeakersTab();
            CreateServerTab();
            CreateAudioTab();
            CreateAdvancedTab();
            
            _tabControl.TabPages.Add(_speakersTab);
            _tabControl.TabPages.Add(_serverTab);
            _tabControl.TabPages.Add(_audioTab);
            _tabControl.TabPages.Add(_advancedTab);
            
            // Buttons
            var buttonPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                FlowDirection = FlowDirection.RightToLeft,
                Height = 45,
                Padding = new Padding(10)
            };
            
            _cancelButton = new Button
            {
                Text = "Cancel",
                DialogResult = DialogResult.Cancel,
                Width = 80
            };
            
            _okButton = new Button
            {
                Text = "OK",
                DialogResult = DialogResult.OK,
                Width = 80
            };
            _okButton.Click += OkButton_Click;
            
            buttonPanel.Controls.Add(_cancelButton);
            buttonPanel.Controls.Add(_okButton);
            
            Controls.Add(_tabControl);
            Controls.Add(buttonPanel);
            
            AcceptButton = _okButton;
            CancelButton = _cancelButton;
        }

        private void CreateSpeakersTab()
        {
            _speakersTab = new TabPage("Speakers");
            
            var mainPanel = new Panel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(10)
            };
            
            // Connection mode group
            var modeGroup = new GroupBox
            {
                Text = "Connection Mode",
                Dock = DockStyle.Top,
                Height = 90,
                Padding = new Padding(10)
            };
            
            _modeClientInitiatedRadio = new RadioButton
            {
                Text = "Client-Initiated (Speakers connect to MusicBee)",
                AutoSize = true,
                Location = new Point(15, 25)
            };
            _modeClientInitiatedRadio.CheckedChanged += ConnectionModeChanged;
            
            _modeServerInitiatedRadio = new RadioButton
            {
                Text = "Server-Initiated (MusicBee connects to speakers)",
                AutoSize = true,
                Location = new Point(15, 50)
            };
            _modeServerInitiatedRadio.CheckedChanged += ConnectionModeChanged;
            
            modeGroup.Controls.Add(_modeClientInitiatedRadio);
            modeGroup.Controls.Add(_modeServerInitiatedRadio);
            
            // Speaker list group
            var speakerGroup = new GroupBox
            {
                Text = "Available Speakers",
                Dock = DockStyle.Fill,
                Padding = new Padding(10)
            };
            
            _speakerListBox = new CheckedListBox
            {
                Dock = DockStyle.Fill,
                CheckOnClick = true,
                IntegralHeight = false
            };
            
            var buttonPanel = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 35
            };
            
            _refreshSpeakersButton = new Button
            {
                Text = "Refresh",
                Width = 80,
                Location = new Point(0, 5)
            };
            _refreshSpeakersButton.Click += RefreshSpeakersButton_Click;
            
            _speakerStatusLabel = new Label
            {
                Text = "Searching for speakers...",
                AutoSize = true,
                Location = new Point(90, 10),
                ForeColor = SystemColors.GrayText
            };
            
            buttonPanel.Controls.Add(_refreshSpeakersButton);
            buttonPanel.Controls.Add(_speakerStatusLabel);
            
            speakerGroup.Controls.Add(_speakerListBox);
            speakerGroup.Controls.Add(buttonPanel);
            
            // Info label
            var infoLabel = new Label
            {
                Text = "In Server-Initiated mode, MusicBee discovers speakers on your network and connects to them directly.\n" +
                       "In Client-Initiated mode, speakers must be configured to connect to MusicBee's IP address.",
                Dock = DockStyle.Bottom,
                Height = 50,
                ForeColor = SystemColors.GrayText
            };
            
            mainPanel.Controls.Add(speakerGroup);
            mainPanel.Controls.Add(modeGroup);
            mainPanel.Controls.Add(infoLabel);
            
            _speakersTab.Controls.Add(mainPanel);
        }
        
        private void ConnectionModeChanged(object? sender, EventArgs e)
        {
            var serverInitiated = _modeServerInitiatedRadio.Checked;
            _speakerListBox.Enabled = serverInitiated;
            _refreshSpeakersButton.Enabled = serverInitiated;
            
            // Update related settings visibility
            _enableServerCheckbox.Enabled = !serverInitiated;
            _enableMdnsCheckbox.Enabled = !serverInitiated;
            
            if (serverInitiated)
            {
                _speakerStatusLabel.Text = "Select speakers to stream to";
                RefreshSpeakerList();
            }
            else
            {
                _speakerStatusLabel.Text = "Speakers will connect to this server";
            }
        }
        
        private void RefreshSpeakersButton_Click(object? sender, EventArgs e)
        {
            _discoveryService?.Refresh();
            _speakerStatusLabel.Text = "Searching...";
            // Don't call RefreshSpeakerList here - it will be called by the SpeakersChanged event
        }
        
        private void RefreshSpeakerList()
        {
            if (_discoveryService == null)
            {
                _speakerStatusLabel.Text = "Discovery not available";
                return;
            }
            
            var speakers = _discoveryService.Speakers;
            
            // Save current selections
            var selected = new HashSet<string>();
            for (int i = 0; i < _speakerListBox.Items.Count; i++)
            {
                if (_speakerListBox.GetItemChecked(i) && _speakerListBox.Items[i] is SpeakerListItem item)
                {
                    selected.Add(item.Speaker.Id);
                }
            }
            
            // Also include previously saved selections
            foreach (var id in _settings.SelectedSpeakerIds)
            {
                selected.Add(id);
            }
            
            _speakerListBox.Items.Clear();
            
            foreach (var speaker in speakers)
            {
                var item = new SpeakerListItem(speaker);
                var index = _speakerListBox.Items.Add(item);
                _speakerListBox.SetItemChecked(index, selected.Contains(speaker.Id));
            }
            
            _speakerStatusLabel.Text = speakers.Count == 0 
                ? "No speakers found" 
                : $"Found {speakers.Count} speaker(s)";
        }
        
        private void OnSpeakersChanged(object? sender, EventArgs e)
        {
            // Debounce - only refresh once every 500ms
            if (_lastSpeakerRefresh != null && (DateTime.UtcNow - _lastSpeakerRefresh.Value).TotalMilliseconds < 500)
                return;
            
            _lastSpeakerRefresh = DateTime.UtcNow;
            
            if (InvokeRequired)
            {
                BeginInvoke(new Action(RefreshSpeakerList));
            }
            else
            {
                RefreshSpeakerList();
            }
        }
        
        private DateTime? _lastSpeakerRefresh;
        
        /// <summary>
        /// Helper class for speaker list items.
        /// </summary>
        private class SpeakerListItem
        {
            public DiscoveredSpeaker Speaker { get; }
            
            public SpeakerListItem(DiscoveredSpeaker speaker)
            {
                Speaker = speaker;
            }
            
            public override string ToString()
            {
                var status = Speaker.IsConnected ? " [Connected]" : "";
                return $"{Speaker.Name} ({Speaker.Address}:{Speaker.Port}){status}";
            }
        }

        private void CreateServerTab()
        {
            _serverTab = new TabPage("Server");
            
            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 5,
                Padding = new Padding(10)
            };
            
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 60));
            
            int row = 0;
            
            // Enable server
            _enableServerCheckbox = new CheckBox
            {
                Text = "Enable SendSpin Server",
                AutoSize = true,
                Dock = DockStyle.Fill
            };
            layout.Controls.Add(_enableServerCheckbox, 0, row);
            layout.SetColumnSpan(_enableServerCheckbox, 2);
            row++;
            
            // Server name
            layout.Controls.Add(new Label { Text = "Server Name:", AutoSize = true, Dock = DockStyle.Fill }, 0, row);
            _serverNameTextbox = new TextBox { Dock = DockStyle.Fill };
            layout.Controls.Add(_serverNameTextbox, 1, row);
            row++;
            
            // Server port
            layout.Controls.Add(new Label { Text = "Server Port:", AutoSize = true, Dock = DockStyle.Fill }, 0, row);
            _serverPortNumeric = new NumericUpDown
            {
                Minimum = 1024,
                Maximum = 65535,
                Dock = DockStyle.Fill
            };
            layout.Controls.Add(_serverPortNumeric, 1, row);
            row++;
            
            // Enable mDNS
            _enableMdnsCheckbox = new CheckBox
            {
                Text = "Enable mDNS Discovery",
                AutoSize = true,
                Dock = DockStyle.Fill
            };
            layout.Controls.Add(_enableMdnsCheckbox, 0, row);
            layout.SetColumnSpan(_enableMdnsCheckbox, 2);
            row++;
            
            // Mute local playback
            _muteLocalPlaybackCheckbox = new CheckBox
            {
                Text = "Mute Local Playback When Streaming",
                AutoSize = true,
                Dock = DockStyle.Fill
            };
            layout.Controls.Add(_muteLocalPlaybackCheckbox, 0, row);
            layout.SetColumnSpan(_muteLocalPlaybackCheckbox, 2);
            row++;
            
            // Mute info label
            var muteInfoLabel = new Label
            {
                Text = "When enabled, MusicBee's local audio output will be muted while streaming to SendSpin speakers.",
                AutoSize = true,
                ForeColor = SystemColors.GrayText,
                Dock = DockStyle.Fill
            };
            layout.Controls.Add(muteInfoLabel, 0, row);
            layout.SetColumnSpan(muteInfoLabel, 2);
            row++;
            
            // Info label
            var infoLabel = new Label
            {
                Text = "mDNS allows SendSpin clients to automatically discover this server on the network.",
                AutoSize = true,
                ForeColor = SystemColors.GrayText,
                Dock = DockStyle.Fill
            };
            layout.Controls.Add(infoLabel, 0, row);
            layout.SetColumnSpan(infoLabel, 2);
            
            _serverTab.Controls.Add(layout);
        }

        private void CreateAudioTab()
        {
            _audioTab = new TabPage("Audio");
            
            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 8,
                Padding = new Padding(10)
            };
            
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 60));
            
            int row = 0;
            
            // Codec
            layout.Controls.Add(new Label { Text = "Audio Codec:", AutoSize = true, Dock = DockStyle.Fill }, 0, row);
            _codecCombobox = new ComboBox
            {
                Dock = DockStyle.Fill,
                DropDownStyle = ComboBoxStyle.DropDownList
            };
            _codecCombobox.Items.AddRange(new object[] { "Opus (Recommended)", "FLAC (Lossless)", "PCM (Uncompressed)" });
            _codecCombobox.SelectedIndexChanged += CodecCombobox_SelectedIndexChanged;
            layout.Controls.Add(_codecCombobox, 1, row);
            row++;
            
            // Sample rate
            layout.Controls.Add(new Label { Text = "Sample Rate:", AutoSize = true, Dock = DockStyle.Fill }, 0, row);
            _sampleRateCombobox = new ComboBox
            {
                Dock = DockStyle.Fill,
                DropDownStyle = ComboBoxStyle.DropDownList
            };
            _sampleRateCombobox.Items.AddRange(new object[] { "44100 Hz", "48000 Hz", "96000 Hz" });
            layout.Controls.Add(_sampleRateCombobox, 1, row);
            row++;
            
            // Channels
            layout.Controls.Add(new Label { Text = "Channels:", AutoSize = true, Dock = DockStyle.Fill }, 0, row);
            _channelsCombobox = new ComboBox
            {
                Dock = DockStyle.Fill,
                DropDownStyle = ComboBoxStyle.DropDownList
            };
            _channelsCombobox.Items.AddRange(new object[] { "Stereo (2)", "Mono (1)" });
            layout.Controls.Add(_channelsCombobox, 1, row);
            row++;
            
            // Bit depth
            layout.Controls.Add(new Label { Text = "Bit Depth:", AutoSize = true, Dock = DockStyle.Fill }, 0, row);
            _bitDepthCombobox = new ComboBox
            {
                Dock = DockStyle.Fill,
                DropDownStyle = ComboBoxStyle.DropDownList
            };
            _bitDepthCombobox.Items.AddRange(new object[] { "16-bit", "24-bit" });
            layout.Controls.Add(_bitDepthCombobox, 1, row);
            row++;
            
            // Opus bitrate
            _opusBitrateLabel = new Label { Text = "Opus Bitrate (kbps):", AutoSize = true, Dock = DockStyle.Fill };
            layout.Controls.Add(_opusBitrateLabel, 0, row);
            _opusBitrateNumeric = new NumericUpDown
            {
                Minimum = 32,
                Maximum = 512,
                Increment = 16,
                Dock = DockStyle.Fill
            };
            layout.Controls.Add(_opusBitrateNumeric, 1, row);
            row++;
            
            // Separator
            layout.Controls.Add(new Label { Text = "MusicBee Audio Processing:", AutoSize = true, Font = new Font(Font, FontStyle.Bold), Dock = DockStyle.Fill }, 0, row);
            layout.SetColumnSpan(layout.GetControlFromPosition(0, row)!, 2);
            row++;
            
            // Enable DSP
            _enableDspCheckbox = new CheckBox
            {
                Text = "Apply MusicBee DSP Effects",
                AutoSize = true,
                Dock = DockStyle.Fill
            };
            layout.Controls.Add(_enableDspCheckbox, 0, row);
            layout.SetColumnSpan(_enableDspCheckbox, 2);
            row++;
            
            // Replay gain
            layout.Controls.Add(new Label { Text = "Replay Gain:", AutoSize = true, Dock = DockStyle.Fill }, 0, row);
            _replayGainCombobox = new ComboBox
            {
                Dock = DockStyle.Fill,
                DropDownStyle = ComboBoxStyle.DropDownList
            };
            _replayGainCombobox.Items.AddRange(new object[] { "Off", "Track", "Album", "Smart" });
            layout.Controls.Add(_replayGainCombobox, 1, row);
            
            _audioTab.Controls.Add(layout);
        }

        private void CreateAdvancedTab()
        {
            _advancedTab = new TabPage("Advanced");
            
            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 4,
                Padding = new Padding(10)
            };
            
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 60));
            
            int row = 0;
            
            // Buffer size
            layout.Controls.Add(new Label { Text = "Client Buffer (ms):", AutoSize = true, Dock = DockStyle.Fill }, 0, row);
            _bufferSizeNumeric = new NumericUpDown
            {
                Minimum = 50,
                Maximum = 1000,
                Increment = 50,
                Dock = DockStyle.Fill
            };
            layout.Controls.Add(_bufferSizeNumeric, 1, row);
            row++;
            
            // Buffer info
            var bufferInfo = new Label
            {
                Text = "Higher values improve stability but increase latency. Lower values reduce latency but may cause audio glitches.",
                AutoSize = true,
                ForeColor = SystemColors.GrayText,
                Dock = DockStyle.Fill
            };
            layout.Controls.Add(bufferInfo, 0, row);
            layout.SetColumnSpan(bufferInfo, 2);
            row++;
            
            // Debug logging
            _logDebugCheckbox = new CheckBox
            {
                Text = "Enable Debug Logging",
                AutoSize = true,
                Dock = DockStyle.Fill
            };
            layout.Controls.Add(_logDebugCheckbox, 0, row);
            layout.SetColumnSpan(_logDebugCheckbox, 2);
            row++;
            
            // Debug info
            var debugInfo = new Label
            {
                Text = "Debug logs are written to MusicBee's log folder.",
                AutoSize = true,
                ForeColor = SystemColors.GrayText,
                Dock = DockStyle.Fill
            };
            layout.Controls.Add(debugInfo, 0, row);
            layout.SetColumnSpan(debugInfo, 2);
            
            _advancedTab.Controls.Add(layout);
        }

        private void LoadSettings()
        {
            // Connection mode
            _modeClientInitiatedRadio.Checked = _settings.ConnectionMode == ConnectionMode.ClientInitiated;
            _modeServerInitiatedRadio.Checked = _settings.ConnectionMode == ConnectionMode.ServerInitiated;
            
            // Speakers
            RefreshSpeakerList();
            
            // Server
            _enableServerCheckbox.Checked = _settings.EnableServer;
            _serverNameTextbox.Text = _settings.ServerName;
            _serverPortNumeric.Value = _settings.ServerPort;
            _enableMdnsCheckbox.Checked = _settings.EnableMdns;
            _muteLocalPlaybackCheckbox.Checked = _settings.MuteLocalPlayback;
            
            // Audio
            _codecCombobox.SelectedIndex = _settings.AudioCodec.ToLowerInvariant() switch
            {
                "opus" => 0,
                "flac" => 1,
                "pcm" => 2,
                _ => 0
            };
            
            _sampleRateCombobox.SelectedIndex = _settings.SampleRate switch
            {
                44100 => 0,
                48000 => 1,
                96000 => 2,
                _ => 1
            };
            
            _channelsCombobox.SelectedIndex = _settings.Channels == 1 ? 1 : 0;
            _bitDepthCombobox.SelectedIndex = _settings.BitDepth == 24 ? 1 : 0;
            _opusBitrateNumeric.Value = _settings.OpusBitrate / 1000;
            
            _enableDspCheckbox.Checked = _settings.EnableDsp;
            _replayGainCombobox.SelectedIndex = (int)_settings.ReplayGainMode;
            
            // Advanced
            _bufferSizeNumeric.Value = _settings.BufferSizeMs;
            _logDebugCheckbox.Checked = _settings.LogDebugInfo;
            
            UpdateOpusBitrateVisibility();
        }

        private void SaveSettings()
        {
            // Connection mode
            _settings.ConnectionMode = _modeServerInitiatedRadio.Checked 
                ? ConnectionMode.ServerInitiated 
                : ConnectionMode.ClientInitiated;
            
            // Selected speakers
            _settings.SelectedSpeakerIds.Clear();
            for (int i = 0; i < _speakerListBox.Items.Count; i++)
            {
                if (_speakerListBox.GetItemChecked(i) && _speakerListBox.Items[i] is SpeakerListItem item)
                {
                    _settings.SelectedSpeakerIds.Add(item.Speaker.Id);
                }
            }
            SelectedSpeakerIds = _settings.SelectedSpeakerIds;
            
            // Server
            _settings.EnableServer = _enableServerCheckbox.Checked;
            _settings.ServerName = _serverNameTextbox.Text;
            _settings.ServerPort = (int)_serverPortNumeric.Value;
            _settings.EnableMdns = _enableMdnsCheckbox.Checked;
            _settings.MuteLocalPlayback = _muteLocalPlaybackCheckbox.Checked;
            
            // Audio
            _settings.AudioCodec = _codecCombobox.SelectedIndex switch
            {
                0 => "opus",
                1 => "flac",
                2 => "pcm",
                _ => "opus"
            };
            
            _settings.SampleRate = _sampleRateCombobox.SelectedIndex switch
            {
                0 => 44100,
                1 => 48000,
                2 => 96000,
                _ => 48000
            };
            
            _settings.Channels = _channelsCombobox.SelectedIndex == 1 ? 1 : 2;
            _settings.BitDepth = _bitDepthCombobox.SelectedIndex == 1 ? 24 : 16;
            _settings.OpusBitrate = (int)_opusBitrateNumeric.Value * 1000;
            
            _settings.EnableDsp = _enableDspCheckbox.Checked;
            _settings.ReplayGainMode = (Plugin.ReplayGainMode)_replayGainCombobox.SelectedIndex;
            
            // Advanced
            _settings.BufferSizeMs = (int)_bufferSizeNumeric.Value;
            _settings.LogDebugInfo = _logDebugCheckbox.Checked;
        }

        private void CodecCombobox_SelectedIndexChanged(object? sender, EventArgs e)
        {
            UpdateOpusBitrateVisibility();
        }

        private void UpdateOpusBitrateVisibility()
        {
            var isOpus = _codecCombobox.SelectedIndex == 0;
            _opusBitrateLabel.Visible = isOpus;
            _opusBitrateNumeric.Visible = isOpus;
        }

        private void OkButton_Click(object? sender, EventArgs e)
        {
            SaveSettings();
        }
    }
}
