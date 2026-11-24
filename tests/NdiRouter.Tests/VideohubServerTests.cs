using System.Net;
using System.Net.Sockets;
using System.Text;
using NdiRouter.Core;
using NdiRouter.Videohub;

namespace NdiRouter.Tests;

/// <summary>A minimal Videohub client over a real TCP socket.</summary>
internal sealed class TestHubClient : IDisposable
{
    private readonly TcpClient _client = new();
    private readonly NetworkStream _stream;
    private readonly VideohubReader _reader = new();
    private readonly Queue<VideohubBlock> _blocks = new();

    public TestHubClient(int port)
    {
        _client.Connect(IPAddress.Loopback, port);
        _client.ReceiveTimeout = 3000;
        _stream = _client.GetStream();
    }

    public void Send(string text) => _stream.Write(Encoding.UTF8.GetBytes(text));

    public VideohubBlock Next()
    {
        var buffer = new byte[4096];
        while (_blocks.Count == 0)
        {
            var read = _stream.Read(buffer);
            if (read == 0) throw new IOException("server closed the connection");
            foreach (var block in _reader.Feed(buffer.AsSpan(0, read))) _blocks.Enqueue(block);
        }
        return _blocks.Dequeue();
    }

    /// <summary>The next block, which must have this header; returns its lines.</summary>
    public IReadOnlyList<string> Expect(string header)
    {
        var block = Next();
        Assert.Equal(header, block.Header);
        return block.Lines;
    }

    /// <summary>Everything sent on connecting, up to and including END PRELUDE.</summary>
    public List<VideohubBlock> Prelude()
    {
        var blocks = new List<VideohubBlock>();
        do blocks.Add(Next()); while (blocks[^1].Header != "END PRELUDE:");
        return blocks;
    }

    /// <summary>True if nothing arrives within the time given.</summary>
    public bool Quiet(int milliseconds)
    {
        if (_blocks.Count > 0) return false;
        _client.ReceiveTimeout = milliseconds;
        try
        {
            var buffer = new byte[4096];
            var read = _stream.Read(buffer);
            foreach (var block in _reader.Feed(buffer.AsSpan(0, read))) _blocks.Enqueue(block);
            return _blocks.Count == 0;
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

public class VideohubServerTests : IDisposable
{
    private readonly RouterCore _router;
    private readonly VideohubServer _server;

    public VideohubServerTests()
    {
        _router = TestRouter.Create(new FakeNdi(), inputs: 6, outputs: 4);
        _server = new VideohubServer(_router);
        _server.Start(new VideohubSettings { Enabled = true, BindAddress = "127.0.0.1", Port = 0 });
    }

    public void Dispose()
    {
        _server.Dispose();
        _router.Dispose();
    }

    /// <summary>A connected client that has read its prelude.</summary>
    private TestHubClient Connect()
    {
        var client = new TestHubClient(_server.BoundPort);
        client.Prelude();
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (_server.Clients().Count == 0 && DateTime.UtcNow < deadline) Thread.Sleep(10);
        return client;
    }

    [Fact]
    public void A_client_is_sent_the_whole_router_on_connecting()
    {
        _router.Route(1, 4, "test");
        using var client = new TestHubClient(_server.BoundPort);
        var blocks = client.Prelude();

        Assert.Equal(new[]
        {
            "PROTOCOL PREAMBLE:", "VIDEOHUB DEVICE:", "INPUT LABELS:", "OUTPUT LABELS:",
            "VIDEO OUTPUT ROUTING:", "VIDEO OUTPUT LOCKS:", "END PRELUDE:"
        }, blocks.Select(b => b.Header));

        Assert.Equal(new[] { "Version: 2.3" }, blocks[0].Lines);
        Assert.Contains("Video inputs: 7", blocks[1].Lines); // six inputs and "(clear)"
        Assert.Contains("Video outputs: 4", blocks[1].Lines);
        Assert.Equal(new[] { "0 Cam 1", "1 Cam 2", "2 Cam 3", "3 Cam 4", "4 Cam 5", "5 Cam 6", "6 (clear)" }, blocks[2].Lines);
        Assert.Equal(new[] { "0 Output 1", "1 Output 2", "2 Output 3", "3 Output 4" }, blocks[3].Lines);
        Assert.Equal(new[] { "0 6", "1 4", "2 6", "3 6" }, blocks[4].Lines); // unrouted outputs on "(clear)"
        Assert.Equal(new[] { "0 U", "1 U", "2 U", "3 U" }, blocks[5].Lines);
    }

    [Fact]
    public void A_route_is_acknowledged_then_sent_to_every_client()
    {
        using var a = Connect();
        using var b = Connect();

        a.Send("VIDEO OUTPUT ROUTING:\n2 3\n\n");

        Assert.Empty(a.Expect("ACK"));
        Assert.Equal(new[] { "2 3" }, a.Expect("VIDEO OUTPUT ROUTING:"));
        Assert.Equal(new[] { "2 3" }, b.Expect("VIDEO OUTPUT ROUTING:"));
        Assert.Equal(3, _router.GetRoute(2));

        // Routing "(clear)" clears it.
        a.Send("VIDEO OUTPUT ROUTING:\n2 6\n\n");
        a.Expect("ACK");
        Assert.Equal(new[] { "2 6" }, a.Expect("VIDEO OUTPUT ROUTING:"));
        Assert.Null(_router.GetRoute(2));
    }

    [Fact]
    public void Routes_made_elsewhere_reach_clients()
    {
        using var client = Connect();
        _router.Route(0, 5, "panel");
        Assert.Equal(new[] { "0 5" }, client.Expect("VIDEO OUTPUT ROUTING:"));
    }

    [Fact]
    public void Line_feeds_carriage_returns_or_both_end_a_line()
    {
        using var client = Connect();
        client.Send("VIDEO OUTPUT ROUTING:\r\n1 2\r\n\r\n");
        client.Expect("ACK");
        Assert.Equal(new[] { "1 2" }, client.Expect("VIDEO OUTPUT ROUTING:"));

        client.Send("VIDEO OUTPUT ROUTING:\r1 3\r\r");
        client.Expect("ACK");
        Assert.Equal(new[] { "1 3" }, client.Expect("VIDEO OUTPUT ROUTING:"));
    }

    [Fact]
    public void A_client_owns_its_locks_and_sees_everyone_elses_as_locked()
    {
        using var client = Connect();

        client.Send("VIDEO OUTPUT LOCKS:\n1 O\n\n");
        client.Expect("ACK");
        Assert.Equal(new[] { "1 O" }, client.Expect("VIDEO OUTPUT LOCKS:"));
        Assert.Equal(new OutputLock("Videohub 127.0.0.1"), _router.GetLock(1));

        _router.Lock(3, new OutputLock(OutputLock.Panel), "panel");
        Assert.Equal(new[] { "3 L" }, client.Expect("VIDEO OUTPUT LOCKS:"));

        // Its route to an output someone else holds is acknowledged, then put back.
        client.Send("VIDEO OUTPUT ROUTING:\n3 2\n\n");
        client.Expect("ACK");
        Assert.Equal(new[] { "3 6" }, client.Expect("VIDEO OUTPUT ROUTING:"));
        Assert.Null(_router.GetRoute(3));

        // An ordinary unlock of someone else's lock is refused; a forced one is not.
        client.Send("VIDEO OUTPUT LOCKS:\n3 U\n\n");
        client.Expect("ACK");
        Assert.Equal(new[] { "3 L" }, client.Expect("VIDEO OUTPUT LOCKS:"));

        client.Send("VIDEO OUTPUT LOCKS:\n3 F\n\n");
        client.Expect("ACK");
        Assert.Equal(new[] { "3 U" }, client.Expect("VIDEO OUTPUT LOCKS:"));
        Assert.Null(_router.GetLock(3));
    }

    [Fact]
    public void Renames_are_put_back_unless_allowed()
    {
        using var client = Connect();

        client.Send("OUTPUT LABELS:\n0 Big screen\n\n");
        client.Expect("ACK");
        Assert.Equal(new[] { "0 Output 1" }, client.Expect("OUTPUT LABELS:"));
        Assert.Equal("Output 1", _router.GetOutput(0).Label);

        _server.UpdateSettings(new VideohubSettings { Enabled = true, AllowLabelChanges = true });
        client.Send("OUTPUT LABELS:\n0 Big screen\n\n");
        client.Expect("ACK");
        Assert.Equal(new[] { "0 Big screen" }, client.Expect("OUTPUT LABELS:"));
        Assert.Equal("Big screen", _router.GetOutput(0).Label);
    }

    [Fact]
    public void A_rename_anywhere_sends_only_the_label_that_changed()
    {
        using var client = Connect();
        _router.UpdateInput(4, i => i.Label = "Graphics");
        Assert.Equal(new[] { "4 Graphics" }, client.Expect("INPUT LABELS:"));

        // A change that leaves the label alone (here, a SW-P-08 name) sends nothing.
        _router.UpdateInput(4, i => i.Name4 = "GFX");
        Assert.True(client.Quiet(300));
    }

    [Fact]
    public void A_header_on_its_own_asks_for_that_block_and_ping_is_answered()
    {
        using var client = Connect();

        client.Send("OUTPUT LABELS:\n\n");
        client.Expect("ACK");
        Assert.Equal(4, client.Expect("OUTPUT LABELS:").Count);

        client.Send("PING:\n\n");
        Assert.Empty(client.Expect("ACK"));
    }

    [Fact]
    public void Requests_it_cannot_carry_out_are_refused_with_nak()
    {
        using var client = Connect();

        client.Send("VIDEO OUTPUT ROUTING:\n9 1\n\n"); // no output 10
        client.Expect("NAK");

        client.Send("VIDEO OUTPUT ROUTING:\n1 7\n\n"); // no input past "(clear)"
        client.Expect("NAK");

        client.Send("VIDEO OUTPUT LOCKS:\n1 X\n\n");
        client.Expect("NAK");

        client.Send("MONITORING OUTPUT LABELS:\n\n"); // this router has none
        client.Expect("NAK");

        Assert.Equal(new int?[] { null, null, null, null }, _router.Routes());
    }

    [Fact]
    public void A_resize_resends_everything_but_the_preamble()
    {
        using var client = Connect();
        _router.Resize(3, 2, 2);

        Assert.Contains("Video inputs: 4", client.Expect("VIDEOHUB DEVICE:"));
        Assert.Equal(new[] { "0 Cam 1", "1 Cam 2", "2 Cam 3", "3 (clear)" }, client.Expect("INPUT LABELS:"));
        Assert.Equal(2, client.Expect("OUTPUT LABELS:").Count);
        Assert.Equal(new[] { "0 3", "1 3" }, client.Expect("VIDEO OUTPUT ROUTING:"));
        Assert.Equal(new[] { "0 U", "1 U" }, client.Expect("VIDEO OUTPUT LOCKS:"));
    }

    [Fact]
    public void The_port_is_held_exclusively()
    {
        using var other = new VideohubServer(_router);
        Assert.Throws<SocketException>(() =>
            other.Start(new VideohubSettings { BindAddress = "127.0.0.1", Port = _server.BoundPort }));
    }
}

public class VideohubReaderTests
{
    [Fact]
    public void Blocks_split_across_reads_and_line_endings_are_put_together()
    {
        var reader = new VideohubReader();
        Assert.Empty(reader.Feed("VIDEO OUTPUT ROU"u8));
        Assert.Empty(reader.Feed("TING:\r"u8));
        var blocks = reader.Feed("\n0 1\n1 2\r\n\r\n\nPING:\r\r"u8);

        Assert.Equal(2, blocks.Count);
        Assert.Equal("VIDEO OUTPUT ROUTING:", blocks[0].Header);
        Assert.Equal(new[] { "0 1", "1 2" }, blocks[0].Lines);
        Assert.Equal("PING:", blocks[1].Header);
        Assert.Empty(blocks[1].Lines);
    }

    [Fact]
    public void Labels_are_utf8()
    {
        var blocks = new VideohubReader().Feed(Encoding.UTF8.GetBytes("INPUT LABELS:\n0 Caméra 1\n\n"));
        Assert.Equal("0 Caméra 1", Assert.Single(blocks).Lines[0]);
    }
}
