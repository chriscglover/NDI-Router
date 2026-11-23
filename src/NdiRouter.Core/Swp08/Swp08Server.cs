using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using NdiRouter.Core;

namespace NdiRouter.Swp08;

public sealed class Swp08Settings
{
    public bool Enabled { get; set; } = true;

    /// <summary>Interface to listen on; 0.0.0.0 for all of them.</summary>
    public string BindAddress { get; set; } = "0.0.0.0";

    /// <summary>8910 is the port Companion's SW-P-08 module defaults to.</summary>
    public int Port { get; set; } = 8910;

    /// <summary>Matrix number on the wire (0 = matrix 1).</summary>
    public int Matrix { get; set; }

    /// <summary>Level number on the wire (0 = level 1).</summary>
    public int Level { get; set; }

    /// <summary>
    /// NDI carries video, audio and metadata together, so there is only one real level. With
    /// this on, every level addresses it: a controller that switches video and audio levels
    /// together gets the same answer on both.
    /// </summary>
    public bool AnyLevel { get; set; } = true;

    /// <summary>
    /// Source number (on the wire) that stands for "nothing routed". Connecting it clears the
    /// output; a cleared output reports it. 1023 is the highest normal-mode source.
    /// </summary>
    public int ClearSource { get; set; } = 1023;

    /// <summary>Send NAMES UPDATED (Issue 32) so controllers re-read names after an edit.</summary>
    public bool SendNamesUpdated { get; set; } = true;

    /// <summary>Log every message, not just connections and routes.</summary>
    public bool LogTraffic { get; set; }

    /// <summary>How long to wait for a controller to ACK a message before resending it.</summary>
    public int AckTimeoutMs { get; set; } = 1000;

    /// <summary>
    /// Times a message is sent before giving up on it: once, then re-sent up to 5 times, as SW-P-88
    /// 7.2.2 has a Pro-Bel controller do.
    /// </summary>
    public int SendAttempts { get; set; } = 6;

    public Swp08Settings Clone() => (Swp08Settings)MemberwiseClone();
}

/// <summary>
/// Makes the router controllable over SW-P-08 on TCP. Each connection is an independent link,
/// as a serial port on a hardware router would be: inbound messages are ACKed (or NAKed when
/// corrupt) at once and processed in order; outbound messages go one at a time, each waiting
/// for the controller's ACK and resent on NAK or timeout.
///
/// Any route change - from a controller, the panel, a preset - is announced to every connected
/// controller with CROSSPOINT CONNECTED, including the one that asked for it.
/// </summary>
public sealed class Swp08Server : IDisposable
{
    private readonly RouterCore _router;
    private readonly object _gate = new();
    private readonly List<Swp08Connection> _connections = new();
    private readonly System.Threading.Timer _namesTimer;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private volatile Swp08Settings _settings = new();
    private bool _pendingSourceNames;
    private bool _pendingDestNames;

    public Swp08Server(RouterCore router)
    {
        _router = router;
        _router.RouteChanged += OnRouteChanged;
        _router.NamesChanged += OnNamesChanged;
        _namesTimer = new System.Threading.Timer(_ => FlushNamesUpdated());
    }

    public event Action<string>? Log;
    public event EventHandler? ClientsChanged;

    /// <summary>
    /// The spec has a controller send NAMES UPDATED five seconds after the last edit, so a run
    /// of edits becomes one re-read.
    /// </summary>
    public int NamesUpdatedDelayMs { get; set; } = 5000;

    public bool IsListening => _listener is not null;

    /// <summary>The port actually bound - useful when started on port 0.</summary>
    public int BoundPort { get; private set; }

    internal Swp08Settings CurrentSettings => _settings;

    /// <summary>Applies settings that do not need the listener restarted (matrix, level, logging...).</summary>
    public void UpdateSettings(Swp08Settings settings) => _settings = settings.Clone();

    public void Start(Swp08Settings settings)
    {
        Stop();
        _settings = settings.Clone();

        if (!IPAddress.TryParse(settings.BindAddress, out var address)) address = IPAddress.Any;

        // Exclusive, so a second copy of this application (or anything else on the port) makes
        // the start fail loudly. Windows otherwise lets two listeners share a port, and the
        // controller ends up talking to whichever one answers.
        var listener = new TcpListener(address, settings.Port) { ExclusiveAddressUse = true };
        listener.Start();

        _listener = listener;
        _cts = new CancellationTokenSource();
        BoundPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        Log?.Invoke($"SW-P-08: listening on {address}:{BoundPort} (matrix {settings.Matrix + 1}, " +
                    $"{(settings.AnyLevel ? "all levels" : $"level {settings.Level + 1}")}).");
        _ = AcceptLoopAsync(listener, _cts.Token);
    }

    public void Stop()
    {
        var listener = _listener;
        if (listener is null) return;

        _listener = null;
        _cts?.Cancel();
        listener.Stop();

        List<Swp08Connection> connections;
        lock (_gate) connections = _connections.ToList();
        foreach (var connection in connections) connection.Close();

        Log?.Invoke("SW-P-08: stopped.");
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
            var connection = new Swp08Connection(this, client);
            lock (_gate) _connections.Add(connection);
            Log?.Invoke($"SW-P-08: controller connected from {connection.Remote}.");
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
                    Log?.Invoke($"SW-P-08: controller {connection.Remote} disconnected.");
                    ClientsChanged?.Invoke(this, EventArgs.Empty);
                }
            }, CancellationToken.None);
        }
    }

    internal void Report(string message) => Log?.Invoke(message);

    // ------------------------------------------------------------------ inbound

    /// <summary>Acts on one message from a controller. Runs on that connection's reader, in arrival order.</summary>
    internal void Handle(Swp08Connection connection, Swp08Frame frame)
    {
        var s = _settings;
        if (s.LogTraffic)
            Log?.Invoke($"SW-P-08 {connection.Remote} -> {Swp08Command.Name(frame.Command)} {Hex(frame.Payload)}");

        try
        {
            switch (frame.Command)
            {
                case Swp08Command.CrosspointInterrogate:
                    Interrogate(connection, Swp08Messages.ParseInterrogate(frame.Payload), extended: false);
                    break;

                case Swp08Command.ExtendedInterrogate:
                    connection.UsesExtended = true;
                    Interrogate(connection, Swp08Messages.ParseExtendedInterrogate(frame.Payload), extended: true);
                    break;

                case Swp08Command.CrosspointConnect:
                    Connect(connection, Swp08Messages.ParseConnect(frame.Payload));
                    break;

                case Swp08Command.ExtendedConnect:
                    connection.UsesExtended = true;
                    Connect(connection, Swp08Messages.ParseExtendedConnect(frame.Payload));
                    break;

                case Swp08Command.CrosspointTallyDumpRequest:
                {
                    var (matrix, level) = Swp08Messages.ParseTallyDumpRequest(frame.Payload);
                    if (!Addressed(connection, matrix, level, "TALLY DUMP")) break;
                    connection.Enqueue(Swp08Messages.TallyDump(matrix, level, EncodedRoutes()));
                    break;
                }

                case Swp08Command.ExtendedTallyDumpRequest:
                {
                    connection.UsesExtended = true;
                    var (matrix, level) = Swp08Messages.ParseExtendedTallyDumpRequest(frame.Payload);
                    if (!Addressed(connection, matrix, level, "EXT TALLY DUMP")) break;
                    connection.Enqueue(Swp08Messages.ExtendedTallyDump(matrix, level, EncodedRoutes()));
                    break;
                }

                case Swp08Command.GetSourceNames:
                case Swp08Command.GetDestNames:
                case Swp08Command.ExtendedGetSourceNames:
                case Swp08Command.ExtendedGetDestNames:
                {
                    var request = Swp08Messages.ParseNameRequest(frame.Command, frame.Payload);
                    if (request.Extended) connection.UsesExtended = true;
                    if (request.Matrix != s.Matrix)
                    {
                        Ignored(connection, $"name request for matrix {request.Matrix + 1}");
                        break;
                    }

                    // Each length has its own name: a 4-character request gets the 4-character
                    // mnemonic, not the 12-character name cut short.
                    var names = request.Sources
                        ? Enumerable.Range(0, _router.InputCount).Select(i => _router.InputName(i, request.Length)).ToList()
                        : Enumerable.Range(0, _router.OutputCount).Select(o => _router.OutputName(o, request.Length)).ToList();
                    connection.Enqueue(Swp08Messages.NameResponses(request, names));
                    break;
                }

                case Swp08Command.ProtocolImplementationRequest:
                    connection.Enqueue(Swp08Messages.ProtocolImplementation());
                    break;

                case Swp08Command.DualControllerStatusRequest:
                    connection.Enqueue(Swp08Messages.DualControllerStatus());
                    break;

                default:
                    // ACKed already, as the spec requires of any well-formed message.
                    if (s.LogTraffic) Ignored(connection, $"command {Swp08Command.Name(frame.Command)} (not implemented)");
                    break;
            }
        }
        catch (FormatException ex)
        {
            Log?.Invoke($"SW-P-08 {connection.Remote}: malformed {Swp08Command.Name(frame.Command)}: {ex.Message}");
        }
    }

    private bool Addressed(Swp08Connection connection, int matrix, int level, string what)
    {
        var s = _settings;
        if (matrix == s.Matrix && (s.AnyLevel || level == s.Level)) return true;
        Ignored(connection, $"{what} for matrix {matrix + 1} level {level + 1}");
        return false;
    }

    private void Ignored(Swp08Connection connection, string what)
    {
        if (_settings.LogTraffic) Log?.Invoke($"SW-P-08 {connection.Remote}: ignored {what}.");
    }

    private void Interrogate(Swp08Connection connection, CrosspointRequest request, bool extended)
    {
        if (!Addressed(connection, request.Matrix, request.Level, "INTERROGATE")) return;
        if (request.Destination >= _router.OutputCount)
        {
            Ignored(connection, $"INTERROGATE of destination {request.Destination + 1}, which does not exist");
            return;
        }

        var source = Encode(_router.GetRoute(request.Destination));
        var normal = !extended && Swp08Messages.FitsNormal(request.Matrix, request.Level, request.Destination, source);
        connection.Enqueue(normal
            ? Swp08Messages.Tally(request.Matrix, request.Level, request.Destination, source)
            : Swp08Messages.ExtendedTally(request.Matrix, request.Level, request.Destination, source));
    }

    private void Connect(Swp08Connection connection, CrosspointRequest request)
    {
        if (!Addressed(connection, request.Matrix, request.Level, "CONNECT")) return;

        var s = _settings;
        if (request.Destination >= _router.OutputCount)
        {
            Log?.Invoke($"SW-P-08 {connection.Remote}: CONNECT to destination {request.Destination + 1}, which does not exist.");
            return;
        }

        int? input;
        if (request.Source == s.ClearSource)
        {
            input = null;
        }
        else if (request.Source is int source && source < _router.InputCount)
        {
            input = source;
        }
        else
        {
            Log?.Invoke($"SW-P-08 {connection.Remote}: CONNECT from source {request.Source + 1}, which does not exist.");
            return;
        }

        // The CONNECTED that confirms this goes out from OnRouteChanged, to every controller.
        _router.Route(request.Destination, input, $"SW-P-08 {connection.Remote}", request.Level);
    }

    /// <summary>The wire source for a route: the input index, or the clear source when nothing is routed.</summary>
    private int Encode(int? input) => input ?? _settings.ClearSource;

    private List<int> EncodedRoutes() => _router.Routes().Select(Encode).ToList();

    // ------------------------------------------------------------------ outbound

    private void OnRouteChanged(object? sender, RouteChangedEventArgs e)
    {
        var s = _settings;
        var source = Encode(e.Input);

        // A route made on one level of an all-levels router is announced on that level and on the
        // configured one, so a controller watching either sees it.
        var levels = new List<int> { e.Level ?? s.Level };
        if (s.AnyLevel && !levels.Contains(s.Level)) levels.Add(s.Level);

        List<Swp08Connection> connections;
        lock (_gate) connections = _connections.ToList();
        if (connections.Count == 0) return;

        foreach (var level in levels)
        {
            var extended = Swp08Messages.ExtendedConnected(s.Matrix, level, e.Output, source);
            var normal = Swp08Messages.FitsNormal(s.Matrix, level, e.Output, source)
                ? Swp08Messages.Connected(s.Matrix, level, e.Output, source)
                : null;

            foreach (var connection in connections)
                connection.Enqueue(connection.UsesExtended || normal is null ? extended : normal);
        }
    }

    private void OnNamesChanged(object? sender, NamesChangedEventArgs e)
    {
        if (!_settings.SendNamesUpdated) return;

        lock (_gate)
        {
            _pendingSourceNames |= e.Inputs;
            _pendingDestNames |= e.Outputs;
        }

        _namesTimer.Change(NamesUpdatedDelayMs, Timeout.Infinite);
    }

    private void FlushNamesUpdated()
    {
        bool sources, destinations;
        List<Swp08Connection> connections;
        lock (_gate)
        {
            sources = _pendingSourceNames;
            destinations = _pendingDestNames;
            _pendingSourceNames = _pendingDestNames = false;
            connections = _connections.ToList();
        }

        if (!sources && !destinations) return;

        var frame = Swp08Messages.NamesUpdated(_settings.Matrix, sources, destinations);
        foreach (var connection in connections) connection.Enqueue(frame);
    }

    internal static string Hex(byte[] bytes) => Convert.ToHexString(bytes);

    public void Dispose()
    {
        _router.RouteChanged -= OnRouteChanged;
        _router.NamesChanged -= OnNamesChanged;
        Stop();
        _namesTimer.Dispose();
    }
}

/// <summary>One controller's TCP connection.</summary>
internal sealed class Swp08Connection
{
    private readonly Swp08Server _server;
    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private readonly Swp08FrameDecoder _decoder = new();
    private readonly object _writeGate = new();
    private readonly object _ackGate = new();
    private readonly Channel<byte[]> _outbound =
        Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
    private readonly DateTime _connectedAt = DateTime.Now;
    private TaskCompletionSource<bool>? _pendingAck;
    private bool _everAcked;
    private int _silentTimeouts;
    private volatile bool _peerNeverAcks;

    public Swp08Connection(Swp08Server server, TcpClient client)
    {
        _server = server;
        _client = client;
        _stream = client.GetStream();
        Remote = client.Client.RemoteEndPoint?.ToString() ?? "unknown";
    }

    public string Remote { get; }

    /// <summary>Set once the controller uses an extended command; it then gets extended replies.</summary>
    public volatile bool UsesExtended;

    public string Describe() =>
        $"{Remote}  connected {_connectedAt:HH:mm:ss}{(UsesExtended ? ", extended" : string.Empty)}" +
        $"{(_peerNeverAcks ? ", does not ACK" : string.Empty)}";

    public void Enqueue(byte[] frame) => _outbound.Writer.TryWrite(frame);

    public void Enqueue(IEnumerable<byte[]> frames)
    {
        foreach (var frame in frames) Enqueue(frame);
    }

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

                foreach (var item in _decoder.Feed(buffer.AsSpan(0, read))) OnItem(item);
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

    private void OnItem(Swp08Item item)
    {
        switch (item.Kind)
        {
            case Swp08ItemKind.PeerAck:
                _everAcked = true;
                _peerNeverAcks = false;
                lock (_ackGate) _pendingAck?.TrySetResult(true);
                break;

            case Swp08ItemKind.PeerNak:
                lock (_ackGate) _pendingAck?.TrySetResult(false);
                break;

            case Swp08ItemKind.Corrupt:
                WriteRaw(Swp08Codec.NakBytes());
                _server.Report($"SW-P-08 {Remote}: NAKed a bad message ({item.Error}).");
                break;

            case Swp08ItemKind.Frame:
                WriteRaw(Swp08Codec.AckBytes());
                _server.Handle(this, item.Frame!);
                break;
        }
    }

    private async Task WriteLoopAsync(CancellationToken token)
    {
        try
        {
            await foreach (var frame in _outbound.Reader.ReadAllAsync(token))
                await SendAsync(frame, token);
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>One message, waiting for its ACK and resending it on NAK or timeout.</summary>
    private async Task SendAsync(byte[] frame, CancellationToken token)
    {
        var settings = _server.CurrentSettings;
        if (settings.LogTraffic)
            _server.Report($"SW-P-08 {Remote} <- {Swp08Command.Name(frame.Length > 2 ? frame[2] : (byte)0)} {Swp08Server.Hex(frame)}");

        var attempts = Math.Max(1, settings.SendAttempts);
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            var ack = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_ackGate) _pendingAck = ack;

            if (!WriteRaw(frame)) return;

            // A controller that has never ACKed anything is not going to: some IP control systems
            // treat TCP as reliable and skip the ACK layer. Waiting on every message would hold
            // each one up by the full timeout, so stop waiting for them.
            if (_peerNeverAcks) return;

            var finished = await Task.WhenAny(ack.Task, Task.Delay(Math.Max(50, settings.AckTimeoutMs), token));
            token.ThrowIfCancellationRequested();

            if (finished == ack.Task)
            {
                if (ack.Task.Result)
                {
                    _silentTimeouts = 0;
                    return;
                }

                continue; // NAKed: send it again
            }

            if (!_everAcked && ++_silentTimeouts >= 3)
            {
                _peerNeverAcks = true;
                _server.Report($"SW-P-08 {Remote}: controller does not ACK; sending to it without waiting.");
                return;
            }
        }

        _server.Report($"SW-P-08 {Remote}: no ACK after {attempts} attempt(s); gave up on one message.");
    }

    private bool WriteRaw(byte[] bytes)
    {
        lock (_writeGate)
        {
            try
            {
                _stream.Write(bytes);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    public void Close()
    {
        try { _client.Close(); } catch { /* already gone */ }
    }
}
