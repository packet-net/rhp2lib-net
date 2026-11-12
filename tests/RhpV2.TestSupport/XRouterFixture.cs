using System;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Xunit;

namespace RhpV2.TestSupport;

/// <summary>
/// Spins up a single <c>ghcr.io/packethacking/xrouter</c> container per
/// test class collection and exposes its mapped TCP port for the RHP
/// server (port 9000 inside the container).
///
/// Requires a running Docker daemon. If <c>InitializeAsync</c> fails to
/// start the container, the failure propagates and every test in the
/// class fails loudly — there's no silent skip.
/// </summary>
public sealed class XRouterFixture : IAsyncLifetime
{
    private const string Image = XRouterImage.Name;

    /// <summary>
    /// Minimal loopback config — RHPPORT explicitly enabled so the
    /// xrouter binds an RHP listener on the Linux stack. No radio.
    /// </summary>
    private const string XRouterCfg = """
        # XROUTER.CFG used by rhp2lib-net integration tests.
        # Loopback-only; no radio. RHPPORT explicitly enabled on 9000 so
        # the Testcontainers test fixture can drive the real RHP server.

        DNS=8.8.8.8

        NODECALL=G9DUM-1
        NODEALIAS=DUMMY
        CONSOLECALL=G9DUM
        CHATCALL=G9DUM-8
        CHATALIAS=DUMCHT

        # Explicit RHP server port. Without this directive xrouter
        # doesn't open the listener on the Linux stack in this dummy
        # config.
        RHPPORT=9000

        CTEXT
        Integration test node.
        ***

        INFOTEXT
        rhp2lib-net integration tests.
        ***

        IDTEXT
        !5824.22N/00515.00W Test Router (DUMMY)
        ***

        INTERFACE=1
        	TYPE=LOOPBACK
        	PROTOCOL=KISS
        	MTU=256
        ENDINTERFACE

        PORT=1
        	ID="Loopback port"
        	INTERFACENUM=1
        ENDPORT
        """;

    /// <summary>Mapped host port for the container's RHP server (9000/tcp).</summary>
    public ushort RhpPort { get; private set; }

    /// <summary>Host the test should connect to. Always loopback.</summary>
    public string Host => "127.0.0.1";

    private IContainer? _container;

    public async Task InitializeAsync()
    {
        _container = new ContainerBuilder()
            .WithImage(Image)
            .WithPortBinding(9000, assignRandomHostPort: true)
            .WithResourceMapping(
                Encoding.UTF8.GetBytes(XRouterCfg),
                "/data/XROUTER.CFG")
            // The image's entrypoint tails LOG/*.TXT; wait for the
            // canonical "started" line before we let tests run.
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilMessageIsLogged("xrouter version "))
            .Build();

        await _container.StartAsync().ConfigureAwait(false);
        RhpPort = _container.GetMappedPublicPort(9000);

        // The wait strategy fires as soon as xrouter starts; the RHP
        // listener follows a moment later. Poll briefly so tests don't
        // race the server.
        await WaitForRhpAcceptingAsync(Host, RhpPort, TimeSpan.FromSeconds(15))
            .ConfigureAwait(false);
    }

    public async Task DisposeAsync()
    {
        if (_container is not null)
        {
            try { await _container.StopAsync(); } catch { }
            try { await _container.DisposeAsync(); } catch { }
        }
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

