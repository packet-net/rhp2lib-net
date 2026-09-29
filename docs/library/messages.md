# Messages and enums

Every wire message has a strongly-typed DTO under `RhpV2.Client.Protocol`.
They all derive from the abstract `RhpMessage` and surface their `type`
discriminator through a read-only `Type` property.

## DTO catalogue

| Discriminator | DTO                       | Direction |
|---------------|---------------------------|-----------|
| `auth`        | `AuthMessage`             | C → S     |
| `authReply`   | `AuthReplyMessage`        | S → C     |
| `open`        | `OpenMessage`             | C → S     |
| `openReply`   | `OpenReplyMessage`        | S → C     |
| `socket`      | `SocketMessage`           | C → S     |
| `socketReply` | `SocketReplyMessage`      | S → C     |
| `bind`        | `BindMessage`             | C → S     |
| `bindReply`   | `BindReplyMessage`        | S → C     |
| `listen`      | `ListenMessage`           | C → S     |
| `listenReply` | `ListenReplyMessage`      | S → C     |
| `connect`     | `ConnectMessage`          | C → S     |
| `connectReply`| `ConnectReplyMessage`     | S → C     |
| `send`        | `SendMessage`             | C → S     |
| `sendReply`   | `SendReplyMessage`        | S → C     |
| `sendto`      | `SendToMessage`           | C → S     |
| `sendtoReply` | `SendToReplyMessage`      | S → C     |
| `recv`        | `RecvMessage`             | S → C     |
| `accept`      | `AcceptMessage`           | S → C     |
| `status`      | `StatusMessage`           | both      |
| `statusReply` | `StatusReplyMessage`      | S → C     |
| `close`       | `CloseMessage`            | both      |
| `closeReply`  | `CloseReplyMessage`       | S → C     |
| _(other)_     | `UnknownMessage`          | S → C     |

## Common base members

```csharp
public abstract class RhpMessage
{
    public abstract string Type { get; }   // "auth", "openReply", ...
    public int? Id    { get; set; }        // request/reply correlation
    public int? Seqno { get; set; }        // server-pushed events
}
```

* `Id` is auto-assigned by `RhpClient.RequestAsync` if you leave it null.
* `Seqno` is set by the server on async notifications (RECV, ACCEPT,
  STATUS, server-initiated CLOSE) and is round-tripped on read.

## Server extensions on replies

`OpenReplyMessage.Crossed` (`bool?`) carries pdn's `"crossed": true`
extension: the open succeeded, and the node's call crossed the peer's
own call to us.  It is `null` whenever the server says nothing, which is
always the case with XRouter, so treat `null` as "unknown" rather than
"did not cross".  A `"crossed": false` on the wire reads as `null` too,
so only `true` ever means crossed.  `RhpClient.OpenAsync` returns just the handle; call
`OpenWithReplyAsync` (same parameters) to get the reply itself:

```csharp
var reply = await rhp.OpenWithReplyAsync(
    ProtocolFamily.Ax25, SocketMode.Stream,
    port: "1", local: "G8PZT", remote: "GB7PZT",
    flags: OpenFlags.Active);

if (reply.Crossed == true)
{
    // The far end dialled us at the same moment; don't wait for its prompt.
}
```

See [Server extensions](../protocol.md#server-extensions) in the
protocol primer.

## Constants and enums

The `RhpV2.Client.Protocol` namespace ships strongly-typed constants for
every place the wire format uses a string token:

```csharp
public static class ProtocolFamily { public const string Ax25 = "ax25"; ... }
public static class SocketMode     { public const string Stream = "stream"; ... }

[Flags]
public enum OpenFlags
{
    Passive          = 0x00,
    TraceIncoming    = 0x01,
    TraceOutgoing    = 0x02,
    TraceSupervisory = 0x04,
    Active           = 0x80,
}

[Flags]
public enum StatusFlags { None = 0, ConOk = 1, Connected = 2, Busy = 4 }

public static class RhpErrorCode { public const int Ok = 0; ... public static string Text(int); }
public static class RhpMessageType { public const string Auth = "auth"; ... }
```

## Serializing & parsing manually

You shouldn't normally need this — `RhpClient` does it for you — but
`RhpJson` is exposed for tools and tests:

```csharp
byte[] bytes = RhpJson.Serialize(new OpenMessage {
    Pfam = ProtocolFamily.Ax25, Mode = SocketMode.Stream,
    Local = "G8PZT", Flags = (int)OpenFlags.Passive,
});

RhpMessage parsed = RhpJson.Deserialize(bytes);
if (parsed is OpenReplyMessage r) { ... }
```

The serializer always places `type` as the first key for readability and
spec compliance.  The deserializer dispatches on `type` to the right DTO
and returns `UnknownMessage` for unknown values, preserving the raw JSON.
