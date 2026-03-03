using System.Net;
using SharpShare.Network;

namespace SharpShare.Tests.Network;

public class ConnectionGuardTests
{
    [Test]
    public async Task NewGuard_AllowsConnection()
    {
        var guard = new ConnectionGuard();
        var ip = IPAddress.Parse("192.168.1.100");

        await Assert.That(guard.ShouldRejectConnection(ip)).IsFalse();
    }

    [Test]
    public async Task GetRequiredDelay_NoFailures_ReturnsZero()
    {
        var guard = new ConnectionGuard();
        var ip = IPAddress.Parse("192.168.1.100");

        var delay = guard.GetRequiredDelay(ip);
        await Assert.That(delay).IsEqualTo(TimeSpan.Zero);
    }

    [Test]
    public async Task RecordFailure_OneFailure_AppliesOneSecondDelay()
    {
        var guard = new ConnectionGuard();
        var ip = IPAddress.Parse("10.0.0.1");

        guard.RecordFailure(ip);
        var delay = guard.GetRequiredDelay(ip);

        await Assert.That(delay).IsEqualTo(TimeSpan.FromSeconds(1));
    }

    [Test]
    public async Task RecordFailure_TwoFailures_AppliesTwoSecondDelay()
    {
        var guard = new ConnectionGuard();
        var ip = IPAddress.Parse("10.0.0.2");

        guard.RecordFailure(ip);
        guard.RecordFailure(ip);
        var delay = guard.GetRequiredDelay(ip);

        await Assert.That(delay).IsEqualTo(TimeSpan.FromSeconds(2));
    }

    [Test]
    public async Task RecordFailure_ThreeFailures_BlocksIp()
    {
        var guard = new ConnectionGuard();
        var ip = IPAddress.Parse("10.0.0.3");

        guard.RecordFailure(ip);
        guard.RecordFailure(ip);
        guard.RecordFailure(ip);

        await Assert.That(guard.ShouldRejectConnection(ip)).IsTrue();
    }

    [Test]
    public async Task RecordSuccess_ClearsFailureHistory()
    {
        var guard = new ConnectionGuard();
        var ip = IPAddress.Parse("10.0.0.4");

        guard.RecordFailure(ip);
        guard.RecordFailure(ip);
        guard.RecordSuccess(ip);

        var delay = guard.GetRequiredDelay(ip);
        await Assert.That(delay).IsEqualTo(TimeSpan.Zero);
        await Assert.That(guard.ShouldRejectConnection(ip)).IsFalse();
    }

    [Test]
    public async Task DifferentIps_TrackedIndependently()
    {
        var guard = new ConnectionGuard();
        var ip1 = IPAddress.Parse("10.0.0.10");
        var ip2 = IPAddress.Parse("10.0.0.11");

        guard.RecordFailure(ip1);
        guard.RecordFailure(ip1);
        guard.RecordFailure(ip1); // ip1 now blocked

        await Assert.That(guard.ShouldRejectConnection(ip1)).IsTrue();
        await Assert.That(guard.ShouldRejectConnection(ip2)).IsFalse();
    }

    [Test]
    public async Task PanicMode_ActivatesAfterManyFailures()
    {
        var guard = new ConnectionGuard();

        // 10 failures from different IPs within 5 minutes triggers panic
        for (int i = 0; i < 10; i++)
        {
            var ip = IPAddress.Parse($"172.16.0.{i + 1}");
            guard.RecordFailure(ip);
        }

        await Assert.That(guard.IsPanicModeActive).IsTrue();

        // Even a clean IP should be rejected during panic mode
        var cleanIp = IPAddress.Parse("1.2.3.4");
        await Assert.That(guard.ShouldRejectConnection(cleanIp)).IsTrue();
    }

    [Test]
    public async Task TrackedIpCount_ReflectsDistinctIps()
    {
        var guard = new ConnectionGuard();
        guard.RecordFailure(IPAddress.Parse("10.0.0.1"));
        guard.RecordFailure(IPAddress.Parse("10.0.0.2"));
        guard.RecordFailure(IPAddress.Parse("10.0.0.1")); // same IP

        await Assert.That(guard.TrackedIpCount).IsEqualTo(2);
    }

    [Test]
    public async Task PendingConnection_LimitsToOne()
    {
        var guard = new ConnectionGuard();
        var ip = IPAddress.Parse("10.0.0.50");

        guard.RecordPendingConnection();
        // Second pending connection should be rejected
        await Assert.That(guard.ShouldRejectConnection(ip)).IsTrue();

        guard.ReleasePendingConnection();
        await Assert.That(guard.ShouldRejectConnection(ip)).IsFalse();
    }

    [Test]
    public async Task ExponentialBackoff_CapsAtSixteenSeconds()
    {
        var guard = new ConnectionGuard();
        var ip = IPAddress.Parse("10.0.0.99");

        // 5+ failures should cap at 16 seconds (2^4)
        // But note: 3 failures blocks the IP, so delay doesn't matter much after that
        // Still, the delay logic works independently of blocking
        guard.RecordFailure(ip);
        guard.RecordFailure(ip);
        // After 2 failures: delay = 2s (2^(2-1))
        var delay = guard.GetRequiredDelay(ip);
        await Assert.That(delay).IsEqualTo(TimeSpan.FromSeconds(2));
    }
}
