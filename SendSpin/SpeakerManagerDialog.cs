using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace MusicBeePlugin.SendSpin
{
    /// <summary>
    /// Dialog for managing speakers and groups
    /// </summary>
    public class SpeakerManagerDialog : Form
    {
        private readonly SendSpinServer? _server;
        private readonly GroupManager? _groupManager;
        
        // Controls - initialized in InitializeComponents
        private SplitContainer _splitContainer = null!;
        private ListView _speakerListView = null!;
        private ListView _groupListView = null!;
        private Label _statusLabel = null!;
        
        // Speaker controls
        private TrackBar _speakerVolumeTrackbar = null!;
        private CheckBox _speakerMuteCheckbox = null!;
        private Button _moveSpeakerButton = null!;
        
        // Group controls
        private Button _createGroupButton = null!;
        private Button _deleteGroupButton = null!;
        private Button _renameGroupButton = null!;
        private TrackBar _groupVolumeTrackbar = null!;
        private CheckBox _groupMuteCheckbox = null!;
        
        private Timer _refreshTimer = null!;

        public SpeakerManagerDialog(SendSpinServer? server, GroupManager? groupManager)
        {
            _server = server;
            _groupManager = groupManager;
            
            InitializeComponents();
            RefreshData();
            
            // Set up auto-refresh
            _refreshTimer = new Timer { Interval = 2000 };
            _refreshTimer.Tick += (s, e) => RefreshData();
            _refreshTimer.Start();
        }

        private void InitializeComponents()
        {
            Text = "SendSpin Speaker Manager";
            Size = new Size(800, 500);
            MinimumSize = new Size(600, 400);
            StartPosition = FormStartPosition.CenterParent;
            
            // Main split container
            _splitContainer = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Vertical,
                SplitterDistance = 400
            };
            
            // Left panel - Groups
            var groupPanel = CreateGroupPanel();
            _splitContainer.Panel1.Controls.Add(groupPanel);
            
            // Right panel - Speakers
            var speakerPanel = CreateSpeakerPanel();
            _splitContainer.Panel2.Controls.Add(speakerPanel);
            
            // Status bar
            _statusLabel = new Label
            {
                Dock = DockStyle.Bottom,
                Height = 25,
                TextAlign = ContentAlignment.MiddleLeft,
                BorderStyle = BorderStyle.Fixed3D,
                Padding = new Padding(5, 0, 5, 0)
            };
            
            Controls.Add(_splitContainer);
            Controls.Add(_statusLabel);
            
            FormClosing += (s, e) => _refreshTimer.Stop();
        }

        private Panel CreateGroupPanel()
        {
            var panel = new Panel { Dock = DockStyle.Fill };
            
            // Header
            var headerLabel = new Label
            {
                Text = "Groups",
                Font = new Font(Font, FontStyle.Bold),
                Dock = DockStyle.Top,
                Height = 25
            };
            
            // Group list
            _groupListView = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                MultiSelect = false,
                HideSelection = false
            };
            _groupListView.Columns.Add("Name", 150);
            _groupListView.Columns.Add("Speakers", 80);
            _groupListView.Columns.Add("Volume", 60);
            _groupListView.SelectedIndexChanged += GroupListView_SelectedIndexChanged;
            
            // Controls panel
            var controlsPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                Height = 100,
                FlowDirection = FlowDirection.TopDown,
                Padding = new Padding(5)
            };
            
            // Buttons row
            var buttonsPanel = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.LeftToRight,
                Height = 30,
                AutoSize = true
            };
            
            _createGroupButton = new Button { Text = "New Group", Width = 80 };
            _createGroupButton.Click += CreateGroupButton_Click;
            
            _deleteGroupButton = new Button { Text = "Delete", Width = 60, Enabled = false };
            _deleteGroupButton.Click += DeleteGroupButton_Click;
            
            _renameGroupButton = new Button { Text = "Rename", Width = 60, Enabled = false };
            _renameGroupButton.Click += RenameGroupButton_Click;
            
            buttonsPanel.Controls.Add(_createGroupButton);
            buttonsPanel.Controls.Add(_deleteGroupButton);
            buttonsPanel.Controls.Add(_renameGroupButton);
            
            // Volume row
            var volumePanel = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.LeftToRight,
                Height = 30,
                AutoSize = true
            };
            
            volumePanel.Controls.Add(new Label { Text = "Group Volume:", AutoSize = true });
            _groupVolumeTrackbar = new TrackBar
            {
                Minimum = 0,
                Maximum = 100,
                Width = 150,
                TickFrequency = 10,
                Enabled = false
            };
            _groupVolumeTrackbar.ValueChanged += GroupVolumeTrackbar_ValueChanged;
            volumePanel.Controls.Add(_groupVolumeTrackbar);
            
            _groupMuteCheckbox = new CheckBox { Text = "Mute", AutoSize = true, Enabled = false };
            _groupMuteCheckbox.CheckedChanged += GroupMuteCheckbox_CheckedChanged;
            volumePanel.Controls.Add(_groupMuteCheckbox);
            
            controlsPanel.Controls.Add(buttonsPanel);
            controlsPanel.Controls.Add(volumePanel);
            
            panel.Controls.Add(_groupListView);
            panel.Controls.Add(controlsPanel);
            panel.Controls.Add(headerLabel);
            
            return panel;
        }

        private Panel CreateSpeakerPanel()
        {
            var panel = new Panel { Dock = DockStyle.Fill };
            
            // Header
            var headerLabel = new Label
            {
                Text = "Speakers",
                Font = new Font(Font, FontStyle.Bold),
                Dock = DockStyle.Top,
                Height = 25
            };
            
            // Speaker list
            _speakerListView = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                MultiSelect = false,
                HideSelection = false
            };
            _speakerListView.Columns.Add("Name", 150);
            _speakerListView.Columns.Add("Group", 100);
            _speakerListView.Columns.Add("Volume", 60);
            _speakerListView.Columns.Add("Status", 80);
            _speakerListView.SelectedIndexChanged += SpeakerListView_SelectedIndexChanged;
            
            // Controls panel
            var controlsPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                Height = 100,
                FlowDirection = FlowDirection.TopDown,
                Padding = new Padding(5)
            };
            
            // Move to group
            var movePanel = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.LeftToRight,
                Height = 30,
                AutoSize = true
            };
            
            _moveSpeakerButton = new Button { Text = "Move to Group...", Width = 120, Enabled = false };
            _moveSpeakerButton.Click += MoveSpeakerButton_Click;
            movePanel.Controls.Add(_moveSpeakerButton);
            
            // Volume row
            var volumePanel = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.LeftToRight,
                Height = 30,
                AutoSize = true
            };
            
            volumePanel.Controls.Add(new Label { Text = "Speaker Volume:", AutoSize = true });
            _speakerVolumeTrackbar = new TrackBar
            {
                Minimum = 0,
                Maximum = 100,
                Width = 150,
                TickFrequency = 10,
                Enabled = false
            };
            _speakerVolumeTrackbar.ValueChanged += SpeakerVolumeTrackbar_ValueChanged;
            volumePanel.Controls.Add(_speakerVolumeTrackbar);
            
            _speakerMuteCheckbox = new CheckBox { Text = "Mute", AutoSize = true, Enabled = false };
            _speakerMuteCheckbox.CheckedChanged += SpeakerMuteCheckbox_CheckedChanged;
            volumePanel.Controls.Add(_speakerMuteCheckbox);
            
            controlsPanel.Controls.Add(movePanel);
            controlsPanel.Controls.Add(volumePanel);
            
            panel.Controls.Add(_speakerListView);
            panel.Controls.Add(controlsPanel);
            panel.Controls.Add(headerLabel);
            
            return panel;
        }

        private void RefreshData()
        {
            if (_groupManager == null || _server == null) return;
            
            // Remember selections
            var selectedGroupId = _groupListView.SelectedItems.Count > 0 
                ? _groupListView.SelectedItems[0].Tag as string 
                : null;
            var selectedSpeakerId = _speakerListView.SelectedItems.Count > 0 
                ? _speakerListView.SelectedItems[0].Tag as string 
                : null;
            
            // Refresh groups
            _groupListView.Items.Clear();
            foreach (var group in _groupManager.GetGroups())
            {
                var item = new ListViewItem(group.Name)
                {
                    Tag = group.Id
                };
                item.SubItems.Add(group.SpeakerCount.ToString());
                item.SubItems.Add($"{(int)(group.Volume * 100)}%");
                
                if (group.Muted)
                {
                    item.ForeColor = Color.Gray;
                }
                
                _groupListView.Items.Add(item);
                
                if (group.Id == selectedGroupId)
                {
                    item.Selected = true;
                }
            }
            
            // Refresh speakers
            _speakerListView.Items.Clear();
            foreach (var speaker in _groupManager.GetSpeakers())
            {
                var group = _groupManager.GetGroup(speaker.GroupId);
                var item = new ListViewItem(speaker.Name)
                {
                    Tag = speaker.Id
                };
                item.SubItems.Add(group?.Name ?? "Unknown");
                item.SubItems.Add($"{(int)(speaker.Volume * 100)}%");
                item.SubItems.Add(speaker.SyncState);
                
                if (speaker.Muted)
                {
                    item.ForeColor = Color.Gray;
                }
                
                _speakerListView.Items.Add(item);
                
                if (speaker.Id == selectedSpeakerId)
                {
                    item.Selected = true;
                }
            }
            
            // Update status
            var speakerCount = _groupManager.GetSpeakers().Count();
            var groupCount = _groupManager.GetGroups().Count();
            _statusLabel.Text = $"Connected: {speakerCount} speaker(s) in {groupCount} group(s) | Server: {(_server.IsRunning ? "Running" : "Stopped")}";
        }

        #region Event Handlers

        private void GroupListView_SelectedIndexChanged(object? sender, EventArgs e)
        {
            var hasSelection = _groupListView.SelectedItems.Count > 0;
            var isDefaultGroup = hasSelection && 
                _groupListView.SelectedItems[0].Tag as string == "default";
            
            _deleteGroupButton.Enabled = hasSelection && !isDefaultGroup;
            _renameGroupButton.Enabled = hasSelection;
            _groupVolumeTrackbar.Enabled = hasSelection;
            _groupMuteCheckbox.Enabled = hasSelection;
            
            if (hasSelection && _groupManager != null)
            {
                var groupId = _groupListView.SelectedItems[0].Tag as string;
                var group = _groupManager.GetGroup(groupId!);
                if (group != null)
                {
                    _groupVolumeTrackbar.Value = (int)(group.Volume * 100);
                    _groupMuteCheckbox.Checked = group.Muted;
                }
            }
        }

        private void SpeakerListView_SelectedIndexChanged(object? sender, EventArgs e)
        {
            var hasSelection = _speakerListView.SelectedItems.Count > 0;
            
            _moveSpeakerButton.Enabled = hasSelection;
            _speakerVolumeTrackbar.Enabled = hasSelection;
            _speakerMuteCheckbox.Enabled = hasSelection;
            
            if (hasSelection && _groupManager != null)
            {
                var speakerId = _speakerListView.SelectedItems[0].Tag as string;
                var speaker = _groupManager.GetSpeaker(speakerId!);
                if (speaker != null)
                {
                    _speakerVolumeTrackbar.Value = (int)(speaker.Volume * 100);
                    _speakerMuteCheckbox.Checked = speaker.Muted;
                }
            }
        }

        private void CreateGroupButton_Click(object? sender, EventArgs e)
        {
            using (var dialog = new InputDialog("Create Group", "Enter group name:"))
            {
                if (dialog.ShowDialog(this) == DialogResult.OK && !string.IsNullOrWhiteSpace(dialog.InputText))
                {
                    _groupManager?.CreateGroup(dialog.InputText);
                    RefreshData();
                }
            }
        }

        private void DeleteGroupButton_Click(object? sender, EventArgs e)
        {
            if (_groupListView.SelectedItems.Count == 0) return;
            
            var groupId = _groupListView.SelectedItems[0].Tag as string;
            var groupName = _groupListView.SelectedItems[0].Text;
            
            var result = MessageBox.Show(
                $"Delete group '{groupName}'? Speakers will be moved to the default group.",
                "Confirm Delete",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question
            );
            
            if (result == DialogResult.Yes && groupId != null)
            {
                _groupManager?.DeleteGroup(groupId);
                RefreshData();
            }
        }

        private void RenameGroupButton_Click(object? sender, EventArgs e)
        {
            if (_groupListView.SelectedItems.Count == 0) return;
            
            var groupId = _groupListView.SelectedItems[0].Tag as string;
            var currentName = _groupListView.SelectedItems[0].Text;
            
            using (var dialog = new InputDialog("Rename Group", "Enter new name:", currentName))
            {
                if (dialog.ShowDialog(this) == DialogResult.OK && !string.IsNullOrWhiteSpace(dialog.InputText) && groupId != null)
                {
                    _groupManager?.RenameGroup(groupId, dialog.InputText);
                    RefreshData();
                }
            }
        }

        private void MoveSpeakerButton_Click(object? sender, EventArgs e)
        {
            if (_speakerListView.SelectedItems.Count == 0 || _groupManager == null) return;
            
            var speakerId = _speakerListView.SelectedItems[0].Tag as string;
            
            // Show group selection dialog
            using (var dialog = new GroupSelectionDialog(_groupManager.GetGroups().ToList()))
            {
                if (dialog.ShowDialog(this) == DialogResult.OK && dialog.SelectedGroupId != null && speakerId != null)
                {
                    _groupManager.MoveSpeakerToGroup(speakerId, dialog.SelectedGroupId);
                    RefreshData();
                }
            }
        }

        private void GroupVolumeTrackbar_ValueChanged(object? sender, EventArgs e)
        {
            if (_groupListView.SelectedItems.Count == 0) return;
            
            var groupId = _groupListView.SelectedItems[0].Tag as string;
            if (groupId != null)
            {
                _groupManager?.SetGroupVolume(groupId, _groupVolumeTrackbar.Value / 100f);
            }
        }

        private void GroupMuteCheckbox_CheckedChanged(object? sender, EventArgs e)
        {
            if (_groupListView.SelectedItems.Count == 0) return;
            
            var groupId = _groupListView.SelectedItems[0].Tag as string;
            if (groupId != null)
            {
                _groupManager?.SetGroupMute(groupId, _groupMuteCheckbox.Checked);
                RefreshData();
            }
        }

        private void SpeakerVolumeTrackbar_ValueChanged(object? sender, EventArgs e)
        {
            if (_speakerListView.SelectedItems.Count == 0) return;
            
            var speakerId = _speakerListView.SelectedItems[0].Tag as string;
            if (speakerId != null)
            {
                _groupManager?.SetSpeakerVolume(speakerId, _speakerVolumeTrackbar.Value / 100f);
            }
        }

        private void SpeakerMuteCheckbox_CheckedChanged(object? sender, EventArgs e)
        {
            if (_speakerListView.SelectedItems.Count == 0) return;
            
            var speakerId = _speakerListView.SelectedItems[0].Tag as string;
            if (speakerId != null)
            {
                _groupManager?.SetSpeakerMute(speakerId, _speakerMuteCheckbox.Checked);
                RefreshData();
            }
        }

        #endregion
    }

    /// <summary>
    /// Simple input dialog
    /// </summary>
    internal class InputDialog : Form
    {
        private TextBox _textBox;
        public string InputText => _textBox.Text;

        public InputDialog(string title, string prompt, string defaultValue = "")
        {
            Text = title;
            Size = new Size(350, 150);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            
            var promptLabel = new Label
            {
                Text = prompt,
                Location = new Point(10, 15),
                AutoSize = true
            };
            
            _textBox = new TextBox
            {
                Text = defaultValue,
                Location = new Point(10, 40),
                Width = 310
            };
            
            var okButton = new Button
            {
                Text = "OK",
                DialogResult = DialogResult.OK,
                Location = new Point(165, 75),
                Width = 75
            };
            
            var cancelButton = new Button
            {
                Text = "Cancel",
                DialogResult = DialogResult.Cancel,
                Location = new Point(245, 75),
                Width = 75
            };
            
            Controls.Add(promptLabel);
            Controls.Add(_textBox);
            Controls.Add(okButton);
            Controls.Add(cancelButton);
            
            AcceptButton = okButton;
            CancelButton = cancelButton;
        }
    }

    /// <summary>
    /// Dialog for selecting a group
    /// </summary>
    internal class GroupSelectionDialog : Form
    {
        private ListBox _groupListBox;
        public string? SelectedGroupId { get; private set; }

        public GroupSelectionDialog(System.Collections.Generic.List<SpeakerGroup> groups)
        {
            Text = "Select Group";
            Size = new Size(300, 300);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            
            _groupListBox = new ListBox
            {
                Dock = DockStyle.Top,
                Height = 200
            };
            
            foreach (var group in groups)
            {
                _groupListBox.Items.Add(new GroupListItem(group.Id, group.Name));
            }
            
            var buttonsPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                FlowDirection = FlowDirection.RightToLeft,
                Height = 40,
                Padding = new Padding(5)
            };
            
            var cancelButton = new Button
            {
                Text = "Cancel",
                DialogResult = DialogResult.Cancel,
                Width = 75
            };
            
            var okButton = new Button
            {
                Text = "OK",
                Width = 75
            };
            okButton.Click += (s, e) =>
            {
                if (_groupListBox.SelectedItem is GroupListItem item)
                {
                    SelectedGroupId = item.Id;
                    DialogResult = DialogResult.OK;
                    Close();
                }
            };
            
            buttonsPanel.Controls.Add(cancelButton);
            buttonsPanel.Controls.Add(okButton);
            
            Controls.Add(_groupListBox);
            Controls.Add(buttonsPanel);
            
            AcceptButton = okButton;
            CancelButton = cancelButton;
        }

        private class GroupListItem
        {
            public string Id { get; }
            public string Name { get; }

            public GroupListItem(string id, string name)
            {
                Id = id;
                Name = name;
            }

            public override string ToString() => Name;
        }
    }
}
