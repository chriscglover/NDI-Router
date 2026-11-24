using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NdiRouter.Core;
using NdiRouter.Umd;

namespace NdiRouter.Gui.ViewModels;

/// <summary>A choice in an output's "routed from" list: an input, or clear.</summary>
public sealed record RouteChoice(int? Input, string Text)
{
    public override string ToString() => Text;
}

/// <summary>
/// The parts of an input or output row shared by both tables: the label and the three SW-P-08
/// names. A name field holds the typed-in name, or is empty with the generated one as its
/// watermark - so generated names show grey, exactly as they are sent.
/// </summary>
public abstract partial class NamedRowViewModel : ObservableObject
{
    protected readonly RouterCore Router;
    protected bool Syncing;

    protected NamedRowViewModel(RouterCore router, int index)
    {
        Router = router;
        Index = index;
    }

    public int Index { get; }
    public string Number => (Index + 1).ToString();
    public abstract string FallbackLabel { get; }

    [ObservableProperty] private string _label = string.Empty;
    [ObservableProperty] private string _name4 = string.Empty;
    [ObservableProperty] private string _name8 = string.Empty;
    [ObservableProperty] private string _name12 = string.Empty;
    [ObservableProperty] private string _name4Generated = string.Empty;
    [ObservableProperty] private string _name8Generated = string.Empty;
    [ObservableProperty] private string _name12Generated = string.Empty;

    protected abstract void Edit(Action<ISwp08Named> edit);
    protected abstract void EditLabel(string label);

    partial void OnLabelChanged(string value)
    {
        if (!Syncing) EditLabel(value.Trim());
    }

    partial void OnName4Changed(string value) => CommitName(4, value);
    partial void OnName8Changed(string value) => CommitName(8, value);
    partial void OnName12Changed(string value) => CommitName(12, value);

    /// <summary>Typing the generated name, or clearing the field, leaves the name generated so it follows the label.</summary>
    private void CommitName(int length, string value)
    {
        if (Syncing) return;

        var stored = value;
        Edit(item =>
        {
            stored = Swp08Names.ToStore(item, length, value, FallbackLabel);
            Swp08Names.Set(item, length, stored);
        });

        // Typing the generated name changes nothing in the router, so no refresh comes back to
        // empty the field; empty it here, or it would look typed-in when it is not.
        if (stored == value) return;
        Syncing = true;
        try
        {
            switch (length)
            {
                case 4: Name4 = stored; break;
                case 8: Name8 = stored; break;
                case 12: Name12 = stored; break;
            }
        }
        finally
        {
            Syncing = false;
        }
    }

    [RelayCommand]
    private void GenerateNames() => Edit(Swp08Names.Reset);

    protected void SyncNames(ISwp08Named item)
    {
        Label = item.Label;
        Name4 = item.Name4;
        Name8 = item.Name8;
        Name12 = item.Name12;

        var source = string.IsNullOrWhiteSpace(item.Label) ? FallbackLabel : item.Label;
        Name4Generated = Swp08Names.Generate(source, 4);
        Name8Generated = Swp08Names.Generate(source, 8);
        Name12Generated = Swp08Names.Generate(source, 12);
    }
}

public sealed partial class InputRowViewModel(RouterCore router, int index, ObservableCollection<string> sources)
    : NamedRowViewModel(router, index)
{
    /// <summary>The NDI sources on the network, for the source box's suggestions.</summary>
    public ObservableCollection<string> Sources { get; } = sources;

    private static readonly IBrush OnlineBrush = new SolidColorBrush(Color.Parse("#4CAF50"));
    private static readonly IBrush OfflineBrush = new SolidColorBrush(Color.Parse("#E5534B"));
    private static readonly IBrush UnassignedBrush = new SolidColorBrush(Color.Parse("#777777"));

    public override string FallbackLabel => $"IN {Index + 1}";

    [ObservableProperty] private string _ndiSource = string.Empty;
    [ObservableProperty] private string _status = string.Empty;
    [ObservableProperty] private IBrush _statusBrush = UnassignedBrush;

    protected override void Edit(Action<ISwp08Named> edit) => Router.UpdateInput(Index, i => edit(i));
    protected override void EditLabel(string label) => Router.UpdateInput(Index, i => i.Label = label);

    partial void OnNdiSourceChanged(string value)
    {
        if (!Syncing) Router.AssignSource(Index, value ?? string.Empty);
    }

    public void Sync(RouterInput input, bool online)
    {
        Syncing = true;
        try
        {
            SyncNames(input);
            NdiSource = input.NdiSource;
            (Status, StatusBrush) = input.NdiSource.Length == 0 ? ("unassigned", UnassignedBrush)
                : online ? ("online", OnlineBrush)
                : ("offline", OfflineBrush);
        }
        finally
        {
            Syncing = false;
        }
    }
}

public sealed partial class OutputRowViewModel(RouterCore router, int index, ObservableCollection<RouteChoice> choices, Action<string> log)
    : NamedRowViewModel(router, index)
{
    /// <summary>Clear, then every input, for the "routed from" drop-down.</summary>
    public ObservableCollection<RouteChoice> Choices { get; } = choices;

    public override string FallbackLabel => $"OUT {Index + 1}";

    [ObservableProperty] private string _ndiName = string.Empty;
    [ObservableProperty] private RouteChoice? _route;
    [ObservableProperty] private int _receivers;
    [ObservableProperty] private bool _umdEnabled;
    [ObservableProperty] private decimal _umdAddress;
    [ObservableProperty] private string _umdText = string.Empty;

    /// <summary>Locked by anyone. Ticking locks it for the panel; clearing unlocks it, whoever holds it.</summary>
    [ObservableProperty] private bool _isLocked;

    /// <summary>Who holds the lock, for the tooltip.</summary>
    [ObservableProperty] private string? _lockedBy;

    protected override void Edit(Action<ISwp08Named> edit) => Router.UpdateOutput(Index, o => edit(o));
    protected override void EditLabel(string label) => Router.UpdateOutput(Index, o => o.Label = label);

    partial void OnNdiNameChanged(string value)
    {
        if (Syncing) return;

        var name = (value ?? string.Empty).Trim();
        var clash = Router.Outputs().Where((_, i) => i != Index)
            .Any(o => string.Equals(o.NdiName, name, StringComparison.OrdinalIgnoreCase));

        if (name.Length == 0 || clash)
        {
            log(name.Length == 0
                ? $"Output {Index + 1} needs a name to be published under; kept \"{Router.GetOutput(Index).NdiName}\"."
                : $"Another output is already published as \"{name}\"; output {Index + 1} kept its name.");
            Syncing = true;
            NdiName = Router.GetOutput(Index).NdiName;
            Syncing = false;
            return;
        }

        Router.UpdateOutput(Index, o => o.NdiName = name);
    }

    partial void OnRouteChanged(RouteChoice? value)
    {
        // A drop-down whose list is being rebuilt briefly reports no selection; that is not a route.
        if (Syncing || value is null) return;
        var current = Router.GetRoute(Index);
        if (current == value.Input) return;
        if (Router.Route(Index, value.Input, "outputs table", requester: OutputLock.Panel)) return;

        // Locked by someone else (the router has logged it): show the route it still has.
        Syncing = true;
        Route = current is int i && i + 1 < Choices.Count ? Choices[i + 1] : Choices[0];
        Syncing = false;
    }

    partial void OnIsLockedChanged(bool value)
    {
        if (Syncing) return;
        if (value) Router.Lock(Index, new OutputLock(OutputLock.Panel), "outputs table");
        else Router.Unlock(Index, OutputLock.Panel, "outputs table", force: true);
    }

    partial void OnUmdEnabledChanged(bool value)
    {
        if (!Syncing) Router.UpdateOutput(Index, o => o.UmdEnabled = value);
    }

    partial void OnUmdAddressChanged(decimal value)
    {
        if (!Syncing) Router.UpdateOutput(Index, o => o.UmdAddress = (int)Math.Clamp(value, 0, 65534));
    }

    public void Sync(RouterOutput output, IReadOnlyList<RouterInput> inputs, IReadOnlyList<RouteChoice> choices,
        TslSettings tsl, int receivers)
    {
        Syncing = true;
        try
        {
            SyncNames(output);
            NdiName = output.NdiName;

            Route = output.Input is int i && i + 1 < choices.Count ? choices[i + 1] : choices[0];
            OnPropertyChanged(nameof(Route)); // the list may have been rebuilt around an equal choice

            Receivers = receivers;
            IsLocked = output.Lock is not null;
            LockedBy = output.Lock is { } held ? $"Locked by {held}" : "Not locked - tick to lock it from the panel";
            UmdEnabled = output.UmdEnabled;
            UmdAddress = output.UmdAddress;
            UmdText = output.UmdEnabled ? UmdEngine.Build(Index, output, inputs, tsl).Message.Text : string.Empty;
        }
        finally
        {
            Syncing = false;
        }
    }

    public void SetReceivers(int receivers) => Receivers = receivers;
}
