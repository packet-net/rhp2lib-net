using System.Text;
using System.Text.Json;
using RhpV2.Client.Protocol;
using Xunit;

namespace RhpV2.Client.Tests;

public class MessageSerializationTests
{
    private static string Json(RhpMessage m) =>
        Encoding.UTF8.GetString(RhpJson.Serialize(m));

    [Fact]
    public void Auth_Serializes_With_Type_User_Pass()
    {
        var json = Json(new AuthMessage { User = "g8pzt", Pass = "secret", Id = 1 });
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("auth", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal("g8pzt", doc.RootElement.GetProperty("user").GetString());
        Assert.Equal("secret", doc.RootElement.GetProperty("pass").GetString());
        Assert.Equal(1, doc.RootElement.GetProperty("id").GetInt32());
    }

    [Fact]
    public void Open_Omits_Null_Fields()
    {
        var json = Json(new OpenMessage
        {
            Pfam = ProtocolFamily.Ax25,
            Mode = SocketMode.Stream,
            Local = "G8PZT",
            Flags = (int)OpenFlags.Passive,
        });
        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.TryGetProperty("local", out _));
        Assert.False(doc.RootElement.TryGetProperty("remote", out _));
        Assert.False(doc.RootElement.TryGetProperty("port", out _));
        Assert.False(doc.RootElement.TryGetProperty("id", out _));
    }

    [Fact]
    public void OpenReply_Deserializes_From_Spec_Example()
    {
        // PWP-0222 spec example uses lowercase errcode/errtext.
        var wire = """{"type":"openReply","id":7,"handle":1234,"errcode":0,"errtext":"Ok"}""";
        var msg = (OpenReplyMessage)RhpJson.Deserialize(Encoding.UTF8.GetBytes(wire));
        Assert.Equal(7, msg.Id);
        Assert.Equal(1234, msg.Handle);
        Assert.Equal(0, msg.ErrCode);
        Assert.Equal("Ok", msg.ErrText);
    }

    [Fact]
    public void OpenReply_Deserializes_From_Real_Xrouter_Wire_Format()
    {
        // Real xrouter (ghcr.io/packethacking/xrouter) sends errCode and
        // errText with capital C/T on every reply, *not* just AUTHREPLY
        // as the published spec implies.
        var wire = """{"type":"openReply","id":7,"handle":1234,"errCode":0,"errText":"Ok"}""";
        var msg = (OpenReplyMessage)RhpJson.Deserialize(Encoding.UTF8.GetBytes(wire));
        Assert.Equal(0, msg.ErrCode);
        Assert.Equal("Ok", msg.ErrText);
    }

    [Fact]
    public void OpenReply_Serializes_With_CapitalC_ErrCode_To_Match_Xrouter()
    {
        // The library writes the capitalised form on the wire so the
        // mock server's output is byte-identical to the real xrouter's.
        var json = Json(new OpenReplyMessage { Handle = 1, ErrCode = 0, ErrText = "Ok" });
        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.TryGetProperty("errCode", out _));
        Assert.True(doc.RootElement.TryGetProperty("errText", out _));
        Assert.False(doc.RootElement.TryGetProperty("errcode", out _));
        Assert.False(doc.RootElement.TryGetProperty("errtext", out _));
    }

    [Fact]
    public void AuthReply_Deserializes_With_CapitalC_ErrCode()
    {
        // Per the spec AUTHREPLY uses "errCode"/"errText" with capital C.
        var wire = """{"type":"authReply","id":1,"errCode":14,"errText":"Unauthorised"}""";
        var msg = (AuthReplyMessage)RhpJson.Deserialize(Encoding.UTF8.GetBytes(wire));
        Assert.Equal(14, msg.ErrCode);
        Assert.Equal("Unauthorised", msg.ErrText);
    }

    [Fact]
    public void Recv_With_TraceFields_Roundtrips()
    {
        var wire = """{"type":"recv","seqno":11,"handle":50,"data":"hi","action":"rcvd","srce":"M0XYZ","dest":"G8PZT","ctrl":3,"frametype":"RR","rseq":4,"cr":"R","pf":"F"}""";
        var msg = (RecvMessage)RhpJson.Deserialize(Encoding.UTF8.GetBytes(wire));
        Assert.Equal(11, msg.Seqno);
        Assert.Equal(50, msg.Handle);
        Assert.Equal("RR", msg.FrameType);
        Assert.Equal("rcvd", msg.Action);
        Assert.Equal(4, msg.Rseq);
    }

    [Fact]
    public void Status_FromServer_Decodes_Flags()
    {
        var wire = """{"type":"status","seqno":2,"handle":9,"flags":6}""";
        var msg = (StatusMessage)RhpJson.Deserialize(Encoding.UTF8.GetBytes(wire));
        var flags = (StatusFlags)(msg.Flags ?? 0);
        Assert.True(flags.HasFlag(StatusFlags.Connected));
        Assert.True(flags.HasFlag(StatusFlags.Busy));
        Assert.False(flags.HasFlag(StatusFlags.ConOk));
    }

    [Fact]
    public void Accept_Decodes_Child_And_Remote()
    {
        // PWP-0222 spec example writes port as an unquoted number.
        var wire = """{"type":"accept","seqno":3,"handle":1,"child":2,"remote":"M0XYZ","local":"G8PZT","port":2}""";
        var msg = (AcceptMessage)RhpJson.Deserialize(Encoding.UTF8.GetBytes(wire));
        Assert.Equal(1, msg.Handle);
        Assert.Equal(2, msg.Child);
        Assert.Equal("M0XYZ", msg.Remote);
        Assert.Equal("2", msg.Port);
    }

    [Fact]
    public void Accept_Decodes_Port_From_Real_Xrouter_String_Form()
    {
        // Real xrouter sends port as a JSON string, not an unquoted
        // number. The library normalises both forms to string?.
        var wire = """{"type":"accept","seqno":3,"handle":1,"child":2,"remote":"M0XYZ","local":"G8PZT","port":"2"}""";
        var msg = (AcceptMessage)RhpJson.Deserialize(Encoding.UTF8.GetBytes(wire));
        Assert.Equal("2", msg.Port);
    }

    [Fact]
    public void Recv_TraceMode_Decodes_Numeric_Port_And_Extra_Fields()
    {
        // Real xrouter TRACE-mode recv frames carry port as a JSON
        // number (unlike DGRAM where port is a string), plus tseq, ilen,
        // pid, ptcl that the spec doesn't enumerate.
        var wire = """{"type":"recv","seqno":1,"handle":5,"action":"sent","port":1,"srce":"G9DUM","dest":"G9DUM-1","ctrl":0,"frametype":"I","rseq":0,"tseq":0,"cr":"C","ilen":2,"pid":240,"ptcl":"DATA","data":"i\r"}""";
        var msg = (RecvMessage)RhpJson.Deserialize(Encoding.UTF8.GetBytes(wire));
        Assert.Equal("1", msg.Port);          // numeric on the wire, string in the model
        Assert.Equal(0, msg.Tseq);
        Assert.Equal(2, msg.Ilen);
        Assert.Equal(240, msg.Pid);
        Assert.Equal("DATA", msg.Ptcl);
        Assert.Equal("I", msg.FrameType);
    }

    [Fact]
    public void Recv_DgramMode_Decodes_String_Port_And_Addressing()
    {
        // Real xrouter DGRAM-mode recv carries port as a JSON string
        // and includes local/remote addressing.
        var wire = """{"type":"recv","handle":7,"action":"rcvd","port":"2","remote":"G8PZT-3","local":"G9DUM-4","data":"hello UI\r"}""";
        var msg = (RecvMessage)RhpJson.Deserialize(Encoding.UTF8.GetBytes(wire));
        Assert.Equal("2", msg.Port);
        Assert.Equal("G8PZT-3", msg.Remote);
        Assert.Equal("G9DUM-4", msg.Local);
        Assert.Equal("hello UI\r", msg.Data);
    }

    [Fact]
    public void Unknown_Type_Yields_UnknownMessage()
    {
        var wire = """{"type":"newFutureMessage","id":99,"foo":"bar"}""";
        var msg = RhpJson.Deserialize(Encoding.UTF8.GetBytes(wire));
        var unk = Assert.IsType<UnknownMessage>(msg);
        Assert.Equal("newFutureMessage", unk.Type);
        Assert.Equal(99, unk.Id);
    }

    [Fact]
    public void Missing_Type_Throws_ProtocolException()
    {
        var wire = """{"id":1,"errcode":0}""";
        Assert.Throws<RhpProtocolException>(
            () => RhpJson.Deserialize(Encoding.UTF8.GetBytes(wire)));
    }

    [Fact]
    public void ConnectReply_Tolerates_PascalCase_Variant()
    {
        // The PWP-0222 spec writes the type as "ConnectReply" — be forgiving.
        var wire = """{"type":"ConnectReply","id":1,"handle":50,"errcode":0,"errtext":"Ok"}""";
        var msg = RhpJson.Deserialize(Encoding.UTF8.GetBytes(wire));
        var typed = Assert.IsType<ConnectReplyMessage>(msg);
        Assert.Equal(50, typed.Handle);
    }

    [Theory]
    [InlineData("bindReply")]
    [InlineData("listenReply")]
    [InlineData("connectReply")]
    [InlineData("sendReply")]
    [InlineData("sendtoReply")]
    [InlineData("statusReply")]
    [InlineData("closeReply")]
    [InlineData("socketReply")]
    public void ReplyMessages_Serialize_With_CapitalC_ErrCode(string type)
    {
        // Pin the wire-output casing for every reply type produced by the
        // mock so its bytes match what real xrouter emits.
        RhpMessage msg = type switch
        {
            "bindReply"    => new BindReplyMessage    { Handle = 1, ErrCode = 14, ErrText = "x" },
            "listenReply"  => new ListenReplyMessage  { Handle = 1, ErrCode = 14, ErrText = "x" },
            "connectReply" => new ConnectReplyMessage { Handle = 1, ErrCode = 14, ErrText = "x" },
            "sendReply"    => new SendReplyMessage    { Handle = 1, ErrCode = 14, ErrText = "x" },
            "sendtoReply"  => new SendToReplyMessage  { Handle = 1, ErrCode = 14, ErrText = "x" },
            "statusReply"  => new StatusReplyMessage  { Handle = 1, ErrCode = 14, ErrText = "x" },
            "closeReply"   => new CloseReplyMessage   { Handle = 1, ErrCode = 14, ErrText = "x" },
            "socketReply"  => new SocketReplyMessage  { Handle = 1, ErrCode = 14, ErrText = "x" },
            _ => throw new InvalidOperationException(type),
        };

        var json = Json(msg);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(type, doc.RootElement.GetProperty("type").GetString());
        Assert.True(doc.RootElement.TryGetProperty("errCode", out _),
            $"{type} missing errCode capital C: {json}");
        Assert.True(doc.RootElement.TryGetProperty("errText", out _),
            $"{type} missing errText capital T: {json}");
        Assert.False(doc.RootElement.TryGetProperty("errcode", out _),
            $"{type} unexpectedly has lowercase errcode: {json}");
    }

    [Fact]
    public void DataEncoding_PreservesBinaryBytes()
    {
        var bytes = new byte[] { 0x00, 0x7F, 0x80, 0xFF, 0x01, 0x0A };
        var wire = RhpDataEncoding.ToWireString(bytes);
        var back = RhpDataEncoding.FromWireString(wire);
        Assert.Equal(bytes, back);
    }

    [Fact]
    public void Binary_Data_Serializes_To_Ascii_Only_Wire_Bytes()
    {
        // The Latin-1 binary convention only round-trips through xrouter
        // because every non-ASCII code point leaves the serializer as a
        // \u00XX escape, never as raw multi-byte UTF-8. That depends on
        // RhpJson.Options using the default (escape-everything) encoder —
        // pin it so a future Options tweak can't silently corrupt payloads.
        var payload = new byte[256];
        for (var i = 0; i < 256; i++) payload[i] = (byte)i;

        var wire = RhpJson.Serialize(new SendMessage
        {
            Handle = 1,
            Data = RhpDataEncoding.ToWireString(payload),
        });

        Assert.All(wire, b => Assert.True(b < 0x80,
            $"non-ASCII byte 0x{b:X2} on the wire"));

        var back = (SendMessage)RhpJson.Deserialize(wire);
        Assert.Equal(payload, RhpDataEncoding.FromWireString(back.Data));
    }

    [Fact]
    public void Unknown_Type_With_NonNumeric_Id_Still_Yields_UnknownMessage()
    {
        // Forward-compatible frames may shape id/seqno however they like;
        // that must not become a parse error.
        var wire = """{"type":"futureMessage","id":"abc","seqno":"xyz","foo":1}""";
        var msg = RhpJson.Deserialize(Encoding.UTF8.GetBytes(wire));
        var unk = Assert.IsType<UnknownMessage>(msg);
        Assert.Equal("futureMessage", unk.Type);
        Assert.Null(unk.Id);
        Assert.Null(unk.Seqno);
    }
}
