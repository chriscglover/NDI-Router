using System.Net.Sockets;
using NdiRouter.Config;
using NdiRouter.Ndi;
using NdiRouter.Swp08;
using NdiRouter.Umd;
using NdiRouter.Videohub;

namespace NdiRouter.Core;

/// <summary>
/// Owns the running pieces - NDI runtime, router, source discovery, SW-P-08 and Videohub servers
/// and TSL sender - and applies settings to them. The window talks to this rather than to each piece.
/// </summary>
public sealed class RouterHost : IDisposable
{
    /// <param name="runtime">The NDI runtime; loaded from the system when null.</param>
    /// <param name="configPath">Where settings are saved; the user's settings file when null.</param>
    public RouterHost(AppConfig config, NdiRuntime? runtime = null, string? configPath = null)
    {
        Config = config;
        ConfigPath = configPath ?? ConfigStore.DefaultPath;
        Ndi = runtime ?? NdiRuntime.Initialize();
        Router = new RouterCore(Ndi, config.Inputs, config.Outputs, config.Presets, config.Ndi.OutputGroups);
        Router.Log += Write;

        Finder = new NdiFinder();

        Swp08 = new Swp08Server(Router);
        Swp08.Log += Write;

        Videohub = new VideohubServer(Router);
        Videohub.Log += Write;

        Tsl = new TslOutput(Router);
        Tsl.Log += Write;
    }

    public AppConfig Config { get; }
    public string ConfigPath { get; }
    public NdiRuntime Ndi { get; }
    public RouterCore Router { get; }
    public NdiFinder Finder { get; }
    public Swp08Server Swp08 { get; }
    public VideohubServer Videohub { get; }
    public TslOutput Tsl { get; }

    public event Action<string>? Log;

    private void Write(string message) => Log?.Invoke(message);

    public void Start()
    {
        if (Ndi.IsAvailable)
        {
            Write($"NDI: {Ndi.Status}");
            Write($"NDI: publishing {Router.OutputCount} output(s): " +
                  string.Join(", ", Router.Outputs().Select(o => NdiName.OwnSourceName(o.NdiName))));
            StartFinder();
        }
        else
        {
            Write($"NDI: {Ndi.Status} Routing still works over SW-P-08 and the panel, but nothing is published.");
        }

        if (Config.Swp08.Enabled) StartSwp08(Config.Swp08);
        if (Config.Videohub.Enabled) StartVideohub(Config.Videohub);
        Tsl.Apply(Config.Tsl);
    }

    private void StartFinder()
    {
        if (!Ndi.IsAvailable) return;
        Finder.Start(Config.Ndi.ShowLocalSources, Config.Ndi.FindGroups, Config.Ndi.ExtraIps);
    }

    private bool StartSwp08(Swp08Settings settings)
    {
        try
        {
            Swp08.Start(settings);
            return true;
        }
        catch (SocketException ex)
        {
            Write($"SW-P-08: cannot listen on port {settings.Port}: {ex.Message}");
            return false;
        }
    }

    public void ApplySwp08(Swp08Settings settings)
    {
        var previous = Config.Swp08;
        Config.Swp08 = settings.Clone();

        if (!settings.Enabled)
        {
            Swp08.Stop();
            return;
        }

        var restart = !Swp08.IsListening || previous.Port != settings.Port || previous.BindAddress != settings.BindAddress;
        if (restart)
            StartSwp08(settings);
        else
            Swp08.UpdateSettings(settings);
    }

    private bool StartVideohub(VideohubSettings settings)
    {
        try
        {
            Videohub.Start(settings);
            return true;
        }
        catch (SocketException ex)
        {
            Write($"Videohub: cannot listen on port {settings.Port}: {ex.Message}");
            return false;
        }
    }

    public void ApplyVideohub(VideohubSettings settings)
    {
        var previous = Config.Videohub;
        Config.Videohub = settings.Clone();

        if (!settings.Enabled)
        {
            Videohub.Stop();
            return;
        }

        var restart = !Videohub.IsListening || previous.Port != settings.Port || previous.BindAddress != settings.BindAddress;
        if (restart)
            StartVideohub(settings);
        else
            Videohub.UpdateSettings(settings);
    }

    public void ApplyTsl(TslSettings settings)
    {
        Config.Tsl = settings.Clone();
        Tsl.Apply(settings);
    }

    public void ApplyNdi(NdiSettings settings)
    {
        var previous = Config.Ndi;
        Config.Ndi = settings;

        if (previous.ShowLocalSources != settings.ShowLocalSources || previous.FindGroups != settings.FindGroups ||
            previous.ExtraIps != settings.ExtraIps)
        {
            StartFinder();
            Write("NDI: discovery restarted with the new settings.");
        }

        if (previous.OutputGroups != settings.OutputGroups)
        {
            Router.SetOutputGroups(settings.OutputGroups);
            Write($"NDI: outputs republished in groups \"{(settings.OutputGroups.Length > 0 ? settings.OutputGroups : "default")}\".");
        }
    }

    /// <summary>Replaces the router's layout with one read from NDI Router.</summary>
    public void Import(NdiRouterImport.Result imported)
    {
        Router.Resize(imported.Inputs.Count, imported.Outputs.Count, Math.Max(imported.Presets.Count, 1));

        for (var i = 0; i < imported.Inputs.Count; i++)
        {
            var source = imported.Inputs[i];
            Router.UpdateInput(i, input =>
            {
                input.Label = source.Label;
                input.Name4 = input.Name8 = input.Name12 = string.Empty;
                input.NdiSource = source.NdiSource;
                input.Style = source.Style.Clone();
            });
        }

        for (var o = 0; o < imported.Outputs.Count; o++)
        {
            var source = imported.Outputs[o];
            Router.UpdateOutput(o, output =>
            {
                output.Label = source.Label;
                output.Name4 = output.Name8 = output.Name12 = string.Empty;
                output.NdiName = source.NdiName;
                output.Style = source.Style.Clone();
            });
            Router.Route(o, source.Input, "import");
        }

        for (var p = 0; p < imported.Presets.Count; p++)
        {
            Router.RenamePreset(p, imported.Presets[p].Label);
            Router.ClearPreset(p);
        }
    }

    /// <summary>Copies the router's current state into the configuration and writes it.</summary>
    public void Save(string? path = null)
    {
        Config.Inputs = Router.Inputs();
        Config.Outputs = Router.Outputs();
        Config.Presets = Router.Presets();
        ConfigStore.Save(Config, path ?? ConfigPath);
    }

    public void Dispose()
    {
        Tsl.Dispose();
        Videohub.Dispose();
        Swp08.Dispose();
        Finder.Dispose();
        Router.Dispose();
    }
}
