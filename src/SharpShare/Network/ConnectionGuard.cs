using System.Collections.Concurrent;
using System.Net;
using SharpShare.Storage;

namespace SharpShare.Network;

/// <summary>
/// Protects the listener from brute force attacks, dictionary attacks, and connection flooding.
/// Single-peer design: only one authenticated connection allowed at a time.
/// </summary>
public sealed class ConnectionGuard
{
    private const int MaxFailuresPerIp = 3;
    private const int IpBlockMinutes = 15;
    private const int PanicModeMinutes = 5;
    private const int PanicModeThreshold = 10;
    private const int PanicModeWindowMinutes = 5;
    private const int MaxPendingConnections = 1;
    private const int MaxNewConnectionsPerSecond = 5;

    private readonly ConcurrentDictionary<IPAddress, AttemptRecord> attemptsByIp = new();
    private DateTime panicModeUntilUtc = DateTime.MinValue;
    private int totalRecentFailures;
    private DateTime recentFailuresWindowStartUtc = DateTime.UtcNow;
    private int pendingConnectionCount;
    private int connectionsThisSecond;
    private DateTime currentSecondUtc = DateTime.UtcNow;
    private readonly object globalLock = new();

    /// <summary>
    /// Returns true if this connection should be rejected immediately (no response sent).
    /// </summary>
    public bool ShouldRejectConnection(IPAddress remoteIp)
    {
        var now = DateTime.UtcNow;

        // Panic mode — reject everything
        if (now < panicModeUntilUtc)
        {
            RollingFileLogger.Log(LogLevel.Warning, $"Connection from {remoteIp} rejected: panic mode active");
            return true;
        }

        // IP-specific block
        if (attemptsByIp.TryGetValue(remoteIp, out var record) && now < record.BlockedUntilUtc)
        {
            RollingFileLogger.Log(LogLevel.Warning, $"Connection from {remoteIp} rejected: IP blocked until {record.BlockedUntilUtc:HH:mm:ss}");
            return true;
        }

        // Global rate limit: max N connections per second
        lock (globalLock)
        {
            if ((now - currentSecondUtc).TotalSeconds >= 1.0)
            {
                currentSecondUtc = now;
                connectionsThisSecond = 0;
            }
            connectionsThisSecond++;

            if (connectionsThisSecond > MaxNewConnectionsPerSecond)
            {
                RollingFileLogger.Log(LogLevel.Warning, $"Connection from {remoteIp} rejected: global rate limit exceeded");
                return true;
            }

            // Max pending (unauthenticated) connections
            if (pendingConnectionCount >= MaxPendingConnections)
            {
                RollingFileLogger.Log(LogLevel.Warning, $"Connection from {remoteIp} rejected: max pending connections reached");
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Returns the delay to impose before sending the auth challenge to this IP.
    /// Exponential backoff: 0s, 1s, 2s, 4s, 8s, 16s based on past failures.
    /// </summary>
    public TimeSpan GetRequiredDelay(IPAddress remoteIp)
    {
        if (!attemptsByIp.TryGetValue(remoteIp, out var record) || record.FailureCount == 0)
            return TimeSpan.Zero;

        int delaySeconds = 1 << Math.Min(record.FailureCount - 1, 4); // 1, 2, 4, 8, 16
        return TimeSpan.FromSeconds(delaySeconds);
    }

    /// <summary>
    /// Call when a connection attempt begins authentication.
    /// </summary>
    public void RecordPendingConnection()
    {
        Interlocked.Increment(ref pendingConnectionCount);
    }

    /// <summary>
    /// Call when a connection attempt finishes authentication (success or failure) or is dropped.
    /// </summary>
    public void ReleasePendingConnection()
    {
        Interlocked.Decrement(ref pendingConnectionCount);
    }

    /// <summary>
    /// Records a failed authentication attempt from the given IP.
    /// May trigger IP block or panic mode.
    /// </summary>
    public void RecordFailure(IPAddress remoteIp)
    {
        var now = DateTime.UtcNow;

        var record = attemptsByIp.GetOrAdd(remoteIp, _ => new AttemptRecord());
        lock (record)
        {
            record.FailureCount++;
            record.LastAttemptUtc = now;

            if (record.FailureCount >= MaxFailuresPerIp)
            {
                record.BlockedUntilUtc = now.AddMinutes(IpBlockMinutes);
                RollingFileLogger.Log(LogLevel.Warning,
                    $"IP {remoteIp} blocked for {IpBlockMinutes} minutes after {record.FailureCount} failed attempts");
            }
        }

        // Check for panic mode
        lock (globalLock)
        {
            if ((now - recentFailuresWindowStartUtc).TotalMinutes > PanicModeWindowMinutes)
            {
                recentFailuresWindowStartUtc = now;
                totalRecentFailures = 0;
            }
            totalRecentFailures++;

            if (totalRecentFailures >= PanicModeThreshold)
            {
                panicModeUntilUtc = now.AddMinutes(PanicModeMinutes);
                totalRecentFailures = 0;
                RollingFileLogger.Log(LogLevel.Error,
                    $"PANIC MODE activated for {PanicModeMinutes} minutes after {PanicModeThreshold} total failures");
            }
        }
    }

    /// <summary>
    /// Records a successful authentication from the given IP. Clears failure history for that IP.
    /// </summary>
    public void RecordSuccess(IPAddress remoteIp)
    {
        if (attemptsByIp.TryGetValue(remoteIp, out var record))
        {
            lock (record)
            {
                record.FailureCount = 0;
                record.BlockedUntilUtc = DateTime.MinValue;
            }
        }
    }

    /// <summary>
    /// Removes expired entries from the attempt tracking dictionary.
    /// Should be called periodically (e.g., every 5 minutes).
    /// </summary>
    public void CleanupExpiredEntries()
    {
        var now = DateTime.UtcNow;
        var expiredIps = new List<IPAddress>();

        foreach (var kvp in attemptsByIp)
        {
            var record = kvp.Value;
            lock (record)
            {
                // Remove entries where the block has expired AND there have been no recent attempts
                if (now > record.BlockedUntilUtc &&
                    (now - record.LastAttemptUtc).TotalMinutes > IpBlockMinutes)
                {
                    expiredIps.Add(kvp.Key);
                }
            }
        }

        foreach (var ip in expiredIps)
            attemptsByIp.TryRemove(ip, out _);
    }

    /// <summary>True if panic mode is currently active.</summary>
    public bool IsPanicModeActive => DateTime.UtcNow < panicModeUntilUtc;

    /// <summary>Number of tracked IP addresses.</summary>
    public int TrackedIpCount => attemptsByIp.Count;

    private sealed class AttemptRecord
    {
        public int FailureCount;
        public DateTime LastAttemptUtc;
        public DateTime BlockedUntilUtc = DateTime.MinValue;
    }
}
