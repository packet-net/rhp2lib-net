using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using RhpV2.Client.Protocol;

namespace RhpV2.Client.Testing;

/// <summary>
/// An in-process mock RHPv2 server suitable for integration tests and the
/// developer test harness.  It accepts a single TCP connection at a time on
/// an ephemeral port, dispatches frames through a handler delegate, and lets
/// the test push asynchronous notifications back to the client.
///
/// This is *not* a full XRouter implementation — it implements just enough
/// of the message lifecycle (handle allocation, reply correlation, basic
/// validation) to exercise the client library against a real socket.
/// </summary>
public sealed class MockRhpServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private Task? _acceptTask;
    private readonly List<MockRhpSession> _sessions = new();
    private readonly object _sessionsLock = new();
    private int _nextHandle = 100;
    private int _nextSeqno = 1;

    /// <summary>The TCP endpoint the server is bound to (for client connection).</summary>
    public IPEndPoint Endpoint { get; }

    /// <summary>
    /// Tracks every frame the server received, in order.  Useful for tests
    /// that want to assert on the wire format produced by the client.
    /// </summary>
    public ConcurrentQueue<RhpMessage> ReceivedFrames { get; } = new();

    /// <summary>
    /// Optional override: given an incoming message, return the reply (or null
    /// to use the default behavior).  Defaults handle AUTH→OK, OPEN→assigns a
    /// handle, SEND→OK, CLOSE→OK, etc.
    /// </summary>
    public Func<RhpMessage, RhpMessage?>? Handler { get; set; }

    /// <summary>
    /// When true, the mock receives requests but never replies — useful for
    /// testing client-side timeout / disconnect behaviour.
    /// </summary>
    public bool SuppressReplies { get; set; }

    /// <summary>Require AUTH before any other request (default: false).</summary>
    public bool RequireAuth { get; set; }

    /// <summary>Username/password pair when <see cref="RequireAuth"/> is true.</summary>
    public (string User, string Pass)? Credentials { get; set; }

    public MockRhpServer(int port = 0)
    {
        _listener = new TcpListener(IPAddress.Loopback, port);
        _listener.Start();
        Endpoint = (IPEndPoint)_listener.LocalEndpoint;
    }

    public void Start()
    {
        _acceptTask = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException) { return; }

            var session = new MockRhpSession(this, client);
            lock (_sessionsLock) _sessions.Add(session);
            _ = Task.Run(() => session.RunAsync(ct));
        }
    }

    /// <summary>Push a server-initiated notification to all live sessions.</summary>
    public async Task BroadcastAsync(RhpMessage notification, CancellationToken ct = default)
    {
        notification.Seqno ??= NextSeqno();
        MockRhpSession[] snapshot;
        lock (_sessionsLock) snapshot = _sessions.ToArray();
        foreach (var s in snapshot)
            await s.WriteAsync(notification, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Push a raw, pre-encoded frame payload to all live sessions.  Unlike
    /// <see cref="BroadcastAsync"/> the bytes are framed but not serialized,
    /// so tests can inject malformed JSON or other wire shapes the typed
    /// API can't produce.
    /// </summary>
    public async Task BroadcastRawAsync(ReadOnlyMemory<byte> payload, CancellationToken ct = default)
    {
        MockRhpSession[] snapshot;
        lock (_sessionsLock) snapshot = _sessions.ToArray();
        foreach (var s in snapshot)
            await s.WriteRawAsync(payload, ct).ConfigureAwait(false);
    }

    internal int NextHandle() => Interlocked.Increment(ref _nextHandle);
    internal int NextSeqno() => Interlocked.Increment(ref _nextSeqno);

    public async ValueTask DisposeAsync()
    {
        try { _cts.Cancel(); } catch { }
        try { _listener.Stop(); } catch { }
        if (_acceptTask is not null) { try { await _acceptTask.ConfigureAwait(false); } catch { } }
        MockRhpSession[] snap;
        lock (_sessionsLock) snap = _sessions.ToArray();
        foreach (var s in snap) await s.DisposeAsync().ConfigureAwait(false);
        _cts.Dispose();
    }
}

internal sealed class MockRhpSession : IAsyncDisposable
{
    private readonly MockRhpServer _server;
    private readonly TcpClient _tcp;
    private readonly NetworkStream _stream;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly HashSet<int> _handles = new();
    private bool _authenticated;

    public MockRhpSession(MockRhpServer server, TcpClient tcp)
    {
        _server = server;
        _tcp = tcp;
        _stream = tcp.GetStream();
    }

    public async Task RunAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var frame = await RhpFraming.ReadFrameAsync(_stream, ct).ConfigureAwait(false);
                if (frame is null) break;
                RhpMessage incoming;
                try { incoming = RhpJson.Deserialize(frame); }
                catch { continue; }

                _server.ReceivedFrames.Enqueue(incoming);
                if (_server.SuppressReplies) continue;
                var reply = HandleDefault(incoming);
                if (_server.Handler is { } custom)
                {
                    var overridden = custom(incoming);
                    if (overridden is not null) reply = overridden;
                }
                if (reply is not null)
                {
                    // Echo the request id only on actual reply types.
                    // Notification-shaped messages (RECV, ACCEPT, STATUS
                    // pushed on success, server-CLOSE) carry a seqno
                    // instead and must NOT correlate with a request —
                    // matching what real xrouter does on the wire.
                    if (reply.Seqno is null && incoming.Id is int id)
                        reply.Id = id;
                    await WriteAsync(reply, ct).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception) { }
        finally
        {
            try { _tcp.Close(); } catch { }
        }
    }

    private RhpMessage? HandleDefault(RhpMessage msg)
    {
        if (_server.RequireAuth && !_authenticated && msg is not AuthMessage)
            return MakeError(msg, RhpErrorCode.Unauthorised);

        switch (msg)
        {
            case AuthMessage auth:
                if (_server.Credentials is { } creds &&
                    (auth.User != creds.User || auth.Pass != creds.Pass))
                    return new AuthReplyMessage { ErrCode = RhpErrorCode.Unauthorised, ErrText = "Unauthorised" };
                _authenticated = true;
                return new AuthReplyMessage { ErrCode = 0, ErrText = "Ok" };

            case OpenMessage:
            {
                var h = _server.NextHandle();
                _handles.Add(h);
                return new OpenReplyMessage { Handle = h, ErrCode = 0, ErrText = "Ok" };
            }
            case SocketMessage:
            {
                var h = _server.NextHandle();
                _handles.Add(h);
                return new SocketReplyMessage { Handle = h, ErrCode = 0, ErrText = "Ok" };
            }
            case BindMessage b:
                return _handles.Contains(b.Handle)
                    ? new BindReplyMessage { Handle = b.Handle, ErrCode = 0, ErrText = "Ok" }
                    : new BindReplyMessage { Handle = b.Handle, ErrCode = RhpErrorCode.InvalidHandle, ErrText = "Invalid handle" };
            case ListenMessage l:
                return _handles.Contains(l.Handle)
                    ? new ListenReplyMessage { Handle = l.Handle, ErrCode = 0, ErrText = "Ok" }
                    : new ListenReplyMessage { Handle = l.Handle, ErrCode = RhpErrorCode.InvalidHandle, ErrText = "Invalid handle" };
            case ConnectMessage c:
                return _handles.Contains(c.Handle)
                    ? new ConnectReplyMessage { Handle = c.Handle, ErrCode = 0, ErrText = "Ok" }
                    : new ConnectReplyMessage { Handle = c.Handle, ErrCode = RhpErrorCode.InvalidHandle, ErrText = "Invalid handle" };
            case SendMessage s:
                return _handles.Contains(s.Handle)
                    ? new SendReplyMessage { Handle = s.Handle, ErrCode = 0, ErrText = "Ok" }
                    : new SendReplyMessage { Handle = s.Handle, ErrCode = RhpErrorCode.InvalidHandle, ErrText = "Invalid handle" };
            case SendToMessage st:
                return _handles.Contains(st.Handle)
                    ? new SendToReplyMessage { Handle = st.Handle, ErrCode = 0, ErrText = "Ok" }
                    : new SendToReplyMessage { Handle = st.Handle, ErrCode = RhpErrorCode.InvalidHandle, ErrText = "Invalid handle" };
            case CloseMessage cm:
                _handles.Remove(cm.Handle);
                return new CloseReplyMessage { Handle = cm.Handle, ErrCode = 0, ErrText = "Ok" };
            case StatusMessage st when st.Id is not null:
                return _handles.Contains(st.Handle)
                    // Spec: STATUSREPLY only on failure; success ⇒ a STATUS notification.
                    ? new StatusMessage { Handle = st.Handle, Flags = (int)StatusFlags.Connected, Seqno = _server.NextSeqno() }
                    : new StatusReplyMessage { Handle = st.Handle, ErrCode = RhpErrorCode.InvalidHandle, ErrText = "Invalid handle" };
        }
        return null;
    }

    private static RhpMessage MakeError(RhpMessage incoming, int code)
    {
        // Approximation: produce a generic reply that the client understands.
        // Always use a per-type reply so client error correlation works.
        return incoming switch
        {
            AuthMessage    => new AuthReplyMessage    { ErrCode = code, ErrText = RhpErrorCode.Text(code) },
            OpenMessage    => new OpenReplyMessage    { ErrCode = code, ErrText = RhpErrorCode.Text(code) },
            SocketMessage  => new SocketReplyMessage  { ErrCode = code, ErrText = RhpErrorCode.Text(code) },
            BindMessage b  => new BindReplyMessage    { Handle = b.Handle, ErrCode = code, ErrText = RhpErrorCode.Text(code) },
            ListenMessage l=> new ListenReplyMessage  { Handle = l.Handle, ErrCode = code, ErrText = RhpErrorCode.Text(code) },
            ConnectMessage c=> new ConnectReplyMessage{ Handle = c.Handle, ErrCode = code, ErrText = RhpErrorCode.Text(code) },
            SendMessage s  => new SendReplyMessage    { Handle = s.Handle, ErrCode = code, ErrText = RhpErrorCode.Text(code) },
            SendToMessage t=> new SendToReplyMessage  { Handle = t.Handle, ErrCode = code, ErrText = RhpErrorCode.Text(code) },
            CloseMessage cl=> new CloseReplyMessage   { Handle = cl.Handle, ErrCode = code, ErrText = RhpErrorCode.Text(code) },
            StatusMessage s=> new StatusReplyMessage  { Handle = s.Handle, ErrCode = code, ErrText = RhpErrorCode.Text(code) },
            _              => new StatusReplyMessage  { ErrCode = code, ErrText = RhpErrorCode.Text(code) },
        };
    }

    public async Task WriteAsync(RhpMessage message, CancellationToken ct)
    {
        var bytes = RhpJson.Serialize(message);
        await WriteRawAsync(bytes, ct).ConfigureAwait(false);
    }

    public async Task WriteRawAsync(ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await RhpFraming.WriteFrameAsync(_stream, payload, ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        try { _tcp.Close(); } catch { }
        _writeLock.Dispose();
        return ValueTask.CompletedTask;
    }
}
