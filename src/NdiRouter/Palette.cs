using System.Reflection;
using Avalonia.Media;

namespace NdiRouter.Gui;

/// <summary>The router panel's colours - dark, like NDI Router's - and the status lights.</summary>
internal static class Palette
{
    public static readonly Color Tile = Color.Parse("#2B2B2B");
    public static readonly Color TileBorder = Color.Parse("#444444");
    public static readonly Color Text = Color.Parse("#DADADA");

    /// <summary>An input on air to every selected output - red, like a tally.</summary>
    public static readonly Color Active = Color.Parse("#D93A3A");

    /// <summary>A selected output.</summary>
    public static readonly Color Selected = Color.Parse("#3A9BE8");

    /// <summary>A preset waiting to be stored to.</summary>
    public static readonly Color Armed = Color.Parse("#E8A82E");

    public static readonly IBrush Ok = new SolidColorBrush(Color.Parse("#4CAF50"));
    public static readonly IBrush Waiting = new SolidColorBrush(Color.Parse("#E8A82E"));
    public static readonly IBrush Off = new SolidColorBrush(Color.Parse("#777777"));
    public static readonly IBrush Fault = new SolidColorBrush(Color.Parse("#E5534B"));

    public static Color? Parse(string? hex) =>
        !string.IsNullOrWhiteSpace(hex) && Color.TryParse(hex, out var colour) ? colour : null;

    public static string ToHex(Color colour) => $"#{colour.R:X2}{colour.G:X2}{colour.B:X2}";

    public static Color Blend(Color a, Color b, double amount) => Color.FromRgb(
        (byte)(a.R + (b.R - a.R) * amount),
        (byte)(a.G + (b.G - a.G) * amount),
        (byte)(a.B + (b.B - a.B) * amount));

    /// <summary>Colours offered for button backgrounds and text, in the style dialog.</summary>
    public static IReadOnlyList<(string Name, string Hex)> Swatches { get; } = new[]
    {
        ("Default", ""), ("Charcoal", "#3C3C3C"), ("Red", "#B3261E"), ("Orange", "#C2571A"), ("Amber", "#B8860B"),
        ("Green", "#2E7D32"), ("Teal", "#00796B"), ("Blue", "#1565C0"), ("Indigo", "#3949AB"), ("Purple", "#6A1B9A"),
        ("Pink", "#AD1457"), ("White", "#F2F2F2"), ("Black", "#101010")
    };
}

/// <summary>The version shown in the title bar and log.</summary>
internal static class AppVersion
{
    public static string Display { get; } =
        typeof(AppVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "dev";
}
