using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GunWall.Services.Remote;

/// <summary>
/// The engine pipe's wire format (stage 4a): a 4-byte little-endian length, then
/// that many bytes of UTF-8 JSON. One request gets one response with the same id;
/// the service also sends state messages on its own. Nothing here trusts the other
/// side: sizes are capped and a malformed frame ends the connection.
/// </summary>
public static class EngineWire
{
    /// <summary>Version 1 of the protocol. A window and a service that disagree
    /// refuse to talk ("GunWall is updating - restart the window").</summary>
    public const int ProtocolVersion = 3;   // 2: detected and notice messages (0.99.208); 3: parts (0.99.218)

    /// <summary>The largest single frame either side accepts. A larger message is
    /// sent as several frames (0.99.218) - a long blocklist, a pasted list.</summary>
    public const int MaxMessageBytes = 1024 * 1024;

    /// <summary>The largest message after its parts are joined. Bounds what a
    /// misbehaving peer can make the other side hold in memory.</summary>
    public const int MaxAssembledBytes = 16 * 1024 * 1024;

    /// <summary>Set in a frame's length when more parts of the same message follow.</summary>
    private const int MoreFlag = 0x4000_0000;

    /// <summary>Beside the hand-over pipe, not instead of it, until 4b.</summary>
    public const string PipeName = "GunWall.Engine.v1";

    /// <summary>Fields included: several engine results are value tuples
    /// (ListBackups), whose items are fields, not properties.</summary>
    public static readonly JsonSerializerOptions Json = new()
    {
        IncludeFields = true,
        // DetectionResult's lists are get-only; fill them instead of skipping them.
        PreferredObjectCreationHandling = JsonObjectCreationHandling.Populate,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = false,
        NumberHandling = JsonNumberHandling.Strict,
        MaxDepth = 32,
    };

    public static void Write(Stream s, EngineMessage m)
    {
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(m, Json);
        if (body.Length > MaxAssembledBytes) throw new InvalidDataException($"message of {body.Length} bytes is over the limit");
        int off = 0;
        do
        {
            int n = Math.Min(MaxMessageBytes, body.Length - off);
            bool more = off + n < body.Length;
            var len = BitConverter.GetBytes(n | (more ? MoreFlag : 0));
            if (!BitConverter.IsLittleEndian) Array.Reverse(len);
            s.Write(len, 0, 4);
            s.Write(body, off, n);
            off += n;
        } while (off < body.Length);
        s.Flush();
    }

    /// <summary>Null at a clean end of stream.</summary>
    public static EngineMessage? Read(Stream s)
    {
        var len = new byte[4];
        byte[]? body = null;
        using var joined = new MemoryStream();
        bool more;
        do
        {
            if (!ReadExactly(s, len))
            {
                if (joined.Length == 0) return null;
                throw new EndOfStreamException("message cut short between parts");
            }
            var le = (byte[])len.Clone();
            if (!BitConverter.IsLittleEndian) Array.Reverse(le);
            int raw = BitConverter.ToInt32(le, 0);
            more = (raw & MoreFlag) != 0;
            int n = raw & ~MoreFlag;
            if (raw < 0 || n <= 0 || n > MaxMessageBytes) throw new InvalidDataException($"frame length {n} is out of range");
            if (joined.Length + n > MaxAssembledBytes) throw new InvalidDataException("message over the joined size limit");
            var part = new byte[n];
            if (!ReadExactly(s, part)) throw new EndOfStreamException("frame cut short");
            if (!more && joined.Length == 0) body = part;      // the usual case: one frame
            else joined.Write(part, 0, n);
        } while (more);
        body ??= joined.ToArray();
        return JsonSerializer.Deserialize<EngineMessage>(body, Json)
               ?? throw new InvalidDataException("empty message");
    }

    private static bool ReadExactly(Stream s, byte[] buf)
    {
        int got = 0;
        while (got < buf.Length)
        {
            int r = s.Read(buf, got, buf.Length - got);
            if (r == 0)
            {
                if (got == 0) return false;
                throw new EndOfStreamException("frame cut short");
            }
            got += r;
        }
        return true;
    }

    public static JsonElement ToElement(object? value, Type type) =>
        JsonSerializer.SerializeToElement(value, type, Json);

    public static object? FromElement(JsonElement? e, Type type) =>
        e is not { ValueKind: not JsonValueKind.Undefined } el ? null : el.Deserialize(type, Json);
}

/// <summary>Every message on the engine pipe. Which fields are set depends on
/// <see cref="Kind"/>.</summary>
public sealed class EngineMessage
{
    /// <summary>hello, call, reply, state.</summary>
    public string Kind { get; set; } = "";
    public long Id { get; set; }

    // hello (both directions)
    public int Protocol { get; set; }
    public string AppVersion { get; set; } = "";

    // call
    public string Member { get; set; } = "";
    public List<JsonElement> Args { get; set; } = new();

    // reply
    public bool Ok { get; set; }
    public string Error { get; set; } = "";
    /// <summary>"denied" when the call was refused rather than failed.</summary>
    public string ErrorKind { get; set; } = "";
    public JsonElement? Result { get; set; }
    /// <summary>Values of out parameters, in order.</summary>
    public List<JsonElement> Outs { get; set; } = new();

    // state (also on a reply to anything that is not a read)
    public Dictionary<string, JsonElement>? State { get; set; }
    // detected (service -> window): what detection decided since the last batch -
    // packet-log rows, activity, prompts. notice: what enforcement wants said.
    public List<DetectionResult>? Detected { get; set; }
    public List<UpkeepNotice>? Notices { get; set; }

    /// <summary>Increases with every state the service captures. A reply's state
    /// and a pushed state arrive on different threads; the newer one wins.</summary>
    public long StateSeq { get; set; }
}
