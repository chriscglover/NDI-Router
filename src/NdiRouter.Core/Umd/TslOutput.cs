using System.Net;
using System.Net.Sockets;
using NdiRouter.Core;
using NdiRouter.Net;
using NdiRouter.Tsl;

namespace NdiRouter.Umd;

public sealed class TslSettings
{
    public bool Enabled { get; set; }
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 8900;
    public bool UseTcp { get; set; }
    public bool StreamFraming { get; set; }
    public TslVersion Version { get; set; } = TslVersion.V50;
    public int Screen { get; set; }
    public bool ScreenBroadcast { get; set; }
    public bool Unicode { get; set; }

    /// <summary>Each display is re-sent on this interval; a change goes out at once regardless.</summary>
    public int RepeatIntervalMs { get; set; } = 5000;

    public string RoutedTemplate { get; set; } = UmdLabel.DefaultRoutedTemplate;
    public string ClearedTemplate { get; set; } = UmdLabel.DefaultClearedTemplate;
    public FitMode FitMode { get; set; } = FitMode.Truncate;
    public bool Uppercase { get; set; }

    public TallyColour RoutedLamp { get; set; } = TallyColour.Off;
    public TallyColour ClearedLamp { get; set; } = TallyColour.Off;
    public bool DriveTextTally { get; set; }
    public int Brightness { get; set; } = 3;

    public TslSettings Clone() => (TslSettings)MemberwiseClone();
}

/// <summary>What one display should be showing right now.</summary>
public sealed record UmdDisplay(int OutputIndex, int Address, bool Routed, UmdMessage Message);

/// <summary>Works out each output's display text and lamps. Kept apart from the sender so the UI can preview it.</summary>
public static class UmdEngine
{
    public static int MaxTextLength(TslVersion version) =>
        version == TslVersion.V50 ? 0 : TslPacketBuilder.V31TextLength;

    public static int MaxAddress(TslVersion version) =>
        version == TslVersion.V50 ? TslPacketBuilder.MaxV50Address : TslPacketBuilder.MaxLegacyAddress;

    public static UmdDisplay Build(int outputIndex, RouterOutput output, IReadOnlyList<RouterInput> inputs, TslSettings settings)
    {
        var inputIndex = output.Input is int i && i < inputs.Count ? i : (int?)null;
        var input = inputIndex is int k ? inputs[k] : null;
        var routed = input is not null;
        var context = new UmdContext(outputIndex, output, inputIndex, input);

        var text = UmdLabel.Format(routed ? settings.RoutedTemplate : settings.ClearedTemplate, context, output.UmdAddress);

        // A routed template that resolves to nothing (an input with no label, say) would leave the
        // display blank, which reads as "no signal". Fall back rather than show nothing.
        if (string.IsNullOrWhiteSpace(text) && routed) text = input!.NdiSource;
        if (string.IsNullOrWhiteSpace(text) && routed) text = $"Input {inputIndex + 1}";

        if (settings.Uppercase) text = text.ToUpperInvariant();
        text = UmdLabel.Fit(text, MaxTextLength(settings.Version), settings.FitMode);

        var lamp = routed ? settings.RoutedLamp : settings.ClearedLamp;
        var message = new UmdMessage
        {
            Address = output.UmdAddress,
            Text = text,
            LeftTally = lamp,
            RightTally = lamp,
            TextTally = settings.DriveTextTally ? lamp : TallyColour.Off,
            Brightness = settings.Brightness
        };

        return new UmdDisplay(outputIndex, output.UmdAddress, routed, message);
    }

    /// <summary>Every enabled output whose address the chosen protocol version can carry.</summary>
    public static List<UmdDisplay> BuildAll(RouterCore router, TslSettings settings)
    {
        var inputs = router.Inputs();
        var outputs = router.Outputs();
        var max = MaxAddress(settings.Version);

        var displays = new List<UmdDisplay>();
        for (var o = 0; o < outputs.Count; o++)
        {
            var output = outputs[o];
            if (!output.UmdEnabled || output.UmdAddress < 0 || output.UmdAddress > max) continue;
            displays.Add(Build(o, output, inputs, settings));
        }

        return displays;
    }

    public static byte[] BuildPacket(UmdDisplay display, TslSettings settings)
    {
        var screen = settings.ScreenBroadcast ? TslPacketBuilder.V50BroadcastScreen : settings.Screen;
        var packet = TslPacketBuilder.Build(settings.Version, display.Message, screen, settings.Unicode);
        return settings.UseTcp && settings.StreamFraming ? StreamFraming.Wrap(packet) : packet;
    }
}

/// <summary>
/// Sends TSL UMD packets for the router's outputs. Runs on its own task.
///
/// Packets are spaced evenly - one display per tick, a full pass per repeat interval - and a
/// display whose text or lamp has changed jumps the queue. Sending all the displays in one burst
/// looks harmless but is not: over TCP the packets coalesce into one read at the far end, and a
/// receiver that treats each read as one message (Companion's TSL listener does) keeps the first
/// and drops the rest.
/// </summary>
public sealed class TslOutput : IDisposable
{
    private readonly RouterCore _router;
    private readonly SemaphoreSlim _wake = new(0, int.MaxValue);
    private readonly Dictionary<int, (int Address, string Text, TallyColour Lamp)> _lastSent = new();
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private TslSettings _settings = new();
    private long _packets;

    public TslOutput(RouterCore router)
    {
        _router = router;
        _router.RouteChanged += (_, _) => Wake();
        _router.NamesChanged += (_, _) => Wake();
        _router.LayoutChanged += (_, _) => Wake();
    }

    public event Action<string>? Log;

    /// <summary>Raised from the sending task whenever the state line changes.</summary>
    public event Action<string>? StatusChanged;

    public long PacketsSent => Interlocked.Read(ref _packets);

    public string Status { get; private set; } = "TSL: off";

    public void Apply(TslSettings settings)
    {
        Stop();
        _settings = settings.Clone();
        lock (_lastSent) _lastSent.Clear();

        if (!settings.Enabled)
        {
            SetStatus("TSL: off");
            return;
        }

        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _loop = Task.Run(() => RunAsync(_settings, token), CancellationToken.None);
    }

    /// <summary>Re-sends a display on its next tick, e.g. after a template change.</summary>
    public void Wake()
    {
        if (_wake.CurrentCount < 4) _wake.Release();
    }

    public void Stop()
    {
        var cts = _cts;
        if (cts is null) return;
        _cts = null;
        cts.Cancel();
        try { _loop?.Wait(2000); } catch { /* cancelled */ }
        cts.Dispose();
    }

    private async Task RunAsync(TslSettings settings, CancellationToken token)
    {
        IPacketSender? sender = null;
        var cursor = 0;
        var announced = false;

        try
        {
            while (!token.IsCancellationRequested)
            {
                var displays = UmdEngine.BuildAll(_router, settings);
                if (displays.Count == 0)
                {
                    SetStatus("TSL: running, no displays enabled");
                    await WaitAsync(1000, token);
                    continue;
                }

                if (sender is null)
                {
                    try
                    {
                        sender = await OpenAsync(settings, token);
                        lock (_lastSent) _lastSent.Clear(); // a fresh connection gets everything
                        Log?.Invoke($"TSL: sending to {sender.Description} ({VersionName(settings.Version)}), " +
                                    $"{displays.Count} display(s) every {settings.RepeatIntervalMs} ms.");
                        announced = false;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        // A multiviewer that is still booting is normal; retry quietly.
                        if (!announced) Log?.Invoke($"TSL: cannot reach {settings.Host}:{settings.Port} ({ex.Message}); retrying every 5 s.");
                        announced = true;
                        SetStatus("TSL: waiting for destination");
                        await WaitAsync(5000, token, wakeable: false);
                        continue;
                    }
                }

                // A changed display first; otherwise the next one round.
                UmdDisplay? next;
                lock (_lastSent)
                {
                    next = displays.FirstOrDefault(d =>
                        !_lastSent.TryGetValue(d.OutputIndex, out var last) ||
                        last.Address != d.Address || last.Text != d.Message.Text || last.Lamp != d.Message.LeftTally);
                }

                if (next is null)
                {
                    if (cursor >= displays.Count) cursor = 0;
                    next = displays[cursor];
                    cursor = (cursor + 1) % displays.Count;
                }

                try
                {
                    sender.Send(UmdEngine.BuildPacket(next, settings));
                    lock (_lastSent) _lastSent[next.OutputIndex] = (next.Address, next.Message.Text, next.Message.LeftTally);
                    var count = Interlocked.Increment(ref _packets);
                    SetStatus($"TSL: {sender.Description}, {count} packets");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Log?.Invoke($"TSL: send to {sender.Description} failed ({ex.Message}); reconnecting.");
                    sender.Dispose();
                    sender = null;
                    await WaitAsync(1000, token, wakeable: false);
                    continue;
                }

                var spacing = Math.Max(10, settings.RepeatIntervalMs / displays.Count);
                await WaitAsync(spacing, token);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            sender?.Dispose();
            SetStatus("TSL: off");
        }
    }

    /// <summary>
    /// Waits out the spacing, or until a route changes - but never less than 20 ms after the
    /// previous packet, so changes arriving together still leave one at a time.
    /// </summary>
    private async Task WaitAsync(int milliseconds, CancellationToken token, bool wakeable = true)
    {
        if (!wakeable)
        {
            await Task.Delay(milliseconds, token);
            return;
        }

        var started = Environment.TickCount64;
        await _wake.WaitAsync(milliseconds, token);
        var elapsed = Environment.TickCount64 - started;
        if (elapsed < 20) await Task.Delay((int)(20 - elapsed), token);
    }

    private static async Task<IPacketSender> OpenAsync(TslSettings settings, CancellationToken token)
    {
        var endpoint = new IPEndPoint(await ResolveAsync(settings.Host, token), settings.Port);
        if (!settings.UseTcp) return new UdpPacketSender(endpoint);

        var tcp = new TcpPacketSender(endpoint);
        try
        {
            tcp.Connect();
            return tcp;
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    }

    private static async Task<IPAddress> ResolveAsync(string host, CancellationToken token)
    {
        if (IPAddress.TryParse(host, out var address)) return address;
        var resolved = (await Dns.GetHostAddressesAsync(host, token))
            .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
        return resolved ?? throw new InvalidOperationException($"could not resolve \"{host}\" to an IPv4 address");
    }

    public static string VersionName(TslVersion version) => version switch
    {
        TslVersion.V31 => "TSL 3.1",
        TslVersion.V40 => "TSL 4.0",
        _ => "TSL 5.0"
    };

    private void SetStatus(string status)
    {
        if (status == Status) return;
        Status = status;
        StatusChanged?.Invoke(status);
    }

    public void Dispose()
    {
        Stop();
        _wake.Dispose();
    }
}
