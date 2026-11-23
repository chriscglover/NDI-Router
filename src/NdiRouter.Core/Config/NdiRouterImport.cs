using System.Text.Json;
using NdiRouter.Core;

namespace NdiRouter.Config;

/// <summary>
/// Reads the layout saved by NDI Router from NDI Tools
/// (%LOCALAPPDATA%\NDI\NDI_Router\Application.NDI.Router.json): its inputs with their labels and
/// assigned sources, its outputs with their labels, routing names and routes, and its presets.
/// Only names and routes are taken - NDI Router's presets store nothing this file exposes.
/// </summary>
public static class NdiRouterImport
{
    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NDI", "NDI_Router", "Application.NDI.Router.json");

    public sealed record Result(List<RouterInput> Inputs, List<RouterOutput> Outputs, List<RouterPreset> Presets);

    public static Result Read(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;

        var inputs = new List<RouterInput>();
        foreach (var model in Array(root, "RouterInputModels").OrderBy(m => Int(m, "Index") ?? 0))
        {
            var index = Int(model, "Index") ?? inputs.Count;
            while (inputs.Count < index) inputs.Add(RouterDefaults.Input(inputs.Count));

            var input = RouterDefaults.Input(index);
            input.Label = String(model, "Label") ?? input.Label;
            if (model.TryGetProperty("SourceItem", out var item) && item.ValueKind == JsonValueKind.Object)
                input.NdiSource = String(item, "FullName") ?? string.Empty;
            input.Style = Style(model);
            inputs.Add(input);
        }

        var outputs = new List<RouterOutput>();
        foreach (var model in Array(root, "RouterOutputModels").OrderBy(m => Int(m, "Index") ?? 0))
        {
            var index = Int(model, "Index") ?? outputs.Count;
            while (outputs.Count < index) outputs.Add(RouterDefaults.Output(outputs.Count));

            var output = RouterDefaults.Output(index);
            output.Label = String(model, "Label") ?? output.Label;
            output.NdiName = String(model, "RoutingName") ?? output.NdiName;
            output.Style = Style(model);

            if (model.TryGetProperty("RouterInputModel", out var routed) && routed.ValueKind == JsonValueKind.Object &&
                Int(routed, "Index") is int routedIndex && routedIndex >= 0 && routedIndex < inputs.Count)
            {
                output.Input = routedIndex;
            }

            outputs.Add(output);
        }

        var presets = Array(root, "RouterPresetModels")
            .OrderBy(m => Int(m, "Index") ?? 0)
            .Select((m, i) => new RouterPreset { Label = String(m, "Label") ?? RouterDefaults.Preset(i).Label })
            .ToList();

        if (inputs.Count == 0 || outputs.Count == 0)
            throw new InvalidDataException("The file has no NDI Router inputs or outputs in it.");

        return new Result(inputs, outputs, presets);
    }

    private static ButtonStyle Style(JsonElement model)
    {
        var style = new ButtonStyle
        {
            BackColor = Colour(String(model, "BackgroundColor")),
            ForeColor = Colour(String(model, "ForegroundColor")),
            Bold = model.TryGetProperty("IsBoldChecked", out var b) && b.ValueKind == JsonValueKind.True,
            Italic = model.TryGetProperty("IsFontStyleItalic", out var it) && it.ValueKind == JsonValueKind.True,
            Underline = model.TryGetProperty("IsTextDecorationUnderline", out var u) && u.ValueKind == JsonValueKind.True
        };

        if (model.TryGetProperty("IsFontSmallSizeChecked", out var s) && s.ValueKind == JsonValueKind.True)
            style.FontSize = ButtonFontSize.Small;
        else if (model.TryGetProperty("IsFontLargeSizeChecked", out var l) && l.ValueKind == JsonValueKind.True)
            style.FontSize = ButtonFontSize.Large;

        return style;
    }

    /// <summary>NDI Router stores WPF "#AARRGGBB"; its defaults (#FF292929 / #FFC8C8C8) map to "use the panel default".</summary>
    private static string Colour(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || !value.StartsWith('#')) return string.Empty;
        var hex = value[1..];
        if (hex.Length == 8) hex = hex[2..];
        if (hex.Length != 6) return string.Empty;
        hex = hex.ToUpperInvariant();
        return hex is "292929" or "C8C8C8" ? string.Empty : "#" + hex;
    }

    private static IEnumerable<JsonElement> Array(JsonElement root, string name) =>
        root.TryGetProperty(name, out var array) && array.ValueKind == JsonValueKind.Array
            ? array.EnumerateArray().ToList()
            : Enumerable.Empty<JsonElement>();

    private static string? String(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int? Int(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var i)
            ? i
            : null;
}
