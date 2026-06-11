using System.Threading;
using System.Threading.Tasks;
using RhpV2.Client.Protocol;
using RhpV2.Client.Testing;
using Xunit;

namespace RhpV2.Client.Tests;

public class RhpClientTests
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task Connects_To_MockServer_And_Authenticates()
    {
        await using var server = new MockRhpServer { RequireAuth = true, Credentials = ("g8pzt", "pw") };
        server.Start();

        await using var client = await RhpClient.ConnectAsync("127.0.0.1", server.Endpoint.Port);
        await client.AuthenticateAsync("g8pzt", "pw");
        // No exception ⇒ pass.
    }

    [Fact]
    public async Task Authenticate_BadPassword_Throws_RhpServerException()
    {
        await using var server = new MockRhpServer { RequireAuth = true, Credentials = ("g8pzt", "right") };
        server.Start();
        await using var client = await RhpClient.ConnectAsync("127.0.0.1", server.Endpoint.Port);

        var ex = await Assert.ThrowsAsync<RhpServerException>(
            async () => await client.AuthenticateAsync("g8pzt", "wrong"));
        Assert.Equal(RhpErrorCode.Unauthorised, ex.ErrorCode);
    }

    [Fact]
    public async Task OpenAsync_Returns_Handle_From_Server()
    {
        await using var server = new MockRhpServer();
        server.Start();
        await using var client = await RhpClient.ConnectAsync("127.0.0.1", server.Endpoint.Port);

        var h = await client.OpenAsync(
            ProtocolFamily.Ax25, SocketMode.Stream,
            port: "1", local: "G8PZT", flags: OpenFlags.Passive);

        Assert.True(h > 0);
    }

    [Fact]
    public async Task SendOnHandle_Returns_OkReply()
    {
        await using var server = new MockRhpServer();
        server.Start();
        await using var client = await RhpClient.ConnectAsync("127.0.0.1", server.Endpoint.Port);

        var h = await client.OpenAsync(
            ProtocolFamily.Ax25, SocketMode.Stream,
            port: "1", local: "G8PZT", remote: "M0XYZ", flags: OpenFlags.Active);

        var reply = await client.SendOnHandleAsync(h, "hello\r");
        Assert.Equal(0, reply.ErrCode);
        Assert.Equal(h, reply.Handle);
    }

    [Fact]
    public async Task Send_OnInvalidHandle_Throws_With_InvalidHandle()
    {
        await using var server = new MockRhpServer();
        server.Start();
        await using var client = await RhpClient.ConnectAsync("127.0.0.1", server.Endpoint.Port);

        var ex = await Assert.ThrowsAsync<RhpServerException>(
            async () => await client.SendOnHandleAsync(99999, "x"));
        Assert.Equal(RhpErrorCode.InvalidHandle, ex.ErrorCode);
    }

    [Fact]
    public async Task Recv_FromServer_Fires_Received_Event()
    {
        await using var server = new MockRhpServer();
        server.Start();
        await using var client = await RhpClient.ConnectAsync("127.0.0.1", server.Endpoint.Port);

        var h = await client.OpenAsync(
            ProtocolFamily.Ax25, SocketMode.Stream,
            port: "1", local: "G8PZT", remote: "M0XYZ", flags: OpenFlags.Active);

        var tcs = new TaskCompletionSource<RecvMessage>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        client.Received += (_, e) => tcs.TrySetResult(e.Message);

        await server.BroadcastAsync(new RecvMessage { Handle = h, Data = "ping\r" });

        var got = await tcs.Task.WaitAsync(DefaultTimeout);
        Assert.Equal(h, got.Handle);
        Assert.Equal("ping\r", got.Data);
    }

    [Fact]
    public async Task Accept_FromServer_Fires_Accepted_Event()
    {
        await using var server = new MockRhpServer();
        server.Start();
        await using var client = await RhpClient.ConnectAsync("127.0.0.1", server.Endpoint.Port);

        var listener = await client.OpenAsync(
            ProtocolFamily.Ax25, SocketMode.Stream,
            port: "1", local: "G8PZT", flags: OpenFlags.Passive);

        var tcs = new TaskCompletionSource<AcceptMessage>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        client.Accepted += (_, e) => tcs.TrySetResult(e.Message);

        await server.BroadcastAsync(new AcceptMessage
        {
            Handle = listener,
            Child = 9999,
            Remote = "M0XYZ",
            Local = "G8PZT",
            Port = "1",
        });

        var got = await tcs.Task.WaitAsync(DefaultTimeout);
        Assert.Equal(listener, got.Handle);
        Assert.Equal(9999, got.Child);
        Assert.Equal("M0XYZ", got.Remote);
    }

    [Fact]
    public async Task Status_FromServer_Fires_StatusChanged()
    {
        await using var server = new MockRhpServer();
        server.Start();
        await using var client = await RhpClient.ConnectAsync("127.0.0.1", server.Endpoint.Port);

        var h = await client.OpenAsync(
            ProtocolFamily.Ax25, SocketMode.Stream,
            port: "1", local: "G8PZT", remote: "M0XYZ", flags: OpenFlags.Active);

        var tcs = new TaskCompletionSource<StatusMessage>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        client.StatusChanged += (_, e) => tcs.TrySetResult(e.Message);

        await server.BroadcastAsync(new StatusMessage
        {
            Handle = h,
            Flags = (int)(StatusFlags.Connected),
        });

        var got = await tcs.Task.WaitAsync(DefaultTimeout);
        Assert.Equal(h, got.Handle);
        Assert.Equal((int)StatusFlags.Connected, got.Flags);
    }

    [Fact]
    public async Task Close_FromServer_Fires_Closed_Event()
    {
        await using var server = new MockRhpServer();
        server.Start();
        await using var client = await RhpClient.ConnectAsync("127.0.0.1", server.Endpoint.Port);

        var h = await client.OpenAsync(
            ProtocolFamily.Ax25, SocketMode.Stream,
            port: "1", local: "G8PZT", remote: "M0XYZ", flags: OpenFlags.Active);

        var tcs = new TaskCompletionSource<int>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        client.Closed += (_, e) => tcs.TrySetResult(e.Handle);

        await server.BroadcastAsync(new CloseMessage { Handle = h });
        var got = await tcs.Task.WaitAsync(DefaultTimeout);
        Assert.Equal(h, got);
    }

    [Fact]
    public async Task ParallelRequests_Are_Correlated_By_Id()
    {
        await using var server = new MockRhpServer();
        server.Start();
        await using var client = await RhpClient.ConnectAsync("127.0.0.1", server.Endpoint.Port);

        // Issue many opens concurrently and verify each gets a distinct handle.
        var tasks = Enumerable.Range(0, 32)
            .Select(_ => client.OpenAsync(
                ProtocolFamily.Ax25, SocketMode.Stream,
                port: "1", local: "G8PZT", flags: OpenFlags.Passive))
            .ToArray();

        var handles = await Task.WhenAll(tasks);
        Assert.Equal(handles.Length, handles.Distinct().Count());
        Assert.All(handles, h => Assert.True(h > 0));
    }

    [Fact]
    public async Task QueryStatusAsync_Returns_Flags_From_Status_Notification()
    {
        // Per spec, status query success returns a status NOTIFICATION
        // (no id), not a statusReply. Verify the library races those
        // two response types correctly.
        await using var server = new MockRhpServer();
        server.Start();
        await using var client = await RhpClient.ConnectAsync("127.0.0.1", server.Endpoint.Port);

        var h = await client.OpenAsync(
            ProtocolFamily.Ax25, SocketMode.Stream,
            port: "1", local: "G8PZT", remote: "M0XYZ", flags: OpenFlags.Active);

        var flags = await client.QueryStatusAsync(h);
        Assert.True((flags & StatusFlags.Connected) != 0);
    }

    [Fact]
    public async Task QueryStatusAsync_Throws_On_Invalid_Handle_Via_StatusReply()
    {
        // Failure path remains: server emits statusReply with errCode,
        // which the library throws as RhpServerException.
        await using var server = new MockRhpServer();
        server.Start();
        await using var client = await RhpClient.ConnectAsync("127.0.0.1", server.Endpoint.Port);

        var ex = await Assert.ThrowsAsync<RhpServerException>(
            async () => await client.QueryStatusAsync(99_999));
        Assert.Equal(RhpErrorCode.InvalidHandle, ex.ErrorCode);
    }

    [Fact]
    public async Task QueryStatusAsync_Times_Out_When_Server_Silent()
    {
        // No notification, no reply → timeout becomes RhpProtocolException.
        await using var server = new MockRhpServer { SuppressReplies = true };
        server.Start();
        await using var client = await RhpClient.ConnectAsync("127.0.0.1", server.Endpoint.Port);

        await Assert.ThrowsAsync<RhpProtocolException>(
            async () => await client.QueryStatusAsync(1, responseTimeout: TimeSpan.FromMilliseconds(200)));
    }

    [Fact]
    public async Task ConnectAsync_Tolerates_Xrouter_ErrCode_Mirrors_Handle_Quirk()
    {
        // Real xrouter returns connectReply with errCode = handle (rather
        // than 0) on success, alongside errText="Ok". The library treats
        // any "Ok" text as success regardless of the numeric code so this
        // path doesn't throw.
        await using var server = new MockRhpServer();
        server.Handler = msg => msg switch
        {
            ConnectMessage c => new ConnectReplyMessage
            {
                Handle  = c.Handle,
                ErrCode = c.Handle, // the bug: code mirrors handle on success
                ErrText = "Ok",
            },
            _ => null,
        };
        server.Start();
        await using var client = await RhpClient.ConnectAsync("127.0.0.1", server.Endpoint.Port);

        var h = await client.SocketAsync(ProtocolFamily.Ax25, SocketMode.Stream);
        // Should NOT throw, even though errCode is non-zero.
        await client.ConnectAsync(h, "G8PZT-1");
    }

    [Fact]
    public async Task ConnectAsync_Still_Throws_On_Real_Failure()
    {
        // Sanity check that we didn't accidentally swallow real errors.
        await using var server = new MockRhpServer();
        server.Handler = msg => msg switch
        {
            ConnectMessage c => new ConnectReplyMessage
            {
                Handle  = c.Handle,
                ErrCode = RhpErrorCode.NoRoute,
                ErrText = "No Route",
            },
            _ => null,
        };
        server.Start();
        await using var client = await RhpClient.ConnectAsync("127.0.0.1", server.Endpoint.Port);

        var h = await client.SocketAsync(ProtocolFamily.Ax25, SocketMode.Stream);
        var ex = await Assert.ThrowsAsync<RhpServerException>(
            async () => await client.ConnectAsync(h, "NOROUTE"));
        Assert.Equal(RhpErrorCode.NoRoute, ex.ErrorCode);
    }

    [Fact]
    public async Task Disconnected_Event_Fires_When_Server_Closes()
    {
        var server = new MockRhpServer();
        server.Start();
        await using var client = await RhpClient.ConnectAsync("127.0.0.1", server.Endpoint.Port);

        var tcs = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        client.Disconnected += (_, _) => tcs.TrySetResult(true);

        await server.DisposeAsync();
        var ok = await tcs.Task.WaitAsync(DefaultTimeout);
        Assert.True(ok);
    }

    [Fact]
    public async Task Throwing_Event_Handler_Does_Not_Kill_The_Connection()
    {
        await using var server = new MockRhpServer();
        server.Start();
        await using var client = await RhpClient.ConnectAsync("127.0.0.1", server.Endpoint.Port);

        var h = await client.OpenAsync(
            ProtocolFamily.Ax25, SocketMode.Stream,
            port: "1", local: "G8PZT", remote: "M0XYZ", flags: OpenFlags.Active);

        var handlerRan = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        client.Received += (_, _) =>
        {
            handlerRan.TrySetResult(true);
            throw new InvalidOperationException("subscriber bug");
        };

        await server.BroadcastAsync(new RecvMessage { Handle = h, Data = "boom\r" });
        await handlerRan.Task.WaitAsync(DefaultTimeout);

        // The read loop must survive the throwing subscriber: a later
        // request on the same connection still gets its reply.
        var reply = await client.SendOnHandleAsync(h, "still alive\r")
            .WaitAsync(DefaultTimeout);
        Assert.Equal(0, reply.ErrCode);
    }

    [Fact]
    public async Task Undecodable_Frame_Surfaces_Raw_Text_And_Connection_Continues()
    {
        await using var server = new MockRhpServer();
        server.Start();
        await using var client = await RhpClient.ConnectAsync("127.0.0.1", server.Endpoint.Port);

        var tcs = new TaskCompletionSource<UnknownMessage>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        client.UnknownReceived += (_, e) =>
        {
            if (e.Message is UnknownMessage u) tcs.TrySetResult(u);
        };

        await server.BroadcastRawAsync(System.Text.Encoding.UTF8.GetBytes("this is not json"));

        var unknown = await tcs.Task.WaitAsync(DefaultTimeout);
        Assert.Equal("<parse-error>", unknown.Type);
        Assert.Equal("this is not json", unknown.Raw["raw"]!.GetValue<string>());

        // The bad frame must not desync or fault the read loop.
        var h = await client.OpenAsync(
            ProtocolFamily.Ax25, SocketMode.Stream,
            port: "1", local: "G8PZT", flags: OpenFlags.Passive);
        Assert.True(h > 0);
    }

    [Fact]
    public async Task Send_Above_MaxSendDataLength_Throws_Instead_Of_Hanging()
    {
        await using var server = new MockRhpServer();
        server.Start();
        await using var client = await RhpClient.ConnectAsync("127.0.0.1", server.Endpoint.Port);

        var h = await client.OpenAsync(
            ProtocolFamily.Ax25, SocketMode.Stream,
            port: "1", local: "G8PZT", remote: "M0XYZ", flags: OpenFlags.Active);

        // Default guard sits at the bottom of the observed xrouter cliff.
        var ex = await Assert.ThrowsAsync<ArgumentException>(
            async () => await client.SendOnHandleAsync(h, new string('X', 8101)));
        Assert.Contains("MaxSendDataLength", ex.Message);

        // Nothing should have hit the wire for the rejected send.
        Assert.DoesNotContain(server.ReceivedFrames, m => m is SendMessage);
    }

    [Fact]
    public async Task Send_Limit_Is_Configurable_And_Can_Be_Disabled()
    {
        await using var server = new MockRhpServer();
        server.Start();
        await using var client = await RhpClient.ConnectAsync("127.0.0.1", server.Endpoint.Port);
        client.MaxSendDataLength = null;

        var h = await client.OpenAsync(
            ProtocolFamily.Ax25, SocketMode.Stream,
            port: "1", local: "G8PZT", remote: "M0XYZ", flags: OpenFlags.Active);

        var reply = await client.SendOnHandleAsync(h, new string('X', 8101))
            .WaitAsync(DefaultTimeout);
        Assert.Equal(0, reply.ErrCode);
    }

    [Fact]
    public async Task Pending_Requests_Fail_When_Connection_Drops()
    {
        var server = new MockRhpServer { SuppressReplies = true };
        server.Start();
        await using var client = await RhpClient.ConnectAsync("127.0.0.1", server.Endpoint.Port);

        var task = client.OpenAsync(
            ProtocolFamily.Ax25, SocketMode.Stream,
            port: "1", local: "G8PZT", flags: OpenFlags.Passive);

        // Give the request a moment to land on the server, then yank the carpet.
        await Task.Delay(50);
        await server.DisposeAsync();

        await Assert.ThrowsAnyAsync<RhpProtocolException>(async () => await task);
    }
}
