using System.Text;

namespace NdiRouter.Swp08;

/// <summary>A crosspoint request decoded from a controller. Source is null for an interrogate.</summary>
public readonly record struct CrosspointRequest(int Matrix, int Level, int Destination, int? Source);

/// <summary>Which names a controller asked for, and at what length.</summary>
public readonly record struct NameRequest(bool Sources, bool Extended, int Matrix, int Level, int LengthCode)
{
    public int Length => Swp08Codec.NameLengths[LengthCode];
}

/// <summary>
/// Encoders and decoders for the messages this router speaks.
///
/// Normal (SW-P-08) crosspoint messages pack a 10-bit destination and source into four bytes:
/// <code>
///   byte 1  matrix (bits 4-7), level (bits 0-3)
///   byte 2  destination / 128 (bits 4-6), source / 128 (bits 0-2)
///   byte 3  destination mod 128
///   byte 4  source mod 128
/// </code>
/// Extended messages (0x81-0x84, 0x95/0x97, 0xE4-0xEB) carry the matrix and level as whole bytes
/// and destinations and sources as 16-bit big-endian words.
/// </summary>
public static class Swp08Messages
{
    /// <summary>Highest destination or source normal-mode messages can carry.</summary>
    public const int NormalMaxIndex = 1023;

    /// <summary>Highest destination or source the byte-form tally dump may carry (SW-P-88 8.2.3).</summary>
    public const int ByteDumpMaxIndex = 191;

    public static bool FitsNormal(int matrix, int level, int destination, int source) =>
        matrix is >= 0 and <= 15 && level is >= 0 and <= 15 &&
        destination is >= 0 and <= NormalMaxIndex && source is >= 0 and <= NormalMaxIndex;

    // ------------------------------------------------------------------ crosspoints

    public static CrosspointRequest ParseInterrogate(byte[] p)
    {
        Need(p, 3, "INTERROGATE");
        return new CrosspointRequest(p[0] >> 4, p[0] & 0x0F, ((p[1] & 0x70) << 3) | (p[2] & 0x7F), null);
    }

    public static CrosspointRequest ParseConnect(byte[] p)
    {
        Need(p, 4, "CONNECT");
        return new CrosspointRequest(
            p[0] >> 4,
            p[0] & 0x0F,
            ((p[1] & 0x70) << 3) | (p[2] & 0x7F),
            ((p[1] & 0x07) << 7) | (p[3] & 0x7F));
    }

    public static CrosspointRequest ParseExtendedInterrogate(byte[] p)
    {
        Need(p, 4, "EXT INTERROGATE");
        return new CrosspointRequest(p[0], p[1], (p[2] << 8) | p[3], null);
    }

    public static CrosspointRequest ParseExtendedConnect(byte[] p)
    {
        Need(p, 6, "EXT CONNECT");
        return new CrosspointRequest(p[0], p[1], (p[2] << 8) | p[3], (p[4] << 8) | p[5]);
    }

    private static byte[] PackNormal(int matrix, int level, int destination, int source) => new[]
    {
        (byte)(((matrix & 0x0F) << 4) | (level & 0x0F)),
        (byte)((((destination >> 7) & 0x07) << 4) | ((source >> 7) & 0x07)),
        (byte)(destination & 0x7F),
        (byte)(source & 0x7F)
    };

    private static byte[] PackExtended(int matrix, int level, int destination, int source) => new[]
    {
        (byte)matrix, (byte)level,
        (byte)(destination >> 8), (byte)destination,
        (byte)(source >> 8), (byte)source
    };

    /// <summary>CROSSPOINT TALLY (0x03): the answer to an INTERROGATE.</summary>
    public static byte[] Tally(int matrix, int level, int destination, int source) =>
        Swp08Codec.Encode(Swp08Command.CrosspointTally, PackNormal(matrix, level, destination, source));

    /// <summary>CROSSPOINT CONNECTED (0x04): sent on every port once a route has been made.</summary>
    public static byte[] Connected(int matrix, int level, int destination, int source) =>
        Swp08Codec.Encode(Swp08Command.CrosspointConnected, PackNormal(matrix, level, destination, source));

    public static byte[] ExtendedTally(int matrix, int level, int destination, int source) =>
        Swp08Codec.Encode(Swp08Command.ExtendedTally, PackExtended(matrix, level, destination, source));

    public static byte[] ExtendedConnected(int matrix, int level, int destination, int source) =>
        Swp08Codec.Encode(Swp08Command.ExtendedConnected, PackExtended(matrix, level, destination, source));

    // ------------------------------------------------------------------ tally dumps

    public static (int Matrix, int Level) ParseTallyDumpRequest(byte[] p)
    {
        Need(p, 1, "TALLY DUMP REQUEST");
        return (p[0] >> 4, p[0] & 0x0F);
    }

    public static (int Matrix, int Level) ParseExtendedTallyDumpRequest(byte[] p)
    {
        Need(p, 2, "EXT TALLY DUMP REQUEST");
        return (p[0], p[1]);
    }

    /// <summary>
    /// CROSSPOINT TALLY DUMP responses covering every destination, in order. SW-P-88 8.2.3 says the
    /// byte form (0x16) "assumes a maximum destination/source number of 191", so it is used only
    /// within that; beyond it, the word form (0x17). Split so no message exceeds 64 tallies
    /// (8.2.4) or the 133-byte message limit.
    /// </summary>
    /// <param name="sources">The source routed to each destination, by destination index, already encoded.</param>
    public static List<byte[]> TallyDump(int matrix, int level, IReadOnlyList<int> sources)
    {
        var frames = new List<byte[]>();
        var matrixLevel = (byte)(((matrix & 0x0F) << 4) | (level & 0x0F));
        var useBytes = sources.Count <= ByteDumpMaxIndex + 1 && sources.All(s => s is >= 0 and <= ByteDumpMaxIndex);

        if (useBytes)
        {
            // command, matrix/level, count, first destination, then one byte per tally
            var perMessage = Math.Min(Swp08Codec.MaxTalliesPerMessage, Swp08Codec.MaxDataLength - 4);
            for (var first = 0; first < sources.Count; first += perMessage)
            {
                var count = Math.Min(perMessage, sources.Count - first);
                var payload = new byte[3 + count];
                payload[0] = matrixLevel;
                payload[1] = (byte)count;
                payload[2] = (byte)first;
                for (var i = 0; i < count; i++) payload[3 + i] = (byte)sources[first + i];
                frames.Add(Swp08Codec.Encode(Swp08Command.CrosspointTallyDumpByte, payload));
            }
        }
        else
        {
            // command, matrix/level, count, first destination (word), then one word per tally
            var perMessage = Math.Min(Swp08Codec.MaxTalliesPerMessage, (Swp08Codec.MaxDataLength - 5) / 2);
            for (var first = 0; first < sources.Count; first += perMessage)
            {
                var count = Math.Min(perMessage, sources.Count - first);
                var payload = new byte[4 + 2 * count];
                payload[0] = matrixLevel;
                payload[1] = (byte)count;
                payload[2] = (byte)(first >> 8);
                payload[3] = (byte)first;
                for (var i = 0; i < count; i++)
                {
                    payload[4 + 2 * i] = (byte)(sources[first + i] >> 8);
                    payload[5 + 2 * i] = (byte)sources[first + i];
                }
                frames.Add(Swp08Codec.Encode(Swp08Command.CrosspointTallyDumpWord, payload));
            }
        }

        return frames;
    }

    /// <summary>EXTENDED CROSSPOINT TALLY DUMP (0x97): matrix and level as whole bytes, then as the word form.</summary>
    public static List<byte[]> ExtendedTallyDump(int matrix, int level, IReadOnlyList<int> sources)
    {
        var frames = new List<byte[]>();
        var perMessage = Math.Min(Swp08Codec.MaxTalliesPerMessage, (Swp08Codec.MaxDataLength - 6) / 2);
        for (var first = 0; first < sources.Count; first += perMessage)
        {
            var count = Math.Min(perMessage, sources.Count - first);
            var payload = new byte[5 + 2 * count];
            payload[0] = (byte)matrix;
            payload[1] = (byte)level;
            payload[2] = (byte)count;
            payload[3] = (byte)(first >> 8);
            payload[4] = (byte)first;
            for (var i = 0; i < count; i++)
            {
                payload[5 + 2 * i] = (byte)(sources[first + i] >> 8);
                payload[6 + 2 * i] = (byte)sources[first + i];
            }
            frames.Add(Swp08Codec.Encode(Swp08Command.ExtendedTallyDumpWord, payload));
        }

        return frames;
    }

    // ------------------------------------------------------------------ names

    /// <summary>
    /// Decodes a GET SOURCE / DEST NAMES request:
    /// <code>
    ///   0x64 / 0x66   matrix (bits 4-7) and level (bits 0-3), length code
    ///   0xE4          matrix, level, length code
    ///   0xE6          matrix, length code
    /// </code>
    /// </summary>
    public static NameRequest ParseNameRequest(byte command, byte[] p)
    {
        NameRequest request;
        switch (command)
        {
            case Swp08Command.GetSourceNames:
                Need(p, 2, "GET SOURCE NAMES");
                request = new NameRequest(true, false, p[0] >> 4, p[0] & 0x0F, p[1]);
                break;
            case Swp08Command.GetDestNames:
                Need(p, 2, "GET DEST NAMES");
                request = new NameRequest(false, false, p[0] >> 4, p[0] & 0x0F, p[1]);
                break;
            case Swp08Command.ExtendedGetSourceNames:
                Need(p, 3, "EXT GET SOURCE NAMES");
                request = new NameRequest(true, true, p[0], p[1], p[2]);
                break;
            case Swp08Command.ExtendedGetDestNames:
                Need(p, 2, "EXT GET DEST NAMES");
                request = new NameRequest(false, true, p[0], 0, p[1]);
                break;
            default:
                throw new FormatException($"0x{command:X2} is not a name request");
        }

        if (request.LengthCode >= Swp08Codec.NameLengths.Length)
            throw new FormatException($"unknown name length code {request.LengthCode}");
        return request;
    }

    /// <summary>
    /// SOURCE / DEST NAMES responses covering every name, split across as many messages as the
    /// size limit needs:
    /// <code>
    ///   0x6A / 0x6B   matrix/level byte, length code, first index (word), count, names
    ///   0xEA          matrix, level, length code, first index (word), count, names
    ///   0xEB          matrix, length code, first index (word), count, names
    /// </code>
    /// Each name is ASCII, space padded or cut to the requested length.
    /// </summary>
    public static List<byte[]> NameResponses(NameRequest request, IReadOnlyList<string> names)
    {
        var command = (request.Sources, request.Extended) switch
        {
            (true, false) => Swp08Command.SourceNamesResponse,
            (false, false) => Swp08Command.DestNamesResponse,
            (true, true) => Swp08Command.ExtendedSourceNamesResponse,
            _ => Swp08Command.ExtendedDestNamesResponse
        };

        var header = new List<byte>();
        if (request.Extended)
        {
            header.Add((byte)request.Matrix);
            if (request.Sources) header.Add((byte)request.Level);
        }
        else
        {
            header.Add((byte)(((request.Matrix & 0x0F) << 4) | (request.Level & 0x0F)));
        }
        header.Add((byte)request.LengthCode);

        var length = request.Length;
        var room = Swp08Codec.MaxDataLength - 1 - header.Count - 3; // command, header, first index, count
        var perMessage = Math.Max(1, Math.Min(255, room / length));

        var frames = new List<byte[]>();
        for (var first = 0; first < names.Count; first += perMessage)
        {
            var count = Math.Min(perMessage, names.Count - first);
            var payload = new List<byte>(header) { (byte)(first >> 8), (byte)first, (byte)count };
            for (var i = 0; i < count; i++) payload.AddRange(FixedName(names[first + i], length));
            frames.Add(Swp08Codec.Encode(command, payload.ToArray()));
        }

        return frames;
    }

    /// <summary>Printable ASCII, cut or space padded to exactly <paramref name="length"/> characters.</summary>
    public static byte[] FixedName(string name, int length)
    {
        var bytes = new byte[length];
        Array.Fill(bytes, (byte)' ');
        var text = name ?? string.Empty;
        var count = 0;
        foreach (var c in text)
        {
            if (count >= length) break;
            bytes[count++] = c is >= ' ' and <= '~' ? (byte)c : (byte)'?';
        }
        return bytes;
    }

    /// <summary>
    /// NAMES UPDATED (0x1E, SW-P-08 Issue 32): tells controllers to re-read names. A 32-bit
    /// matrix bitmap (bit 0 = the first matrix), then 32 bits of name-type flags, both high byte
    /// first. Flags 0, 1 and 8 are 4, 8 and 12 character source names; 2, 3 and 9 the
    /// destination equivalents.
    /// </summary>
    public static byte[] NamesUpdated(int matrix, bool sources, bool destinations)
    {
        var bitmap = matrix is >= 0 and < 32 ? 1u << matrix : 0xFFFFFFFFu;
        var flags = 0u;
        if (sources) flags |= (1u << 0) | (1u << 1) | (1u << 8);
        if (destinations) flags |= (1u << 2) | (1u << 3) | (1u << 9);

        return Swp08Codec.Encode(Swp08Command.NamesUpdated,
            (byte)(bitmap >> 24), (byte)(bitmap >> 16), (byte)(bitmap >> 8), (byte)bitmap,
            (byte)(flags >> 24), (byte)(flags >> 16), (byte)(flags >> 8), (byte)flags);
    }

    // ------------------------------------------------------------------ housekeeping

    /// <summary>Commands this router sends.</summary>
    public static readonly byte[] TransmittedCommands =
    {
        Swp08Command.CrosspointTally, Swp08Command.CrosspointConnected,
        Swp08Command.DualControllerStatusResponse,
        Swp08Command.CrosspointTallyDumpByte, Swp08Command.CrosspointTallyDumpWord,
        Swp08Command.NamesUpdated, Swp08Command.ProtocolImplementationResponse,
        Swp08Command.SourceNamesResponse, Swp08Command.DestNamesResponse,
        Swp08Command.ExtendedTally, Swp08Command.ExtendedConnected, Swp08Command.ExtendedTallyDumpWord,
        Swp08Command.ExtendedSourceNamesResponse, Swp08Command.ExtendedDestNamesResponse
    };

    /// <summary>Commands this router acts on.</summary>
    public static readonly byte[] ReceivedCommands =
    {
        Swp08Command.CrosspointInterrogate, Swp08Command.CrosspointConnect,
        Swp08Command.DualControllerStatusRequest, Swp08Command.CrosspointTallyDumpRequest,
        Swp08Command.ProtocolImplementationRequest,
        Swp08Command.GetSourceNames, Swp08Command.GetDestNames,
        Swp08Command.ExtendedInterrogate, Swp08Command.ExtendedConnect, Swp08Command.ExtendedTallyDumpRequest,
        Swp08Command.ExtendedGetSourceNames, Swp08Command.ExtendedGetDestNames
    };

    /// <summary>
    /// PROTOCOL IMPLEMENTATION RESPONSE (0x62): how many commands this router sends, how many it
    /// accepts, then the two lists - the layout of SW-P-88 Appendix 1, 10.1, whose example is
    /// <c>62 08 09</c> followed by 8 transmitted and 9 received command numbers. Companion asks for
    /// this on connect and only uses the commands listed, so leaving one out silently disables it.
    /// </summary>
    public static byte[] ProtocolImplementation()
    {
        var payload = new List<byte> { (byte)TransmittedCommands.Length, (byte)ReceivedCommands.Length };
        payload.AddRange(TransmittedCommands);
        payload.AddRange(ReceivedCommands);
        return Swp08Codec.Encode(Swp08Command.ProtocolImplementationResponse, payload.ToArray());
    }

    /// <summary>
    /// DUAL CONTROLLER STATUS RESPONSE (0x09): this controller is the master and is active.
    /// SW-P-88 11.5.1 says only that bit 1 of byte 1 tells active from idle. SW-P-02 Issue 37 defines
    /// the same message fully - bit 0 master (0) or slave (1), bit 1 active (1) or idle (0), then a
    /// byte for the idle controller's health (0 = OK) - and that is what this sends. Bit 1 matters: a
    /// controller managing a redundant pair sends nothing to a controller that says it is idle.
    /// </summary>
    public static byte[] DualControllerStatus() =>
        Swp08Codec.Encode(Swp08Command.DualControllerStatusResponse, 0x02, 0x00);

    private static void Need(byte[] payload, int length, string what)
    {
        if (payload.Length < length)
            throw new FormatException($"{what} needs {length} payload bytes, got {payload.Length}");
    }
}
