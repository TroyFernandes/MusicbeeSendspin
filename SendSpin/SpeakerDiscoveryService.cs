using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Makaretu.Dns;

namespace MusicBeePlugin.SendSpin
{
    /// <summary>
    /// Represents a discovered SendSpin speaker/client.
    /// </summary>
    public class DiscoveredSpeaker
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public IPAddress? Address { get; set; }
        public int Port { get; set; }
        public string? Target { get; set; } // SRV target host, used to match A records
        public string Path { get; set; } = "/sendspin";
        public DateTime LastSeen { get; set; }
        public bool IsConnected { get; set; }
        
        public string WebSocketUrl => Address != null 
            ? $"ws://{Address}:{Port}{Path}" 
            : string.Empty;
            
        public override string ToString() => $"{Name} ({Address}:{Port})";
    }

    /// <summary>
    /// Service for discovering SendSpin speakers via mDNS.
    /// Uses the _sendspin._tcp.local. service type as per spec.
    /// </summary>
    public class SpeakerDiscoveryService : IDisposable
    {
        private const string ServiceType = "_sendspin._tcp.local.";
        
        private readonly ConcurrentDictionary<string, DiscoveredSpeaker> _speakers = new();
        private readonly Action<string> _logger;
        private MulticastService? _mdns;
        private ServiceDiscovery? _serviceDiscovery;
        private CancellationTokenSource? _cts;
        private bool _isRunning;
        private bool _disposed;

        /// <summary>
        /// Event raised when a new speaker is discovered.
        /// </summary>
        public event EventHandler<DiscoveredSpeaker>? SpeakerDiscovered;
        
        /// <summary>
        /// Event raised when a speaker is lost (removed from network).
        /// </summary>
        public event EventHandler<DiscoveredSpeaker>? SpeakerLost;
        
        /// <summary>
        /// Event raised when the speaker list changes.
        /// </summary>
        public event EventHandler? SpeakersChanged;

        public SpeakerDiscoveryService(Action<string> logger)
        {
            _logger = logger ?? (_ => { });
        }

        /// <summary>
        /// Gets all currently known speakers.
        /// </summary>
        public IReadOnlyList<DiscoveredSpeaker> Speakers => _speakers.Values.ToList();

        /// <summary>
        /// Gets a speaker by its ID.
        /// </summary>
        public DiscoveredSpeaker? GetSpeaker(string id)
        {
            _speakers.TryGetValue(id, out var speaker);
            return speaker;
        }

        /// <summary>
        /// Start discovering SendSpin speakers on the network.
        /// </summary>
        public void Start()
        {
            if (_isRunning)
            {
                _logger("Speaker discovery is already running");
                return;
            }

            try
            {
                _cts = new CancellationTokenSource();
                
                _mdns = new MulticastService();
                _serviceDiscovery = new ServiceDiscovery(_mdns);
                
                // Subscribe to service instance discovery
                _serviceDiscovery.ServiceInstanceDiscovered += OnServiceInstanceDiscovered;
                _serviceDiscovery.ServiceInstanceShutdown += OnServiceInstanceShutdown;
                
                // Handle address resolution - this catches ALL mDNS answers
                _mdns.AnswerReceived += OnAnswerReceived;
                
                // Start the multicast service
                _mdns.Start();
                
                // Query for SendSpin client services (speakers)
                _serviceDiscovery.QueryServiceInstances(ServiceType);
                _logger($"Speaker discovery started, querying for {ServiceType}");
                
                // Also send a direct PTR query for _sendspin._tcp.local
                var query = new Message();
                query.Questions.Add(new Question { Name = "_sendspin._tcp.local", Type = DnsType.PTR });
                _mdns.SendQuery(query);
                _logger("Sent direct PTR query for _sendspin._tcp.local");
                
                _isRunning = true;
                
                // Start periodic re-query to find new speakers
                Task.Run(PeriodicQueryAsync, _cts.Token);
            }
            catch (Exception ex)
            {
                _logger($"Failed to start speaker discovery: {ex.Message}");
                Stop();
            }
        }

        /// <summary>
        /// Stop discovering speakers.
        /// </summary>
        public void Stop()
        {
            if (!_isRunning && _mdns == null)
                return;
                
            _logger("Stopping speaker discovery");
            
            try
            {
                _cts?.Cancel();
                
                if (_serviceDiscovery != null)
                {
                    _serviceDiscovery.ServiceInstanceDiscovered -= OnServiceInstanceDiscovered;
                    _serviceDiscovery.ServiceInstanceShutdown -= OnServiceInstanceShutdown;
                    _serviceDiscovery.Dispose();
                    _serviceDiscovery = null;
                }
                
                if (_mdns != null)
                {
                    _mdns.AnswerReceived -= OnAnswerReceived;
                    _mdns.Stop();
                    _mdns = null;
                }
                
                _cts?.Dispose();
                _cts = null;
            }
            catch (Exception ex)
            {
                _logger($"Error stopping speaker discovery: {ex.Message}");
            }
            
            _isRunning = false;
        }

        /// <summary>
        /// Trigger a new query for speakers.
        /// </summary>
        public void Refresh()
        {
            if (_isRunning && _serviceDiscovery != null)
            {
                _logger("Refreshing speaker discovery");
                _serviceDiscovery.QueryServiceInstances(ServiceType);
            }
        }

        private async Task PeriodicQueryAsync()
        {
            while (_cts != null && !_cts.Token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(30), _cts.Token);
                    
                    // Re-query for services
                    _serviceDiscovery?.QueryServiceInstances(ServiceType);
                    
                    // Remove stale speakers (not seen in 2 minutes)
                    var staleThreshold = DateTime.UtcNow.AddMinutes(-2);
                    var stale = _speakers.Where(kvp => kvp.Value.LastSeen < staleThreshold && !kvp.Value.IsConnected).ToList();
                    
                    foreach (var kvp in stale)
                    {
                        if (_speakers.TryRemove(kvp.Key, out var speaker))
                        {
                            _logger($"Speaker lost (stale): {speaker.Name}");
                            SpeakerLost?.Invoke(this, speaker);
                            SpeakersChanged?.Invoke(this, EventArgs.Empty);
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger($"Error in periodic query: {ex.Message}");
                }
            }
        }

        private void OnServiceInstanceDiscovered(object? sender, ServiceInstanceDiscoveryEventArgs e)
        {
            try
            {
                var serviceName = e.ServiceInstanceName.ToString();

                // Only SendSpin speakers - without this this fires for every
                // mDNS service on the LAN and sends an ANY query for each.
                if (!serviceName.Contains("_sendspin._tcp") || serviceName.Contains("_sendspin-server"))
                    return;

                _logger($"Discovered service: {serviceName}");
                var instanceName = e.ServiceInstanceName.Labels.FirstOrDefault() ?? serviceName;
                
                // Request more details (SRV, TXT records)
                _mdns?.SendQuery(e.ServiceInstanceName, DnsClass.IN, DnsType.ANY);
            }
            catch (Exception ex)
            {
                _logger($"Error handling service discovery: {ex.Message}");
            }
        }

        private void OnServiceInstanceShutdown(object? sender, ServiceInstanceShutdownEventArgs e)
        {
            try
            {
                var serviceName = e.ServiceInstanceName.ToString();
                if (!serviceName.Contains("_sendspin._tcp") || serviceName.Contains("_sendspin-server"))
                    return;
                var instanceName = e.ServiceInstanceName.Labels.FirstOrDefault() ?? serviceName;
                
                if (_speakers.TryRemove(instanceName, out var speaker))
                {
                    _logger($"Speaker shutdown: {speaker.Name}");
                    SpeakerLost?.Invoke(this, speaker);
                    SpeakersChanged?.Invoke(this, EventArgs.Empty);
                }
            }
            catch (Exception ex)
            {
                _logger($"Error handling service shutdown: {ex.Message}");
            }
        }

        private void OnAnswerReceived(object? sender, MessageEventArgs e)
        {
            try
            {
                // Process each answer record
                foreach (var record in e.Message.Answers.Concat(e.Message.AdditionalRecords))
                {
                    ProcessRecord(record);
                }
            }
            catch (Exception ex)
            {
                _logger($"Error processing mDNS answer: {ex.Message}");
            }
        }

        private void ProcessRecord(ResourceRecord record)
        {
            var name = record.Name.ToString();
            
            // Log all sendspin-related records for debugging
            if (name.IndexOf("sendspin", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                _logger($"Found sendspin record: {record.GetType().Name} - {name}");
            }
            
            // Process PTR records for service discovery
            if (record is PTRRecord ptr)
            {
                var ptrName = ptr.Name.ToString();
                if (ptrName.Contains("_sendspin._tcp") && !ptrName.Contains("_sendspin-server"))
                {
                    var domainName = ptr.DomainName.ToString();
                    _logger($"PTR record: {ptrName} -> {domainName}");

                    // Keepalive: re-announces prove the speaker is still alive
                    var instanceName = domainName.Split('.')[0];
                    if (_speakers.TryGetValue(instanceName, out var known))
                        known.LastSeen = DateTime.UtcNow;
                    
                    // Request SRV and TXT records for this instance
                    _mdns?.SendQuery(ptr.DomainName, DnsClass.IN, DnsType.SRV);
                    _mdns?.SendQuery(ptr.DomainName, DnsClass.IN, DnsType.TXT);
                }
                return;
            }
            
            // A records are named by host (e.g. "echo-dot.local."), not by
            // service type, so route them before the service-name guard.
            if (record is ARecord a)
            {
                ProcessAddressRecord(a.Name.ToString(), a.Address);
                return;
            }

            // SRV/TXT: only for the speaker service type (_sendspin._tcp),
            // excluding the server type so we don't discover ourselves
            if (!name.Contains("_sendspin._tcp") || name.Contains("_sendspin-server"))
                return;

            if (record is SRVRecord srv)
            {
                ProcessSrvRecord(srv);
            }
            else if (record is TXTRecord txt)
            {
                ProcessTxtRecord(txt, name);
            }
        }

        private void ProcessSrvRecord(SRVRecord srv)
        {
            var instanceName = srv.Name.Labels.FirstOrDefault() ?? srv.Name.ToString();
            
            var speaker = _speakers.GetOrAdd(instanceName, _ => new DiscoveredSpeaker
            {
                Id = instanceName,
                Name = instanceName
            });
            
            speaker.Port = srv.Port;
            speaker.Target = srv.Target.ToString();
            speaker.LastSeen = DateTime.UtcNow;
            
            _logger($"SRV record: {instanceName} -> {srv.Target}:{srv.Port}");
            
            // Request A record for the target
            _mdns?.SendQuery(srv.Target, DnsClass.IN, DnsType.A);
            
            NotifySpeakerUpdate(speaker);
        }

        private void ProcessTxtRecord(TXTRecord txt, string serviceName)
        {
            var instanceName = txt.Name.Labels.FirstOrDefault() ?? serviceName;
            
            if (!_speakers.TryGetValue(instanceName, out var speaker))
            {
                speaker = new DiscoveredSpeaker
                {
                    Id = instanceName,
                    Name = instanceName
                };
                _speakers[instanceName] = speaker;
            }
            
            speaker.LastSeen = DateTime.UtcNow;
            
            // Parse TXT records for path
            foreach (var str in txt.Strings)
            {
                var parts = str.Split(new[] { '=' }, 2);
                if (parts.Length == 2)
                {
                    var key = parts[0].ToLowerInvariant();
                    var value = parts[1];
                    
                    switch (key)
                    {
                        case "path":
                            speaker.Path = value;
                            _logger($"TXT record: {instanceName} path={value}");
                            break;
                    }
                }
            }
            
            NotifySpeakerUpdate(speaker);
        }

        private void ProcessAddressRecord(string name, IPAddress address)
        {
            // A records are named by host (e.g. "echo-dot.local."), so match
            // them against the SRV target. Assigning "first speaker without an
            // address" would grab the IP of whatever mDNS host answered.
            var host = name.TrimEnd('.').ToLowerInvariant();
            foreach (var speaker in _speakers.Values)
            {
                if (string.IsNullOrEmpty(speaker.Target))
                    continue;

                if (speaker.Target.TrimEnd('.').ToLowerInvariant() != host)
                    continue;

                speaker.LastSeen = DateTime.UtcNow;
                if (speaker.Address == null)
                {
                    speaker.Address = address;
                    _logger($"Address resolved: {speaker.Name} -> {address}");
                    NotifySpeakerUpdate(speaker);
                }
            }
        }

        private void NotifySpeakerUpdate(DiscoveredSpeaker speaker)
        {
            // Only notify if we have complete information
            if (speaker.Address != null && speaker.Port > 0)
            {
                var isNew = !_speakers.ContainsKey(speaker.Id) || 
                            (DateTime.UtcNow - speaker.LastSeen).TotalSeconds < 1;
                
                if (isNew)
                {
                    _logger($"Speaker ready: {speaker.Name} at {speaker.WebSocketUrl}");
                    SpeakerDiscovered?.Invoke(this, speaker);
                }
                
                SpeakersChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        /// <summary>
        /// Mark a speaker as connected.
        /// </summary>
        public void SetSpeakerConnected(string id, bool connected)
        {
            if (_speakers.TryGetValue(id, out var speaker))
            {
                speaker.IsConnected = connected;
                SpeakersChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            
            _disposed = true;
            Stop();
            _speakers.Clear();
        }
    }
}
