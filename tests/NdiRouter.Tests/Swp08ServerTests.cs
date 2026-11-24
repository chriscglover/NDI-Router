using System.Net;
using System.Net.Sockets;
using NdiRouter.Core;
using NdiRouter.Swp08;

namespace NdiRouter.Tests;

/// <summary>A minimal SW-P-08 controller over a real TCP socket.</summary>
internal sealed class TestController : IDisposable
{
    private readonly TcpClient _client = new();
    private readonly NetworkStream _stream;
    private readonly Swp08FrameDecoder _decoder = new();
    private readonly List<Swp08Item> _items = new();

    public TestController(int port, bool sendAcks = true)
    {
        _client.Connect(IPAddress.Loopback, port);
        _client.ReceiveTimeout = 3000;
        _stream = _client.GetStream();
        SendAcks = sendAcks;
    }

    public bool SendAcks { get; set; }

    public void Send(byte[] frame) => _stream.Write(frame);

    /// <summary>Sends a message and expects the router's ACK for it.</summary>
    public void Request(byte command, params byte[] payload)
    {
        Send(Swp08Codec.Encode(command, payload));

        // A broadcast can arrive ahead of the ACK; leave it queued for NextFrame.
        while (true)
        {
            var ack = _items.FindIndex(i => i.Kind == Swp08ItemKind.PeerAck);
            if (ack >= 0)
            {
                _items.RemoveAt(ack);
                return;
            }

            Assert.DoesNotContain(_items, i => i.Kind == Swp08ItemKind.PeerNak);
            Fill();
        }
    }

    private void Fill()
    {
        var buffer = new byte[1024];
        var read = _stream.Read(buffer);
        if (read == 0) throw new IOException("router closed the connection");
        _items.AddRange(_decoder.Feed(buffer.AsSpan(0, read)));
    }

    /// <summary>The next message from the router, ACKing it as a controller should.</summary>
    public Swp08Frame NextFrame()
    {
        while (true)
        {
            var item = Next();
            if (item.Kind != Swp08ItemKind.Frame) continue;
            if (SendAcks) _stream.Write(Swp08Codec.AckBytes());
            return item.Frame!;
        }
    }

    public Swp08Item Next()
    {
        while (_items.Count == 0) Fill();
        var item = _items[0];
        _items.RemoveAt(0);
        return item;
    }

    /// <summary>True if nothing arrives within the time given.</summary>
    public bool Quiet(int milliseconds)
    {
        if (_items.Count > 0) return false;
        _client.ReceiveTimeout = milliseconds;
        try
        {
            Fill();
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        finally
        {
            _client.ReceiveTimeout = 3000;
        }
    }

    public void Dispose() => _client.Dispose();
}

public class Swp08ServerTests : IDisposable
{
    private readonly RouterCore _router;
    private readonly Swp08Server _server;

    public Swp08ServerTests()
    {
        _router = TestRouter.Create(new FakeNdi(), inputs: 6, outputs: 4);
        _server = new Swp08Server(_router) { NamesUpdatedDelayMs = 100 };
        _server.Start(new Swp08Settings { BindAddress = "127.0.0.1", Port = 0, AckTimeoutMs = 300 });
    }

    public void Dispose()
    {
        _server.Dispose();
        _router.Dispose();
    }

    private TestController Connect(bool acks = true)
    {
        var controller = new TestController(_server.BoundPort, acks);
        // Wait until the server has registered the connection, so broadcasts reach it.
        var deadline = DateTime.UtcNow.AddSeconds(3);
        var expected = _server.Clients().Count + 1;
        while (_server.Clients().Count < expected && DateTime.UtcNow < deadline) Thread.Sleep(10);
        return controller;
    }

    [Fact]
    public void Connect_routes_and_is_confirmed_with_connected()
    {
        using var controller = Connect();

        controller.Request(Swp08Command.CrosspointConnect, 0x00, 0x00, 2, 4); // dest 2 <- source 4

        var reply = controller.NextFrame();
        Assert.Equal(Swp08Command.CrosspointConnected, reply.Command);
        Assert.Equal(new CrosspointRequest(0, 0, 2, 4), Swp08Messages.ParseConnect(reply.Payload));
        Assert.Equal(4, _router.GetRoute(2));
    }

    [Fact]
    public void Interrogate_reports_the_route_and_the_clear_source_when_unrouted()
    {
        using var controller = Connect();
        _router.Route(1, 5, "test");
        controller.NextFrame(); // its CONNECTED

        controller.Request(Swp08Command.CrosspointInterrogate, 0x00, 0x00, 1);
        var tally = controller.NextFrame();
        Assert.Equal(Swp08Command.CrosspointTally, tally.Command);
        Assert.Equal(new CrosspointRequest(0, 0, 1, 5), Swp08Messages.ParseConnect(tally.Payload));

        controller.Request(Swp08Command.CrosspointInterrogate, 0x00, 0x00, 3);
        tally = controller.NextFrame();
        Assert.Equal(1023, Swp08Messages.ParseConnect(tally.Payload).Source);
    }

    [Fact]
    public void Connecting_the_clear_source_clears_the_output()
    {
        using var controller = Connect();
        _router.Route(0, 1, "test");
        controller.NextFrame(); // CONNECTED for the panel route

        controller.Request(Swp08Command.CrosspointConnect, 0x00, 0x07, 0, 0x7F); // source 1023
        var reply = controller.NextFrame();

        Assert.Null(_router.GetRoute(0));
        Assert.Equal(1023, Swp08Messages.ParseConnect(reply.Payload).Source);
    }

    [Fact]
    public void Routes_made_anywhere_are_broadcast_to_every_controller()
    {
        using var a = Connect();
        using var b = Connect();

        _router.Route(3, 2, "panel");

        foreach (var controller in new[] { a, b })
        {
            var frame = controller.NextFrame();
            Assert.Equal(Swp08Command.CrosspointConnected, frame.Command);
            Assert.Equal(new CrosspointRequest(0, 0, 3, 2), Swp08Messages.ParseConnect(frame.Payload));
        }
    }

    [Fact]
    public void Tally_dump_reports_every_destination()
    {
        using var controller = Connect();
        _router.Route(0, 1, "test");
        _router.Route(2, 0, "test");
        for (var i = 0; i < 2; i++) controller.NextFrame();

        controller.Request(Swp08Command.CrosspointTallyDumpRequest, 0x00);
        var dump = controller.NextFrame();

        // An unrouted output reports 1023, which needs the word form.
        Assert.Equal(Swp08Command.CrosspointTallyDumpWord, dump.Command);
        Assert.Equal(new byte[] { 0x00, 4, 0, 0, 0, 1, 0x03, 0xFF, 0, 0, 0x03, 0xFF }, dump.Payload);
    }

    [Fact]
    public void Extended_tally_dump_and_interrogate()
    {
        using var controller = Connect();
        _router.Route(1, 3, "test");
        controller.NextFrame();

        controller.Request(Swp08Command.ExtendedTallyDumpRequest, 0, 0);
        var dump = controller.NextFrame();
        Assert.Equal(Swp08Command.ExtendedTallyDumpWord, dump.Command);
        Assert.Equal(new byte[] { 0, 0, 4, 0, 0, 0x03, 0xFF, 0, 3, 0x03, 0xFF, 0x03, 0xFF }, dump.Payload);

        controller.Request(Swp08Command.ExtendedInterrogate, 0, 0, 0, 1);
        var tally = controller.NextFrame();
        Assert.Equal(Swp08Command.ExtendedTally, tally.Command);
        Assert.Equal(new byte[] { 0, 0, 0, 1, 0, 3 }, tally.Payload);
    }

    [Fact]
    public void Extended_controllers_get_extended_connected()
    {
        using var controller = Connect();
        controller.Request(Swp08Command.ExtendedConnect, 0, 0, 0, 2, 0, 5);

        var reply = controller.NextFrame();
        Assert.Equal(Swp08Command.ExtendedConnected, reply.Command);
        Assert.Equal(new byte[] { 0, 0, 0, 2, 0, 5 }, reply.Payload);
        Assert.Equal(5, _router.GetRoute(2));
    }

    [Fact]
    public void Source_and_destination_names_are_served()
    {
        using var controller = Connect();
        _server.NamesUpdatedDelayMs = 60000; // keep NAMES UPDATED out of this exchange
        _router.UpdateInput(0, i => i.Name8 = "CAM ONE");

        controller.Request(Swp08Command.GetSourceNames, 0x00, 1); // 8 characters
        var sources = controller.NextFrame();
        Assert.Equal(Swp08Command.SourceNamesResponse, sources.Command);
        Assert.Equal(6, sources.Payload[4]);
        Assert.Equal("CAM ONE ", System.Text.Encoding.ASCII.GetString(sources.Payload, 5, 8));
        Assert.Equal("Cam 2   ", System.Text.Encoding.ASCII.GetString(sources.Payload, 13, 8));

        controller.Request(Swp08Command.ExtendedGetDestNames, 0, 2); // 12 characters
        var dests = controller.NextFrame();
        Assert.Equal(Swp08Command.ExtendedDestNamesResponse, dests.Command);
        Assert.Equal(4, dests.Payload[4]);
        Assert.Equal("Output 1    ", System.Text.Encoding.ASCII.GetString(dests.Payload, 5, 12));
    }

    [Fact]
    public void Each_name_length_gets_its_own_name()
    {
        using var controller = Connect();
        _server.NamesUpdatedDelayMs = 60000;
        _router.UpdateInput(0, i => i.Label = "Studio Camera 1");
        _router.UpdateInput(1, i => i.Name4 = "CM2");

        string Name(Swp08Frame f, int i, int length) =>
            System.Text.Encoding.ASCII.GetString(f.Payload, 5 + i * length, length);

        controller.Request(Swp08Command.GetSourceNames, 0x00, 0); // 4 characters
        var four = controller.NextFrame();
        Assert.Equal("SCm1", Name(four, 0, 4));  // generated mnemonic, not "Stud"
        Assert.Equal("CM2 ", Name(four, 1, 4));  // typed in

        controller.Request(Swp08Command.GetSourceNames, 0x00, 2); // 12 characters
        var twelve = controller.NextFrame();
        Assert.Equal("Std Camera 1", Name(twelve, 0, 12));
        Assert.Equal("Cam 2       ", Name(twelve, 1, 12)); // the 4-character name does not leak into 12

        controller.Request(Swp08Command.GetSourceNames, 0x00, 3); // 16, if a controller asks
        Assert.Equal("Studio Camera 1 ", Name(controller.NextFrame(), 0, 16));
    }

    [Fact]
    public void Answers_protocol_implementation_and_dual_controller_status()
    {
        using var controller = Connect();

        controller.Request(Swp08Command.ProtocolImplementationRequest);
        Assert.Equal(Swp08Command.ProtocolImplementationResponse, controller.NextFrame().Command);

        controller.Request(Swp08Command.DualControllerStatusRequest);
        var status = controller.NextFrame();
        Assert.Equal(Swp08Command.DualControllerStatusResponse, status.Command);
        Assert.Equal(0x02, status.Payload[0] & 0x02); // bit 1 set: this controller is active
    }

    [Fact]
    public void Other_matrices_are_acked_but_ignored()
    {
        using var controller = Connect();
        controller.Request(Swp08Command.CrosspointConnect, 0x10, 0x00, 0, 1); // matrix 2
        Assert.True(controller.Quiet(400));
        Assert.Null(_router.GetRoute(0));
    }

    [Fact]
    public void Any_level_routes_and_confirms_on_the_requested_and_configured_levels()
    {
        using var controller = Connect();
        controller.Request(Swp08Command.CrosspointConnect, 0x01, 0x00, 0, 1); // level 2

        var levels = new[] { controller.NextFrame(), controller.NextFrame() }
            .Select(f => Swp08Messages.ParseConnect(f.Payload).Level).Order().ToArray();
        Assert.Equal(new[] { 0, 1 }, levels);
        Assert.Equal(1, _router.GetRoute(0));
    }

    [Fact]
    public void A_corrupt_message_is_naked()
    {
        using var controller = Connect();
        var frame = Swp08Codec.Encode(Swp08Command.CrosspointInterrogate, 0, 0, 0);
        frame[^3] ^= 0xFF;
        controller.Send(frame);
        Assert.Equal(Swp08ItemKind.PeerNak, controller.Next().Kind);
    }

    [Fact]
    public void A_nak_makes_the_router_resend()
    {
        using var controller = Connect(acks: false);
        _router.Route(0, 2, "test");

        var first = controller.Next();
        Assert.Equal(Swp08ItemKind.Frame, first.Kind);
        controller.Send(Swp08Codec.NakBytes());

        var again = controller.Next();
        Assert.Equal(Swp08ItemKind.Frame, again.Kind);
        Assert.Equal(first.Frame!.Payload, again.Frame!.Payload);
        controller.Send(Swp08Codec.AckBytes());
    }

    [Fact]
    public void A_controller_that_never_acks_still_gets_every_message()
    {
        using var controller = Connect(acks: false);
        for (var i = 0; i < 6; i++) _router.Route(i % 4, i, "test");

        // Each message may be resent while the router learns this controller does not ACK; every
        // route must still arrive, in order, without the link stalling.
        var seen = new List<int>();
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (seen.Distinct().Count() < 6 && DateTime.UtcNow < deadline)
        {
            var item = controller.Next();
            if (item.Kind == Swp08ItemKind.Frame) seen.Add(Swp08Messages.ParseConnect(item.Frame!.Payload).Source!.Value);
        }

        Assert.Equal(new[] { 0, 1, 2, 3, 4, 5 }, seen.Distinct());
    }

    [Fact]
    public void Editing_a_name_sends_names_updated()
    {
        using var controller = Connect();
        _router.UpdateOutput(1, o => o.Label = "Monitor wall");

        var frame = controller.NextFrame();
        Assert.Equal(Swp08Command.NamesUpdated, frame.Command);
        Assert.Equal(new byte[] { 0, 0, 0, 1, 0, 0, 0x02, 0x0C }, frame.Payload);
    }

    [Fact]
    public void Protect_connect_locks_the_destination_and_is_broadcast_to_every_controller()
    {
        using var a = Connect();
        using var b = Connect();

        a.Request(Swp08Command.ProtectConnect, 0x00, 0x00, 2, 5); // dest 2, device 5

        foreach (var controller in new[] { a, b })
        {
            var frame = controller.NextFrame();
            Assert.Equal(Swp08Command.ProtectConnected, frame.Command);
            Assert.Equal(new byte[] { 0x00, Swp08Protect.ProBel, 0x00, 2, 5 }, frame.Payload);
        }
        Assert.Equal(new OutputLock("SW-P-08 127.0.0.1", 5), _router.GetLock(2));

        // The controller that holds it can still route it.
        a.Request(Swp08Command.CrosspointConnect, 0x00, 0x00, 2, 1);
        Assert.Equal(Swp08Command.CrosspointConnected, a.NextFrame().Command);
        Assert.Equal(1, _router.GetRoute(2));
    }

    [Fact]
    public void A_destination_locked_elsewhere_refuses_routes_and_protect_changes()
    {
        _router.Route(1, 3, "test");
        _router.Lock(1, new OutputLock(OutputLock.Panel), "panel");
        using var controller = Connect();

        // The route stays, and the controller is told what it still is.
        controller.Request(Swp08Command.CrosspointConnect, 0x00, 0x00, 1, 4);
        var tally = controller.NextFrame();
        Assert.Equal(Swp08Command.CrosspointTally, tally.Command);
        Assert.Equal(new CrosspointRequest(0, 0, 1, 3), Swp08Messages.ParseConnect(tally.Payload));
        Assert.Equal(3, _router.GetRoute(1));

        // So does the lock: held by the panel, which SW-P-08 sees as device 1023. An unsuccessful
        // protect change is answered on every link, like a successful one, with the protect that stands.
        using var other = Connect();
        controller.Request(Swp08Command.ProtectDisconnect, 0x00, 0x00, 1, 5);
        foreach (var c in new[] { controller, other })
        {
            var protect = c.NextFrame();
            Assert.Equal(Swp08Command.ProtectDisconnected, protect.Command);
            Assert.Equal(new byte[] { 0x00, Swp08Protect.ProBel, 0x07, 1, 0x7F }, protect.Payload);
        }

        controller.Request(Swp08Command.ProtectConnect, 0x00, 0x00, 1, 5);
        var refused = controller.NextFrame();
        Assert.Equal(Swp08Command.ProtectConnected, refused.Command);
        Assert.Equal(new byte[] { 0x00, Swp08Protect.ProBel, 0x07, 1, 0x7F }, refused.Payload);
        Assert.Equal(Swp08Command.ProtectConnected, other.NextFrame().Command);

        Assert.Equal(OutputLock.Panel, _router.GetLock(1)!.Owner);
    }

    [Fact]
    public void Protect_dumps_carry_ten_bit_device_numbers()
    {
        var dump = Swp08Messages.ProtectTallyDump(0, 0, 0, new[] { (Swp08Protect.ProBel, 1023), (Swp08Protect.None, 0) });
        Assert.Equal(new byte[] { 0x14, 0x00, 2, 0, 0, 0x13, 0xFF, 0x00, 0x00 }, dump[0][2..^4]);
    }

    [Fact]
    public void Protects_can_be_interrogated_dumped_and_removed()
    {
        using var controller = Connect();
        controller.Request(Swp08Command.ProtectConnect, 0x00, 0x00, 0, 9);
        controller.NextFrame(); // PROTECT CONNECTED

        controller.Request(Swp08Command.ProtectInterrogate, 0x00, 0x00, 0);
        var tally = controller.NextFrame();
        Assert.Equal(Swp08Command.ProtectTally, tally.Command);
        Assert.Equal(new byte[] { 0x00, Swp08Protect.ProBel, 0x00, 0, 9 }, tally.Payload);

        // Count, first destination, then a word each: protect state on top, device below.
        controller.Request(Swp08Command.ProtectTallyDumpRequest, 0x00, 0, 0);
        var dump = controller.NextFrame();
        Assert.Equal(Swp08Command.ProtectTallyDump, dump.Command);
        Assert.Equal(new byte[] { 0x00, 4, 0, 0, 0x10, 9, 0, 0, 0, 0, 0, 0 }, dump.Payload);

        controller.Request(Swp08Command.ProtectDisconnect, 0x00, 0x00, 0, 9);
        var freed = controller.NextFrame();
        Assert.Equal(Swp08Command.ProtectDisconnected, freed.Command);
        Assert.Equal(new byte[] { 0x00, Swp08Protect.None, 0x00, 0, 9 }, freed.Payload);
        Assert.Null(_router.GetLock(0));
    }

    [Fact]
    public void Extended_protects_carry_whole_words()
    {
        using var controller = Connect();
        controller.Request(Swp08Command.ExtendedProtectConnect, 0, 0, 0, 3, 0x04, 0x00); // dest 3, device 1024

        var frame = controller.NextFrame();
        Assert.Equal(Swp08Command.ExtendedProtectConnected, frame.Command);
        Assert.Equal(new byte[] { 0, 0, Swp08Protect.ProBel, 0, 3, 0x04, 0x00 }, frame.Payload);
        Assert.Equal(1024, _router.GetLock(3)!.Device);
    }

    [Fact]
    public void Protect_messages_are_listed_in_the_protocol_implementation()
    {
        Assert.Contains(Swp08Command.ProtectConnect, Swp08Messages.ReceivedCommands);
        Assert.Contains(Swp08Command.ExtendedProtectTallyDumpRequest, Swp08Messages.ReceivedCommands);
        Assert.Contains(Swp08Command.ProtectConnected, Swp08Messages.TransmittedCommands);
        Assert.Contains(Swp08Command.ExtendedProtectDisconnected, Swp08Messages.TransmittedCommands);
    }

    [Fact]
    public void The_port_is_held_exclusively()
    {
        using var other = new Swp08Server(_router);
        Assert.Throws<SocketException>(() =>
            other.Start(new Swp08Settings { BindAddress = "127.0.0.1", Port = _server.BoundPort }));
    }
}
