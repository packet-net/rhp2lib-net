using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Docker.DotNet.Models;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;
using Xunit;

namespace RhpV2.TestSupport;

/// <summary>
/// Two xrouter containers connected by AXUDP, running on a private
/// Docker network with static IPs that match each container's
/// <c>IPADDRESS=</c> directive.  Driving Node A's RHP and connecting
/// AX.25 to Node B's callsign exercises the full stack: RHP framing →
/// xrouter AX.25 layer 2 → AXUDP encapsulation → IP/UDP across the
/// container network → Node B's AX.25 stack → Node B's command
/// processor.
///
/// <para>
/// Requires a running Docker daemon. If the fixture can't bring both
/// containers up, the failure propagates from
/// <see cref="InitializeAsync"/> and every test in the class fails
/// loudly — there's no silent skip.
/// </para>
/// </summary>
public sealed class XRouterPairFixture : IAsyncLifetime
{
    private const string Image = XRouterImage.Name;

    // Pick a random /24 inside 10.0.0.0/8 per fixture instance — multiple
    // fixtures can run simultaneously (xunit parallelises test classes
    // by default) and each needs its own non-overlapping subnet on the
    // Docker daemon. Avoid 10.99.99.x since older versions of this code
    // used it as a hard-coded literal.
    private static readonly Random Rng = new();
    private readonly string _subnetPrefix;
    public string Subnet { get; }
    public string NodeAContainerIp { get; }
    public string NodeBContainerIp { get; }

    public XRouterPairFixture()
    {
        // 10.<rand>.<rand>.0/24 with rand chosen to avoid common
        // collision ranges (Docker default bridge is 172.17.0.0/16,
        // we stay in the 10/8 RFC1918 range).
        var b = Rng.Next(50, 250);
        var c = Rng.Next(50, 250);
        _subnetPrefix = $"10.{b}.{c}";
        Subnet = $"{_subnetPrefix}.0/24";
        NodeAContainerIp = $"{_subnetPrefix}.2";
        NodeBContainerIp = $"{_subnetPrefix}.3";
    }

    /// <summary>NODECALL of node A.</summary>
    public string NodeACallsign => "G9DUM-1";
    /// <summary>NODECALL of node B.</summary>
    public string NodeBCallsign => "G8PZT-1";
    /// <summary>CONSOLECALL of node A — used as <c>local</c> when binding.</summary>
    public string NodeAConsoleCallsign => "G9DUM";

    /// <summary>Mapped host port for Node A's RHP listener.</summary>
    public ushort NodeARhpPort { get; private set; }
    /// <summary>Mapped host port for Node B's RHP listener.</summary>
    public ushort NodeBRhpPort { get; private set; }

    public string Host => "127.0.0.1";

    private INetwork? _network;
    private IContainer? _nodeA;
    private IContainer? _nodeB;

    public async Task InitializeAsync()
    {
        try
        {
            _network = new NetworkBuilder()
                .WithName($"axudp-{Guid.NewGuid():N}")
                .WithCreateParameterModifier(p =>
                {
                    p.IPAM = new IPAM
                    {
                        Driver = "default",
                        Config = new List<IPAMConfig> { new() { Subnet = Subnet } },
                    };
                })
                .Build();
            await _network.CreateAsync().ConfigureAwait(false);

            _nodeA = BuildNode(
                nodeCall: NodeACallsign,
                consoleCall: NodeAConsoleCallsign,
                nodeAlias: "NODEA",
                chatCall: "G9DUM-8",
                chatAlias: "NACHT",
                staticIp: NodeAContainerIp,
                peerIp: NodeBContainerIp,
                udpLocal: 10093,
                udpRemote: 10094,
                peerNodeCall: NodeBCallsign,
                peerNodeAlias: "NODEB");
            _nodeB = BuildNode(
                nodeCall: NodeBCallsign,
                consoleCall: "G8PZT",
                nodeAlias: "NODEB",
                chatCall: "G8PZT-8",
                chatAlias: "NBCHT",
                staticIp: NodeBContainerIp,
                peerIp: NodeAContainerIp,
                udpLocal: 10094,
                udpRemote: 10093,
                peerNodeCall: NodeACallsign,
                peerNodeAlias: "NODEA");

            await Task.WhenAll(_nodeA.StartAsync(), _nodeB.StartAsync()).ConfigureAwait(false);

            NodeARhpPort = _nodeA.GetMappedPublicPort(9000);
            NodeBRhpPort = _nodeB.GetMappedPublicPort(9000);

            await Task.WhenAll(
                WaitForRhpAcceptingAsync(Host, NodeARhpPort, TimeSpan.FromSeconds(15)),
                WaitForRhpAcceptingAsync(Host, NodeBRhpPort, TimeSpan.FromSeconds(15)))
                .ConfigureAwait(false);
        }
        catch
        {
            await SafeDisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task DisposeAsync()
    {
        await SafeDisposeAsync().ConfigureAwait(false);
    }

    private async Task SafeDisposeAsync()
    {
        foreach (var c in new[] { _nodeA, _nodeB })
        {
            if (c is null) continue;
            try { await c.StopAsync(); } catch { }
            try { await c.DisposeAsync(); } catch { }
        }
        if (_network is not null)
        {
            try { await _network.DeleteAsync(); } catch { }
            try { await _network.DisposeAsync(); } catch { }
        }
    }

    private IContainer BuildNode(
        string nodeCall,
        string consoleCall,
        string nodeAlias,
        string chatCall,
        string chatAlias,
        string staticIp,
        string peerIp,
        int udpLocal,
        int udpRemote,
        string peerNodeCall = "",
        string peerNodeAlias = "")
    {
        var cfgText = $"""
DNS=8.8.8.8

NODECALL={nodeCall}
NODEALIAS={nodeAlias}
CONSOLECALL={consoleCall}
CHATCALL={chatCall}
CHATALIAS={chatAlias}

RHPPORT=9000
IPADDRESS={staticIp}

CTEXT
{nodeAlias}.
***

INFOTEXT
{nodeAlias}.
***

IDTEXT
!5824.22N/00515.00W {nodeAlias}
***

INTERFACE=1
	TYPE=LOOPBACK
	PROTOCOL=KISS
	MTU=256
ENDINTERFACE
PORT=1
	ID="Loopback"
	INTERFACENUM=1
ENDPORT

INTERFACE=2
	TYPE=AXUDP
	MTU=256
ENDINTERFACE
PORT=2
	ID="AXUDP partner"
	INTERFACENUM=2
	IPLINK={peerIp}
	UDPLOCAL={udpLocal}
	UDPREMOTE={udpRemote}
	FRACK=2000
	RESPTIME=200
ENDPORT
""";

        // Pre-populate XRNODES with a static, locked NetRom route to
        // the peer node. Without this, fresh xrouter takes the
        // NODESINTERVAL window (minutes) to discover the peer through
        // NODES broadcasts — way too slow for an integration test.
        // Format documented in xrouter's XRNODES(8) man page.
        var xrnodes = string.IsNullOrEmpty(peerNodeCall)
            ? string.Empty
            : $"""
ROUTE ADD {peerNodeCall} 2 200 !
NODE ADD {peerNodeAlias}:{peerNodeCall} {peerNodeCall} 2 200 !
""";

        var builder = new ContainerBuilder()
            .WithImage(Image)
            .WithNetwork(_network!)
            .WithCreateParameterModifier(p =>
            {
                p.NetworkingConfig ??= new NetworkingConfig();
                p.NetworkingConfig.EndpointsConfig ??= new Dictionary<string, EndpointSettings>();
                if (!p.NetworkingConfig.EndpointsConfig.TryGetValue(_network!.Name, out var ep))
                {
                    ep = new EndpointSettings();
                    p.NetworkingConfig.EndpointsConfig[_network.Name] = ep;
                }
                ep.IPAMConfig = new EndpointIPAMConfig { IPv4Address = staticIp };
            })
            .WithPortBinding(9000, assignRandomHostPort: true)
            .WithResourceMapping(System.Text.Encoding.UTF8.GetBytes(cfgText), "/data/XROUTER.CFG")
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilMessageIsLogged("xrouter version "));

        if (!string.IsNullOrEmpty(xrnodes))
            builder = builder.WithResourceMapping(
                System.Text.Encoding.UTF8.GetBytes(xrnodes), "/data/XRNODES");

        return builder.Build();
    }

    private static async Task WaitForRhpAcceptingAsync(string host, int port, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        Exception? last = null;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var probe = new TcpClient();
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await probe.ConnectAsync(host, port, cts.Token).ConfigureAwait(false);
                return;
            }
            catch (Exception ex)
            {
                last = ex;
                await Task.Delay(250).ConfigureAwait(false);
            }
        }
        throw new TimeoutException(
            $"RHP listener at {host}:{port} did not accept within {timeout}.", last);
    }
}

