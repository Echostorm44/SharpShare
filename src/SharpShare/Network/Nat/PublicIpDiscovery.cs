using SharpShare.Storage;
using System.Net;

namespace SharpShare.Network.Nat;

/// <summary>
/// Discovers the public IP address using multiple methods in order of preference: 1. STUN (UDP — fast, no third-party
/// dependency) 2. HTTPS API services (reliable fallback — HTTP is rarely blocked)
/// </summary>
public static class PublicIpDiscovery
{
    private static readonly string[] HttpIpServices =[ "https://api.ipify.org", "https://icanhazip.com", "https://ifconfig.me/ip", "https://checkip.amazonaws.com", ];

    /// <summary>
    /// Attempts to discover the public IP using STUN first, then HTTP API fallbacks. Returns null only if all methods
    /// fail.
    /// </summary>
    public static async Task<IPAddress?> DiscoverAsync(
        TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(8);

        // Try STUN first (fast, no HTTP overhead)
        try
        {
            var stunResult = await StunClient.DiscoverPublicIpAsync(
                TimeSpan.FromSeconds(3), cancellationToken);
            if (stunResult != null)
            {
                return stunResult;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            RollingFileLogger.Log(LogLevel.Warning, $"PublicIpDiscovery: STUN failed: {ex.Message}");
        }

        // Fall back to HTTP API services
        return await DiscoverViaHttpAsync(effectiveTimeout, cancellationToken);
    }

    /// <summary>
    /// Discovers public IP via HTTPS API services. Tries multiple services in parallel, returns the first successful
    /// result.
    /// </summary>
    internal static async Task<IPAddress?> DiscoverViaHttpAsync(
        TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);

        using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("SharpShare/1.0");

        // Try all services in parallel, return first success
        var tasks = new List<Task<IPAddress?>>();
        foreach (string serviceUrl in HttpIpServices)
        {
            tasks.Add(QueryHttpServiceAsync(httpClient, serviceUrl, cts.Token));
        }

        while (tasks.Count > 0)
        {
            var completed = await Task.WhenAny(tasks);
            tasks.Remove(completed);

            try
            {
                var result = await completed;
                if (result != null)
                {
                    RollingFileLogger.Log(LogLevel.Info,
                        $"PublicIpDiscovery: Public IP is {result} (via HTTP)");
                    return result;
                }
            }
            catch
            {
                // Try next service
            }
        }

        RollingFileLogger.Log(LogLevel.Warning,
            "PublicIpDiscovery: All HTTP IP services failed");
        return null;
    }

    private static async Task<IPAddress?> QueryHttpServiceAsync(
        HttpClient httpClient, string url, CancellationToken cancellationToken)
    {
        try
        {
            string response = await httpClient.GetStringAsync(url, cancellationToken);
            string trimmed = response.Trim();

            if (IPAddress.TryParse(trimmed, out var ip))
            {
                return ip;
            }

            RollingFileLogger.Log(LogLevel.Warning,
                $"PublicIpDiscovery: Unexpected response from {url}: '{trimmed}'");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            RollingFileLogger.Log(LogLevel.Warning,
                $"PublicIpDiscovery: {url} failed: {ex.Message}");
        }

        return null;
    }
}
