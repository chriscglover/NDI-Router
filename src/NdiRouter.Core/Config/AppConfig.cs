using System.Text.Json;
using System.Text.Json.Serialization;
using NdiRouter.Core;
using NdiRouter.Swp08;
using NdiRouter.Umd;

namespace NdiRouter.Config;

public sealed class NdiSettings
{
    public bool ShowLocalSources { get; set; } = true;

    /// <summary>Groups to discover sources in; blank for the machine's default groups.</summary>
    public string FindGroups { get; set; } = string.Empty;

    /// <summary>Comma separated addresses of machines to query directly, for sources mDNS cannot reach.</summary>
    public string ExtraIps { get; set; } = string.Empty;

    /// <summary>Groups the router's outputs are published in; blank for the default.</summary>
    public string OutputGroups { get; set; } = string.Empty;
}

/// <summary>Everything the application remembers between runs, including the current routes.</summary>
public sealed class AppConfig
{
    public List<RouterInput> Inputs { get; set; } = new();
    public List<RouterOutput> Outputs { get; set; } = new();
    public List<RouterPreset> Presets { get; set; } = new();

    public NdiSettings Ndi { get; set; } = new();
    public Swp08Settings Swp08 { get; set; } = new();
    public TslSettings Tsl { get; set; } = new();

    /// <summary>A brand new configuration: NDI Router's default 8 inputs, 4 outputs and 6 presets.</summary>
    public static AppConfig CreateDefault()
    {
        var config = new AppConfig();
        config.Normalise();
        return config;
    }

    /// <summary>Fills in anything missing, so a hand-edited or older file still loads into a working router.</summary>
    public void Normalise()
    {
        Inputs ??= new();
        Outputs ??= new();
        Presets ??= new();
        Ndi ??= new();
        Swp08 ??= new();
        Tsl ??= new();

        if (Inputs.Count == 0) Inputs.AddRange(Enumerable.Range(0, RouterDefaults.Inputs).Select(RouterDefaults.Input));
        if (Outputs.Count == 0) Outputs.AddRange(Enumerable.Range(0, RouterDefaults.Outputs).Select(RouterDefaults.Output));
        if (Presets.Count == 0) Presets.AddRange(Enumerable.Range(0, RouterDefaults.Presets).Select(RouterDefaults.Preset));

        foreach (var input in Inputs)
        {
            input.Label ??= string.Empty;
            input.NdiSource ??= string.Empty;
            input.Style ??= new ButtonStyle();
            NormaliseNames(input);
        }

        for (var i = 0; i < Outputs.Count; i++)
        {
            var output = Outputs[i];
            output.Label ??= string.Empty;
            NormaliseNames(output);
            if (string.IsNullOrWhiteSpace(output.NdiName)) output.NdiName = $"Router Out {i + 1}";
            output.Style ??= new ButtonStyle();
        }

        foreach (var preset in Presets) preset.Label ??= string.Empty;
    }

    /// <summary>
    /// Earlier versions had one SW-P-08 name, often just a copy of the label. A copy of the label,
    /// or a name too long for 12 characters, goes back to being generated - it then follows the
    /// label, and is abbreviated rather than cut off. Only a name genuinely different from the
    /// label, that fits, is kept, as the 12-character name. A stored name identical to the one
    /// that would be generated is cleared too, so it keeps following the label.
    /// </summary>
    private static void NormaliseNames(ISwp08Named item)
    {
        var legacy = (item.LegacyShortName ?? string.Empty).Trim();
        item.LegacyShortName = null;

        item.Name4 ??= string.Empty;
        item.Name8 ??= string.Empty;
        item.Name12 ??= string.Empty;

        if (item.Name12.Length == 0 && legacy.Length is > 0 and <= 12 && legacy != item.Label.Trim())
            item.Name12 = legacy;

        foreach (var length in Swp08Names.Lengths)
            Swp08Names.Set(item, length, Swp08Names.ToStore(item, length, Swp08Names.Override(item, length), item.Label));
    }
}

public static class ConfigStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    /// <summary>%APPDATA%\NdiRouter\config.json - the executable often lives somewhere read-only.</summary>
    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NdiRouter", "config.json");

    public static AppConfig Load(string? path = null)
    {
        path ??= DefaultPath;
        if (!File.Exists(path)) return AppConfig.CreateDefault();

        var config = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path), Options) ?? new AppConfig();
        config.Normalise();
        return config;
    }

    public static void Save(AppConfig config, string? path = null)
    {
        path ??= DefaultPath;
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        // Write then swap, so a crash or power cut mid-save cannot leave a half-written file.
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(config, Options));
        File.Move(temp, path, overwrite: true);
    }
}
