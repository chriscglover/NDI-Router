using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using NdiRouter.Core;
using NdiRouter.Gui.ViewModels;

namespace NdiRouter.Gui.Views;

/// <summary>The window's dialogs, for the view models.</summary>
internal sealed class WindowDialogs(Window owner) : IDialogs
{
    public Task<string?> PromptAsync(string title, string message, string value)
    {
        var box = new TextBox { Text = value, MinWidth = 380 };
        var dialog = DialogWindow.Create(title, message, box, focus: box);
        return dialog.ShowAsync<string?>(owner, ok: () => box.Text?.Trim() ?? string.Empty, cancel: null);
    }

    public Task<bool> ConfirmAsync(string title, string message)
    {
        var dialog = DialogWindow.Create(title, message, content: null, focus: null);
        return dialog.ShowAsync(owner, ok: () => true, cancel: false);
    }

    public Task<string[]?> EditNamesAsync(ISwp08Named item, string fallbackLabel)
    {
        var label = string.IsNullOrWhiteSpace(item.Label) ? fallbackLabel : item.Label;
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), RowSpacing = 8, ColumnSpacing = 12 };
        var boxes = new List<TextBox>();

        for (var k = 0; k < Swp08Names.Lengths.Length; k++)
        {
            var length = Swp08Names.Lengths[k];
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var caption = new TextBlock { Text = $"{length} characters", VerticalAlignment = VerticalAlignment.Center };
            var box = new TextBox
            {
                Text = Swp08Names.IsGenerated(item, length) ? string.Empty : Swp08Names.Override(item, length),
                Watermark = Swp08Names.Generate(label, length),
                MaxLength = length,
                Classes = { "mono" },
                Width = 12 * length + 40,
                HorizontalAlignment = HorizontalAlignment.Left
            };
            Grid.SetRow(caption, k);
            Grid.SetRow(box, k);
            Grid.SetColumn(box, 1);
            grid.Children.Add(caption);
            grid.Children.Add(box);
            boxes.Add(box);
        }

        var dialog = DialogWindow.Create("SW-P-08 names",
            $"Names control systems are sent for \"{label}\". Leave a name empty and the grey one shown is " +
            "generated from the label, and follows it when the label changes.",
            grid, focus: boxes[0]);
        return dialog.ShowAsync<string[]?>(owner, ok: () => boxes.Select(b => b.Text ?? string.Empty).ToArray(), cancel: null);
    }

    public Task<ButtonStyle?> EditStyleAsync(ButtonStyle style, string title)
    {
        var editor = new StyleEditor(style, title);
        var dialog = DialogWindow.Create("Button style", null, editor, focus: null);
        return dialog.ShowAsync<ButtonStyle?>(owner, ok: () => editor.Result, cancel: null);
    }

    public async Task<string?> OpenFileAsync(string title, string? suggestedFolder, string? suggestedName)
    {
        var options = new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("NDI Tools router settings") { Patterns = new[] { "*.json" } },
                FilePickerFileTypes.All
            }
        };
        if (suggestedFolder is not null && Directory.Exists(suggestedFolder))
            options.SuggestedStartLocation = await owner.StorageProvider.TryGetFolderFromPathAsync(suggestedFolder);

        var files = await owner.StorageProvider.OpenFilePickerAsync(options);
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    public async Task CopyToClipboardAsync(string text)
    {
        if (owner.Clipboard is { } clipboard) await clipboard.SetTextAsync(text);
    }
}

/// <summary>A small modal window: an optional message, optional content, OK and Cancel.</summary>
internal sealed class DialogWindow : Window
{
    private readonly Button _ok = new() { Content = "OK", Classes = { "accent" }, MinWidth = 80, IsDefault = true };
    private readonly Button _cancel = new() { Content = "Cancel", MinWidth = 80, IsCancel = true };
    private Control? _focus;

    public static DialogWindow Create(string title, string? message, Control? content, Control? focus)
    {
        var window = new DialogWindow
        {
            Title = title,
            SizeToContent = SizeToContent.WidthAndHeight,
            CanResize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            _focus = focus
        };

        var stack = new StackPanel { Margin = new Thickness(20), Spacing = 14, MaxWidth = 520 };
        if (message is not null) stack.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
        if (content is not null) stack.Children.Add(content);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(window._ok);
        buttons.Children.Add(window._cancel);
        stack.Children.Add(buttons);

        window.Content = stack;
        return window;
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (_focus is TextBox box)
        {
            box.Focus();
            box.SelectAll();
        }
    }

    public Task<T> ShowAsync<T>(Window owner, Func<T> ok, T cancel)
    {
        Icon = owner.Icon;
        _ok.Click += (_, _) => Close(ok());
        _cancel.Click += (_, _) => Close(cancel);
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close(cancel);
        };

        // Closing with the title bar's X returns default(T) - null or false - the same as Cancel.
        return ShowDialog<T>(owner);
    }
}

/// <summary>Background and text swatches, size and emphasis, with a live preview.</summary>
internal sealed class StyleEditor : StackPanel
{
    private readonly ButtonStyle _style;
    private readonly Border _preview;
    private readonly TextBlock _previewText;

    public StyleEditor(ButtonStyle style, string title)
    {
        _style = style.Clone();
        Spacing = 10;

        _previewText = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(title) ? "Preview" : title,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center
        };
        _preview = new Border
        {
            Width = 160,
            Height = 76,
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Palette.TileBorder),
            Padding = new Thickness(8),
            Child = _previewText,
            HorizontalAlignment = HorizontalAlignment.Left
        };

        Children.Add(Heading("BACKGROUND"));
        Children.Add(Swatches(hex => _style.BackColor = hex));
        Children.Add(Heading("TEXT"));
        Children.Add(Swatches(hex => _style.ForeColor = hex));

        var size = new ComboBox
        {
            ItemsSource = Enum.GetValues<ButtonFontSize>(),
            SelectedItem = _style.FontSize,
            MinWidth = 120
        };
        size.SelectionChanged += (_, _) =>
        {
            if (size.SelectedItem is ButtonFontSize s) _style.FontSize = s;
            Show();
        };

        var bold = Check("Bold", _style.Bold, v => _style.Bold = v);
        var italic = Check("Italic", _style.Italic, v => _style.Italic = v);
        var underline = Check("Underline", _style.Underline, v => _style.Underline = v);

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14 };
        row.Children.Add(size);
        row.Children.Add(bold);
        row.Children.Add(italic);
        row.Children.Add(underline);
        Children.Add(Heading("TEXT STYLE"));
        Children.Add(row);

        Children.Add(Heading("PREVIEW"));
        Children.Add(_preview);
        Show();
    }

    public ButtonStyle Result => _style.Clone();

    private static TextBlock Heading(string text) => new() { Text = text, Classes = { "heading" } };

    private CheckBox Check(string text, bool value, Action<bool> set)
    {
        var box = new CheckBox { Content = text, IsChecked = value };
        box.IsCheckedChanged += (_, _) =>
        {
            set(box.IsChecked == true);
            Show();
        };
        return box;
    }

    private WrapPanel Swatches(Action<string> set)
    {
        var panel = new WrapPanel { MaxWidth = 480 };
        foreach (var (name, hex) in Palette.Swatches)
        {
            var colour = Palette.Parse(hex);
            var swatch = new Button
            {
                Width = 30,
                Height = 24,
                Margin = new Thickness(0, 0, 6, 6),
                Padding = new Thickness(0),
                Background = colour is { } c ? new SolidColorBrush(c) : new SolidColorBrush(Palette.Tile),
                BorderBrush = Brushes.Gray,
                BorderThickness = new Thickness(1),
                Content = colour is null ? new TextBlock { Text = "–", HorizontalAlignment = HorizontalAlignment.Center } : null
            };
            ToolTip.SetTip(swatch, name);
            swatch.Click += (_, _) =>
            {
                set(hex);
                Show();
            };
            panel.Children.Add(swatch);
        }
        return panel;
    }

    private void Show()
    {
        var back = Palette.Parse(_style.BackColor) ?? Palette.Tile;
        var fore = Palette.Parse(_style.ForeColor) ?? Palette.Text;
        _preview.Background = new SolidColorBrush(back);
        _previewText.Foreground = new SolidColorBrush(fore);
        _previewText.FontSize = _style.FontSize switch { ButtonFontSize.Small => 12, ButtonFontSize.Large => 18, _ => 14.5 };
        _previewText.FontWeight = _style.Bold ? FontWeight.Bold : FontWeight.Normal;
        _previewText.FontStyle = _style.Italic ? FontStyle.Italic : FontStyle.Normal;
        _previewText.TextDecorations = _style.Underline ? TextDecorations.Underline : null;
    }
}
