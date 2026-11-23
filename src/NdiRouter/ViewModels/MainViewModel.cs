using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NdiRouter.Config;
using NdiRouter.Core;
using NdiRouter.Ndi;

namespace NdiRouter.Gui.ViewModels;

/// <summary>
/// Everything behind the main window: the router panel, the inputs and outputs tables, the
/// settings tabs, the status bar and the log.
/// </summary>
/// <remarks>
/// Threading: properties and collections are only touched on the UI thread. Router, discovery,
/// SW-P-08 and TSL events arrive on other threads and are posted to it, and bursts of them are
/// coalesced into one refresh.
/// </remarks>
public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private const int MaxLogLines = 3000;

    private readonly RouterHost _host;
    private readonly DispatcherTimer _pollTimer;
    private readonly DispatcherTimer _saveTimer;
    private readonly HashSet<int> _selected = new() { 0 };
    private HashSet<string> _online = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<string> _discovered = Array.Empty<string>();
    private int[] _connections = Array.Empty<int>();
    private bool _refreshQueued;
    private bool _layoutChanged;
    private volatile bool _dirty;
    private bool _disposed;

    public MainViewModel(RouterHost host)
    {
        _host = host;
        Swp08 = new Swp08ViewModel(host);
        Tsl = new TslViewModel(host, () => Refresh(layout: false));
        Setup = new SetupViewModel(host, AddLog);

        host.Log += message => Post(() => AddLog(message));
        host.Router.RouteChanged += OnRouteChanged;
        host.Router.NamesChanged += (_, _) => QueueRefresh(layout: false);
        host.Router.LayoutChanged += (_, _) => QueueRefresh(layout: true);
        host.Finder.SourcesChanged += sources => Post(() => OnSources(sources));
        host.Tsl.StatusChanged += _ => Post(UpdateStatus);
        host.Swp08.ClientsChanged += (_, _) => Post(() =>
        {
            Swp08.Refresh();
            UpdateStatus();
        });

        Title = $"NDI Router {AppVersion.Display}";
        AddLog($"NDI Router {AppVersion.Display}");

        Refresh(layout: true);
        UpdateStatus();

        _pollTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => Poll());
        _saveTimer = new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background, (_, _) => SaveIfDirty());
        _pollTimer.Start();
        _saveTimer.Start();
    }

    /// <summary>Set by the window; until then every question is answered "cancel".</summary>
    public IDialogs Dialogs { get; set; } = new NoDialogs();

    public string Title { get; }

    /// <summary>"v1.0.0", shown beside the name in the top bar.</summary>
    public string Version { get; } = "v" + AppVersion.Display;

    public Swp08ViewModel Swp08 { get; }
    public TslViewModel Tsl { get; }
    public SetupViewModel Setup { get; }

    public ObservableCollection<TileViewModel> OutputTiles { get; } = new();
    public ObservableCollection<TileViewModel> InputTiles { get; } = new();
    public ObservableCollection<TileViewModel> PresetTiles { get; } = new();
    public ObservableCollection<InputRowViewModel> InputRows { get; } = new();
    public ObservableCollection<OutputRowViewModel> OutputRows { get; } = new();
    public ObservableCollection<RouteChoice> RouteChoices { get; } = new();

    /// <summary>NDI sources on the network, less this router's own outputs.</summary>
    public ObservableCollection<string> Sources { get; } = new();

    public ObservableCollection<string> Log { get; } = new();

    [ObservableProperty] private bool _storeArmed;
    [ObservableProperty] private string _panelHint = "Click an output, then an input to route it. Ctrl+click selects several outputs.";
    [ObservableProperty] private string _ndiStatus = string.Empty;
    [ObservableProperty] private IBrush _ndiBrush = Palette.Off;
    [ObservableProperty] private string _swp08Status = string.Empty;
    [ObservableProperty] private IBrush _swp08Brush = Palette.Off;
    [ObservableProperty] private string _tslStatus = string.Empty;
    [ObservableProperty] private IBrush _tslBrush = Palette.Off;

    public RouterCore Router => _host.Router;

    // ------------------------------------------------------------------ events in

    private static void Post(Action action) => Dispatcher.UIThread.Post(action);

    private void OnRouteChanged(object? sender, RouteChangedEventArgs e)
    {
        if (e.Changed)
        {
            var input = e.Input is int i ? $"input {i + 1} \"{Router.GetInput(i).Label}\"" : "clear";
            var line = $"Route: output {e.Output + 1} \"{Router.GetOutput(e.Output).Label}\" <- {input} ({e.Origin})";
            Post(() => AddLog(line));
        }
        QueueRefresh(layout: false);
    }

    private readonly object _refreshGate = new();

    private void QueueRefresh(bool layout)
    {
        lock (_refreshGate)
        {
            _dirty = true;
            _layoutChanged |= layout;
            if (_refreshQueued) return;
            _refreshQueued = true;
        }

        Post(() =>
        {
            bool rebuild;
            lock (_refreshGate)
            {
                _refreshQueued = false;
                rebuild = _layoutChanged;
                _layoutChanged = false;
            }
            Refresh(rebuild);
        });
    }

    private void OnSources(IReadOnlyList<string> sources)
    {
        _discovered = sources;
        _online = new HashSet<string>(sources, StringComparer.OrdinalIgnoreCase);
        Refresh(layout: false);
        UpdateStatus();
    }

    // ------------------------------------------------------------------ refresh

    /// <summary>Brings the panel and both tables up to date with the router.</summary>
    public void Refresh(bool layout)
    {
        if (_disposed) return;

        var inputs = Router.Inputs();
        var outputs = Router.Outputs();
        var presets = Router.Presets();

        Resize(OutputTiles, outputs.Count, i => new TileViewModel(TileKind.Output, i));
        Resize(InputTiles, inputs.Count, i => new TileViewModel(TileKind.Input, i));
        Resize(PresetTiles, presets.Count, i => new TileViewModel(TileKind.Preset, i));
        Resize(InputRows, inputs.Count, i => new InputRowViewModel(Router, i, Sources));
        Resize(OutputRows, outputs.Count, i => new OutputRowViewModel(Router, i, RouteChoices, AddLog));

        _selected.RemoveWhere(o => o >= outputs.Count);
        if (_selected.Count == 0 && outputs.Count > 0) _selected.Add(0);

        // Outputs
        for (var o = 0; o < outputs.Count; o++)
        {
            var output = outputs[o];
            var source = output.Input is int i && i < inputs.Count ? inputs[i].Label : "Clear";
            var watching = o < _connections.Length && _connections[o] > 0 ? $"  ·  {_connections[o]} rx" : string.Empty;
            OutputTiles[o].Update(output.Label, "◀ " + source + watching, output.Style, _selected.Contains(o),
                TileState.Normal, dimmed: false, armed: false);
        }

        // Inputs: on air to all the selected outputs (active) or to some (mixed)
        var feeding = new int[inputs.Count];
        foreach (var o in _selected)
            if (outputs[o].Input is int i && i < feeding.Length) feeding[i]++;

        for (var i = 0; i < inputs.Count; i++)
        {
            var input = inputs[i];
            var state = feeding[i] == 0 ? TileState.Normal
                : feeding[i] == _selected.Count ? TileState.Active
                : TileState.Mixed;

            string subtitle;
            bool dimmed;
            if (input.NdiSource.Length == 0)
            {
                subtitle = "unassigned";
                dimmed = true;
            }
            else
            {
                var online = _online.Contains(input.NdiSource);
                subtitle = NdiName.Channel(input.NdiSource) + (online ? string.Empty : "  (offline)");
                dimmed = !online;
            }

            InputTiles[i].Update(input.Label, subtitle, input.Style, selected: false, state, dimmed, armed: false);
        }

        // Presets
        for (var p = 0; p < presets.Count; p++)
            PresetTiles[p].Update(presets[p].Label, presets[p].IsEmpty ? "empty" : string.Empty, new ButtonStyle(),
                selected: false, TileState.Normal, dimmed: presets[p].IsEmpty, armed: StoreArmed);

        // Tables
        var choices = new List<RouteChoice> { new(null, "(clear)") };
        choices.AddRange(inputs.Select((input, i) => new RouteChoice(i, $"{i + 1}: {input.Label}")));
        if (!choices.SequenceEqual(RouteChoices))
        {
            RouteChoices.Clear();
            foreach (var choice in choices) RouteChoices.Add(choice);
        }

        for (var i = 0; i < inputs.Count; i++) InputRows[i].Sync(inputs[i], _online.Contains(inputs[i].NdiSource));
        for (var o = 0; o < outputs.Count; o++)
            OutputRows[o].Sync(outputs[o], inputs, RouteChoices, _host.Config.Tsl, o < _connections.Length ? _connections[o] : 0);

        var own = new HashSet<string>(outputs.Select(o => NdiName.OwnSourceName(o.NdiName)), StringComparer.OrdinalIgnoreCase);
        var sources = _discovered.Where(s => !own.Contains(s)).ToList();
        if (!sources.SequenceEqual(Sources))
        {
            Sources.Clear();
            foreach (var source in sources) Sources.Add(source);
        }

        if (layout) Setup.LoadValues();
    }

    private static void Resize<T>(ObservableCollection<T> items, int count, Func<int, T> create)
    {
        while (items.Count > count) items.RemoveAt(items.Count - 1);
        while (items.Count < count) items.Add(create(items.Count));
    }

    /// <summary>Once a second: how many receivers are watching each output, and the status bar.</summary>
    private void Poll()
    {
        var count = Router.OutputCount;
        var connections = new int[count];
        for (var o = 0; o < count; o++) connections[o] = Router.Connections(o);

        if (!connections.SequenceEqual(_connections))
        {
            _connections = connections;
            Refresh(layout: false);
        }

        UpdateStatus();
    }

    private void UpdateStatus()
    {
        if (_host.Ndi.IsAvailable)
        {
            NdiStatus = $"NDI {_host.Ndi.Version}  ·  {_discovered.Count} source(s)";
            NdiBrush = Palette.Ok;
        }
        else
        {
            NdiStatus = "NDI runtime not loaded";
            NdiBrush = Palette.Fault;
        }

        var clients = _host.Swp08.Clients().Count;
        if (_host.Swp08.IsListening)
        {
            Swp08Status = $"SW-P-08 port {_host.Swp08.BoundPort}  ·  {clients} controller(s)";
            Swp08Brush = clients > 0 ? Palette.Ok : Palette.Waiting;
        }
        else
        {
            Swp08Status = _host.Config.Swp08.Enabled ? "SW-P-08 not listening" : "SW-P-08 off";
            Swp08Brush = _host.Config.Swp08.Enabled ? Palette.Fault : Palette.Off;
        }

        var tsl = _host.Tsl.Status;
        TslStatus = tsl;
        TslBrush = !_host.Config.Tsl.Enabled ? Palette.Off
            : tsl.Contains("packets", StringComparison.Ordinal) ? Palette.Ok
            : Palette.Waiting;
    }

    // ------------------------------------------------------------------ panel

    /// <summary>Selects an output; with <paramref name="additive"/> (Ctrl) it toggles it in or out of the selection.</summary>
    public void SelectOutput(int index, bool additive)
    {
        if (additive)
        {
            if (!_selected.Remove(index)) _selected.Add(index);
            if (_selected.Count == 0) _selected.Add(index); // always something selected
        }
        else
        {
            _selected.Clear();
            _selected.Add(index);
        }

        Refresh(layout: false);
    }

    public IReadOnlyList<int> SelectedOutputs => _selected.Order().ToList();

    /// <summary>Routes an input to every selected output.</summary>
    [RelayCommand]
    private void RouteInput(int index) => Router.Route(_selected.Order(), index, "panel");

    [RelayCommand]
    private void ClearSelected() => Router.Route(_selected.Order(), null, "panel");

    partial void OnStoreArmedChanged(bool value)
    {
        PanelHint = value ? "Click a preset to store the current routes in it." : string.Empty;
        Refresh(layout: false);
    }

    [RelayCommand]
    private void PressPreset(int index)
    {
        var label = Router.GetPreset(index).Label;
        if (StoreArmed)
        {
            Router.StorePreset(index);
            StoreArmed = false;
            PanelHint = $"Stored the current routes in \"{label}\".";
        }
        else if (Router.RecallPreset(index))
        {
            PanelHint = $"Recalled \"{label}\".";
        }
        else
        {
            PanelHint = "That preset is empty - press Store preset, then the preset, to fill it.";
        }
    }

    // ------------------------------------------------------------------ editing from the panel

    public async Task RenameInputAsync(int index)
    {
        var label = await Dialogs.PromptAsync("Rename input", $"Label for input {index + 1}:", Router.GetInput(index).Label);
        if (label is not null) Router.UpdateInput(index, i => i.Label = label);
    }

    public async Task RenameOutputAsync(int index)
    {
        var label = await Dialogs.PromptAsync("Rename output", $"Label for output {index + 1}:", Router.GetOutput(index).Label);
        if (label is not null) Router.UpdateOutput(index, o => o.Label = label);
    }

    public async Task RenamePresetAsync(int index)
    {
        var label = await Dialogs.PromptAsync("Rename preset", $"Label for preset {index + 1}:", Router.GetPreset(index).Label);
        if (label is not null) Router.RenamePreset(index, label);
    }

    public async Task TypeSourceAsync(int index)
    {
        var name = await Dialogs.PromptAsync("NDI source", "Full NDI source name, e.g. MACHINE (Source):", Router.GetInput(index).NdiSource);
        if (name is not null) Router.AssignSource(index, name);
    }

    public void AssignSource(int index, string source) => Router.AssignSource(index, source);

    public async Task EditInputNamesAsync(int index)
    {
        var input = Router.GetInput(index);
        var names = await Dialogs.EditNamesAsync(input, $"IN {index + 1}");
        if (names is not null) Router.UpdateInput(index, i => ApplyNames(i, names, $"IN {index + 1}"));
    }

    public async Task EditOutputNamesAsync(int index)
    {
        var output = Router.GetOutput(index);
        var names = await Dialogs.EditNamesAsync(output, $"OUT {index + 1}");
        if (names is not null) Router.UpdateOutput(index, o => ApplyNames(o, names, $"OUT {index + 1}"));
    }

    private static void ApplyNames(ISwp08Named item, string[] names, string fallback)
    {
        for (var k = 0; k < Swp08Names.Lengths.Length && k < names.Length; k++)
        {
            var length = Swp08Names.Lengths[k];
            Swp08Names.Set(item, length, Swp08Names.ToStore(item, length, names[k], fallback));
        }
    }

    public async Task StyleInputAsync(int index)
    {
        var input = Router.GetInput(index);
        var style = await Dialogs.EditStyleAsync(input.Style, input.Label);
        if (style is not null) Router.UpdateInput(index, i => i.Style = style);
    }

    public async Task StyleOutputAsync(int index)
    {
        var output = Router.GetOutput(index);
        var style = await Dialogs.EditStyleAsync(output.Style, output.Label);
        if (style is not null) Router.UpdateOutput(index, o => o.Style = style);
    }

    public async Task RenameNdiOutputAsync(int index)
    {
        var output = Router.GetOutput(index);
        var name = await Dialogs.PromptAsync("NDI output name",
            $"Name output {index + 1} is published under. It appears on the network as {NdiName.OwnSourceName("name")}.",
            output.NdiName);
        if (name is not null) OutputRows[index].NdiName = name; // validated there
    }

    public async Task SetUmdAddressAsync(int index)
    {
        var text = await Dialogs.PromptAsync("UMD address", $"TSL display address for output {index + 1} (0-65534):",
            Router.GetOutput(index).UmdAddress.ToString());
        if (text is null) return;
        if (int.TryParse(text, out var address) && address is >= 0 and <= 65534)
            Router.UpdateOutput(index, o => o.UmdAddress = address);
        else
            AddLog($"\"{text}\" is not a UMD address; enter a number from 0 to 65534.");
    }

    /// <summary>Puts every input's or output's SW-P-08 names back to generated, asking first if that discards typed ones.</summary>
    [RelayCommand]
    private async Task GenerateAllNamesAsync(string which)
    {
        var inputs = which == "inputs";
        IEnumerable<ISwp08Named> items = inputs ? Router.Inputs() : Router.Outputs();
        var typed = items.Sum(item => Swp08Names.Lengths.Count(n => !Swp08Names.IsGenerated(item, n)));

        if (typed > 0 && !await Dialogs.ConfirmAsync("Generate names",
                $"This replaces {typed} typed-in SW-P-08 name(s) with names generated from the labels. Continue?"))
            return;

        if (inputs)
            for (var i = 0; i < Router.InputCount; i++) Router.UpdateInput(i, Swp08Names.Reset);
        else
            for (var o = 0; o < Router.OutputCount; o++) Router.UpdateOutput(o, Swp08Names.Reset);
    }

    // ------------------------------------------------------------------ import, save, log

    [RelayCommand]
    private async Task ImportNdiRouterAsync()
    {
        var path = await Dialogs.OpenFileAsync("Import NDI Router settings",
            Path.GetDirectoryName(NdiRouterImport.DefaultPath), Path.GetFileName(NdiRouterImport.DefaultPath));
        if (path is null) return;

        NdiRouterImport.Result imported;
        try
        {
            imported = NdiRouterImport.Read(path);
        }
        catch (Exception ex)
        {
            AddLog($"That file could not be read as NDI Router settings: {ex.Message}");
            return;
        }

        var message = $"Replace this router's layout with {imported.Inputs.Count} inputs, {imported.Outputs.Count} outputs " +
                      $"and {imported.Presets.Count} presets from NDI Router, including its current routes?";
        if (Process.GetProcessesByName("Application.NDI.Router").Length > 0)
            message += "\n\nNDI Router is running on this computer. Its outputs have the same names as the ones being imported; " +
                       "close it, or rename these outputs afterwards, so the two do not publish the same sources.";

        if (!await Dialogs.ConfirmAsync("Import", message)) return;

        _host.Import(imported);
        AddLog($"Imported {imported.Inputs.Count} inputs, {imported.Outputs.Count} outputs and {imported.Presets.Count} presets from {path}.");
        Save();
    }

    [RelayCommand]
    private void OpenSettingsFolder()
    {
        var folder = Path.GetDirectoryName(_host.ConfigPath)!;
        Directory.CreateDirectory(folder);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
    }

    [RelayCommand]
    public void Save()
    {
        try
        {
            _dirty = false;
            _host.Save();
        }
        catch (Exception ex)
        {
            AddLog($"Could not save settings: {ex.Message}");
        }
    }

    private void SaveIfDirty()
    {
        if (_dirty) Save();
    }

    public void AddLog(string message)
    {
        Log.Add($"{DateTime.Now:HH:mm:ss.fff}  {message}");
        while (Log.Count > MaxLogLines) Log.RemoveAt(0);
    }

    [RelayCommand]
    private void ClearLog() => Log.Clear();

    [RelayCommand]
    private Task CopyLogAsync() => Dialogs.CopyToClipboardAsync(string.Join(Environment.NewLine, Log));

    public void Dispose()
    {
        if (_disposed) return;
        _pollTimer.Stop();
        _saveTimer.Stop();
        Save();
        _disposed = true;
        _host.Dispose();
    }
}
