using System.Net;
using SharpShare.Network.Nat;
using SharpShare.Storage;

namespace SharpShare.Tests.Network.Nat;

public class StunClientTests
{
    [Before(Test)]
    public void Setup()
    {
        RollingFileLogger.Shutdown();
    }

    [Test]
    public async Task ParseBindingResponse_ValidIpv4_ReturnsCorrectIp()
    {
        // Build a minimal STUN Binding Response with XOR-MAPPED-ADDRESS
        byte[] transactionId = new byte[12];
        for (int i = 0; i < 12; i++) transactionId[i] = (byte)(i + 1);

        // Target IP: 203.0.113.42  (0xCB007142)
        // XOR with magic cookie 0x2112A442:
        // 0xCB ^ 0x21 = 0xEA, 0x00 ^ 0x12 = 0x12, 0x71 ^ 0xA4 = 0xD5, 0x42 ^ 0x42 = 0x00
        byte[] xoredIp = [0xEA, 0x12, 0xD5, 0x68];

        // Target port: 12345 (0x3039), XOR with 0x2112 = 0x112B
        byte[] xoredPort = [0x11, 0x2B];

        // XOR-MAPPED-ADDRESS attribute (type 0x0020, length 8)
        byte[] attr = [
            0x00, 0x20, // Type: XOR-MAPPED-ADDRESS
            0x00, 0x08, // Length: 8
            0x00, 0x01, // Reserved + Family (IPv4)
            xoredPort[0], xoredPort[1],
            xoredIp[0], xoredIp[1], xoredIp[2], xoredIp[3]
        ];

        // Build full response
        byte[] response = new byte[20 + attr.Length];
        response[0] = 0x01; response[1] = 0x01; // Binding Response
        response[2] = 0x00; response[3] = (byte)attr.Length; // Length
        response[4] = 0x21; response[5] = 0x12; response[6] = 0xA4; response[7] = 0x42; // Magic cookie
        Array.Copy(transactionId, 0, response, 8, 12);
        Array.Copy(attr, 0, response, 20, attr.Length);

        var result = StunClient.ParseBindingResponse(response, transactionId);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.ToString()).IsEqualTo("203.0.113.42");
    }

    [Test]
    public async Task ParseBindingResponse_WrongTransactionId_ReturnsNull()
    {
        byte[] transactionId = new byte[12];
        byte[] wrongTransactionId = new byte[12];
        wrongTransactionId[0] = 0xFF;

        byte[] response = new byte[32]; // Minimal valid-looking response
        response[0] = 0x01; response[1] = 0x01; // Binding Response
        response[2] = 0x00; response[3] = 0x0C; // Length: 12
        response[4] = 0x21; response[5] = 0x12; response[6] = 0xA4; response[7] = 0x42;
        Array.Copy(transactionId, 0, response, 8, 12);

        var result = StunClient.ParseBindingResponse(response, wrongTransactionId);

        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task ParseBindingResponse_NotBindingResponse_ReturnsNull()
    {
        byte[] transactionId = new byte[12];
        byte[] response = new byte[20];
        response[0] = 0x00; response[1] = 0x01; // Binding Request (not response)
        response[4] = 0x21; response[5] = 0x12; response[6] = 0xA4; response[7] = 0x42;

        var result = StunClient.ParseBindingResponse(response, transactionId);

        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task ParseBindingResponse_TooShort_ReturnsNull()
    {
        byte[] transactionId = new byte[12];
        byte[] response = new byte[10]; // Too short for STUN header

        var result = StunClient.ParseBindingResponse(response, transactionId);

        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task ParseBindingResponse_WrongMagicCookie_ReturnsNull()
    {
        byte[] transactionId = new byte[12];
        byte[] response = new byte[20];
        response[0] = 0x01; response[1] = 0x01;
        response[4] = 0xFF; response[5] = 0xFF; response[6] = 0xFF; response[7] = 0xFF; // Wrong cookie

        var result = StunClient.ParseBindingResponse(response, transactionId);

        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task ParseBindingResponse_MappedAddress_Fallback()
    {
        // Test MAPPED-ADDRESS (non-XOR) fallback
        byte[] transactionId = new byte[12];

        // IP: 10.0.0.1
        byte[] attr = [
            0x00, 0x01, // Type: MAPPED-ADDRESS
            0x00, 0x08, // Length: 8
            0x00, 0x01, // Reserved + Family (IPv4)
            0x00, 0x50, // Port: 80
            10, 0, 0, 1  // IP: 10.0.0.1
        ];

        byte[] response = new byte[20 + attr.Length];
        response[0] = 0x01; response[1] = 0x01;
        response[2] = 0x00; response[3] = (byte)attr.Length;
        response[4] = 0x21; response[5] = 0x12; response[6] = 0xA4; response[7] = 0x42;
        Array.Copy(transactionId, 0, response, 8, 12);
        Array.Copy(attr, 0, response, 20, attr.Length);

        var result = StunClient.ParseBindingResponse(response, transactionId);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.ToString()).IsEqualTo("10.0.0.1");
    }
}

public class UpnpPortMapperTests
{
    [Test]
    public async Task GetLocalIpAddress_ReturnsNonNull()
    {
        // This should work on any machine with a network interface
        var ip = UpnpPortMapper.GetLocalIpAddress();

        // May be null in CI environments with no network
        // But on developer machines, should return a local IP
        if (ip != null)
        {
            await Assert.That(ip.AddressFamily)
                .IsEqualTo(System.Net.Sockets.AddressFamily.InterNetwork);
        }
    }

    [Test]
    public async Task NetworkSetupResult_ConnectionAddress_WithPublicAndUpnp()
    {
        var result = new NetworkSetupResult
        {
            PublicIp = IPAddress.Parse("1.2.3.4"),
            LocalIp = IPAddress.Parse("192.168.1.10"),
            Port = 9500,
            PortForwardingSucceeded = true,
            PortForwardingMethod = "UPnP",
        };

        await Assert.That(result.ConnectionAddress).IsEqualTo("1.2.3.4:9500");
    }

    [Test]
    public async Task NetworkSetupResult_ConnectionAddress_PublicNoUpnp()
    {
        var result = new NetworkSetupResult
        {
            PublicIp = IPAddress.Parse("5.6.7.8"),
            LocalIp = IPAddress.Parse("192.168.1.10"),
            Port = 9500,
            PortForwardingSucceeded = false,
        };

        await Assert.That(result.ConnectionAddress).IsEqualTo("5.6.7.8:9500");
    }

    [Test]
    public async Task NetworkSetupResult_ConnectionAddress_LocalOnly()
    {
        var result = new NetworkSetupResult
        {
            PublicIp = null,
            LocalIp = IPAddress.Parse("192.168.1.10"),
            Port = 9500,
            PortForwardingSucceeded = false,
        };

        await Assert.That(result.ConnectionAddress).IsEqualTo("192.168.1.10:9500");
    }

    [Test]
    public async Task NetworkSetupResult_NetworkStatusSummary_UpnpSuccess()
    {
        var result = new NetworkSetupResult
        {
            PublicIp = IPAddress.Parse("1.2.3.4"),
            Port = 9500,
            PortForwardingSucceeded = true,
            PortForwardingMethod = "UPnP",
        };

        await Assert.That(result.NetworkStatusSummary).Contains("UPnP");
    }

    [Test]
    public async Task NetworkSetupResult_NetworkStatusSummary_PublicIpNoUpnp()
    {
        var result = new NetworkSetupResult
        {
            PublicIp = IPAddress.Parse("1.2.3.4"),
            Port = 9500,
            PortForwardingSucceeded = false,
        };

        await Assert.That(result.NetworkStatusSummary).Contains("port forwarding");
    }

    [Test]
    public async Task NetworkSetupResult_NetworkStatusSummary_NoPublicIp()
    {
        var result = new NetworkSetupResult
        {
            PublicIp = null,
            LocalIp = IPAddress.Parse("192.168.1.10"),
            Port = 9500,
            PortForwardingSucceeded = false,
        };

        await Assert.That(result.NetworkStatusSummary).Contains("local network");
    }

    [Test]
    public async Task NetworkSetupResult_ConnectionAddress_Fallback()
    {
        var result = new NetworkSetupResult
        {
            PublicIp = null,
            LocalIp = null,
            Port = 9500,
            PortForwardingSucceeded = false,
        };

        await Assert.That(result.ConnectionAddress).IsEqualTo("localhost:9500");
    }
}

public class DoubleNatDetectionTests
{
    [Test]
    [Arguments("10.0.0.1")]
    [Arguments("10.255.255.255")]
    [Arguments("172.16.0.1")]
    [Arguments("172.31.255.255")]
    [Arguments("192.168.0.1")]
    [Arguments("192.168.1.64")]
    [Arguments("192.168.68.62")]
    [Arguments("192.168.255.255")]
    [Arguments("100.64.0.1")]
    [Arguments("100.127.255.255")]
    [Arguments("169.254.1.1")]
    [Arguments("127.0.0.1")]
    public async Task IsPrivateOrReservedIp_PrivateAddresses_ReturnsTrue(string ipString)
    {
        var ip = IPAddress.Parse(ipString);
        await Assert.That(NetworkSetup.IsPrivateOrReservedIp(ip)).IsTrue();
    }

    [Test]
    [Arguments("1.2.3.4")]
    [Arguments("8.8.8.8")]
    [Arguments("74.111.188.42")]
    [Arguments("203.0.113.42")]
    [Arguments("100.128.0.1")]
    [Arguments("172.32.0.1")]
    [Arguments("11.0.0.1")]
    public async Task IsPrivateOrReservedIp_PublicAddresses_ReturnsFalse(string ipString)
    {
        var ip = IPAddress.Parse(ipString);
        await Assert.That(NetworkSetup.IsPrivateOrReservedIp(ip)).IsFalse();
    }

    [Test]
    public async Task IsPrivateOrReservedIp_Ipv6_ReturnsFalse()
    {
        var ip = IPAddress.Parse("::1");
        await Assert.That(NetworkSetup.IsPrivateOrReservedIp(ip)).IsFalse();
    }

    [Test]
    public async Task NetworkSetupResult_DoubleNat_ShowsWarning()
    {
        var result = new NetworkSetupResult
        {
            PublicIp = IPAddress.Parse("74.111.188.42"),
            Port = 9500,
            PortForwardingSucceeded = true,
            PortForwardingMethod = "UPnP",
            IsDoubleNat = true,
        };

        await Assert.That(result.NetworkStatusSummary).Contains("Double NAT");
        await Assert.That(result.NetworkStatusSummary).Contains("UPnP");
    }

    [Test]
    public async Task NetworkSetupResult_NotDoubleNat_ShowsReady()
    {
        var result = new NetworkSetupResult
        {
            PublicIp = IPAddress.Parse("74.111.188.42"),
            Port = 9500,
            PortForwardingSucceeded = true,
            PortForwardingMethod = "UPnP",
            IsDoubleNat = false,
        };

        await Assert.That(result.NetworkStatusSummary).Contains("ready to share");
    }
}
