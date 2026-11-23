namespace NdiRouter.Swp08;

/// <summary>SW-P-08 command bytes (the first DATA byte of every message).</summary>
public static class Swp08Command
{
    public const byte CrosspointInterrogate = 0x01;
    public const byte CrosspointConnect = 0x02;
    public const byte CrosspointTally = 0x03;
    public const byte CrosspointConnected = 0x04;
    public const byte DualControllerStatusRequest = 0x08;
    public const byte DualControllerStatusResponse = 0x09;
    public const byte CrosspointTallyDumpRequest = 0x15;
    public const byte CrosspointTallyDumpByte = 0x16;
    public const byte CrosspointTallyDumpWord = 0x17;
    public const byte NamesUpdated = 0x1E;
    public const byte ProtocolImplementationRequest = 0x61;
    public const byte ProtocolImplementationResponse = 0x62;
    public const byte GetSourceNames = 0x64;
    public const byte GetDestNames = 0x66;
    public const byte SourceNamesResponse = 0x6A;
    public const byte DestNamesResponse = 0x6B;
    public const byte ExtendedInterrogate = 0x81;
    public const byte ExtendedConnect = 0x82;
    public const byte ExtendedTally = 0x83;
    public const byte ExtendedConnected = 0x84;
    public const byte ExtendedTallyDumpRequest = 0x95;
    public const byte ExtendedTallyDumpWord = 0x97;
    public const byte ExtendedGetSourceNames = 0xE4;
    public const byte ExtendedGetDestNames = 0xE6;
    public const byte ExtendedSourceNamesResponse = 0xEA;
    public const byte ExtendedDestNamesResponse = 0xEB;

    public static string Name(byte command) => command switch
    {
        CrosspointInterrogate => "INTERROGATE",
        CrosspointConnect => "CONNECT",
        CrosspointTally => "TALLY",
        CrosspointConnected => "CONNECTED",
        DualControllerStatusRequest => "DUAL CONTROLLER STATUS REQUEST",
        DualControllerStatusResponse => "DUAL CONTROLLER STATUS",
        CrosspointTallyDumpRequest => "TALLY DUMP REQUEST",
        CrosspointTallyDumpByte => "TALLY DUMP (BYTE)",
        CrosspointTallyDumpWord => "TALLY DUMP (WORD)",
        NamesUpdated => "NAMES UPDATED",
        ProtocolImplementationRequest => "PROTOCOL IMPLEMENTATION REQUEST",
        ProtocolImplementationResponse => "PROTOCOL IMPLEMENTATION",
        GetSourceNames => "GET SOURCE NAMES",
        GetDestNames => "GET DEST NAMES",
        SourceNamesResponse => "SOURCE NAMES",
        DestNamesResponse => "DEST NAMES",
        ExtendedInterrogate => "EXT INTERROGATE",
        ExtendedConnect => "EXT CONNECT",
        ExtendedTally => "EXT TALLY",
        ExtendedConnected => "EXT CONNECTED",
        ExtendedTallyDumpRequest => "EXT TALLY DUMP REQUEST",
        ExtendedTallyDumpWord => "EXT TALLY DUMP",
        ExtendedGetSourceNames => "EXT GET SOURCE NAMES",
        ExtendedGetDestNames => "EXT GET DEST NAMES",
        ExtendedSourceNamesResponse => "EXT SOURCE NAMES",
        ExtendedDestNamesResponse => "EXT DEST NAMES",
        _ => $"0x{command:X2}"
    };
}

/// <summary>One decoded message: its command byte and the bytes after it.</summary>
public sealed record Swp08Frame(byte Command, byte[] Payload);

public enum Swp08ItemKind
{
    Frame,
    PeerAck,
    PeerNak,

    /// <summary>A frame failed its byte count or checksum; the receiver must NAK it.</summary>
    Corrupt
}

public readonly record struct Swp08Item(Swp08ItemKind Kind, Swp08Frame? Frame = null, string? Error = null);

/// <summary>
/// SW-P-08 framing: <c>DLE STX DATA BTC CHK DLE ETX</c>, where DATA is the command and its
/// payload, BTC is the byte count of DATA, CHK is the two's complement of the sum of DATA and
/// BTC, and any DLE between STX and ETX is sent twice.
/// </summary>
public static class Swp08Codec
{
    public const byte Dle = 0x10;
    public const byte Stx = 0x02;
    public const byte Etx = 0x03;
    public const byte Ack = 0x06;
    public const byte Nak = 0x15;

    /// <summary>
    /// Largest DATA (command plus payload) this router sends in one message. The spec bounds a
    /// whole message at 133 bytes; framing, BTC and CHK take six of them.
    /// </summary>
    public const int MaxDataLength = 127;

    /// <summary>The protocol allows at most 64 tallies in one tally dump message.</summary>
    public const int MaxTalliesPerMessage = 64;

    /// <summary>Name length codes 0-4. 16 and 32 characters arrived with SW-P-08 Issue 32.</summary>
    public static readonly int[] NameLengths = { 4, 8, 12, 16, 32 };

    public static byte[] AckBytes() => new[] { Dle, Ack };
    public static byte[] NakBytes() => new[] { Dle, Nak };

    public static byte[] Encode(byte command, ReadOnlySpan<byte> payload)
    {
        var length = 1 + payload.Length;
        if (length > 255) throw new ArgumentException("SW-P-08 message data exceeds 255 bytes.", nameof(payload));

        Span<byte> body = stackalloc byte[length + 2];
        body[0] = command;
        payload.CopyTo(body[1..]);
        body[length] = (byte)length;

        var sum = 0;
        for (var i = 0; i <= length; i++) sum += body[i];
        body[length + 1] = (byte)(-sum & 0xFF);

        var frame = new List<byte>(body.Length + 8) { Dle, Stx };
        foreach (var b in body)
        {
            frame.Add(b);
            if (b == Dle) frame.Add(Dle);
        }
        frame.Add(Dle);
        frame.Add(Etx);
        return frame.ToArray();
    }

    public static byte[] Encode(byte command, params byte[] payload) => Encode(command, payload.AsSpan());

    /// <summary>Validates and splits an unstuffed <c>DATA BTC CHK</c> run.</summary>
    public static bool TryDecodeBody(ReadOnlySpan<byte> body, out Swp08Frame? frame, out string? error)
    {
        frame = null;
        if (body.Length < 3)
        {
            error = "message is too short to hold a command, byte count and checksum";
            return false;
        }

        var declared = body[^2];
        if (declared != body.Length - 2)
        {
            error = $"byte count {declared} does not match the {body.Length - 2} data bytes received";
            return false;
        }

        var sum = 0;
        foreach (var b in body) sum += b; // DATA + BTC + CHK sums to zero
        if ((sum & 0xFF) != 0)
        {
            error = "checksum does not validate";
            return false;
        }

        frame = new Swp08Frame(body[0], body[1..^2].ToArray());
        error = null;
        return true;
    }
}

/// <summary>Reassembles SW-P-08 messages, ACKs and NAKs out of a TCP byte stream.</summary>
public sealed class Swp08FrameDecoder
{
    private const int MaxBuffer = 8192;
    private readonly List<byte> _buffer = new();

    public List<Swp08Item> Feed(ReadOnlySpan<byte> data)
    {
        foreach (var b in data) _buffer.Add(b);

        var items = new List<Swp08Item>();
        while (TakeOne(out var item))
        {
            if (item is { } found) items.Add(found);
        }

        // A stream that never forms a valid frame must not grow without bound.
        if (_buffer.Count > MaxBuffer) _buffer.RemoveRange(0, _buffer.Count - 64);
        return items;
    }

    /// <summary>Consumes from the front of the buffer. Returns false when more bytes are needed.</summary>
    private bool TakeOne(out Swp08Item? item)
    {
        item = null;

        // Anything before a DLE is line noise.
        var start = _buffer.IndexOf(Swp08Codec.Dle);
        if (start < 0)
        {
            _buffer.Clear();
            return false;
        }
        if (start > 0) _buffer.RemoveRange(0, start);
        if (_buffer.Count < 2) return false;

        switch (_buffer[1])
        {
            case Swp08Codec.Ack:
                _buffer.RemoveRange(0, 2);
                item = new Swp08Item(Swp08ItemKind.PeerAck);
                return true;

            case Swp08Codec.Nak:
                _buffer.RemoveRange(0, 2);
                item = new Swp08Item(Swp08ItemKind.PeerNak);
                return true;

            case Swp08Codec.Stx:
                break;

            default:
                _buffer.RemoveAt(0); // a DLE that starts nothing
                return true;
        }

        // Walk the frame, undoing DLE stuffing as we go. Scanning for "DLE ETX" without tracking
        // the stuffing would mis-read a data 0x10 (sent as DLE DLE) followed by a data 0x03 as
        // the end of the frame.
        var body = new List<byte>();
        var j = 2;
        while (j < _buffer.Count)
        {
            var b = _buffer[j];
            if (b != Swp08Codec.Dle)
            {
                body.Add(b);
                j++;
                continue;
            }

            if (j + 1 >= _buffer.Count) return false; // wait for the byte after the DLE

            var next = _buffer[j + 1];
            if (next == Swp08Codec.Dle)
            {
                body.Add(Swp08Codec.Dle);
                j += 2;
                continue;
            }

            if (next == Swp08Codec.Etx)
            {
                _buffer.RemoveRange(0, j + 2);
                item = Swp08Codec.TryDecodeBody(body.ToArray(), out var frame, out var error)
                    ? new Swp08Item(Swp08ItemKind.Frame, frame)
                    : new Swp08Item(Swp08ItemKind.Corrupt, Error: error);
                return true;
            }

            // DLE followed by anything else inside a frame: the frame was cut short. Throw away
            // what we have and resynchronise on this DLE.
            _buffer.RemoveRange(0, j);
            item = new Swp08Item(Swp08ItemKind.Corrupt, Error: "frame interrupted before its end marker");
            return true;
        }

        return false;
    }
}
