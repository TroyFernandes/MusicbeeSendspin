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
    /// <summary>A Music Assistant Sendspin <b>server</b> discovered via mDNS.</summary>
    /// <remarks>
    /// The plugin is the Sendspin CLIENT for this role (it dials the server), so it browses the
    /// server-initiated connection service type <c>_sendspin-server._tcp.local.</c> — the mirror
    /// image of <see cref="SpeakerDiscoveryService"/> (which browses <c>_sendspin._tcp</c>, the
    /// speakers that connect to us).
    /// </remarks>
    public class DiscoveredServer
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public IPAddress? Address { get; set; }
        public int Port { get; set; }
        public string? Target { get; set; }          // SRV target host, used to match A records
        public string Path { get; set; } = "/sendspin";
        public DateTime LastSeen { get; set; }

        public string WebSocketUrl => Address != null
            ? $"ws://{Address}:{Port}{Path}"
            : string.Empty;

        public override string ToString() => $"{Name} ({Address}:{Port}{Path})";
    }

    /// <summary>
    /// Discovers Sendspin <b>servers</b> (Music Assistant) via mDNS service type
    /// <c>_sendspin-server._tcp.local.</c>, reading the TXT <c>path</c> (WebSocket endpoint,
    /// fixed by the protocol to /sendspin) and <c>name</c> (friendly name) records.
    /// </summary>
    /// <remarks>
    /// TXT keys per spec connection.md + aiosendspin's advertisement: <c>path</c> REQUIRED
    /// (the "ws" key guessed in the task file does not exist), <c>name</c> optional. The SRV
    /// target is <c>&lt;server_id&gt;.local.</c> and the A records carry the server's addresses.
    /// </remarks>
    public class SourceDiscoveryService : IDisposable
    {
        private const string ServiceType = "_sendspin-server._tcp.local.";

        private readonly ConcurrentDictionary<string, DiscoveredServer> _servers = new();
        private readonly Action<string> _logger;
        private MulticastService? _mdns;
        private ServiceDiscovery? _serviceDiscovery;
        private CancellationTokenSource? _cts;
        private bool _isRunning;
        private bool _disposed;

        public event EventHandler<DiscoveredServer>? ServerDiscovered;
        public event EventHandler<DiscoveredServer>? ServerLost;
        public event EventHandler? ServersChanged;

        public SourceDiscoveryService(Action<string> logger)
        {
            _logger = logger ?? (_ => { });
        }

        public IReadOnlyList<DiscoveredServer> Servers => _servers.Values.ToList();

        public DiscoveredServer? GetServer(string id)
        {
            _servers.TryGetValue(id, out var server);
            return server;
        }

        /// <summary>The first fully resolved server, if any (single-MA setups are the norm).</summary>
        public DiscoveredServer? GetFirstReadyServer() =>
            _servers.Values.FirstOrDefault(s => s.Address != null && s.Port > 0);

        /// <summary>Builds a manual WebSocket URL when discovery is off / fails.</summary>
        public static Uri ManualServerUrl(string host, int port, int discoveredPort, string path)
        {
            int p = port > 0 ? port : (discoveredPort > 0 ? discoveredPort : 8927);
            return new Uri($"ws://{host.Trim()}:{p}{(string.IsNullOrWhiteSpace(path) ? "/sendspin" : path)}");
        }

        public void Start()
        {
            if (_isRunning)
            {
                _logger("Source (server) discovery is already running");
                return;
            }

            try
            {
                _cts = new CancellationTokenSource();
                _mdns = new MulticastService();
                _serviceDiscovery = new ServiceDiscovery(_mdns);

                _serviceDiscovery.ServiceInstanceDiscovered += OnServiceInstanceDiscovered;
                _serviceDiscovery.ServiceInstanceShutdown += OnServiceInstanceShutdown;
                _mdns.AnswerReceived += OnAnswerReceived;

                _mdns.Start();
                _serviceDiscovery.QueryServiceInstances(ServiceType);
                _logger($"Source discovery started, querying for {ServiceType}");

                // Direct PTR query as well (same belt-and-braces as the speaker discovery).
                var query = new Message();
                query.Questions.Add(new Question { Name = "_sendspin-server._tcp.local", Type = DnsType.PTR });
                _mdns.SendQuery(query);

                _isRunning = true;
                Task.Run(PeriodicQueryAsync, _cts.Token);
            }
            catch (Exception ex)
            {
                _logger($"Failed to start source discovery: {ex.Message}");
                Stop();
            }
        }

        public void Stop()
        {
            if (!_isRunning && _mdns == null)
                return;

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
                _logger($"Error stopping source discovery: {ex.Message}");
            }
            _isRunning = false;
        }

        public void Refresh()
        {
            if (_isRunning && _serviceDiscovery != null)
                _serviceDiscovery.QueryServiceInstances(ServiceType);
        }

        private async Task PeriodicQueryAsync()
        {
            while (_cts != null && !_cts.Token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(30), _cts.Token);
                    _serviceDiscovery?.QueryServiceInstances(ServiceType);

                    // Drop servers not seen for 2 minutes (keep-alive re-announcements refresh).
                    var staleThreshold = DateTime.UtcNow.AddMinutes(-2);
                    var stale = _servers.Where(kvp => kvp.Value.LastSeen < staleThreshold).ToList();
                    foreach (var kvp in stale)
                    {
                        if (_servers.TryRemove(kvp.Key, out var server))
                        {
                            _logger($"MA server lost (stale): {server.Name}");
                            ServerLost?.Invoke(this, server);
                            ServersChanged?.Invoke(this, EventArgs.Empty);
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger($"Error in source discovery periodic query: {ex.Message}");
                }
            }
        }

        private void OnServiceInstanceDiscovered(object? sender, ServiceInstanceDiscoveryEventArgs e)
        {
            try
            {
                var serviceName = e.ServiceInstanceName.ToString();
                // Only the SERVER service type — this machine's own speaker announcements are
                // _sendspin._tcp (no "-server"), and every other mDNS service on the LAN is noise.
                if (!serviceName.Contains("_sendspin-server"))
                    return;

                _logger($"Discovered MA server service: {serviceName}");
                _mdns?.SendQuery(e.ServiceInstanceName, DnsClass.IN, DnsType.ANY);
            }
            catch (Exception ex)
            {
                _logger($"Error handling MA server discovery: {ex.Message}");
            }
        }

        private void OnServiceInstanceShutdown(object? sender, ServiceInstanceShutdownEventArgs e)
        {
            try
            {
                var serviceName = e.ServiceInstanceName.ToString();
                if (!serviceName.Contains("_sendspin-server"))
                    return;
                var instanceName = e.ServiceInstanceName.Labels.FirstOrDefault() ?? serviceName;

                if (_servers.TryRemove(instanceName, out var server))
                {
                    _logger($"MA server shutdown: {server.Name}");
                    ServerLost?.Invoke(this, server);
                    ServersChanged?.Invoke(this, EventArgs.Empty);
                }
            }
            catch (Exception ex)
            {
                _logger($"Error handling MA server shutdown: {ex.Message}");
            }
        }

        private void OnAnswerReceived(object? sender, MessageEventArgs e)
        {
            try
            {
                foreach (var record in e.Message.Answers.Concat(e.Message.AdditionalRecords))
                    ProcessRecord(record);
            }
            catch (Exception ex)
            {
                _logger($"Error processing MA server mDNS answer: {ex.Message}");
            }
        }

        private void ProcessRecord(ResourceRecord record)
        {
            var name = record.Name.ToString();

            if (record is PTRRecord ptr)
            {
                var ptrName = ptr.Name.ToString();
                if (ptrName.Contains("_sendspin-server"))
                {
                    var domainName = ptr.DomainName.ToString();
                    var instanceName = domainName.Split('.')[0];
                    if (_servers.TryGetValue(instanceName, out var known))
                        known.LastSeen = DateTime.UtcNow;

                    _mdns?.SendQuery(ptr.DomainName, DnsClass.IN, DnsType.SRV);
                    _mdns?.SendQuery(ptr.DomainName, DnsClass.IN, DnsType.TXT);
                }
                return;
            }

            // A records are named by host (<server_id>.local.), so route them by SRV target.
            if (record is ARecord a)
            {
                ProcessAddressRecord(a.Name.ToString(), a.Address);
                return;
            }

            // SRV/TXT only for the server service type.
            if (!name.Contains("_sendspin-server"))
                return;

            if (record is SRVRecord srv)
                ProcessSrvRecord(srv);
            else if (record is TXTRecord txt)
                ProcessTxtRecord(txt);
        }

        private void ProcessSrvRecord(SRVRecord srv)
        {
            var instanceName = srv.Name.Labels.FirstOrDefault() ?? srv.Name.ToString();

            var server = _servers.GetOrAdd(instanceName, _ => new DiscoveredServer
            {
                Id = instanceName,
                Name = instanceName,
            });

            server.Port = srv.Port;
            server.Target = srv.Target.ToString();
            server.LastSeen = DateTime.UtcNow;

            _logger($"MA server SRV: {instanceName} -> {srv.Target}:{srv.Port}");

            _mdns?.SendQuery(srv.Target, DnsClass.IN, DnsType.A);
            NotifyServerUpdate(server);
        }

        private void ProcessTxtRecord(TXTRecord txt)
        {
            var instanceName = txt.Name.Labels.FirstOrDefault() ?? txt.Name.ToString();

            if (!_servers.TryGetValue(instanceName, out var server))
            {
                server = new DiscoveredServer
                {
                    Id = instanceName,
                    Name = instanceName,
                };
                _servers[instanceName] = server;
            }
            server.LastSeen = DateTime.UtcNow;

            // TXT: path = WebSocket endpoint (REQUIRED, /sendspin), name = friendly name.
            foreach (var str in txt.Strings)
            {
                var parts = str.Split(new[] { '=' }, 2);
                if (parts.Length != 2)
                    continue;
                switch (parts[0].ToLowerInvariant())
                {
                    case "path":
                        if (server.Path != parts[1])
                        {
                            server.Path = parts[1];
                            _logger($"MA server TXT: {instanceName} path={parts[1]}");
                        }
                        break;
                    case "name":
                        if (server.Name != parts[1])
                        {
                            server.Name = parts[1];
                            _logger($"MA server TXT: {instanceName} name={parts[1]}");
                        }
                        break;
                }
            }

            NotifyServerUpdate(server);
        }

        private void ProcessAddressRecord(string name, IPAddress address)
        {
            var host = name.TrimEnd('.').ToLowerInvariant();
            foreach (var server in _servers.Values)
            {
                // Snapshot into locals: mutable class properties don't narrow under the
                // IsNullOrEmpty guard, and the comparison should read one consistent value.
                var target = server.Target;
                var knownAddress = server.Address;
                if (string.IsNullOrEmpty(target))
                    continue;
                if (!target.TrimEnd('.').ToLowerInvariant().Equals(host, StringComparison.Ordinal))
                    continue;

                server.LastSeen = DateTime.UtcNow;
                if (knownAddress == null || !knownAddress.Equals(address))
                {
                    server.Address = address;
                    _logger($"MA server address resolved: {server.Name} -> {address}");
                    NotifyServerUpdate(server);
                }
            }
        }

        private void NotifyServerUpdate(DiscoveredServer server)
        {
            if (server.Address != null && server.Port > 0)
            {
                _logger($"MA server ready: {server.Name} at {server.WebSocketUrl}");
                ServerDiscovered?.Invoke(this, server);
                ServersChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            Stop();
            _servers.Clear();
        }
    }
}