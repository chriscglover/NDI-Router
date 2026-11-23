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

public sealed class MainWindowTests : IDisposable
{
    private readonly string _configPath = Path.Combine(Path.GetTempPath(), $"NdiRouterGui{Guid.NewGuid():N}.json");

    public void Dispose() => File.Delete(_configPath);

    /// <summary>A router like a small studio's, with no NDI on the network and settings in a temporary file.</summary>
    private (MainViewModel Vm, MainWindow Window) Open()
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
        var vm = new MainViewModel(host);
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
    public void Selecting_an_output_then_an_input_routes_it_and_lights_the_input()
    {
        var (vm, window) = Open();

        vm.SelectOutput(1, additive: false);
        vm.RouteInputCommand.Execute(4);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(4, vm.Router.GetRoute(1));
        Assert.Equal(Palette.Active, ((ISolidColorBrush)vm.InputTiles[4].Background).Color);
        Assert.Equal("◀ Studio Wide", vm.OutputTiles[1].Subtitle);
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

        vm.RouteInputCommand.Execute(3);     // both to Playout A
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
        var (vm, window) = Open();
        vm.SelectOutput(0, additive: false);
        vm.SelectOutput(2, additive: true);
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
