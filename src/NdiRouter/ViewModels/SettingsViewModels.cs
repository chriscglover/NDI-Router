using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NdiRouter.Config;
using NdiRouter.Core;
using NdiRouter.Swp08;
using NdiRouter.Tsl;
using NdiRouter.Umd;

namespace NdiRouter.Gui.ViewModels;

/// <summary>SW-P-08 settings, shown 1-based as controllers number things, and the connected controllers.</summary>
public sealed partial class Swp08ViewModel : ObservableObject
{
    private readonly RouterHost _host;

    public Swp08ViewModel(RouterHost host)
    {
        _host = host;
        Load(host.Config.Swp08);
    }

    [ObservableProperty] private bool _enabled;
    [ObservableProperty] private string _bindAddress = "0.0.0.0";
    [ObservableProperty] private decimal _port;
    [ObservableProperty] private decimal _matrix;
    [ObservableProperty] private decimal _level;
    [ObservableProperty] private bool _anyLevel;
    [ObservableProperty] private decimal _clearSource;
    [ObservableProperty] private bool _sendNamesUpdated;
    [ObservableProperty] private bool _logTraffic;
    [ObservableProperty] private decimal _ackTimeoutMs;
    [ObservableProperty] private decimal _sendAttempts;
    [ObservableProperty] private string? _error;
    [ObservableProperty] private string _state = string.Empty;
    [ObservableProperty] private bool _hasClients;

    public ObservableCollection<string> Clients { get; } = new();

    private void Load(Swp08Settings s)
    {
        Enabled = s.Enabled;
        BindAddress = s.BindAddress;
        Port = s.Port;
        Matrix = s.Matrix + 1;
        Level = s.Level + 1;
        AnyLevel = s.AnyLevel;
        ClearSource = s.ClearSource + 1;
        SendNamesUpdated = s.SendNamesUpdated;
        LogTraffic = s.LogTraffic;
        AckTimeoutMs = s.AckTimeoutMs;
        SendAttempts = s.SendAttempts;
    }

    [RelayCommand]
    private void Apply()
    {
        var clearSource = (int)ClearSource - 1;
        if (clearSource < _host.Router.InputCount)
        {
            Error = $"The clear source must be above the last input ({_host.Router.InputCount}), or that input could not be routed.";
            return;
        }

        Error = null;
        _host.ApplySwp08(new Swp08Settings
        {
            Enabled = Enabled,
            BindAddress = string.IsNullOrWhiteSpace(BindAddress) ? "0.0.0.0" : BindAddress.Trim(),
            Port = (int)Port,
            Matrix = (int)Matrix - 1,
            Level = (int)Level - 1,
            AnyLevel = AnyLevel,
            ClearSource = clearSource,
            SendNamesUpdated = SendNamesUpdated,
            LogTraffic = LogTraffic,
            AckTimeoutMs = (int)AckTimeoutMs,
            SendAttempts = (int)SendAttempts
        });
        Refresh();
    }

    public void Refresh()
    {
        var clients = _host.Swp08.Clients();
        Clients.Clear();
        foreach (var client in clients) Clients.Add(client);
        HasClients = clients.Count > 0;

        State = _host.Swp08.IsListening
            ? $"Listening on port {_host.Swp08.BoundPort} - {clients.Count} controller(s) connected."
            : _host.Config.Swp08.Enabled ? "Not listening - see the log." : "Off.";
    }
}

/// <summary>TSL UMD output settings.</summary>
public sealed partial class TslViewModel : ObservableObject
{
    private readonly RouterHost _host;
    private readonly Action _applied;

    public TslViewModel(RouterHost host, Action applied)
    {
        _host = host;
        _applied = applied;
        Load(host.Config.Tsl);
    }

    public IReadOnlyList<string> Versions { get; } = new[] { "TSL 3.1", "TSL 4.0", "TSL 5.0" };
    public IReadOnlyList<string> FitModes { get; } = new[] { "Truncate", "Keep the end", "Squeeze (drop vowels)" };
    public IReadOnlyList<TallyColour> Lamps { get; } = Enum.GetValues<TallyColour>();
    public string TokenHelp { get; } = "Tokens: " + string.Join("  ", UmdLabel.Tokens) +
                                       ". Alternatives: {input.name12|input.label}. Literal fallback: {input.label|\"NO SOURCE\"}.";

    [ObservableProperty] private bool _enabled;
    [ObservableProperty] private string _destination = string.Empty;
    [ObservableProperty] private decimal _port;
    [ObservableProperty] private bool _useTcp;
    [ObservableProperty] private bool _streamFraming;
    [ObservableProperty] private int _versionIndex;
    [ObservableProperty] private decimal _screen;
    [ObservableProperty] private bool _allScreens;
    [ObservableProperty] private bool _unicode;
    [ObservableProperty] private decimal _repeatMs;
    [ObservableProperty] private string _routedTemplate = string.Empty;
    [ObservableProperty] private string _clearedTemplate = string.Empty;
    [ObservableProperty] private int _fitIndex;
    [ObservableProperty] private bool _uppercase;
    [ObservableProperty] private TallyColour _routedLamp;
    [ObservableProperty] private TallyColour _clearedLamp;
    [ObservableProperty] private bool _textTally;
    [ObservableProperty] private decimal _brightness;

    public bool UseUdp
    {
        get => !UseTcp;
        set => UseTcp = !value;
    }

    partial void OnUseTcpChanged(bool value) => OnPropertyChanged(nameof(UseUdp));

    private void Load(TslSettings s)
    {
        Enabled = s.Enabled;
        Destination = s.Host;
        Port = s.Port;
        UseTcp = s.UseTcp;
        StreamFraming = s.StreamFraming;
        VersionIndex = (int)s.Version;
        Screen = s.Screen;
        AllScreens = s.ScreenBroadcast;
        Unicode = s.Unicode;
        RepeatMs = s.RepeatIntervalMs;
        RoutedTemplate = s.RoutedTemplate;
        ClearedTemplate = s.ClearedTemplate;
        FitIndex = (int)s.FitMode;
        Uppercase = s.Uppercase;
        RoutedLamp = s.RoutedLamp;
        ClearedLamp = s.ClearedLamp;
        TextTally = s.DriveTextTally;
        Brightness = s.Brightness;
    }

    [RelayCommand]
    private void Apply()
    {
        _host.ApplyTsl(new TslSettings
        {
            Enabled = Enabled,
            Host = Destination.Trim(),
            Port = (int)Port,
            UseTcp = UseTcp,
            StreamFraming = StreamFraming,
            Version = (TslVersion)Math.Clamp(VersionIndex, 0, 2),
            Screen = (int)Screen,
            ScreenBroadcast = AllScreens,
            Unicode = Unicode,
            RepeatIntervalMs = (int)RepeatMs,
            RoutedTemplate = RoutedTemplate,
            ClearedTemplate = ClearedTemplate,
            FitMode = (FitMode)Math.Clamp(FitIndex, 0, 2),
            Uppercase = Uppercase,
            RoutedLamp = RoutedLamp,
            ClearedLamp = ClearedLamp,
            DriveTextTally = TextTally,
            Brightness = (int)Brightness
        });
        _applied();
    }
}

/// <summary>Router size and NDI discovery and publishing.</summary>
public sealed partial class SetupViewModel : ObservableObject
{
    private readonly RouterHost _host;
    private readonly Action<string> _log;

    public SetupViewModel(RouterHost host, Action<string> log)
    {
        _host = host;
        _log = log;
        LoadValues();
    }

    public decimal MaxInputs => RouterCore.MaxInputs;
    public decimal MaxOutputs => RouterCore.MaxOutputs;
    public decimal MaxPresets => RouterCore.MaxPresets;
    public string NdiStatus => _host.Ndi.Status;
    public string ConfigPath => _host.ConfigPath;

    [ObservableProperty] private decimal _inputs;
    [ObservableProperty] private decimal _outputs;
    [ObservableProperty] private decimal _presets;
    [ObservableProperty] private bool _showLocalSources;
    [ObservableProperty] private string _findGroups = string.Empty;
    [ObservableProperty] private string _extraIps = string.Empty;
    [ObservableProperty] private string _outputGroups = string.Empty;
    [ObservableProperty] private string? _error;

    public void LoadValues()
    {
        Inputs = _host.Router.InputCount;
        Outputs = _host.Router.OutputCount;
        Presets = _host.Router.PresetCount;

        var ndi = _host.Config.Ndi;
        ShowLocalSources = ndi.ShowLocalSources;
        FindGroups = ndi.FindGroups;
        ExtraIps = ndi.ExtraIps;
        OutputGroups = ndi.OutputGroups;
    }

    [RelayCommand]
    private void ApplySize()
    {
        var inputs = (int)Inputs;
        if (_host.Config.Swp08.ClearSource < inputs)
        {
            Error = $"SW-P-08's clear source ({_host.Config.Swp08.ClearSource + 1}) would become an input. Raise it on the SW-P-08 tab first.";
            return;
        }

        Error = null;
        _host.Router.Resize(inputs, (int)Outputs, (int)Presets);
        _log($"Router resized to {inputs} inputs, {(int)Outputs} outputs and {(int)Presets} presets.");
    }

    [RelayCommand]
    private void ApplyNdi() => _host.ApplyNdi(new NdiSettings
    {
        ShowLocalSources = ShowLocalSources,
        FindGroups = FindGroups.Trim(),
        ExtraIps = ExtraIps.Trim(),
        OutputGroups = OutputGroups.Trim()
    });
}
