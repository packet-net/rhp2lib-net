using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace RhpV2.Client.Protocol;

/// <summary>
/// JSON serialization helpers for RHPv2.
///
/// The wire format uses a string discriminator field <c>type</c> on every
/// message.  Real xrouter emits replies with <c>errCode</c>/<c>errText</c>
/// (capital C/T) on *every* error-bearing reply — the published PWP-0222
/// / PWP-0245 docs only mention this as a quirk of AUTHREPLY, but
/// integration testing against ghcr.io/packethacking/xrouter shows it
/// applies to every reply.  We configure
/// <see cref="JsonSerializerOptions.PropertyNameCaseInsensitive"/> = true
/// so any casing we encounter on read is tolerated, and write the
/// canonical capitalised wire names.
/// </summary>
public static class RhpJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
    };

    /// <summary>Serialize a strongly-typed message to UTF-8 JSON bytes.</summary>
    public static byte[] Serialize<T>(T message) where T : RhpMessage
    {
        // STJ won't naturally serialize the abstract `Type` property under the
        // wire name "type", so we lift the message into a JsonNode tree and
        // place the discriminator first.
        var node = JsonSerializer.SerializeToNode(message, message!.GetType(), Options) as JsonObject
            ?? throw new RhpProtocolException("Failed to serialize RHP message.");
        // Strip any auto-emitted "Type"/"type" keys so we can place the
        // discriminator first with the canonical wire name.
        node.Remove("Type");
        node.Remove("type");
        var withType = new JsonObject { ["type"] = message.Type };
        foreach (var kvp in node.ToList())
        {
            node.Remove(kvp.Key);
            withType[kvp.Key] = kvp.Value;
        }
        return JsonSerializer.SerializeToUtf8Bytes(withType, Options);
    }

    /// <summary>
    /// Parse a UTF-8 JSON RHP frame into the appropriate concrete
    /// <see cref="RhpMessage"/> subclass based on the <c>type</c> field.
    /// Unknown / missing types yield a <see cref="UnknownMessage"/>.
    /// </summary>
    public static RhpMessage Deserialize(ReadOnlySpan<byte> json)
    {
        // Peek at the type discriminator without fully materialising twice.
        var node = JsonNode.Parse(json.ToArray()) as JsonObject
            ?? throw new RhpProtocolException("RHP frame is not a JSON object.");

        var typeProp = node["type"]?.GetValue<string>();

        return typeProp switch
        {
            RhpMessageType.Auth         => Cast<AuthMessage>(node),
            RhpMessageType.AuthReply    => Cast<AuthReplyMessage>(node),
            RhpMessageType.Open         => Cast<OpenMessage>(node),
            RhpMessageType.OpenReply    => Cast<OpenReplyMessage>(node),
            RhpMessageType.Socket       => Cast<SocketMessage>(node),
            RhpMessageType.SocketReply  => Cast<SocketReplyMessage>(node),
            RhpMessageType.Bind         => Cast<BindMessage>(node),
            RhpMessageType.BindReply    => Cast<BindReplyMessage>(node),
            RhpMessageType.Listen       => Cast<ListenMessage>(node),
            RhpMessageType.ListenReply  => Cast<ListenReplyMessage>(node),
            RhpMessageType.Connect      => Cast<ConnectMessage>(node),
            // Tolerate the spec's "ConnectReply" typo as well.
            "ConnectReply"              => Cast<ConnectReplyMessage>(node),
            RhpMessageType.ConnectReply => Cast<ConnectReplyMessage>(node),
            RhpMessageType.Send         => Cast<SendMessage>(node),
            RhpMessageType.SendReply    => Cast<SendReplyMessage>(node),
            RhpMessageType.SendTo       => Cast<SendToMessage>(node),
            RhpMessageType.SendToReply  => Cast<SendToReplyMessage>(node),
            RhpMessageType.Recv         => Cast<RecvMessage>(node),
            RhpMessageType.Accept       => Cast<AcceptMessage>(node),
            RhpMessageType.Status       => Cast<StatusMessage>(node),
            RhpMessageType.StatusReply  => Cast<StatusReplyMessage>(node),
            RhpMessageType.Close        => Cast<CloseMessage>(node),
            RhpMessageType.CloseReply   => Cast<CloseReplyMessage>(node),
            null => throw new RhpProtocolException("RHP frame has no 'type' field."),
            _    => new UnknownMessage(typeProp, node),
        };
    }

    private static T Cast<T>(JsonObject node) where T : RhpMessage
        => node.Deserialize<T>(Options)
           ?? throw new RhpProtocolException($"Failed to deserialize {typeof(T).Name}.");
}

/// <summary>
/// Fallback for messages whose <c>type</c> isn't recognised.  Carries the raw
/// JSON for forward compatibility.
/// </summary>
public sealed class UnknownMessage : RhpMessage
{
    public override string Type { get; }
    public JsonObject Raw { get; }

    public UnknownMessage(string type, JsonObject raw)
    {
        Type = type;
        Raw = raw;
        // Unknown messages are by definition outside our schema, so don't
        // trust id/seqno to be numeric — a throw here would turn a
        // forward-compatible frame into a parse error.
        if (raw["id"] is JsonValue idValue && idValue.TryGetValue(out int id)) Id = id;
        if (raw["seqno"] is JsonValue seqValue && seqValue.TryGetValue(out int seq)) Seqno = seq;
    }
}
