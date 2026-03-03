using SharpShare.Storage;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Xml.Linq;

namespace SharpShare.Network.Nat;

/// <summary>
/// Lightweight UPnP client for automatic port forwarding. Discovers IGD (Internet Gateway Device) via SSDP, then uses
/// SOAP to add/remove port mappings and query the external IP address.
/// </summary>
public sealed class UpnpPortMapper : IDisposable
{
    private static readonly TimeSpan DiscoveryTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan HttpTimeout = TimeSpan.FromSeconds(10);

    private readonly HttpClient httpClient;
    private string? controlUrl;
    private string? serviceType;
    private bool disposed;

    public bool IsAvailable => controlUrl != null;

    public UpnpPortMapper()
    {
        httpClient = new HttpClient { Timeout = HttpTimeout };
    }

    /// <summary>
    /// Discovers a UPnP Internet Gateway Device on the local network via SSDP. Returns true if a compatible gateway was
    /// found.
    /// </summary>
    public async Task<bool> DiscoverAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await SendSsdpAndParseAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            RollingFileLogger.Log(LogLevel.Warning, $"UPnP: Discovery failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Adds a TCP port mapping on the gateway. The mapping will forward external traffic on the specified port to this
    /// machine.
    /// </summary>
    public async Task<bool> AddPortMappingAsync(
        int externalPort, int internalPort,
        string description = "SharpShare",
        int leaseDurationSeconds = 3600,
        CancellationToken cancellationToken = default)
    {
        if (controlUrl == null || serviceType == null)
        {
            return false;
        }

        string localIp = GetLocalIpAddress()?.ToString() ?? "0.0.0.0";

        string soapBody = $"""
            <?xml version="1.0"?>
            <s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/"
                        s:encodingStyle="http://schemas.xmlsoap.org/soap/encoding/">
              <s:Body>
                <u:AddPortMapping xmlns:u="{serviceType}">
                  <NewRemoteHost></NewRemoteHost>
                  <NewExternalPort>{externalPort}</NewExternalPort>
                  <NewProtocol>TCP</NewProtocol>
                  <NewInternalPort>{internalPort}</NewInternalPort>
                  <NewInternalClient>{localIp}</NewInternalClient>
                  <NewEnabled>1</NewEnabled>
                  <NewPortMappingDescription>{description}</NewPortMappingDescription>
                  <NewLeaseDuration>{leaseDurationSeconds}</NewLeaseDuration>
                </u:AddPortMapping>
              </s:Body>
            </s:Envelope>
            """;

        try
        {
            bool success = await SendSoapRequestAsync(
                "AddPortMapping", soapBody, cancellationToken);

            if (success)
            {
                RollingFileLogger.Log(LogLevel.Info,
                    $"UPnP: Added port mapping {externalPort} → {localIp}:{internalPort}");
            }
            return success;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            RollingFileLogger.Log(LogLevel.Warning, $"UPnP: AddPortMapping failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Removes a previously added port mapping.
    /// </summary>
    public async Task<bool> RemovePortMappingAsync(
        int externalPort, CancellationToken cancellationToken = default)
    {
        if (controlUrl == null || serviceType == null)
        {
            return false;
        }

        string soapBody = $"""
            <?xml version="1.0"?>
            <s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/"
                        s:encodingStyle="http://schemas.xmlsoap.org/soap/encoding/">
              <s:Body>
                <u:DeletePortMapping xmlns:u="{serviceType}">
                  <NewRemoteHost></NewRemoteHost>
                  <NewExternalPort>{externalPort}</NewExternalPort>
                  <NewProtocol>TCP</NewProtocol>
                </u:DeletePortMapping>
              </s:Body>
            </s:Envelope>
            """;

        try
        {
            bool success = await SendSoapRequestAsync(
                "DeletePortMapping", soapBody, cancellationToken);

            if (success)
            {
                RollingFileLogger.Log(LogLevel.Info,
                    $"UPnP: Removed port mapping for port {externalPort}");
            }
            return success;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            RollingFileLogger.Log(LogLevel.Warning, $"UPnP: RemovePortMapping failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Queries the gateway for its external (public) IP address.
    /// </summary>
    public async Task<IPAddress?> GetExternalIpAsync(CancellationToken cancellationToken = default)
    {
        if (controlUrl == null || serviceType == null)
        {
            return null;
        }

        string soapBody = $"""
            <?xml version="1.0"?>
            <s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/"
                        s:encodingStyle="http://schemas.xmlsoap.org/soap/encoding/">
              <s:Body>
                <u:GetExternalIPAddress xmlns:u="{serviceType}"/>
              </s:Body>
            </s:Envelope>
            """;

        try
        {
            string? responseBody = await SendSoapRequestWithResponseAsync(
                "GetExternalIPAddress", soapBody, cancellationToken);

            if (responseBody == null)
            {
                return null;
            }

            var doc = XDocument.Parse(responseBody);
            string? ipString = doc.Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "NewExternalIPAddress")?.Value;

            if (ipString != null && IPAddress.TryParse(ipString, out var ip))
            {
                RollingFileLogger.Log(LogLevel.Info, $"UPnP: External IP is {ip}");
                return ip;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            RollingFileLogger.Log(LogLevel.Warning, $"UPnP: GetExternalIP failed: {ex.Message}");
        }

        return null;
    }

    // --- SSDP Discovery ---

    /// <summary>
    /// Sends SSDP M-SEARCH requests and tries each responding device's description until a WANIPConnection service is
    /// found. Returns true if found.
    /// </summary>
    private async Task<bool> SendSsdpAndParseAsync(CancellationToken cancellationToken)
    {
        const string ssdpMulticast = "239.255.255.250";
        const int ssdpPort = 1900;

        string[] searchTargets =[ "urn:schemas-upnp-org:device:InternetGatewayDevice:1", "urn:schemas-upnp-org:device:InternetGatewayDevice:2", "urn:schemas-upnp-org:service:WANIPConnection:1", "urn:schemas-upnp-org:service:WANIPConnection:2", "upnp:rootdevice", ];

        using var udpClient = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
        udpClient.Client.SetSocketOption(
            SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 4);

        var multicastEndpoint = new IPEndPoint(IPAddress.Parse(ssdpMulticast), ssdpPort);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(DiscoveryTimeout);

        // Send all M-SEARCH requests with small delays for reliability
        foreach (string st in searchTargets)
        {
            string request = $"M-SEARCH * HTTP/1.1\r\n" +
                           $"HOST: {ssdpMulticast}:{ssdpPort}\r\n" +
                           $"MAN: \"ssdp:discover\"\r\n" +
                           $"MX: 3\r\n" +
                           $"ST: {st}\r\n\r\n";

            byte[] requestBytes = Encoding.ASCII.GetBytes(request);
            await udpClient.SendAsync(requestBytes, multicastEndpoint, cts.Token);
            await Task.Delay(100, cts.Token);
        }

        RollingFileLogger.Log(LogLevel.Info,
            $"UPnP: Sent {searchTargets.Length} SSDP M-SEARCH requests (multicast)");

        // Also try unicast directly to the default gateway
        var gateway = NatPmpClient.GetDefaultGateway();
        if (gateway != null)
        {
            var gatewayEndpoint = new IPEndPoint(gateway, ssdpPort);
            string unicastRequest = $"M-SEARCH * HTTP/1.1\r\n" +
                                  $"HOST: {gateway}:{ssdpPort}\r\n" +
                                  $"MAN: \"ssdp:discover\"\r\n" +
                                  $"MX: 3\r\n" +
                                  $"ST: upnp:rootdevice\r\n\r\n";
            await udpClient.SendAsync(
                Encoding.ASCII.GetBytes(unicastRequest), gatewayEndpoint, cts.Token);

            RollingFileLogger.Log(LogLevel.Info,
                $"UPnP: Also sent unicast M-SEARCH to gateway {gateway}");
        }

        // Process responses — try each unique LOCATION until we find a gateway
        HashSet<string> triedLocations = new(StringComparer.OrdinalIgnoreCase);
        try
        {
            while (!cts.Token.IsCancellationRequested)
            {
                var result = await udpClient.ReceiveAsync(cts.Token);
                string response = Encoding.ASCII.GetString(result.Buffer);

                string? location = ExtractHeader(response, "LOCATION");
                if (location == null || !triedLocations.Add(location))
                {
                    continue;
                }

                RollingFileLogger.Log(LogLevel.Info,
                    $"UPnP: SSDP response from {result.RemoteEndPoint}, location: {location}");

                try
                {
                    if (await ParseDeviceDescriptionAsync(location, cts.Token))
                    {
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    RollingFileLogger.Log(LogLevel.Debug,
                        $"UPnP: Failed to parse device at {location}: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException)
        {
        }

        RollingFileLogger.Log(LogLevel.Info,
            $"UPnP: No gateway device found via SSDP (tried {triedLocations.Count} device(s))");
        return false;
    }

    private async Task<bool> ParseDeviceDescriptionAsync(string locationUrl, CancellationToken cancellationToken)
    {
        string xml = await httpClient.GetStringAsync(locationUrl, cancellationToken);
        var doc = XDocument.Parse(xml);

        XNamespace ns = "urn:schemas-upnp-org:device-1-0";

        // Look for WANIPConnection service
        foreach (var service in doc.Descendants(ns + "service"))
        {
            string? svcType = service.Element(ns + "serviceType")?.Value;
            string? ctrlUrl = service.Element(ns + "controlURL")?.Value;

            if (svcType != null && ctrlUrl != null &&
                svcType.Contains("WANIPConnection"))
            {
                serviceType = svcType;

                // controlURL might be relative
                if (ctrlUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                {
                    controlUrl = ctrlUrl;
                }
                else
                {
                    var baseUri = new Uri(locationUrl);
                    controlUrl = new Uri(baseUri, ctrlUrl).ToString();
                }

                RollingFileLogger.Log(LogLevel.Info,
                    $"UPnP: Found service {svcType} at {controlUrl}");
                return true;
            }
        }

        RollingFileLogger.Log(LogLevel.Info, "UPnP: No WANIPConnection service found in device description");
        return false;
    }

    // --- SOAP Calls ---

    private async Task<bool> SendSoapRequestAsync(
        string action, string soapBody, CancellationToken cancellationToken)
    {
        var response = await SendSoapHttpRequestAsync(action, soapBody, cancellationToken);
        return response.IsSuccessStatusCode;
    }

    private async Task<string?> SendSoapRequestWithResponseAsync(
        string action, string soapBody, CancellationToken cancellationToken)
    {
        var response = await SendSoapHttpRequestAsync(action, soapBody, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    private async Task<HttpResponseMessage> SendSoapHttpRequestAsync(
        string action, string soapBody, CancellationToken cancellationToken)
    {
        var content = new StringContent(soapBody, Encoding.UTF8, "text/xml");
        content.Headers.Add("SOAPAction", $"\"{serviceType}#{action}\"");

        return await httpClient.PostAsync(controlUrl, content, cancellationToken);
    }

    // --- Helpers ---

    private static string? ExtractHeader(string httpResponse, string headerName)
    {
        foreach (string line in httpResponse.Split('\n'))
        {
            string trimmed = line.Trim();
            if (trimmed.StartsWith($"{headerName}:", StringComparison.OrdinalIgnoreCase))
            {
                return trimmed[(headerName.Length + 1)..].Trim();
            }
        }
        return null;
    }

    /// <summary>
    /// Gets the local IP address used for LAN communication.
    /// </summary>
    public static IPAddress? GetLocalIpAddress()
    {
        try
        {
            // Connect to a remote address to discover the local interface used
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect("8.8.8.8", 80);
            return ((IPEndPoint)socket.LocalEndPoint!).Address;
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        httpClient.Dispose();
    }
}
