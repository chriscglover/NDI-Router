using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using NdiRouter.Core;

namespace NdiRouter.Videohub;

public sealed class VideohubSettings
{
    /// <summary>Off until turned on: Blackmagic's own Videohub software may already have the port.</summary>
    public bool Enabled { get; set; }

    /// <summary>Interface to listen on; 0.0.0.0 for all of them.</summary>
    public string BindAddress { get; set; } = "0.0.0.0";

    /// <summary>9990 is the Videohub Ethernet Protocol's port.</summary>
    public int Port { get; set; } = 9990;

    /// <summary>Let clients rename inputs and outputs. Off: a rename is acknowledged, then put back.</summary>
    public bool AllowLabelChanges { get; set; }

    /// <summary>Log every block, not just connections.</summary>
    public bool LogTraffic { get; set; }

    public VideohubSettings Clone() => (VideohubSettings)MemberwiseClone();
}

/// <summary>
/// Makes the router controllable as a Blackmagic Videohub (Videohub Ethernet Protocol 2.3): a
/// text protocol on TCP port 9990. A client gets the router's whole state when it connects, then
/// every change - from it, other clients, SW-P-08 or the panel - as it happens.
///
/// <list type="bullet">
/// <item>Inputs and outputs are numbered from 0, as in SW-P-08.</item>
/// <item>A Videohub output is always routed somewhere, so one extra input, "(clear)", after the
/// real ones stands for nothing routed: a cleared output reports it, and routing it clears.</item>
/// <item>Locks are the router's locks. A client owns ("O") the locks made from its address and sees
/// everyone else's as "L"; "F" unlocks anyone's.</item>
/// <item>There are no monitoring outputs, serial ports, processing units or frame buffers, and the
/// spec has a Videohub without them leave their blocks out.</item>
/// </list>
/// </summary>
public sealed class VideohubServer : IDisposable
{
    public const string ProtocolVersion = "2.3";
    public const string ModelName = "NDI Router";
    public const string ClearLabel = "(clear)";

    private readonly RouterCore _router;
    private readonly object _gate = new();
    private readonly List<VideohubConnection> _connections = new();
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private volatile VideohubSettings _settings = new();

    // What clients were last told, so a names change sends only the labels that changed.
    private string[] _inputLabels;
    private string[] _outputLabels;

    public VideohubServer(RouterCore router)
    {
        _router = router;
        _inputLabels = InputLabels();
        _outputLabels = OutputLabels();

        _router.RouteChanged += OnRouteChanged;
        _router.NamesChanged += OnNamesChanged;
        _router.LayoutChanged += OnLayoutChanged;
        _router.LockChanged += OnLockChanged;
    }

    public event Action<string>? Log;
    public event EventHandler? ClientsChanged;

    public bool IsListening => _listener is not null;

    /// <summary>The port actually bound - useful when started on port 0.</summary>
    public int BoundPort { get; private set; }

    internal VideohubSettings CurrentSettings => _settings;

    /// <summary>Applies settings that do not need the listener restarted.</summary>
    public void UpdateSettings(VideohubSettings settings) => _settings = settings.Clone();

    public void Start(VideohubSettings settings)
    {
        Stop();
        _settings = settings.Clone();

        if (!IPAddress.TryParse(settings.BindAddress, out var address)) address = IPAddress.Any;

        // Exclusive, so Blackmagic's Videohub server or a second copy of this application on the
        // same port makes the start fail loudly rather than share it.
        var listener = new TcpListener(address, settings.Port) { ExclusiveAddressUse = true };
        listener.Start();

        _listener = listener;
        _cts = new CancellationTokenSource();
        BoundPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        Log?.Invoke($"Videohub: listening on {address}:{BoundPort}.");
        _ = AcceptLoopAsync(listener, _cts.Token);
    }

    public void Stop()
    {
        var listener = _listener;
        if (listener is null) return;

        _listener = null;
        _cts?.Cancel();
        listener.Stop();

        List<VideohubConnection> connections;
        lock (_gate) connections = _connections.ToList();
        foreach (var connection in connections) connection.Close();

        Log?.Invoke("Videohub: stopped.");
    }

    public List<string> Clients()
    {
        lock (_gate) return _connections.Select(c => c.Describe()).ToList();
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(token);
            }
            catch
            {
                break; // stopped
            }

            client.NoDelay = true;
            var connection = new VideohubConnection(this, client);

            // The dump is queued before the connection joins the list, so no change can reach
            // the client ahead of the state it changes.
            lock (_gate)
            {
                connection.Send(InitialDump(connection));
                _connections.Add(connection);
            }
            Log?.Invoke($"Videohub: client connected from {connection.Remote}.");
            ClientsChanged?.Invoke(this, EventArgs.Empty);

            _ = Task.Run(async () =>
            {
                try
                {
                    await connection.RunAsync(token);
                }
                finally
                {
                    lock (_gate) _connections.Remove(connection);
                    Log?.Invoke($"Videohub: client {connection.Remote} disconnected.");
                    ClientsChanged?.Invoke(this, EventArgs.Empty);
                }
            }, CancellationToken.None);
        }
    }

    internal void Report(string message) => Log?.Invoke(message);

    // ------------------------------------------------------------------ blocks

    internal static string Block(string header, IEnumerable<string> lines)
    {
        var text = new StringBuilder(header).Append(":\n");
        foreach (var line in lines) text.Append(line).Append('\n');
        return text.Append('\n').ToString();
    }

    private static string Preamble() => Block("PROTOCOL PREAMBLE", new[] { $"Version: {ProtocolVersion}" });

    private string DeviceBlock() => Block("VIDEOHUB DEVICE", new[]
    {
        "Device present: true",
        $"Model name: {ModelName}",
        $"Video inputs: {_router.InputCount + 1}", // the real inputs and "(clear)"
        "Video processing units: 0",
        $"Video outputs: {_router.OutputCount}",
        "Video monitoring outputs: 0",
        "Serial ports: 0"
    });

    /// <summary>Every input's label, then "(clear)". Line breaks would end the line early, so they become spaces.</summary>
    private string[] InputLabels() => _router.Inputs().Select(i => Clean(i.Label)).Append(ClearLabel).ToArray();

    private string[] OutputLabels() => _router.Outputs().Select(o => Clean(o.Label)).ToArray();

    private static string Clean(string label) => (label ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ');

    private static string LabelsBlock(string header, string[] labels, IEnumerable<int>? only = null) =>
        Block(header, (only ?? Enumerable.Range(0, labels.Length)).Where(i => i < labels.Length).Select(i => $"{i} {labels[i]}"));

    private string RoutingBlock(IEnumerable<int>? only = null)
    {
        var routes = _router.Routes();
        var clear = _router.InputCount;
        return Block("VIDEO OUTPUT ROUTING",
            (only ?? Enumerable.Range(0, routes.Length)).Where(o => o < routes.Length).Select(o => $"{o} {routes[o] ?? clear}"));
    }

    private string LocksBlock(VideohubConnection connection, IEnumerable<int>? only = null)
    {
        var locks = _router.Locks();
        return Block("VIDEO OUTPUT LOCKS",
            (only ?? Enumerable.Range(0, locks.Length)).Where(o => o < locks.Length).Select(o => $"{o} {LockLetter(locks[o], connection)}"));
    }

    /// <summary>"O" owned by this client, "L" locked by anyone else, "U" free.</summary>
    private static char LockLetter(OutputLock? held, VideohubConnection connection) =>
        held is null ? 'U' : held.Owner == connection.Owner ? 'O' : 'L';

    /// <summary>Everything a client is sent on connecting, in the spec's order.</summary>
    private string InitialDump(VideohubConnection connection) =>
        Preamble() + EverythingButPreamble(connection) + Block("END PRELUDE", Array.Empty<string>());

    /// <summary>What a client is sent again when the router changes size: the spec's "device changed".</summary>
    private string EverythingButPreamble(VideohubConnection connection) =>
        DeviceBlock() +
        LabelsBlock("INPUT LABELS", InputLabels()) +
        LabelsBlock("OUTPUT LABELS", OutputLabels()) +
        RoutingBlock() +
        LocksBlock(connection);

    // ------------------------------------------------------------------ inbound

    private const string Ack = "ACK\n\n";
    private const string Nak = "NAK\n\n";

    /// <summary>
    /// Acts on one block from a client. Runs on that connection's reader, in arrival order. The
    /// ACK or NAK is queued before anything the request changes, so the client sees ACK and then
    /// the status update, as the spec's dialogues show.
    /// </summary>
    internal void Handle(VideohubConnection connection, VideohubBlock block)
    {
        if (_settings.LogTraffic)
            Log?.Invoke($"Videohub {connection.Remote} -> {block.Header} {string.Join(" | ", block.Lines)}");

        if (!block.Header.EndsWith(':') || block.TooLong)
        {
            NakFor(connection, block, block.TooLong ? "too many lines" : "not a block header");
            return;
        }

        var header = block.Header[..^1].Trim().ToUpperInvariant();
        var lines = block.Lines;

        switch (header)
        {
            case "PING":
                connection.Send(Ack);
                break;

            // A header on its own asks for that block's current state.
            case "PROTOCOL PREAMBLE" when lines.Count == 0:
                connection.Send(Ack + Preamble());
                break;
            case "VIDEOHUB DEVICE" when lines.Count == 0:
                connection.Send(Ack + DeviceBlock());
                break;
            case "INPUT LABELS" when lines.Count == 0:
                connection.Send(Ack + LabelsBlock("INPUT LABELS", InputLabels()));
                break;
            case "OUTPUT LABELS" when lines.Count == 0:
                connection.Send(Ack + LabelsBlock("OUTPUT LABELS", OutputLabels()));
                break;
            case "VIDEO OUTPUT ROUTING" when lines.Count == 0:
                connection.Send(Ack + RoutingBlock());
                break;
            case "VIDEO OUTPUT LOCKS" when lines.Count == 0:
                connection.Send(Ack + LocksBlock(connection));
                break;

            case "INPUT LABELS":
                Labels(connection, block, inputs: true);
                break;
            case "OUTPUT LABELS":
                Labels(connection, block, inputs: false);
                break;
            case "VIDEO OUTPUT ROUTING":
                Routes(connection, block);
                break;
            case "VIDEO OUTPUT LOCKS":
                Locks(connection, block);
                break;

            default:
                // Monitoring outputs, serial ports and the rest: this router has none.
                NakFor(connection, block, "not something this router has");
                break;
        }
    }

    private void NakFor(VideohubConnection connection, VideohubBlock block, string why)
    {
        connection.Send(Nak);
        if (_settings.LogTraffic) Log?.Invoke($"Videohub {connection.Remote}: NAKed \"{block.Header}\" ({why}).");
    }

    /// <summary>Splits "7 2" or "7 New label" into the number and the rest. False if it does not start with a number.</summary>
    private static bool TrySplit(string line, out int index, out string rest)
    {
        var space = line.IndexOf(' ');
        var number = space < 0 ? line : line[..space];
        rest = space < 0 ? string.Empty : line[(space + 1)..];
        return int.TryParse(number, System.Globalization.NumberStyles.None, null, out index);
    }

    private void Routes(VideohubConnection connection, VideohubBlock block)
    {
        var outputs = _router.OutputCount;
        var clear = _router.InputCount;
        var routes = new List<(int Output, int Input)>();

        foreach (var line in block.Lines)
        {
            if (!TrySplit(line.Trim(), out var output, out var rest) || output >= outputs ||
                !int.TryParse(rest.Trim(), System.Globalization.NumberStyles.None, null, out var input) || input > clear)
            {
                NakFor(connection, block, $"\"{line}\" is not an output and input of this router");
                return;
            }
            routes.Add((output, input));
        }

        connection.Send(Ack);
        var origin = $"Videohub {connection.Remote}";
        foreach (var (output, input) in routes)
        {
            // Done: the change goes to every client from OnRouteChanged. Refused because someone
            // else has it locked: this client is told the route it still has.
            if (!_router.Route(output, input == clear ? null : input, origin, requester: connection.Owner))
                connection.Send(RoutingBlock(new[] { output }));
        }
    }

    private void Locks(VideohubConnection connection, VideohubBlock block)
    {
        var outputs = _router.OutputCount;
        var changes = new List<(int Output, char Letter)>();

        foreach (var line in block.Lines)
        {
            var valid = TrySplit(line.Trim(), out var output, out var rest);
            var letter = rest.Trim().ToUpperInvariant();
            if (!valid || output >= outputs || letter is not ("O" or "U" or "F"))
            {
                NakFor(connection, block, $"\"{line}\" is not an output and O, U or F");
                return;
            }
            changes.Add((output, letter[0]));
        }

        connection.Send(Ack);
        var origin = $"Videohub {connection.Remote}";
        foreach (var (output, letter) in changes)
        {
            var done = letter switch
            {
                'O' => _router.Lock(output, new OutputLock(connection.Owner), origin),
                'U' => _router.Unlock(output, connection.Owner, origin),
                _ => _router.Unlock(output, connection.Owner, origin, force: true)
            };

            // Done: every client hears from OnLockChanged. Refused: this one is told it is still "L".
            if (!done) connection.Send(LocksBlock(connection, new[] { output }));
        }
    }

    private void Labels(VideohubConnection connection, VideohubBlock block, bool inputs)
    {
        var count = inputs ? _router.InputCount : _router.OutputCount; // "(clear)" cannot be renamed
        var labels = new List<(int Index, string Label)>();

        foreach (var line in block.Lines)
        {
            if (!TrySplit(line.TrimStart(), out var index, out var label) || index >= count)
            {
                NakFor(connection, block, $"\"{line}\" is not {(inputs ? "an input" : "an output")} of this router");
                return;
            }
            labels.Add((index, Clean(label.TrimEnd())));
        }

        connection.Send(Ack);

        if (!_settings.AllowLabelChanges)
        {
            // Acknowledged but not done: the client is sent the labels it tried to change, as the
            // spec has a Videohub do for a request it could not carry out.
            Log?.Invoke($"Videohub {connection.Remote}: rename refused; renaming from Videohub clients is turned off.");
            var header = inputs ? "INPUT LABELS" : "OUTPUT LABELS";
            connection.Send(LabelsBlock(header, inputs ? InputLabels() : OutputLabels(), labels.Select(l => l.Index)));
            return;
        }

        foreach (var (index, label) in labels)
        {
            if (inputs) _router.UpdateInput(index, i => i.Label = label);
            else _router.UpdateOutput(index, o => o.Label = label);
        }
    }

    // ------------------------------------------------------------------ outbound

    private List<VideohubConnection> Snapshot()
    {
        lock (_gate) return _connections.ToList();
    }

    private void OnRouteChanged(object? sender, RouteChangedEventArgs e)
    {
        var connections = Snapshot();
        if (connections.Count == 0 || e.Output >= _router.OutputCount) return;

        var block = Block("VIDEO OUTPUT ROUTING", new[] { $"{e.Output} {e.Input ?? _router.InputCount}" });
        foreach (var connection in connections) connection.Send(block);
    }

    private void OnLockChanged(object? sender, LockChangedEventArgs e)
    {
        // Each client sees its own locks as "O" and everyone else's as "L", so each gets its own block.
        foreach (var connection in Snapshot())
            connection.Send(Block("VIDEO OUTPUT LOCKS", new[] { $"{e.Output} {LockLetter(e.Current, connection)}" }));
    }

    private void OnNamesChanged(object? sender, NamesChangedEventArgs e)
    {
        var inputs = InputLabels();
        var outputs = OutputLabels();
        List<int> changedInputs, changedOutputs;
        List<VideohubConnection> connections;

        lock (_gate)
        {
            if (inputs.Length != _inputLabels.Length || outputs.Length != _outputLabels.Length)
            {
                // A size change mid-way; OnLayoutChanged sends everything.
                return;
            }

            changedInputs = Enumerable.Range(0, inputs.Length).Where(i => inputs[i] != _inputLabels[i]).ToList();
            changedOutputs = Enumerable.Range(0, outputs.Length).Where(o => outputs[o] != _outputLabels[o]).ToList();
            _inputLabels = inputs;
            _outputLabels = outputs;
            connections = _connections.ToList();
        }

        var text = (changedInputs.Count > 0 ? LabelsBlock("INPUT LABELS", inputs, changedInputs) : string.Empty) +
                   (changedOutputs.Count > 0 ? LabelsBlock("OUTPUT LABELS", outputs, changedOutputs) : string.Empty);
        if (text.Length == 0) return;
        foreach (var connection in connections) connection.Send(text);
    }

    /// <summary>A different number of inputs or outputs: everything but the preamble again, as for a new Videohub.</summary>
    private void OnLayoutChanged(object? sender, EventArgs e)
    {
        var inputs = InputLabels();
        var outputs = OutputLabels();
        List<VideohubConnection> connections;

        lock (_gate)
        {
            if (inputs.Length == _inputLabels.Length && outputs.Length == _outputLabels.Length) return; // only a preset changed
            _inputLabels = inputs;
            _outputLabels = outputs;
            connections = _connections.ToList();
        }

        foreach (var connection in connections) connection.Send(EverythingButPreamble(connection));
    }

    public void Dispose()
    {
        _router.RouteChanged -= OnRouteChanged;
        _router.NamesChanged -= OnNamesChanged;
        _router.LayoutChanged -= OnLayoutChanged;
        _router.LockChanged -= OnLockChanged;
        Stop();
    }
}

/// <summary>One client's TCP connection: a reader turning bytes into blocks, and a writer sending queued text in order.</summary>
internal sealed class VideohubConnection
{
    private readonly VideohubServer _server;
    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private readonly VideohubReader _reader = new();
    private readonly Channel<string> _outbound =
        Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
    private readonly DateTime _connectedAt = DateTime.Now;

    public VideohubConnection(VideohubServer server, TcpClient client)
    {
        _server = server;
        _client = client;
        _stream = client.GetStream();
        Remote = client.Client.RemoteEndPoint?.ToString() ?? "unknown";
        Owner = $"Videohub {RemoteAddress.Of(client)}";
    }

    public string Remote { get; }

    /// <summary>
    /// Who this client is to the router's locks: "Videohub 10.0.0.5". The spec ties a lock to the
    /// address it was made from, so every client on one machine shares them.
    /// </summary>
    public string Owner { get; }

    public string Describe() => $"{Remote}  connected {_connectedAt:HH:mm:ss}";

    public void Send(string text) => _outbound.Writer.TryWrite(text);

    public async Task RunAsync(CancellationToken serverToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(serverToken);
        var writer = Task.Run(() => WriteLoopAsync(cts.Token), CancellationToken.None);
        var buffer = new byte[4096];

        try
        {
            while (!cts.IsCancellationRequested)
            {
                var read = await _stream.ReadAsync(buffer, cts.Token);
                if (read == 0) break;

                foreach (var block in _reader.Feed(buffer.AsSpan(0, read))) _server.Handle(this, block);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException or SocketException)
        {
            // disconnected
        }
        finally
        {
            cts.Cancel();
            _outbound.Writer.TryComplete();
            try { await writer; } catch { /* already closing */ }
            Close();
        }
    }

    private async Task WriteLoopAsync(CancellationToken token)
    {
        try
        {
            await foreach (var text in _outbound.Reader.ReadAllAsync(token))
            {
                if (_server.CurrentSettings.LogTraffic)
                    _server.Report($"Videohub {Remote} <- {text.TrimEnd('\n').Replace("\n", " | ")}");
                await _stream.WriteAsync(Encoding.UTF8.GetBytes(text), token);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException or SocketException)
        {
            // closing
        }
    }

    public void Close()
    {
        try { _client.Close(); } catch { /* already gone */ }
    }
}
