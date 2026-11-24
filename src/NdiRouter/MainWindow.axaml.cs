using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using NdiRouter.Gui.ViewModels;
using NdiRouter.Gui.Views;
using NdiRouter.Ndi;

namespace NdiRouter.Gui;

public partial class MainWindow : Window
{
    private MainViewModel? _viewModel;

    public MainWindow()
    {
        InitializeComponent();

        foreach (var list in new[] { OutputTiles, InputTiles, PresetTiles })
        {
            list.Tapped += OnTileTapped;
            list.AddHandler(ContextRequestedEvent, OnTileContextRequested, RoutingStrategies.Bubble);
        }

        OutputTiles.DoubleTapped += OnOutputDoubleTapped;
        InputRowsList.AddHandler(ContextRequestedEvent, OnInputRowContextRequested, RoutingStrategies.Bubble);

        // Tunnel, so the shortcuts win over whichever panel button has focus (Enter would click it)
        AddHandler(KeyDownEvent, OnPanelKeyDown, RoutingStrategies.Tunnel);
    }

    /// <summary>
    /// Router tab shortcuts: Enter takes, Ctrl+Z undoes, Escape un-picks the source. Nowhere else,
    /// and never while typing, so Enter and Ctrl+Z keep their usual meaning in the tables.
    /// </summary>
    private void OnPanelKeyDown(object? sender, KeyEventArgs e)
    {
        if (_viewModel is null || !RouterTab.IsSelected) return;
        if (e.Source is TextBox or AutoCompleteBox or NumericUpDown or ComboBox) return;

        switch (e.Key, e.KeyModifiers)
        {
            case (Key.Enter, KeyModifiers.None):
                if (_viewModel.TakeCommand.CanExecute(null)) _viewModel.TakeCommand.Execute(null);
                e.Handled = true;
                break;
            case (Key.Z, KeyModifiers.Control):
                if (_viewModel.UndoCommand.CanExecute(null)) _viewModel.UndoCommand.Execute(null);
                e.Handled = true;
                break;
            case (Key.Escape, KeyModifiers.None):
                _viewModel.CancelPick();
                e.Handled = true;
                break;
        }
    }

    /// <summary>The second click of a double-click: take the picked source to this destination.</summary>
    private void OnOutputDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (_viewModel is null || TileFrom(e) is not { Kind: TileKind.Output } tile) return;
        _viewModel.TakeTo(tile.Index);
        e.Handled = true;
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (_viewModel is not null)
        {
            _viewModel.Log.CollectionChanged -= OnLogChanged;
            _viewModel.Preview.PictureUpdated -= OnPictureUpdated;
        }

        _viewModel = DataContext as MainViewModel;
        if (_viewModel is null) return;

        _viewModel.Dialogs = new WindowDialogs(this);
        _viewModel.Log.CollectionChanged += OnLogChanged;
        _viewModel.Preview.PictureUpdated += OnPictureUpdated;
    }

    /// <summary>The bitmap is reused frame to frame, so the image has to be told its pixels changed.</summary>
    private void OnPictureUpdated() => PreviewImage.InvalidateVisual();

    /// <summary>Keeps the newest log line in view.</summary>
    private void OnLogChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add && _viewModel?.Log.Count > 0)
            LogList.ScrollIntoView(_viewModel.Log.Count - 1);
    }

    private static TileViewModel? TileFrom(RoutedEventArgs e) => (e.Source as StyledElement)?.DataContext as TileViewModel;

    private void OnTileTapped(object? sender, TappedEventArgs e)
    {
        if (_viewModel is null || TileFrom(e) is not { } tile) return;

        switch (tile.Kind)
        {
            case TileKind.Output:
                _viewModel.SelectOutput(tile.Index, additive: e.KeyModifiers.HasFlag(KeyModifiers.Control));
                break;
            case TileKind.Input:
                _viewModel.SelectInput(tile.Index);
                break;
            case TileKind.Preset:
                _viewModel.PressPresetCommand.Execute(tile.Index);
                break;
        }

        e.Handled = true;
    }

    private void OnTileContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (_viewModel is null || TileFrom(e) is not { } tile || e.Source is not Control target) return;

        var menu = new ContextMenu
        {
            ItemsSource = tile.Kind switch
            {
                TileKind.Output => OutputMenu(_viewModel, tile.Index),
                TileKind.Input => InputMenu(_viewModel, tile.Index),
                _ => PresetMenu(_viewModel, tile.Index)
            }
        };
        menu.Open(target);
        e.Handled = true;
    }

    private static MenuItem Item(string header, Action action, bool enabled = true)
    {
        var item = new MenuItem { Header = header, IsEnabled = enabled };
        item.Click += (_, _) => action();
        return item;
    }

    private static List<Control> OutputMenu(MainViewModel vm, int index) => new()
    {
        Item("Rename...", () => _ = vm.RenameOutputAsync(index)),
        Item("SW-P-08 names...", () => _ = vm.EditOutputNamesAsync(index)),
        Item("Generate SW-P-08 names from label", () => vm.OutputRows[index].GenerateNamesCommand.Execute(null)),
        Item("NDI output name...", () => _ = vm.RenameNdiOutputAsync(index)),
        Item("UMD address...", () => _ = vm.SetUmdAddressAsync(index)),
        Item("Style...", () => _ = vm.StyleOutputAsync(index)),
        new Separator(),
        LockItem(vm, index),
        Item("Clear route", () => vm.ClearOutput(index))
    };

    /// <summary>"Lock", or "Unlock (locked by ...)" - the panel can unlock anyone's lock.</summary>
    private static MenuItem LockItem(MainViewModel vm, int index) =>
        vm.Router.GetLock(index) is { } held
            ? Item($"Unlock (locked by {held})", () => vm.UnlockOutput(index))
            : Item("Lock", () => vm.LockOutput(index));

    /// <summary>Right-click on an Inputs table row: pick a discovered source, or the row's other actions.</summary>
    private void OnInputRowContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (_viewModel is null || (e.Source as StyledElement)?.DataContext is not InputRowViewModel row
            || e.Source is not Control target) return;

        var vm = _viewModel;
        var index = row.Index;
        var current = vm.Router.GetInput(index).NdiSource;
        var items = new List<Control>();

        // In the source box: its usual Cut/Copy/Paste first, then the sources to pick from
        if (target.FindAncestorOfType<TextBox>(includeSelf: true) is { } text)
        {
            items.Add(Item("Cut", text.Cut, enabled: text.CanCut));
            items.Add(Item("Copy", text.Copy, enabled: text.CanCopy));
            items.Add(Item("Paste", text.Paste, enabled: text.CanPaste));
            items.Add(new Separator());
        }

        items.Add(AssignSourceMenu(vm, index, current));
        items.Add(Item("Unassign source", () => vm.AssignSource(index, string.Empty), enabled: current.Length > 0));
        items.Add(new Separator());
        items.Add(Item("Generate SW-P-08 names from label", () => row.GenerateNamesCommand.Execute(null)));

        new ContextMenu { ItemsSource = items }.Open(target);
        e.Handled = true;
    }

    private static List<Control> InputMenu(MainViewModel vm, int index)
    {
        var current = vm.Router.GetInput(index).NdiSource;

        return new List<Control>
        {
            AssignSourceMenu(vm, index, current),
            Item("Type a source name...", () => _ = vm.TypeSourceAsync(index)),
            Item("Unassign source", () => vm.AssignSource(index, string.Empty), enabled: current.Length > 0),
            new Separator(),
            Item("Rename...", () => _ = vm.RenameInputAsync(index)),
            Item("SW-P-08 names...", () => _ = vm.EditInputNamesAsync(index)),
            Item("Generate SW-P-08 names from label", () => vm.InputRows[index].GenerateNamesCommand.Execute(null)),
            Item("Style...", () => _ = vm.StyleInputAsync(index))
        };
    }

    /// <summary>"Assign NDI source": the discovered sources, grouped by the machine they are on, current one ticked.</summary>
    private static MenuItem AssignSourceMenu(MainViewModel vm, int index, string current)
    {
        var assign = new MenuItem { Header = "Assign NDI source" };
        var machines = vm.Sources.GroupBy(NdiName.Machine, StringComparer.OrdinalIgnoreCase).ToList();
        if (machines.Count == 0)
        {
            assign.ItemsSource = new[] { new MenuItem { Header = "(no NDI sources found)", IsEnabled = false } };
        }
        else
        {
            assign.ItemsSource = machines.Select(machine => new MenuItem
            {
                Header = machine.Key,
                ItemsSource = machine.Select(source =>
                {
                    var item = Item(NdiName.Channel(source), () => vm.AssignSource(index, source));
                    if (string.Equals(source, current, StringComparison.OrdinalIgnoreCase))
                    {
                        item.ToggleType = MenuItemToggleType.CheckBox;
                        item.IsChecked = true;
                    }
                    return item;
                }).ToList()
            }).ToList();
        }

        return assign;
    }

    private static List<Control> PresetMenu(MainViewModel vm, int index)
    {
        var preset = vm.Router.GetPreset(index);
        return new List<Control>
        {
            Item("Store current routes", () => vm.Router.StorePreset(index)),
            Item("Rename...", () => _ = vm.RenamePresetAsync(index)),
            Item("Empty this preset", () => vm.Router.ClearPreset(index), enabled: !preset.IsEmpty)
        };
    }
}
