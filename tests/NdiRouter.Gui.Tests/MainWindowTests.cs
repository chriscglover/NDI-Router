using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NdiRouter.Config;
using NdiRouter.Core;
using NdiRouter.Gui;
using NdiRouter.Gui.ViewModels;
using NdiRouter.Ndi;

[assembly: AvaloniaTestApplication(typeof(NdiRouter.Gui.Tests.TestAppBuilder))]

namespace NdiRouter.Gui.Tests;

public static class TestAppBuilder
{
    // Real Skia rendering, so screenshots can be captured
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

/// <summary>Answers every dialog with what the test asks for.</summary>
internal sealed class TestDialogs : IDialogs
{
    public bool Confirm { get; set; } = true;
    public Task<string?> PromptAsync(string title, string message, string value) => Task.FromResult<string?>(value);
    public Task<bool> ConfirmAsync(string title, string message) => Task.FromResult(Confirm);
    public Task<ButtonStyle?> EditStyleAsync(ButtonStyle style, string title) => Task.FromResult<ButtonStyle?>(style);
    public Task<string[]?> EditNamesAsync(ISwp08Named item, string fallbackLabel) => Task.FromResult<string[]?>(null);
    public Task<string?> OpenFileAsync(string title, string? suggestedFolder, string? suggestedName) => Task.FromResult<string?>(null);
    public Task CopyToClipboardAsync(string text) => Task.CompletedTask;
}

/// <summary>Stands in for the NDI preview receiver: records what it is asked to watch and hands out pictures the test pushes.</summary>
internal sealed class FakePreview : INdiPreview
{
    private byte[] _pixels = Array.Empty<byte>();
    private int _width, _height;
    private bool _fresh;

    public string Source { get; private set; } = string.Empty;
    public long FramesReceived { get; private set; }
    public DateTime LastFrameUtc { get; private set; } = DateTime.MinValue;
    public event Action? FrameArrived;

    public void Show(string? ndiSourceName) => Source = ndiSourceName ?? string.Empty;

    /// <summary>Delivers a frame of 75% colour bars, as a camera's test signal would.</summary>
    public void PushBars(int width, int height)
    {
        (byte B, byte G, byte R)[] bars =
        {
            (191, 191, 191), (0, 191, 191), (191, 191, 0), (0, 191, 0), (191, 0, 191), (0, 0, 191), (191, 0, 0), (16, 16, 16)
        };
        _pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var bar = bars[x * bars.Length / width];
            var at = (y * width + x) * 4;
            (_pixels[at], _pixels[at + 1], _pixels[at + 2], _pixels[at + 3]) = (bar.B, bar.G, bar.R, 255);
        }

        (_width, _height, _fresh) = (width, height, true);
        FramesReceived++;
        LastFrameUtc = DateTime.UtcNow;
        FrameArrived?.Invoke();
    }

    public bool ReadLatest(PreviewFrameReader reader)
    {
        if (!_fresh) return false;
        _fresh = false;
        reader(_pixels, _width, _height);
        return true;
    }

    public void Dispose() { }
}

public sealed class MainWindowTests : IDisposable
{
    private readonly string _configPath = Path.Combine(Path.GetTempPath(), $"NdiRouterGui{Guid.NewGuid():N}.json");

    public void Dispose() => File.Delete(_configPath);

    /// <summary>A router like a small studio's, with no NDI on the network and settings in a temporary file.</summary>
    private (MainViewModel Vm, MainWindow Window) Open(INdiPreview? preview = null)
    {
        var config = AppConfig.CreateDefault();
        string[] labels = { "Camera 1", "Camera 2", "Graphics", "Playout A", "Studio Wide", "Full Screen Cam", "", "" };
        for (var i = 0; i < labels.Length; i++)
        {
            config.Inputs[i].Label = labels[i].Length > 0 ? labels[i] : config.Inputs[i].Label;
            if (labels[i].Length > 0) config.Inputs[i].NdiSource = $"STUDIO-PC ({labels[i]})";
        }
        config.Inputs[2].Style = new ButtonStyle { BackColor = "#1565C0", Bold = true };
        config.Outputs[0].Label = "Multiviewer 1";
        config.Outputs[1].Label = "Studio Monitor";
        config.Outputs[0].Input = 0;
        config.Outputs[1].Input = 2;
        config.Outputs[2].Input = 0;
        config.Presets[0].Label = "Show open";
        config.Presets[0].Routes = new List<int?> { 0, 1, 2, null };

        var host = new RouterHost(config, NdiRuntime.Disabled("tests"), _configPath);
        var vm = new MainViewModel(host, preview);
        var window = new MainWindow { DataContext = vm, Width = 1380, Height = 900 };
        vm.Dialogs = new TestDialogs();
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (vm, window);
    }

    private static void Close(MainViewModel vm, Window window)
    {
        window.Close();
        vm.Dispose();
    }

    private static void SelectTab(Window window, string header)
    {
        var tabs = window.GetVisualDescendants().OfType<TabControl>().Single();
        tabs.SelectedItem = tabs.Items.Cast<TabItem>().Single(t => (string)t.Header! == header);
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void Title_shows_the_version()
    {
        var (vm, window) = Open();
        Assert.Equal($"NDI Router {AppVersion.Display}", window.Title);
        Assert.Equal($"v{AppVersion.Display}", vm.Version);
        Assert.NotNull(window.Icon);
        Assert.Matches(@"^\d+\.\d+\.\d+$", AppVersion.Display); // no "+commit" suffix
        Close(vm, window);
    }

    [AvaloniaFact]
    public void A_picked_source_is_only_routed_when_take_is_pressed()
    {
        var (vm, window) = Open();

        vm.SelectOutput(1, additive: false);
        vm.SelectInput(4);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(2, vm.Router.GetRoute(1)); // still Graphics
        Assert.Equal(4, vm.Preselected);
        Assert.Equal(Palette.Preselected, ((ISolidColorBrush)vm.InputTiles[4].BorderBrush).Color);
        Assert.Equal("Studio Wide  ▶  Studio Monitor", vm.NextRoute);
        Assert.True(vm.TakeCommand.CanExecute(null));

        vm.TakeCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(4, vm.Router.GetRoute(1));
        Assert.Null(vm.Preselected);
        Assert.False(vm.TakeCommand.CanExecute(null));
        Assert.Equal(Palette.Active, ((ISolidColorBrush)vm.InputTiles[4].Background).Color);
        Assert.Equal("◀ Studio Wide", vm.OutputTiles[1].Subtitle);
        Close(vm, window);
    }

    /// <summary>A real double-click on a panel button, through the window's input handling.</summary>
    private static void DoubleClick(Window window, ItemsControl tiles, int index)
    {
        var point = tiles.ContainerFromIndex(index)!.TranslatePoint(new Point(30, 30), window)!.Value;
        for (var click = 0; click < 2; click++)
        {
            window.MouseDown(point, MouseButton.Left);
            window.MouseUp(point, MouseButton.Left);
        }
        Dispatcher.UIThread.RunJobs();
    }

    private static void Press(Window window, Key key, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        window.KeyPress(key, modifiers, PhysicalKey.None, null);
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void Double_clicking_a_destination_takes_the_picked_source_to_it()
    {
        var (vm, window) = Open();

        vm.SelectInput(4);
        DoubleClick(window, window.OutputTiles, 3);
        Assert.Equal(4, vm.Router.GetRoute(3));
        Assert.Equal(new[] { 3 }, vm.SelectedOutputs);
        Assert.Null(vm.Preselected);
        Assert.True(vm.UndoCommand.CanExecute(null));

        // With nothing picked, a double-click only selects
        DoubleClick(window, window.OutputTiles, 1);
        Assert.Equal(2, vm.Router.GetRoute(1));
        Assert.Equal(new[] { 1 }, vm.SelectedOutputs);
        Assert.StartsWith("Pick a source first", vm.PanelHint);
        Close(vm, window);
    }

    [AvaloniaFact]
    public void Enter_takes_ctrl_z_undoes_and_escape_unpicks_on_the_router_tab_only()
    {
        var (vm, window) = Open();
        vm.SelectOutput(1, additive: false);
        vm.SelectInput(4);

        Press(window, Key.Enter);
        Assert.Equal(4, vm.Router.GetRoute(1));

        Press(window, Key.Z, RawInputModifiers.Control);
        Assert.Equal(2, vm.Router.GetRoute(1));

        vm.SelectInput(3);
        Press(window, Key.Escape);
        Assert.Null(vm.Preselected);

        vm.SelectInput(3);
        SelectTab(window, "Inputs");
        Press(window, Key.Enter);
        Assert.Equal(2, vm.Router.GetRoute(1)); // not taken from another tab
        Assert.Equal(3, vm.Preselected);
        Close(vm, window);
    }

    [AvaloniaFact]
    public void Clicking_the_picked_source_again_unpicks_it()
    {
        var (vm, window) = Open();
        vm.SelectInput(3);
        vm.SelectInput(3);
        Assert.Null(vm.Preselected);
        Assert.False(vm.TakeCommand.CanExecute(null));
        Close(vm, window);
    }

    [AvaloniaFact]
    public void Undo_puts_back_what_takes_clears_and_preset_recalls_changed()
    {
        var (vm, window) = Open();
        Assert.False(vm.UndoCommand.CanExecute(null));

        vm.SelectOutput(0, additive: false); // on Camera 1
        vm.SelectOutput(1, additive: true);  // on Graphics
        vm.SelectInput(3);
        vm.TakeCommand.Execute(null);
        Assert.Equal(new int?[] { 3, 3 }, vm.Router.Routes()[..2]);
        Assert.Equal("Undo (Ctrl+Z): Playout A to Multiviewer 1, Studio Monitor", vm.UndoDescription);

        vm.ClearSelectedCommand.Execute(null);
        Assert.Equal(new int?[] { null, null }, vm.Router.Routes()[..2]);

        vm.PressPresetCommand.Execute(0); // 0, 1, 2, null
        Assert.Equal(new int?[] { 0, 1, 2, null }, vm.Router.Routes()[..4]);

        vm.UndoCommand.Execute(null);     // the recall
        Assert.Equal(new int?[] { null, null, 0, null }, vm.Router.Routes()[..4]);
        vm.UndoCommand.Execute(null);     // the clear
        Assert.Equal(new int?[] { 3, 3 }, vm.Router.Routes()[..2]);
        vm.UndoCommand.Execute(null);     // the take
        Assert.Equal(new int?[] { 0, 2, 0 }, vm.Router.Routes()[..3]);

        Assert.False(vm.UndoCommand.CanExecute(null));
        Assert.Equal("Nothing to undo", vm.UndoDescription);
        Close(vm, window);
    }

    [AvaloniaFact]
    public void A_take_that_changes_nothing_leaves_nothing_to_undo()
    {
        var (vm, window) = Open();
        vm.SelectOutput(0, additive: false); // already on Camera 1
        vm.SelectInput(0);
        vm.TakeCommand.Execute(null);
        Assert.False(vm.UndoCommand.CanExecute(null));
        Close(vm, window);
    }

    [AvaloniaFact]
    public void The_preview_shows_a_clicked_source_or_what_a_clicked_destination_carries()
    {
        var preview = new FakePreview();
        var (vm, window) = Open(preview);
        Assert.Equal(string.Empty, preview.Source); // nothing until something is clicked

        vm.SelectOutput(1, additive: false);
        Assert.Equal("STUDIO-PC (Graphics)", preview.Source);
        Assert.Equal("OUT 2  Studio Monitor  ◀  IN 3  Graphics", vm.Preview.Caption);
        Assert.True(vm.OutputTiles[1].IsPreviewed);

        // Follows the destination when it is re-routed from anywhere, e.g. a SW-P-08 controller
        vm.Router.Route(1, 0, "SW-P-08");
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("STUDIO-PC (Camera 1)", preview.Source);

        vm.SelectInput(4);
        Assert.Equal("STUDIO-PC (Studio Wide)", preview.Source);
        Assert.Equal("IN 5  Studio Wide", vm.Preview.Caption);
        Assert.True(vm.InputTiles[4].IsPreviewed);
        Assert.False(vm.OutputTiles[1].IsPreviewed);

        preview.PushBars(640, 360);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(new PixelSize(640, 360), vm.Preview.Picture!.PixelSize);

        vm.SelectInput(6); // no NDI source
        Assert.Equal(string.Empty, preview.Source);
        Assert.Null(vm.Preview.Picture);
        Assert.Contains("No NDI source", vm.Preview.Message);

        vm.SelectOutput(3, additive: false); // routed nowhere
        Assert.Equal("Nothing routed to this destination", vm.Preview.Message);
        Close(vm, window);
    }

    [AvaloniaFact]
    public void Without_an_ndi_runtime_the_monitor_says_so()
    {
        var (vm, window) = Open();
        vm.SelectInput(0);
        Assert.False(vm.Preview.IsAvailable);
        Assert.Contains("NDI runtime is not loaded", vm.Preview.Message);
        Close(vm, window);
    }

    [AvaloniaFact]
    public void An_input_feeding_only_some_selected_outputs_is_shown_as_mixed()
    {
        var (vm, window) = Open();

        vm.SelectOutput(0, additive: false); // camera 1
        vm.SelectOutput(1, additive: true);  // graphics
        Dispatcher.UIThread.RunJobs();

        Assert.True(vm.InputTiles[0].IsMixed);
        Assert.True(vm.InputTiles[2].IsMixed);

        vm.SelectInput(3);                   // both to Playout A
        vm.TakeCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(new int?[] { 3, 3 }, vm.Router.Routes()[..2]);
        Assert.False(vm.InputTiles[3].IsMixed);
        Close(vm, window);
    }

    [AvaloniaFact]
    public void Presets_store_and_recall()
    {
        var (vm, window) = Open();

        vm.PressPresetCommand.Execute(0);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(new int?[] { 0, 1, 2, null }, vm.Router.Routes());

        vm.StoreArmed = true;
        vm.Router.Route(3, 5, "test");
        vm.PressPresetCommand.Execute(1);
        Assert.False(vm.StoreArmed);
        Assert.False(vm.Router.GetPreset(1).IsEmpty);
        Close(vm, window);
    }

    [AvaloniaFact]
    public void Typed_text_is_committed_when_the_field_is_left_not_on_every_keystroke()
    {
        var (vm, window) = Open();
        SelectTab(window, "Inputs");

        var source = window.GetVisualDescendants().OfType<AutoCompleteBox>().First();
        var label = window.GetVisualDescendants().OfType<TextBox>().First(t => t.Text == "Camera 1");

        source.Focus();
        source.Text = string.Empty;
        window.KeyTextInput("OTHER (Feed)");
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("STUDIO-PC (Camera 1)", vm.Router.GetInput(0).NdiSource); // not re-routed mid-typing

        label.Focus();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("OTHER (Feed)", vm.Router.GetInput(0).NdiSource);
        Close(vm, window);
    }

    [AvaloniaFact]
    public void A_name_typed_as_the_generated_one_stays_generated()
    {
        var (vm, window) = Open();
        var row = vm.InputRows[0];

        Assert.Equal("Cmr1", row.Name4Generated); // "Camera 1"
        row.Name4 = "Cmr1";
        Dispatcher.UIThread.RunJobs();
        Assert.True(Swp08Names.IsGenerated(vm.Router.GetInput(0), 4));
        Assert.Equal(string.Empty, row.Name4);

        row.Name4 = "CAM";
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("CAM", vm.Router.GetInput(0).Name4);
        Close(vm, window);
    }

    [AvaloniaFact]
    public void The_outputs_table_routes()
    {
        var (vm, window) = Open();
        var row = vm.OutputRows[3];

        row.Route = vm.RouteChoices[2]; // input 2
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(1, vm.Router.GetRoute(3));
        Assert.Equal("Camera 2", vm.OutputRows[3].UmdText);
        Close(vm, window);
    }

    [AvaloniaFact]
    public void Clashing_ndi_output_names_are_refused()
    {
        var (vm, window) = Open();
        var first = vm.Router.GetOutput(0).NdiName;

        vm.OutputRows[1].NdiName = first;
        Dispatcher.UIThread.RunJobs();

        Assert.NotEqual(first, vm.Router.GetOutput(1).NdiName);
        Assert.Contains(vm.Log, line => line.Contains("already published"));
        Close(vm, window);
    }

    /// <summary>
    /// Renders every tab, saving screenshots when NDIROUTER_SCREENSHOTS names a folder, for
    /// checking the layout by eye. Otherwise just checks that each tab renders.
    /// </summary>
    [AvaloniaFact]
    public void Renders_every_tab()
    {
        var folder = Environment.GetEnvironmentVariable("NDIROUTER_SCREENSHOTS");
        var preview = new FakePreview();
        var (vm, window) = Open(preview);
        vm.SelectOutput(0, additive: false);
        vm.SelectOutput(2, additive: true);
        vm.SelectInput(1); // picked for the next take, and on the monitor
        preview.PushBars(640, 360);
        vm.Preview.Tick();
        vm.AddLog("Route: output 1 \"Multiviewer 1\" <- input 1 \"Camera 1\" (panel)");
        Dispatcher.UIThread.RunJobs();

        foreach (var tab in new[] { "Router", "Inputs", "Outputs", "SW-P-08", "TSL UMD", "Setup" })
        {
            SelectTab(window, tab);
            var frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame);
            if (folder is null) continue;
            Directory.CreateDirectory(folder);
            frame!.Save(Path.Combine(folder, $"{tab.Replace(" ", "").Replace("-", "")}.png"));
        }

        Close(vm, window);
    }
}
