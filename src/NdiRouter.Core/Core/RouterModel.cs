using System.Text.Json.Serialization;

namespace NdiRouter.Core;

public enum ButtonFontSize
{
    Small,
    Medium,
    Large
}

/// <summary>How a button on the router panel looks. Blank colours use the panel's defaults.</summary>
public sealed class ButtonStyle
{
    /// <summary>"#RRGGBB", or blank for the default.</summary>
    public string BackColor { get; set; } = string.Empty;

    /// <summary>"#RRGGBB", or blank for the default.</summary>
    public string ForeColor { get; set; } = string.Empty;

    public ButtonFontSize FontSize { get; set; } = ButtonFontSize.Medium;
    public bool Bold { get; set; }
    public bool Italic { get; set; }
    public bool Underline { get; set; }

    public ButtonStyle Clone() => (ButtonStyle)MemberwiseClone();
}

/// <summary>
/// A router input: a numbered slot with an NDI source assigned to it. Its position in the list
/// is its number - input 1 is SW-P-08 source 0 on the wire.
/// </summary>
public sealed class RouterInput : ISwp08Named
{
    public string Label { get; set; } = string.Empty;

    /// <summary>4-, 8- and 12-character names for SW-P-08 controllers; blank means generated from the label.</summary>
    public string Name4 { get; set; } = string.Empty;
    public string Name8 { get; set; } = string.Empty;
    public string Name12 { get; set; } = string.Empty;

    /// <summary>The single SW-P-08 name of earlier versions; read once and moved into <see cref="Name12"/>.</summary>
    [JsonPropertyName("ShortName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LegacyShortName { get; set; }

    /// <summary>Full NDI source name, "MACHINE (Source)". Blank means nothing is assigned.</summary>
    public string NdiSource { get; set; } = string.Empty;

    public ButtonStyle Style { get; set; } = new();

    public RouterInput Clone() => new()
    {
        Label = Label,
        Name4 = Name4,
        Name8 = Name8,
        Name12 = Name12,
        NdiSource = NdiSource,
        Style = Style.Clone()
    };
}

/// <summary>
/// A router output: an NDI source this application publishes, which shows whichever input is
/// routed to it. Its position in the list is its number - output 1 is SW-P-08 destination 0.
/// </summary>
public sealed class RouterOutput : ISwp08Named
{
    public string Label { get; set; } = string.Empty;

    /// <summary>4-, 8- and 12-character names for SW-P-08 controllers; blank means generated from the label.</summary>
    public string Name4 { get; set; } = string.Empty;
    public string Name8 { get; set; } = string.Empty;
    public string Name12 { get; set; } = string.Empty;

    /// <summary>The single SW-P-08 name of earlier versions; read once and moved into <see cref="Name12"/>.</summary>
    [JsonPropertyName("ShortName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LegacyShortName { get; set; }

    /// <summary>The name this output is published under on the NDI network.</summary>
    public string NdiName { get; set; } = string.Empty;

    /// <summary>Index of the input routed here, or null when the output is cleared.</summary>
    public int? Input { get; set; }

    /// <summary>TSL display address that shows what this output is carrying.</summary>
    public int UmdAddress { get; set; }

    public bool UmdEnabled { get; set; } = true;

    public ButtonStyle Style { get; set; } = new();

    public RouterOutput Clone() => new()
    {
        Label = Label,
        Name4 = Name4,
        Name8 = Name8,
        Name12 = Name12,
        NdiName = NdiName,
        Input = Input,
        UmdAddress = UmdAddress,
        UmdEnabled = UmdEnabled,
        Style = Style.Clone()
    };
}

/// <summary>A stored set of routes, recalled in one go.</summary>
public sealed class RouterPreset
{
    public string Label { get; set; } = string.Empty;

    /// <summary>The input for each output, by output index; null entries clear that output.
    /// The list itself is null until something has been stored.</summary>
    public List<int?>? Routes { get; set; }

    public bool IsEmpty => Routes is null;

    public RouterPreset Clone() => new()
    {
        Label = Label,
        Routes = Routes is null ? null : new List<int?>(Routes)
    };
}

/// <summary>Defaults for a slot that has just been added.</summary>
public static class RouterDefaults
{
    /// <summary>NDI Router opens with 8 inputs, 4 outputs and 6 presets; so does this.</summary>
    public const int Inputs = 8;
    public const int Outputs = 4;
    public const int Presets = 6;

    public static RouterInput Input(int index) => new() { Label = $"Input {index + 1}" };

    /// <summary>
    /// "Router Out n" rather than NDI Router's own "Router Dest n", so the two can run on one
    /// machine without publishing two sources under the same name.
    /// </summary>
    public static RouterOutput Output(int index) => new()
    {
        Label = $"Output {index + 1}",
        NdiName = $"Router Out {index + 1}",
        UmdAddress = index + 1
    };

    public static RouterPreset Preset(int index) => new() { Label = $"Preset {index + 1}" };
}
