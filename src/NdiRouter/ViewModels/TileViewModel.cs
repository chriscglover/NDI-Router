using Avalonia;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using NdiRouter.Core;

namespace NdiRouter.Gui.ViewModels;

public enum TileKind
{
    Output,
    Input,
    Preset
}

public enum TileState
{
    Normal,

    /// <summary>Every selected output carries this input.</summary>
    Active,

    /// <summary>Some, but not all, selected outputs carry this input.</summary>
    Mixed
}

/// <summary>One button on the router panel.</summary>
public sealed partial class TileViewModel(TileKind kind, int index) : ObservableObject
{
    public TileKind Kind { get; } = kind;
    public int Index { get; } = index;

    public string Number { get; } = kind switch
    {
        TileKind.Output => $"OUT {index + 1}",
        TileKind.Input => $"IN {index + 1}",
        _ => $"PRESET {index + 1}"
    };

    [ObservableProperty] private string _title = string.Empty;
    [ObservableProperty] private string _subtitle = string.Empty;
    [ObservableProperty] private IBrush _background = new SolidColorBrush(Palette.Tile);
    [ObservableProperty] private IBrush _foreground = new SolidColorBrush(Palette.Text);
    [ObservableProperty] private IBrush _subForeground = new SolidColorBrush(Palette.Text);
    [ObservableProperty] private IBrush _borderBrush = new SolidColorBrush(Palette.TileBorder);
    [ObservableProperty] private Thickness _borderThickness = new(1);
    [ObservableProperty] private bool _isMixed;

    /// <summary>On the preview monitor; marked with a small PVW badge.</summary>
    [ObservableProperty] private bool _isPreviewed;

    /// <summary>A locked destination; marked with a LOCK badge, whose tooltip says who holds it.</summary>
    [ObservableProperty] private bool _isLocked;
    [ObservableProperty] private string? _lockTip;
    [ObservableProperty] private double _fontSize = 14.5;
    [ObservableProperty] private FontWeight _fontWeight = FontWeight.Normal;
    [ObservableProperty] private FontStyle _fontStyle = FontStyle.Normal;
    [ObservableProperty] private TextDecorationCollection? _textDecorations;

    /// <summary>Works out the tile's look from the button style and its routing state.</summary>
    /// <param name="preselected">A source picked for the next Take: shown with a yellow edge.</param>
    /// <param name="lockedBy">Who has locked this destination, if anyone.</param>
    public void Update(string title, string subtitle, ButtonStyle style, bool selected, TileState state, bool dimmed, bool armed,
        bool preselected = false, bool previewed = false, OutputLock? lockedBy = null)
    {
        IsPreviewed = previewed;
        IsLocked = lockedBy is not null;
        LockTip = lockedBy is null ? null : $"Locked by {lockedBy}";
        selected |= preselected;
        Title = title;
        Subtitle = subtitle;

        var custom = Palette.Parse(style.BackColor);
        var back = custom ?? Palette.Tile;
        if (state == TileState.Active && custom is null) back = Palette.Active;

        var fore = Palette.Parse(style.ForeColor) ?? Palette.Text;
        if (dimmed && state != TileState.Active) fore = Palette.Blend(fore, back, 0.55);

        SetBrush(Background, back, b => Background = b);
        SetBrush(Foreground, fore, b => Foreground = b);
        SetBrush(SubForeground, Palette.Blend(fore, back, 0.35), b => SubForeground = b);

        var edge = armed ? Palette.Armed
            : preselected ? Palette.Preselected
            : selected ? Palette.Selected
            : state != TileState.Normal && custom is not null ? Palette.Active
            : state == TileState.Active ? Palette.Blend(Palette.Active, Colors.White, 0.25)
            : Palette.TileBorder;
        SetBrush(BorderBrush, edge, b => BorderBrush = b);
        BorderThickness = new Thickness(armed || selected || state == TileState.Active ? 3 : 1);

        // A dashed red outline marks an input that only some of the selected outputs carry.
        IsMixed = state == TileState.Mixed && !selected && !armed;

        FontSize = style.FontSize switch
        {
            ButtonFontSize.Small => 12,
            ButtonFontSize.Large => 18,
            _ => 14.5
        };
        FontWeight = style.Bold ? FontWeight.Bold : FontWeight.Normal;
        FontStyle = style.Italic ? FontStyle.Italic : FontStyle.Normal;
        TextDecorations = style.Underline ? Avalonia.Media.TextDecorations.Underline : null;
    }

    /// <summary>Only replaces a brush when its colour changes, so a once-a-second refresh does not redraw every tile.</summary>
    private static void SetBrush(IBrush current, Color colour, Action<IBrush> set)
    {
        if (current is ISolidColorBrush solid && solid.Color == colour) return;
        set(new SolidColorBrush(colour));
    }
}
