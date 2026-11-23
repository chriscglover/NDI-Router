using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using NdiRouter.Config;
using NdiRouter.Core;
using NdiRouter.Gui.ViewModels;

namespace NdiRouter.Gui;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // --config <file> runs with a settings file of its own: a second router on one machine,
            // or a test of a new build that leaves the everyday settings alone.
            var configPath = ConfigPath(desktop.Args ?? Array.Empty<string>());
            var config = LoadConfig(configPath, out var configError);
            var host = new RouterHost(config, configPath: configPath);
            var viewModel = new MainViewModel(host);
            if (configError is not null) viewModel.AddLog(configError);

            desktop.MainWindow = new MainWindow { DataContext = viewModel };
            desktop.MainWindow.Opened += (_, _) => host.Start();
            desktop.Exit += (_, _) => viewModel.Dispose(); // saves, then takes the outputs off the network
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static string ConfigPath(string[] args)
    {
        var at = Array.FindIndex(args, a => a.Equals("--config", StringComparison.OrdinalIgnoreCase));
        return at >= 0 && at + 1 < args.Length ? Path.GetFullPath(args[at + 1]) : ConfigStore.DefaultPath;
    }

    private static AppConfig LoadConfig(string path, out string? error)
    {
        error = null;
        try
        {
            return ConfigStore.Load(path);
        }
        catch (Exception ex)
        {
            // Never overwrite a file that could not be read: keep it aside so nothing is lost.
            var aside = path + ".unreadable";
            try { File.Copy(path, aside, overwrite: true); } catch { /* nothing to keep */ }
            error = $"Settings could not be read ({ex.Message}); starting with defaults. The old file is kept as {aside}.";
            return AppConfig.CreateDefault();
        }
    }
}
