using System.Text.Json.Serialization;

namespace RhpV2.Client.Protocol;

/// <summary>
/// Base class for all RHPv2 messages.  Concrete subclasses correspond to
/// the discriminator values defined in <see cref="RhpMessageType"/>.
/// </summary>
public abstract class RhpMessage
{
    /// <summary>The wire-format <c>type</c> discriminator for this message.</summary>
    [JsonIgnore]
    public abstract string Type { get; }

    /// <summary>
    /// Optional request id used to correlate replies.  When omitted, the
    /// server only replies on error per the spec.
    /// </summary>
    [JsonPropertyName("id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Id { get; set; }

    /// <summary>
    /// Asynchronous notifications carry a server-assigned sequence number
    /// (RECV, ACCEPT, STATUS-from-server, CLOSE-from-server).
    /// </summary>
    [JsonPropertyName("seqno")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Seqno { get; set; }
}

// ---------------------------------------------------------------------------
//  Authentication
// ---------------------------------------------------------------------------

public sealed class AuthMessage : RhpMessage
{
    public override string Type => RhpMessageType.Auth;

    [JsonPropertyName("user")]
    public string User { get; set; } = string.Empty;

    [JsonPropertyName("pass")]
    public string Pass { get; set; } = string.Empty;
}

public sealed class AuthReplyMessage : RhpMessage
{
    public override string Type => RhpMessageType.AuthReply;

    /// <summary>
    /// Real xrouter emits <c>errCode</c>/<c>errText</c> with capital C/T on
    /// every reply, including AUTHREPLY (the published spec only mentions
    /// it as a quirk of AUTHREPLY). The library reads case-insensitively
    /// so lowercase wire forms are still accepted.
    /// </summary>
    [JsonPropertyName("errCode")]
    public int ErrCode { get; set; }

    [JsonPropertyName("errText")]
    public string? ErrText { get; set; }
}

// ---------------------------------------------------------------------------
//  Combined OPEN (active or passive) — the high-level API of RHPv2
// ---------------------------------------------------------------------------

public sealed class OpenMessage : RhpMessage
{
    public override string Type => RhpMessageType.Open;

    [JsonPropertyName("pfam")]
    public string Pfam { get; set; } = string.Empty;

    [JsonPropertyName("mode")]
    public string Mode { get; set; } = string.Empty;

    [JsonPropertyName("port")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Port { get; set; }

    [JsonPropertyName("local")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Local { get; set; }

    [JsonPropertyName("remote")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Remote { get; set; }

    [JsonPropertyName("flags")]
    public int Flags { get; set; }
}

public sealed class OpenReplyMessage : RhpMessage
{
    public override string Type => RhpMessageType.OpenReply;

    [JsonPropertyName("handle")]
    public int Handle { get; set; }

    [JsonPropertyName("errCode")]
    public int ErrCode { get; set; }

    [JsonPropertyName("errText")]
    public string? ErrText { get; set; }

    /// <summary>
    /// pdn's extension E1: <c>true</c> when a successful stream open crossed
    /// the peer's own call to us (the peer's SABM or SABME to our local
    /// callsign arrived while the node was dialling it, or the link was
    /// already up).  pdn sends the key only when it is true; XRouter never
    /// sends it.  <c>null</c> therefore means "unknown", not "did not cross",
    /// and a <c>"crossed": false</c> on the wire reads as <c>null</c> too, so
    /// only <c>true</c> ever means crossed.
    /// See packet.net's <c>docs/rhp2-server.md</c>, Extensions.
    /// </summary>
    [JsonPropertyName("crossed")]
    public bool? Crossed
    {
        get => _crossed;
        set => _crossed = value == true ? true : null;
    }

    private bool? _crossed;
}

// ---------------------------------------------------------------------------
//  BSD-style socket lifecycle
// ---------------------------------------------------------------------------

public sealed class SocketMessage : RhpMessage
{
    public override string Type => RhpMessageType.Socket;
    [JsonPropertyName("pfam")] public string Pfam { get; set; } = string.Empty;
    [JsonPropertyName("mode")] public string Mode { get; set; } = string.Empty;
}

public sealed class SocketReplyMessage : RhpMessage
{
    public override string Type => RhpMessageType.SocketReply;
    [JsonPropertyName("handle")] public int? Handle { get; set; }
    [JsonPropertyName("errCode")] public int ErrCode { get; set; }
    [JsonPropertyName("errText")] public string? ErrText { get; set; }
}

public sealed class BindMessage : RhpMessage
{
    public override string Type => RhpMessageType.Bind;
    [JsonPropertyName("handle")] public int Handle { get; set; }

    /// <summary>
    /// Local address.  Omitted for RAW sockets, which per RHPTEST bind
    /// to a port only — supplying an address returns errCode 16.
    /// </summary>
    [JsonPropertyName("local")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Local { get; set; }

    [JsonPropertyName("port")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Port { get; set; }
}

public sealed class BindReplyMessage : RhpMessage
{
    public override string Type => RhpMessageType.BindReply;
    [JsonPropertyName("handle")] public int Handle { get; set; }
    [JsonPropertyName("errCode")] public int ErrCode { get; set; }
    [JsonPropertyName("errText")] public string? ErrText { get; set; }
}

public sealed class ListenMessage : RhpMessage
{
    public override string Type => RhpMessageType.Listen;
    [JsonPropertyName("handle")] public int Handle { get; set; }
    [JsonPropertyName("flags")] public int Flags { get; set; }
}

public sealed class ListenReplyMessage : RhpMessage
{
    public override string Type => RhpMessageType.ListenReply;
    [JsonPropertyName("handle")] public int Handle { get; set; }
    [JsonPropertyName("errCode")] public int ErrCode { get; set; }
    [JsonPropertyName("errText")] public string? ErrText { get; set; }
}

public sealed class ConnectMessage : RhpMessage
{
    public override string Type => RhpMessageType.Connect;
    [JsonPropertyName("handle")] public int Handle { get; set; }
    [JsonPropertyName("remote")] public string Remote { get; set; } = string.Empty;
}

public sealed class ConnectReplyMessage : RhpMessage
{
    public override string Type => RhpMessageType.ConnectReply;
    [JsonPropertyName("handle")] public int Handle { get; set; }
    [JsonPropertyName("errCode")] public int ErrCode { get; set; }
    [JsonPropertyName("errText")] public string? ErrText { get; set; }
}

// ---------------------------------------------------------------------------
//  Data transfer
// ---------------------------------------------------------------------------

public sealed class SendMessage : RhpMessage
{
    public override string Type => RhpMessageType.Send;

    [JsonPropertyName("handle")] public int Handle { get; set; }

    /// <summary>
    /// Payload — control characters JSON-escaped per spec.  Use the helpers
    /// on <see cref="RhpV2.Client.RhpDataEncoding"/> for binary data.
    /// </summary>
    [JsonPropertyName("data")] public string Data { get; set; } = string.Empty;

    // DGRAM mode only:
    [JsonPropertyName("port")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Port { get; set; }
    [JsonPropertyName("local")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Local { get; set; }
    [JsonPropertyName("remote")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Remote { get; set; }
}

public sealed class SendReplyMessage : RhpMessage
{
    public override string Type => RhpMessageType.SendReply;
    [JsonPropertyName("handle")] public int Handle { get; set; }
    [JsonPropertyName("errCode")] public int ErrCode { get; set; }
    [JsonPropertyName("errText")] public string? ErrText { get; set; }
    /// <summary>STREAM-mode connection status (CONNECTED|BUSY).</summary>
    [JsonPropertyName("status")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? Status { get; set; }
}

public sealed class SendToMessage : RhpMessage
{
    public override string Type => RhpMessageType.SendTo;
    [JsonPropertyName("handle")] public int Handle { get; set; }
    [JsonPropertyName("data")] public string Data { get; set; } = string.Empty;
    [JsonPropertyName("port")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Port { get; set; }
    [JsonPropertyName("local")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Local { get; set; }
    [JsonPropertyName("remote")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Remote { get; set; }
    [JsonPropertyName("tos")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? Tos { get; set; }
}

public sealed class SendToReplyMessage : RhpMessage
{
    public override string Type => RhpMessageType.SendToReply;
    [JsonPropertyName("handle")] public int Handle { get; set; }
    [JsonPropertyName("errCode")] public int ErrCode { get; set; }
    [JsonPropertyName("errText")] public string? ErrText { get; set; }
}

public sealed class RecvMessage : RhpMessage
{
    public override string Type => RhpMessageType.Recv;
    [JsonPropertyName("handle")] public int Handle { get; set; }
    [JsonPropertyName("data")] public string Data { get; set; } = string.Empty;

    // DGRAM addressing — also returned for inbound UI frames. Real
    // xrouter sends `port` as a JSON string in DGRAM mode and as a JSON
    // number in TRACE mode; the converter normalises both.
    [JsonPropertyName("port")]
    [JsonConverter(typeof(StringOrIntConverter))]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Port { get; set; }

    [JsonPropertyName("local")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Local { get; set; }

    [JsonPropertyName("remote")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Remote { get; set; }

    // RAW / TRACE metadata — populated when the listener was opened in
    // RAW or TRACE mode.
    [JsonPropertyName("action")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Action { get; set; }
    [JsonPropertyName("srce")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Srce { get; set; }
    [JsonPropertyName("dest")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Dest { get; set; }
    [JsonPropertyName("ctrl")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? Ctrl { get; set; }
    [JsonPropertyName("frametype")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? FrameType { get; set; }
    [JsonPropertyName("rseq")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? Rseq { get; set; }
    [JsonPropertyName("tseq")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? Tseq { get; set; }
    [JsonPropertyName("cr")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Cr { get; set; }
    [JsonPropertyName("pf")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Pf { get; set; }

    /// <summary>Information field length (TRACE I-frames).</summary>
    [JsonPropertyName("ilen")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? Ilen { get; set; }

    /// <summary>AX.25 PID byte (TRACE I-frames).</summary>
    [JsonPropertyName("pid")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? Pid { get; set; }

    /// <summary>Decoded protocol name e.g. "DATA", "NETROM", "IP" (TRACE).</summary>
    [JsonPropertyName("ptcl")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Ptcl { get; set; }
}

// ---------------------------------------------------------------------------
//  Notifications + status
// ---------------------------------------------------------------------------

public sealed class AcceptMessage : RhpMessage
{
    public override string Type => RhpMessageType.Accept;
    [JsonPropertyName("handle")] public int Handle { get; set; }
    [JsonPropertyName("child")] public int Child { get; set; }
    [JsonPropertyName("remote")] public string? Remote { get; set; }
    [JsonPropertyName("local")] public string? Local { get; set; }

    /// <summary>
    /// Source port the inbound connection arrived on.  Real xrouter
    /// sends this as a JSON string ("2") even though PWP-0222's example
    /// shows an unquoted number; the library normalises both shapes via
    /// <see cref="StringOrIntConverter"/>.
    /// </summary>
    [JsonPropertyName("port")]
    [JsonConverter(typeof(StringOrIntConverter))]
    public string? Port { get; set; }
}

public sealed class StatusMessage : RhpMessage
{
    public override string Type => RhpMessageType.Status;
    [JsonPropertyName("handle")] public int Handle { get; set; }
    [JsonPropertyName("flags")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? Flags { get; set; }
}

public sealed class StatusReplyMessage : RhpMessage
{
    public override string Type => RhpMessageType.StatusReply;
    [JsonPropertyName("handle")] public int Handle { get; set; }
    [JsonPropertyName("errCode")] public int ErrCode { get; set; }
    [JsonPropertyName("errText")] public string? ErrText { get; set; }
}

public sealed class CloseMessage : RhpMessage
{
    public override string Type => RhpMessageType.Close;
    [JsonPropertyName("handle")] public int Handle { get; set; }
}

public sealed class CloseReplyMessage : RhpMessage
{
    public override string Type => RhpMessageType.CloseReply;
    [JsonPropertyName("handle")] public int Handle { get; set; }
    [JsonPropertyName("errCode")] public int ErrCode { get; set; }
    [JsonPropertyName("errText")] public string? ErrText { get; set; }
}
