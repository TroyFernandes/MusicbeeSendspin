using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace MusicBeePlugin.SendSpin
{
    /// <summary>
    /// Helper class for Math operations not available in .NET Framework 4.8
    /// </summary>
    internal static class MathHelper
    {
        public static float Clamp(float value, float min, float max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }
    }

    /// <summary>
    /// Manages speaker groups for synchronized multi-room audio
    /// </summary>
    public class GroupManager
    {
        private readonly ConcurrentDictionary<string, SpeakerGroup> _groups;
        private readonly ConcurrentDictionary<string, Speaker> _speakers;
        private string _defaultGroupId;
        
        public event EventHandler<GroupEventArgs>? GroupChanged;
        public event EventHandler<SpeakerEventArgs>? SpeakerChanged;

        public GroupManager()
        {
            _groups = new ConcurrentDictionary<string, SpeakerGroup>();
            _speakers = new ConcurrentDictionary<string, Speaker>();
            
            // Create default group
            _defaultGroupId = "default";
            _groups.TryAdd(_defaultGroupId, new SpeakerGroup(_defaultGroupId, "All Speakers"));
        }

        /// <summary>
        /// Get all groups
        /// </summary>
        public IEnumerable<SpeakerGroup> GetGroups() => _groups.Values;

        /// <summary>
        /// Get all speakers
        /// </summary>
        public IEnumerable<Speaker> GetSpeakers() => _speakers.Values;

        /// <summary>
        /// Get speakers in a specific group
        /// </summary>
        public IEnumerable<Speaker> GetSpeakersInGroup(string groupId)
        {
            return _speakers.Values.Where(s => s.GroupId == groupId);
        }

        /// <summary>
        /// Add a client as a speaker
        /// </summary>
        public void AddClient(string clientId, string clientName)
        {
            var speaker = new Speaker(clientId, clientName, _defaultGroupId);
            
            if (_speakers.TryAdd(clientId, speaker))
            {
                // Add to default group
                if (_groups.TryGetValue(_defaultGroupId, out var group))
                {
                    group.AddSpeaker(clientId);
                }
                
                SpeakerChanged?.Invoke(this, new SpeakerEventArgs(speaker, SpeakerChangeType.Added));
                Plugin.LogInfo("GroupManager", $"Speaker added: {clientName} ({clientId})");
            }
        }

        /// <summary>
        /// Remove a client/speaker
        /// </summary>
        public void RemoveClient(string clientId)
        {
            if (_speakers.TryRemove(clientId, out var speaker))
            {
                // Remove from current group
                if (_groups.TryGetValue(speaker.GroupId, out var group))
                {
                    group.RemoveSpeaker(clientId);
                }
                
                SpeakerChanged?.Invoke(this, new SpeakerEventArgs(speaker, SpeakerChangeType.Removed));
                Plugin.LogInfo("GroupManager", $"Speaker removed: {speaker.Name} ({clientId})");
            }
        }

        /// <summary>
        /// Create a new group
        /// </summary>
        public SpeakerGroup? CreateGroup(string name)
        {
            var groupId = Guid.NewGuid().ToString();
            var group = new SpeakerGroup(groupId, name);
            
            if (_groups.TryAdd(groupId, group))
            {
                GroupChanged?.Invoke(this, new GroupEventArgs(group, GroupChangeType.Created));
                return group;
            }
            
            return null;
        }

        /// <summary>
        /// Delete a group (moves speakers to default group)
        /// </summary>
        public bool DeleteGroup(string groupId)
        {
            // Can't delete default group
            if (groupId == _defaultGroupId) return false;
            
            if (_groups.TryRemove(groupId, out var group))
            {
                // Move speakers to default group
                foreach (var speakerId in group.SpeakerIds.ToList())
                {
                    MoveSpeakerToGroup(speakerId, _defaultGroupId);
                }
                
                GroupChanged?.Invoke(this, new GroupEventArgs(group, GroupChangeType.Deleted));
                return true;
            }
            
            return false;
        }

        /// <summary>
        /// Move a speaker to a different group
        /// </summary>
        public bool MoveSpeakerToGroup(string speakerId, string targetGroupId)
        {
            if (!_speakers.TryGetValue(speakerId, out var speaker)) return false;
            if (!_groups.TryGetValue(targetGroupId, out var targetGroup)) return false;
            
            var oldGroupId = speaker.GroupId;
            
            // Remove from old group
            if (_groups.TryGetValue(oldGroupId, out var oldGroup))
            {
                oldGroup.RemoveSpeaker(speakerId);
            }
            
            // Add to new group
            speaker.GroupId = targetGroupId;
            targetGroup.AddSpeaker(speakerId);
            
            SpeakerChanged?.Invoke(this, new SpeakerEventArgs(speaker, SpeakerChangeType.Moved));
            
            return true;
        }

        /// <summary>
        /// Set speaker volume
        /// </summary>
        public bool SetSpeakerVolume(string speakerId, float volume)
        {
            if (!_speakers.TryGetValue(speakerId, out var speaker)) return false;
            
            speaker.Volume = MathHelper.Clamp(volume, 0f, 1f);
            SpeakerChanged?.Invoke(this, new SpeakerEventArgs(speaker, SpeakerChangeType.VolumeChanged));
            
            return true;
        }

        /// <summary>
        /// Set speaker mute state
        /// </summary>
        public bool SetSpeakerMute(string speakerId, bool muted)
        {
            if (!_speakers.TryGetValue(speakerId, out var speaker)) return false;
            
            speaker.Muted = muted;
            SpeakerChanged?.Invoke(this, new SpeakerEventArgs(speaker, SpeakerChangeType.MuteChanged));
            
            return true;
        }

        /// <summary>
        /// Set group volume
        /// </summary>
        public bool SetGroupVolume(string groupId, float volume)
        {
            if (!_groups.TryGetValue(groupId, out var group)) return false;
            
            group.Volume = MathHelper.Clamp(volume, 0f, 1f);
            GroupChanged?.Invoke(this, new GroupEventArgs(group, GroupChangeType.VolumeChanged));
            
            return true;
        }

        /// <summary>
        /// Set group mute state
        /// </summary>
        public bool SetGroupMute(string groupId, bool muted)
        {
            if (!_groups.TryGetValue(groupId, out var group)) return false;
            
            group.Muted = muted;
            GroupChanged?.Invoke(this, new GroupEventArgs(group, GroupChangeType.MuteChanged));
            
            return true;
        }

        /// <summary>
        /// Rename a group
        /// </summary>
        public bool RenameGroup(string groupId, string newName)
        {
            if (!_groups.TryGetValue(groupId, out var group)) return false;
            
            group.Name = newName;
            GroupChanged?.Invoke(this, new GroupEventArgs(group, GroupChangeType.Renamed));
            
            return true;
        }

        /// <summary>
        /// Get speaker by ID
        /// </summary>
        public Speaker? GetSpeaker(string speakerId)
        {
            _speakers.TryGetValue(speakerId, out var speaker);
            return speaker;
        }

        /// <summary>
        /// Get group by ID
        /// </summary>
        public SpeakerGroup? GetGroup(string groupId)
        {
            _groups.TryGetValue(groupId, out var group);
            return group;
        }

        /// <summary>
        /// Get the default group
        /// </summary>
        public SpeakerGroup? GetDefaultGroup()
        {
            return GetGroup(_defaultGroupId);
        }
    }

    /// <summary>
    /// Represents a speaker/client
    /// </summary>
    public class Speaker
    {
        public string Id { get; }
        public string Name { get; set; }
        public string GroupId { get; set; }
        public float Volume { get; set; } = 1.0f;
        public bool Muted { get; set; }
        public bool IsConnected { get; set; } = true;
        public string SyncState { get; set; } = "synchronized";
        public DateTime ConnectedAt { get; }

        public Speaker(string id, string name, string groupId)
        {
            Id = id;
            Name = name;
            GroupId = groupId;
            ConnectedAt = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Represents a group of speakers
    /// </summary>
    public class SpeakerGroup
    {
        private readonly HashSet<string> _speakerIds;
        private readonly object _lock = new object();

        public string Id { get; }
        public string Name { get; set; }
        public float Volume { get; set; } = 1.0f;
        public bool Muted { get; set; }

        public IReadOnlyCollection<string> SpeakerIds
        {
            get
            {
                lock (_lock)
                {
                    return _speakerIds.ToList().AsReadOnly();
                }
            }
        }

        public int SpeakerCount
        {
            get
            {
                lock (_lock)
                {
                    return _speakerIds.Count;
                }
            }
        }

        public SpeakerGroup(string id, string name)
        {
            Id = id;
            Name = name;
            _speakerIds = new HashSet<string>();
        }

        public void AddSpeaker(string speakerId)
        {
            lock (_lock)
            {
                _speakerIds.Add(speakerId);
            }
        }

        public void RemoveSpeaker(string speakerId)
        {
            lock (_lock)
            {
                _speakerIds.Remove(speakerId);
            }
        }

        public bool ContainsSpeaker(string speakerId)
        {
            lock (_lock)
            {
                return _speakerIds.Contains(speakerId);
            }
        }
    }

    #region Event Args

    public class SpeakerEventArgs : EventArgs
    {
        public Speaker Speaker { get; }
        public SpeakerChangeType ChangeType { get; }

        public SpeakerEventArgs(Speaker speaker, SpeakerChangeType changeType)
        {
            Speaker = speaker;
            ChangeType = changeType;
        }
    }

    public class GroupEventArgs : EventArgs
    {
        public SpeakerGroup Group { get; }
        public GroupChangeType ChangeType { get; }

        public GroupEventArgs(SpeakerGroup group, GroupChangeType changeType)
        {
            Group = group;
            ChangeType = changeType;
        }
    }

    public enum SpeakerChangeType
    {
        Added,
        Removed,
        Moved,
        VolumeChanged,
        MuteChanged,
        SyncStateChanged
    }

    public enum GroupChangeType
    {
        Created,
        Deleted,
        Renamed,
        VolumeChanged,
        MuteChanged
    }

    #endregion
}
