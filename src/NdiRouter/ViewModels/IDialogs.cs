using NdiRouter.Core;

namespace NdiRouter.Gui.ViewModels;

/// <summary>
/// The questions the view models need to ask the operator. The window implements it with real
/// dialogs; tests answer them directly.
/// </summary>
public interface IDialogs
{
    Task<string?> PromptAsync(string title, string message, string value);

    Task<bool> ConfirmAsync(string title, string message);

    Task<ButtonStyle?> EditStyleAsync(ButtonStyle style, string title);

    /// <summary>Edits the 4, 8 and 12 character names; null when cancelled.</summary>
    Task<string[]?> EditNamesAsync(ISwp08Named item, string fallbackLabel);

    /// <summary>Picks a file to open; null when cancelled.</summary>
    Task<string?> OpenFileAsync(string title, string? suggestedFolder, string? suggestedName);

    Task CopyToClipboardAsync(string text);
}

/// <summary>Answers nothing: every dialog is cancelled. Used until the window supplies real ones.</summary>
public sealed class NoDialogs : IDialogs
{
    public Task<string?> PromptAsync(string title, string message, string value) => Task.FromResult<string?>(null);
    public Task<bool> ConfirmAsync(string title, string message) => Task.FromResult(false);
    public Task<ButtonStyle?> EditStyleAsync(ButtonStyle style, string title) => Task.FromResult<ButtonStyle?>(null);
    public Task<string[]?> EditNamesAsync(ISwp08Named item, string fallbackLabel) => Task.FromResult<string[]?>(null);
    public Task<string?> OpenFileAsync(string title, string? suggestedFolder, string? suggestedName) => Task.FromResult<string?>(null);
    public Task CopyToClipboardAsync(string text) => Task.CompletedTask;
}
