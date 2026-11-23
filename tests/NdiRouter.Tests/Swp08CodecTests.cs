using NdiRouter.Swp08;

namespace NdiRouter.Tests;

public class Swp08CodecTests
{
    private static byte[] Frame(params byte[] body) =>
        new byte[] { 0x10, 0x02 }.Concat(body).Concat(new byte[] { 0x10, 0x03 }).ToArray();

    // ---- SW-P-88 Issue 4b, Appendix 1 worked examples, byte for byte ----

    [Fact]
    public void Spec_protocol_implementation_request()
    {
        // Section 10.1: "10 02 61 01 9E 10 03"
        Assert.Equal(Frame(0x61, 0x01, 0x9E), Swp08Codec.Encode(0x61));
    }

    [Fact]
    public void Spec_protocol_implementation_reply()
    {
        // Section 10.1: "10 02 62 08 09 03 04 16 20 22 53 62 63 01 02 15 1F 20 21 43 4A 61 14 9C 10 03"
        // - 8 commands sent, 9 received, then the two lists: the layout ProtocolImplementation() uses.
        var payload = new byte[]
        {
            0x08, 0x09,
            0x03, 0x04, 0x16, 0x20, 0x22, 0x53, 0x62, 0x63,
            0x01, 0x02, 0x15, 0x1F, 0x20, 0x21, 0x43, 0x4A, 0x61
        };
        Assert.Equal(Frame(new byte[] { 0x62 }.Concat(payload).Concat(new byte[] { 0x14, 0x9C }).ToArray()),
            Swp08Codec.Encode(0x62, payload));

        var ours = Decode(Swp08Messages.ProtocolImplementation()).Payload;
        Assert.Equal(2 + ours[0] + ours[1], ours.Length);
    }

    [Fact]
    public void Spec_interrogate()
    {
        // Section 10.2: matrix 1 level 1 destination 1 (wire numbering)
        var payload = new byte[] { 0x11, 0x00, 0x01 };
        Assert.Equal(Frame(0x01, 0x11, 0x00, 0x01, 0x04, 0xE9), Swp08Codec.Encode(0x01, payload));
        Assert.Equal(new CrosspointRequest(1, 1, 1, null), Swp08Messages.ParseInterrogate(payload));
    }

    [Fact]
    public void Spec_tally_reply()
    {
        // Section 10.2: source 153 routed to destination 1 on level 1
        Assert.Equal(Frame(0x03, 0x11, 0x01, 0x01, 0x19, 0x05, 0xCC), Swp08Messages.Tally(1, 1, 1, 153));
    }

    [Fact]
    public void Spec_connect_and_connected()
    {
        // Section 10.3: matrix 2 / level 2 / destination 3 / source 1 in the document's 1-based numbering
        var payload = new byte[] { 0x11, 0x00, 0x02, 0x00 };
        Assert.Equal(Frame(0x02, 0x11, 0x00, 0x02, 0x00, 0x05, 0xE6), Swp08Codec.Encode(0x02, payload));
        Assert.Equal(new CrosspointRequest(1, 1, 2, 0), Swp08Messages.ParseConnect(payload));
        Assert.Equal(Frame(0x04, 0x11, 0x00, 0x02, 0x00, 0x05, 0xE4), Swp08Messages.Connected(1, 1, 2, 0));
    }

    // ---- framing ----

    [Fact]
    public void Dle_in_data_is_doubled_and_survives_a_round_trip()
    {
        // destination 16 = 0x10 = DLE in byte 3
        var encoded = Swp08Messages.Connected(0, 0, 16, 3);
        var decoder = new Swp08FrameDecoder();
        var items = decoder.Feed(encoded);

        var frame = Assert.Single(items).Frame!;
        Assert.Equal(Swp08Command.CrosspointConnected, frame.Command);
        Assert.Equal(new byte[] { 0x00, 0x00, 0x10, 0x03 }, frame.Payload);
    }

    [Fact]
    public void Stuffed_dle_followed_by_0x03_is_not_mistaken_for_the_end_of_the_frame()
    {
        // data ... 0x10 0x03 ... is sent as 10 10 03; a naive "DLE ETX" scan ends the frame early.
        var encoded = Swp08Messages.Connected(0, 0, 0x10, 0x03);
        var decoder = new Swp08FrameDecoder();

        var item = Assert.Single(decoder.Feed(encoded));
        Assert.Equal(Swp08ItemKind.Frame, item.Kind);
        Assert.Equal(new byte[] { 0x00, 0x00, 0x10, 0x03 }, item.Frame!.Payload);
    }

    [Fact]
    public void Frames_split_across_reads_and_mixed_with_acks_are_reassembled()
    {
        var stream = Swp08Codec.AckBytes()
            .Concat(Swp08Messages.Tally(0, 0, 5, 7))
            .Concat(Swp08Codec.NakBytes())
            .Concat(Swp08Messages.Connected(0, 0, 200, 900))
            .ToArray();

        var decoder = new Swp08FrameDecoder();
        var items = new List<Swp08Item>();
        foreach (var b in stream) items.AddRange(decoder.Feed(new[] { b })); // one byte at a time

        Assert.Equal(
            new[] { Swp08ItemKind.PeerAck, Swp08ItemKind.Frame, Swp08ItemKind.PeerNak, Swp08ItemKind.Frame },
            items.Select(i => i.Kind));
        Assert.Equal(new CrosspointRequest(0, 0, 200, 900), Swp08Messages.ParseConnect(items[3].Frame!.Payload));
    }

    [Fact]
    public void Bad_checksum_is_reported_as_corrupt()
    {
        var encoded = Swp08Messages.Tally(0, 0, 1, 2);
        encoded[^3] ^= 0x01; // flip a checksum bit
        var item = Assert.Single(new Swp08FrameDecoder().Feed(encoded));
        Assert.Equal(Swp08ItemKind.Corrupt, item.Kind);
    }

    [Fact]
    public void Garbage_before_a_frame_is_skipped()
    {
        var stream = new byte[] { 0x55, 0xAA, 0x10, 0x99 }.Concat(Swp08Messages.Tally(0, 0, 1, 2)).ToArray();
        var item = Assert.Single(new Swp08FrameDecoder().Feed(stream));
        Assert.Equal(Swp08ItemKind.Frame, item.Kind);
    }

    // ---- messages ----

    [Fact]
    public void Normal_crosspoint_packs_ten_bit_numbers()
    {
        var frame = new Swp08FrameDecoder().Feed(Swp08Messages.Connected(2, 3, 1000, 513))[0].Frame!;
        Assert.Equal(new CrosspointRequest(2, 3, 1000, 513), Swp08Messages.ParseConnect(frame.Payload));
    }

    [Fact]
    public void Extended_crosspoint_uses_words()
    {
        var frame = new Swp08FrameDecoder().Feed(Swp08Messages.ExtendedConnected(20, 5, 4000, 65000))[0].Frame!;
        Assert.Equal(Swp08Command.ExtendedConnected, frame.Command);
        Assert.Equal(new CrosspointRequest(20, 5, 4000, 65000), Swp08Messages.ParseExtendedConnect(frame.Payload));
    }

    [Fact]
    public void Tally_dump_uses_bytes_when_everything_fits_and_words_when_not()
    {
        var small = Swp08Messages.TallyDump(0, 0, new[] { 3, 1, 2 });
        var frame = Decode(Assert.Single(small));
        Assert.Equal(Swp08Command.CrosspointTallyDumpByte, frame.Command);
        Assert.Equal(new byte[] { 0x00, 3, 0, 3, 1, 2 }, frame.Payload);

        // SW-P-88 8.2.3: the byte form assumes nothing above 191.
        Assert.Equal(Swp08Command.CrosspointTallyDumpByte,
            Decode(Swp08Messages.TallyDump(0, 0, new[] { 191 })[0]).Command);
        Assert.Equal(Swp08Command.CrosspointTallyDumpWord,
            Decode(Swp08Messages.TallyDump(0, 0, new[] { 192 })[0]).Command);
        Assert.All(Swp08Messages.TallyDump(0, 0, Enumerable.Repeat(0, 193).ToList()),
            f => Assert.Equal(Swp08Command.CrosspointTallyDumpWord, Decode(f).Command)); // destination 192

        var withClear = Swp08Messages.TallyDump(0, 0, new[] { 3, 1023 });
        frame = Decode(Assert.Single(withClear));
        Assert.Equal(Swp08Command.CrosspointTallyDumpWord, frame.Command);
        Assert.Equal(new byte[] { 0x00, 2, 0, 0, 0, 3, 0x03, 0xFF }, frame.Payload);
    }

    [Fact]
    public void Tally_dump_never_puts_more_than_64_tallies_in_one_message()
    {
        // 150 destinations: within the byte form's 191, so the first destination is one byte.
        var sources = Enumerable.Range(0, 150).Select(i => i % 50).ToList();
        var frames = Swp08Messages.TallyDump(0, 0, sources).Select(Decode).ToList();

        Assert.All(frames, f => Assert.Equal(Swp08Command.CrosspointTallyDumpByte, f.Command));
        Assert.All(frames, f => Assert.True(f.Payload[1] <= 64));
        Assert.Equal(150, frames.Sum(f => f.Payload[1]));
        Assert.Equal(new[] { 0, 64, 128 }, frames.Select(f => (int)f.Payload[2]));

        // The word form, for larger routers, stays within the 133-byte message limit as well.
        var large = Swp08Messages.TallyDump(0, 0, Enumerable.Range(0, 300).ToList()).Select(Decode).ToList();
        Assert.All(large, f => Assert.True(f.Payload.Length + 1 + 6 <= 133));
        Assert.Equal(300, large.Sum(f => f.Payload[1]));
    }

    [Fact]
    public void Name_responses_carry_fixed_width_names_and_split_at_the_size_limit()
    {
        var names = Enumerable.Range(1, 20).Select(i => $"Camera {i}").ToList();
        var request = new NameRequest(Sources: true, Extended: false, Matrix: 0, Level: 0, LengthCode: 1); // 8 chars
        var frames = Swp08Messages.NameResponses(request, names).Select(Decode).ToList();

        Assert.All(frames, f => Assert.Equal(Swp08Command.SourceNamesResponse, f.Command));
        Assert.All(frames, f => Assert.True(f.Payload.Length + 1 <= Swp08Codec.MaxDataLength));

        var first = frames[0];
        Assert.Equal(0x00, first.Payload[0]); // matrix/level
        Assert.Equal(1, first.Payload[1]);    // length code
        Assert.Equal(0, (first.Payload[2] << 8) | first.Payload[3]);
        var count = first.Payload[4];
        Assert.Equal("Camera 1", System.Text.Encoding.ASCII.GetString(first.Payload, 5, 8));
        Assert.Equal("Camera 2", System.Text.Encoding.ASCII.GetString(first.Payload, 13, 8));

        Assert.Equal(20, frames.Sum(f => f.Payload[4]));
        Assert.Equal(count, (frames[1].Payload[2] << 8) | frames[1].Payload[3]);
    }

    [Fact]
    public void Extended_source_name_response_has_a_level_byte_and_dest_does_not()
    {
        var source = Decode(Swp08Messages.NameResponses(new NameRequest(true, true, 2, 1, 2), new[] { "A" })[0]);
        Assert.Equal(Swp08Command.ExtendedSourceNamesResponse, source.Command);
        Assert.Equal(new byte[] { 2, 1, 2, 0, 0, 1 }, source.Payload[..6]);
        Assert.Equal(12, source.Payload.Length - 6);

        var dest = Decode(Swp08Messages.NameResponses(new NameRequest(false, true, 2, 0, 0), new[] { "MON1" })[0]);
        Assert.Equal(Swp08Command.ExtendedDestNamesResponse, dest.Command);
        Assert.Equal(new byte[] { 2, 0, 0, 0, 1, (byte)'M', (byte)'O', (byte)'N', (byte)'1' }, dest.Payload);
    }

    [Fact]
    public void Thirty_two_character_names_are_supported()
    {
        var request = Swp08Messages.ParseNameRequest(Swp08Command.GetDestNames, new byte[] { 0x00, 4 });
        Assert.Equal(32, request.Length);
    }

    [Fact]
    public void Protocol_implementation_lists_what_is_sent_and_accepted()
    {
        var frame = Decode(Swp08Messages.ProtocolImplementation());
        var sent = frame.Payload[0];
        var accepted = frame.Payload[1];
        Assert.Equal(2 + sent + accepted, frame.Payload.Length);
        Assert.Contains(Swp08Command.CrosspointConnect, frame.Payload[(2 + sent)..]);
        Assert.Contains(Swp08Command.ExtendedTallyDumpRequest, frame.Payload[(2 + sent)..]);
        Assert.Contains(Swp08Command.CrosspointConnected, frame.Payload[2..(2 + sent)]);
    }

    [Fact]
    public void Names_updated_flags_the_matrix_and_name_kinds()
    {
        var frame = Decode(Swp08Messages.NamesUpdated(2, sources: true, destinations: false));
        Assert.Equal(new byte[] { 0, 0, 0, 0x04, 0, 0, 0x01, 0x03 }, frame.Payload);
    }

    private static Swp08Frame Decode(byte[] encoded) => new Swp08FrameDecoder().Feed(encoded).Single().Frame!;
}
