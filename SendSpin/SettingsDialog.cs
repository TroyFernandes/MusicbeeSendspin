using System;
using System.Drawing;
using System.Windows.Forms;

namespace MusicBeePlugin.SendSpin
{
    /// <summary>
    /// Settings dialog for SendSpin plugin (Music Assistant render device path).
    /// </summary>
    public class SettingsDialog : Form
    {
        private PluginSettings _settings;
        private readonly string? _pairingToken;

        // Controls - initialized in InitializeComponents
        private TabControl _tabControl = null!;
        private TabPage _advancedTab = null!;
        private TabPage _assistantTab = null!;


        // Advanced settings
        private CheckBox _logDebugCheckbox = null!;

        // Music Assistant (source render device) tab
        private CheckBox _assistantEnabledCheckbox = null!;
        private TextBox _assistantNameTextbox = null!;
        private CheckBox _assistantAutoDiscoverCheckbox = null!;
        private TextBox _assistantHostTextbox = null!;
        private NumericUpDown _assistantPortNumeric = null!;
        private TextBox _assistantTokenTextbox = null!;

        // Buttons
        private Button _okButton = null!;
        private Button _cancelButton = null!;

        public PluginSettings Settings => _settings;

        public SettingsDialog(PluginSettings settings, string? pairingToken = null)
        {
            _settings = settings.Clone();
            _pairingToken = pairingToken;
            InitializeComponents();
            LoadSettings();
        }

        private void InitializeComponents()
        {
            Text = "SendSpin Settings";
            Size = new Size(500, 500);
            MinimumSize = new Size(450, 450);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;

            // Tab control
            _tabControl = new TabControl
            {
                Dock = DockStyle.Top,
                Height = 400
            };

            // Create tabs
            CreateAdvancedTab();
            CreateAssistantTab();

            _tabControl.TabPages.Add(_advancedTab);
            _tabControl.TabPages.Add(_assistantTab);

            // Buttons
            var buttonPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                Height = 40,
                FlowDirection = FlowDirection.RightToLeft,
                Padding = new Padding(5)
            };

            _cancelButton = new Button
            {
                Text = "Cancel",
                Width = 80,
                DialogResult = DialogResult.Cancel
            };

            _okButton = new Button
            {
                Text = "OK",
                Width = 80,
                DialogResult = DialogResult.OK
            };
            _okButton.Click += OkButton_Click;

            buttonPanel.Controls.Add(_cancelButton);
            buttonPanel.Controls.Add(_okButton);

            Controls.Add(_tabControl);
            Controls.Add(buttonPanel);

            AcceptButton = _okButton;
            CancelButton = _cancelButton;
        }
        private void CreateAssistantTab()
        {
            _assistantTab = new TabPage("Music Assistant");

            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                AutoSize = true,
                Padding = new Padding(10)
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 35));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 65));

            int row = 0;

            _assistantEnabledCheckbox = new CheckBox
            {
                Text = "Enable the Music Assistant render device",
                AutoSize = true,
                Dock = DockStyle.Fill
            };
            layout.Controls.Add(_assistantEnabledCheckbox, 0, row);
            layout.SetColumnSpan(_assistantEnabledCheckbox, 2);
            row++;

            var enabledInfo = new Label
            {
                Text = "\u201CMusic Assistant (Sendspin)\u201D appears in Preferences \u2192 Player \u2192 Output. Selecting it routes playback to Music Assistant (local output is silent) \u2014 no local-mute hack.",
                AutoSize = true,
                ForeColor = SystemColors.GrayText,
                Dock = DockStyle.Fill
            };
            layout.Controls.Add(enabledInfo, 0, row);
            layout.SetColumnSpan(enabledInfo, 2);
            row++;

            layout.Controls.Add(new Label { Text = "Device name:", AutoSize = true, Dock = DockStyle.Fill }, 0, row);
            _assistantNameTextbox = new TextBox { Dock = DockStyle.Fill };
            layout.Controls.Add(_assistantNameTextbox, 1, row);
            row++;

            _assistantAutoDiscoverCheckbox = new CheckBox
            {
                Text = "Find the Music Assistant server automatically (mDNS)",
                AutoSize = true,
                Dock = DockStyle.Fill
            };
            layout.Controls.Add(_assistantAutoDiscoverCheckbox, 0, row);
            layout.SetColumnSpan(_assistantAutoDiscoverCheckbox, 2);
            row++;

            layout.Controls.Add(new Label { Text = "Server host (manual, optional):", AutoSize = true, Dock = DockStyle.Fill }, 0, row);
            _assistantHostTextbox = new TextBox { Dock = DockStyle.Fill };
            layout.Controls.Add(_assistantHostTextbox, 1, row);
            row++;

            layout.Controls.Add(new Label { Text = "Server port (0 = default 8927):", AutoSize = true, Dock = DockStyle.Fill }, 0, row);
            _assistantPortNumeric = new NumericUpDown { Minimum = 0, Maximum = 65535, Dock = DockStyle.Fill };
            layout.Controls.Add(_assistantPortNumeric, 1, row);
            row++;

            // Pairing: the token the operator pastes into Music Assistant (pairing_psk method).
            var pairingLabel = new Label
            {
                Text = "Pairing \u2014 paste this token into Music Assistant (it pairs this MusicBee as a source client):",
                AutoSize = true,
                Dock = DockStyle.Fill,
                ForeColor = SystemColors.ControlText
            };
            layout.Controls.Add(pairingLabel, 0, row);
            layout.SetColumnSpan(pairingLabel, 2);
            row++;

            _assistantTokenTextbox = new TextBox
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                Font = new Font(FontFamily.GenericMonospace, 8.5f),
                ShortcutsEnabled = true
            };
            layout.Controls.Add(_assistantTokenTextbox, 0, row);
            layout.SetColumnSpan(_assistantTokenTextbox, 2);
            row++;

            var copyButton = new Button
            {
                Text = "Copy pairing token",
                AutoSize = true
            };
            copyButton.Click += (sender, args) =>
            {
                if (!string.IsNullOrEmpty(_assistantTokenTextbox.Text))
                {
                    Clipboard.SetText(_assistantTokenTextbox.Text);
                    copyButton.Text = "Copied!";
                }
            };
            layout.Controls.Add(copyButton, 0, row);
            row++;

            var pairingInfo = new Label
            {
                Text = "Pairing is required once: Music Assistant only accepts audio sources from paired clients. Rotating the token (e.g. after it leaked) requires deleting SendSpinSourcePairing.json and re-pairing.",
                AutoSize = true,
                ForeColor = SystemColors.GrayText,
                Dock = DockStyle.Fill
            };
            layout.Controls.Add(pairingInfo, 0, row);
            layout.SetColumnSpan(pairingInfo, 2);

            _assistantTab.Controls.Add(layout);
        }

        private void CreateAdvancedTab()
        {
            _advancedTab = new TabPage("Advanced");

            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                AutoSize = true,
                Padding = new Padding(10)
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 60));

            int row = 0;

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
            // Audio processing is intentionally not configurable (no DSP/ReplayGain on the
            // streamed audio); nothing to load from the removed Audio tab.

            // Advanced
            _logDebugCheckbox.Checked = _settings.LogDebugInfo;

            // Music Assistant (source render device)
            _assistantEnabledCheckbox.Checked = _settings.RenderDeviceEnabled;
            _assistantNameTextbox.Text = _settings.RenderDeviceName;
            _assistantAutoDiscoverCheckbox.Checked = _settings.SourceAutoDiscover;
            _assistantHostTextbox.Text = _settings.SourceServerHost;
            _assistantPortNumeric.Value = _settings.SourceServerPort;
            _assistantTokenTextbox.Text = _pairingToken ?? "(unavailable)";
        }

        private void SaveSettings()
        {
            // Audio processing is intentionally not configurable; nothing to save.

            // Advanced
            _settings.LogDebugInfo = _logDebugCheckbox.Checked;

            // Music Assistant (source render device)
            _settings.RenderDeviceEnabled = _assistantEnabledCheckbox.Checked;
            _settings.RenderDeviceName = string.IsNullOrWhiteSpace(_assistantNameTextbox.Text)
                ? "Music Assistant (Sendspin)"
                : _assistantNameTextbox.Text.Trim();
            _settings.SourceAutoDiscover = _assistantAutoDiscoverCheckbox.Checked;
            _settings.SourceServerHost = _assistantHostTextbox.Text.Trim();
            _settings.SourceServerPort = (int)_assistantPortNumeric.Value;
        }


        private void OkButton_Click(object? sender, EventArgs e)
        {
            SaveSettings();
        }
    }
}