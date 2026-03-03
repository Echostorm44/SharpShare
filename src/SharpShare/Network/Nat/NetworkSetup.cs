using SharpShare.Storage;
using System.Diagnostics;
using System.Net;

namespace SharpShare.Network.Nat;

/// <summary>
/// Result of network setup attempt. Contains the addresses to share with the peer.
/// </summary>
public sealed class NetworkSetupResult
{
    public IPAddress? PublicIp { get; init; }
    public IPAddress? LocalIp { get; init; }
    public int Port { get; init; }
    public bool PortForwardingSucceeded { get; init; }
    public string? PortForwardingMethod { get; init; }
    public string? PortForwardingError { get; init; }
    public string? IpDiscoveryMethod { get; init; }
    public bool FirewallRuleAdded { get; init; }
    public bool IsDoubleNat { get; init; }

    /// <summary>
    /// The address string to tell the other person to use.
    /// </summary>
    public string ConnectionAddress
    {
        get
        {
            if (PublicIp != null)
            {
                return $"{PublicIp}:{Port}";
            }

            if (LocalIp != null)
            {
                return $"{LocalIp}:{Port}";
            }

            return $"localhost:{Port}";
        }
    }

    /// <summary>
    /// Human-readable summary of the network status for the UI.
    /// </summary>
    public string NetworkStatusSummary
    {
        get
        {
            if (PublicIp != null && PortForwardingSucceeded && IsDoubleNat)
            {
                return $"Double NAT detected — port forwarded on inner router via {PortForwardingMethod}, " +
                       "but outer router may also need manual port forwarding";
            }

            if (PublicIp != null && PortForwardingSucceeded)
            {
                return $"Port forwarded automatically via {PortForwardingMethod} — ready to share";
            }

            if (PublicIp != null)
            {
                return "Public IP detected — ensure port forwarding is configured on your router";
            }

            return "Could not detect public IP — sharing may only work on local network";
        }
    }

    // Keep backward compat with existing UI code
    public bool UpnpSucceeded => PortForwardingSucceeded;
}

/// <summary>
/// Coordinates NAT traversal: discovers public IP, sets up port forwarding (UPnP → NAT-PMP fallback), and provides
/// connection details to share with the peer.
/// </summary>
public sealed class NetworkSetup : IDisposable
{
    // RFC 1918 + RFC 6598 (CGNAT) + link-local + loopback
    private static readonly (uint Network, uint Mask)[] PrivateRanges =
    [
        (0x0A000000, 0xFF000000), // 10.0.0.0/8
        (0xAC100000, 0xFFF00000), // 172.16.0.0/12
        (0xC0A80000, 0xFFFF0000), // 192.168.0.0/16
        (0x64400000, 0xFFC00000), // 100.64.0.0/10  (CGNAT / RFC 6598)
        (0xA9FE0000, 0xFFFF0000), // 169.254.0.0/16 (link-local)
        (0x7F000000, 0xFF000000), // 127.0.0.0/8    (loopback)
    ];

    /// <summary>
    /// Returns true if the address is private, link-local, loopback, or CGNAT — i.e. NOT routable on the public
    /// internet. Used to detect double-NAT situations where UPnP reports a private "external" IP.
    /// </summary>
    internal static bool IsPrivateOrReservedIp(IPAddress address)
    {
        if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            return false; // IPv6 not handled here; treat as non-private

#pragma warning disable CS0618 // Address property is obsolete but fine for IPv4
        uint ip = (uint)IPAddress.HostToNetworkOrder((int)address.Address);
#pragma warning restore CS0618

        foreach (var (network, mask) in PrivateRanges)
        {
            if ((ip & mask) == network)
                return true;
        }

        return false;
    }

    private const string FirewallRuleName = "SharpShare";

    private UpnpPortMapper? upnpMapper;
    private int? mappedExternalPort;
    private IPAddress? natPmpGateway;
    private int? natPmpMappedPort;
    private bool firewallRuleAdded;
    private bool disposed;

    /// <summary>
    /// Performs network setup: attempts port forwarding via UPnP then NAT-PMP, discovers public IP, and adds Windows
    /// Firewall rule.
    /// </summary>
    public async Task<NetworkSetupResult> SetupAsync(
        int listenPort, CancellationToken cancellationToken = default)
    {
        RollingFileLogger.Log(LogLevel.Info, $"NetworkSetup: Starting for port {listenPort}");

        IPAddress? localIp = UpnpPortMapper.GetLocalIpAddress();
        IPAddress? publicIp = null;
        bool portForwardingSucceeded = false;
        string? portForwardingMethod = null;
        string? portForwardingError = null;
        string? ipDiscoveryMethod = null;
        bool doubleNatDetected = false;

        // --- Step 1: Try UPnP port forwarding ---
        upnpMapper = new UpnpPortMapper();
        try
        {
            bool discovered = await upnpMapper.DiscoverAsync(cancellationToken);
            if (discovered)
            {
                portForwardingSucceeded = await upnpMapper.AddPortMappingAsync(
                    listenPort, listenPort,
                    cancellationToken: cancellationToken);

                if (portForwardingSucceeded)
                {
                    mappedExternalPort = listenPort;
                    portForwardingMethod = "UPnP";
                    var upnpReportedIp = await upnpMapper.GetExternalIpAsync(cancellationToken);
                    if (upnpReportedIp != null)
                    {
                        if (IsPrivateOrReservedIp(upnpReportedIp))
                        {
                            doubleNatDetected = true;
                            RollingFileLogger.Log(LogLevel.Warning,
                                $"UPnP: External IP {upnpReportedIp} is private — double NAT detected, " +
                                "will discover real public IP via STUN/HTTP");
                        }
                        else
                        {
                            publicIp = upnpReportedIp;
                            ipDiscoveryMethod = "UPnP";
                        }
                    }

                    RollingFileLogger.Log(LogLevel.Info, "UPnP: Port mapping created successfully");
                }
                else
                {
                    portForwardingError = "UPnP gateway found but port mapping failed";
                    RollingFileLogger.Log(LogLevel.Warning, portForwardingError);
                }
            }
            else
            {
                portForwardingError = "No UPnP gateway found";
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            portForwardingError = $"UPnP error: {ex.Message}";
            RollingFileLogger.Log(LogLevel.Warning, portForwardingError);
        }

        // --- Step 2: If UPnP failed, try NAT-PMP ---
        if (!portForwardingSucceeded)
        {
            try
            {
                var gateway = NatPmpClient.GetDefaultGateway();
                if (gateway != null)
                {
                    RollingFileLogger.Log(LogLevel.Info,
                        $"NAT-PMP: Trying port mapping via gateway {gateway}");

                    int mappedPort = await NatPmpClient.MapPortAsync(
                        gateway, listenPort, listenPort,
                        cancellationToken: cancellationToken);

                    if (mappedPort > 0)
                    {
                        portForwardingSucceeded = true;
                        portForwardingMethod = "NAT-PMP";
                        natPmpGateway = gateway;
                        natPmpMappedPort = listenPort;

                        var natPmpReportedIp = await NatPmpClient.GetExternalAddressAsync(
                            gateway, cancellationToken);
                        if (natPmpReportedIp != null)
                        {
                            if (IsPrivateOrReservedIp(natPmpReportedIp))
                            {
                                doubleNatDetected = true;
                                RollingFileLogger.Log(LogLevel.Warning,
                                    $"NAT-PMP: External IP {natPmpReportedIp} is private — double NAT detected");
                            }
                            else
                            {
                                publicIp = natPmpReportedIp;
                                ipDiscoveryMethod = "NAT-PMP";
                            }
                        }

                        RollingFileLogger.Log(LogLevel.Info,
                            $"NAT-PMP: Port mapping created (external port {mappedPort})");
                    }
                    else
                    {
                        RollingFileLogger.Log(LogLevel.Info, "NAT-PMP: Gateway did not respond or denied mapping");
                    }
                }
                else
                {
                    RollingFileLogger.Log(LogLevel.Info, "NAT-PMP: No default gateway found");
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                RollingFileLogger.Log(LogLevel.Warning, $"NAT-PMP: Error: {ex.Message}");
            }
        }

        // --- Step 3: Discover public IP if not already known ---
        if (publicIp == null)
        {
            try
            {
                publicIp = await PublicIpDiscovery.DiscoverAsync(
                    timeout: TimeSpan.FromSeconds(10), cancellationToken: cancellationToken);
                if (publicIp != null)
                {
                    ipDiscoveryMethod = "STUN/HTTP";
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                RollingFileLogger.Log(LogLevel.Warning, $"IP discovery failed: {ex.Message}");
            }
        }

        // --- Step 4: Add Windows Firewall rule (best effort, needs admin) ---
        bool firewallAdded = TryAddFirewallRule(listenPort);

        var result = new NetworkSetupResult
        {
            PublicIp = publicIp,
            LocalIp = localIp,
            Port = listenPort,
            PortForwardingSucceeded = portForwardingSucceeded,
            PortForwardingMethod = portForwardingMethod,
            PortForwardingError = portForwardingError,
            IpDiscoveryMethod = ipDiscoveryMethod,
            FirewallRuleAdded = firewallAdded,
            IsDoubleNat = doubleNatDetected,
        };

        RollingFileLogger.Log(LogLevel.Info,
            $"NetworkSetup: Complete. Address: {result.ConnectionAddress}, " +
            $"PortForwarding: {(portForwardingSucceeded ? portForwardingMethod : "none")}, " +
            $"IPMethod: {ipDiscoveryMethod ?? "none"}, " +
            $"DoubleNAT: {doubleNatDetected}, " +
            $"Firewall: {(firewallAdded ? "rule added" : "not modified")}");

        return result;
    }

    /// <summary>
    /// Cleans up port mappings and firewall rules on shutdown.
    /// </summary>
    public async Task CleanupAsync(CancellationToken cancellationToken = default)
    {
        // Remove UPnP mapping
        if (upnpMapper != null && mappedExternalPort.HasValue)
        {
            try
            {
                await upnpMapper.RemovePortMappingAsync(mappedExternalPort.Value, cancellationToken);
                RollingFileLogger.Log(LogLevel.Info,
                    $"NetworkSetup: Removed UPnP mapping for port {mappedExternalPort.Value}");
            }
            catch (Exception ex)
            {
                RollingFileLogger.Log(LogLevel.Warning,
                    $"NetworkSetup: Failed to remove UPnP mapping: {ex.Message}");
            }
        }

        // Remove NAT-PMP mapping
        if (natPmpGateway != null && natPmpMappedPort.HasValue)
        {
            try
            {
                await NatPmpClient.RemovePortMappingAsync(
                    natPmpGateway, natPmpMappedPort.Value, cancellationToken);
                RollingFileLogger.Log(LogLevel.Info,
                    $"NetworkSetup: Removed NAT-PMP mapping for port {natPmpMappedPort.Value}");
            }
            catch (Exception ex)
            {
                RollingFileLogger.Log(LogLevel.Warning,
                    $"NetworkSetup: Failed to remove NAT-PMP mapping: {ex.Message}");
            }
        }

        // Remove firewall rule
        if (firewallRuleAdded)
        {
            TryRemoveFirewallRule();
        }
    }

    /// <summary>
    /// Attempts to add a Windows Firewall rule allowing incoming TCP on the listen port. Requires administrator
    /// privileges — fails silently without them.
    /// </summary>
    private bool TryAddFirewallRule(int port)
    {
        try
        {
            // First remove any stale rule with the same name
            RunNetsh($"advfirewall firewall delete rule name=\"{FirewallRuleName}\"");

            int exitCode = RunNetsh(
                $"advfirewall firewall add rule name=\"{FirewallRuleName}\" " +
                $"dir=in action=allow protocol=tcp localport={port} " +
                $"profile=any enable=yes");

            if (exitCode == 0)
            {
                firewallRuleAdded = true;
                RollingFileLogger.Log(LogLevel.Info,
                    $"Firewall: Added rule '{FirewallRuleName}' for TCP port {port}");
                return true;
            }

            RollingFileLogger.Log(LogLevel.Info,
                "Firewall: Could not add rule (may need administrator privileges)");
        }
        catch (Exception ex)
        {
            RollingFileLogger.Log(LogLevel.Debug, $"Firewall: TryAddRule failed: {ex.Message}");
        }

        return false;
    }

    private void TryRemoveFirewallRule()
    {
        try
        {
            RunNetsh($"advfirewall firewall delete rule name=\"{FirewallRuleName}\"");
            RollingFileLogger.Log(LogLevel.Info, "Firewall: Removed SharpShare rule");
        }
        catch (Exception ex)
        {
            RollingFileLogger.Log(LogLevel.Debug, $"Firewall: TryRemoveRule failed: {ex.Message}");
        }
    }

    private static int RunNetsh(string arguments)
    {
        var psi = new ProcessStartInfo("netsh", arguments)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        using var process = Process.Start(psi);
        process?.WaitForExit(5000);
        return process?.ExitCode ?? -1;
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        upnpMapper?.Dispose();
    }
}
