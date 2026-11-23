using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using NdiRouter.Core;
using NdiRouter.Ndi;

namespace NdiRouter.Umd;

/// <summary>How an over-long label is squeezed into the 16 characters TSL 3.1 and 4.0 allow.</summary>
public enum FitMode
{
    /// <summary>Keep the first 16 characters.</summary>
    Truncate,

    /// <summary>Keep the last 16 characters - useful when the distinguishing part is on the end.</summary>
    KeepEnd,

    /// <summary>Drop vowels from the longest words first, then spaces, then truncate.</summary>
    Squeeze
}

/// <summary>Everything a UMD template can refer to for one output.</summary>
public sealed record UmdContext(int OutputIndex, RouterOutput Output, int? InputIndex, RouterInput? Input);

/// <summary>
/// Turns "this output is carrying that input" into the text a UMD shows.
///
/// The template is plain text with {tokens} in it. A token may list alternatives separated by
/// "|", and the first that resolves to something non-empty wins, so {input.label|input.ndi}
/// shows the input's label and falls back to its NDI source name. A quoted alternative is
/// literal text: {input.label|"NO SOURCE"}.
/// </summary>
public static class UmdLabel
{
    public const string DefaultRoutedTemplate = "{input.label}";
    public const string DefaultClearedTemplate = "Clear";

    private static readonly Regex TokenPattern = new(@"\{(?<body>[^{}]*)\}", RegexOptions.Compiled);
    private static readonly Regex WhitespacePattern = new(@"\s+", RegexOptions.Compiled);

    /// <summary>Every token, for the help text in the UI.</summary>
    public static IReadOnlyList<string> Tokens { get; } = new[]
    {
        "{input.label}", "{input.name4}", "{input.name8}", "{input.name12}",
        "{input.ndi}", "{input.machine}", "{input.channel}", "{input.number}",
        "{output.label}", "{output.name4}", "{output.name8}", "{output.name12}", "{output.ndi}", "{output.number}",
        "{addr}", "{addr:000}"
    };

    public static string Format(string template, UmdContext context, int address)
    {
        if (string.IsNullOrEmpty(template)) return string.Empty;

        return TokenPattern.Replace(template, match =>
        {
            foreach (var alternative in match.Groups["body"].Value.Split('|'))
            {
                var value = Resolve(alternative.Trim(), context, address);
                if (!string.IsNullOrWhiteSpace(value)) return value;
            }

            return string.Empty;
        });
    }

    private static string Resolve(string token, UmdContext context, int address)
    {
        if (token.Length == 0) return string.Empty;
        if (token.Length >= 2 && token[0] == '"' && token[^1] == '"') return token[1..^1];

        if (token.StartsWith("addr", StringComparison.OrdinalIgnoreCase))
        {
            var colon = token.IndexOf(':');
            return colon < 0
                ? address.ToString(CultureInfo.InvariantCulture)
                : address.ToString(token[(colon + 1)..], CultureInfo.InvariantCulture);
        }

        var dot = token.IndexOf('.');
        var scope = (dot < 0 ? token : token[..dot]).ToLowerInvariant();
        var field = (dot < 0 ? "label" : token[(dot + 1)..]).ToLowerInvariant();

        switch (scope)
        {
            case "input":
            case "source":
            {
                if (context.Input is not { } input || context.InputIndex is not int index) return string.Empty;
                return field switch
                {
                    "label" => input.Label,
                    "name4" => Swp08Names.For(input, 4, input.Label),
                    "name8" => Swp08Names.For(input, 8, input.Label),
                    "name12" => Swp08Names.For(input, 12, input.Label),
                    "ndi" => input.NdiSource,
                    "machine" => input.NdiSource.Length > 0 ? NdiName.Machine(input.NdiSource) : string.Empty,
                    "channel" => input.NdiSource.Length > 0 ? NdiName.Channel(input.NdiSource) : string.Empty,
                    "number" => (index + 1).ToString(CultureInfo.InvariantCulture),
                    _ => string.Empty
                };
            }

            case "output":
            case "dest":
            {
                var output = context.Output;
                return field switch
                {
                    "label" => output.Label,
                    "name4" => Swp08Names.For(output, 4, output.Label),
                    "name8" => Swp08Names.For(output, 8, output.Label),
                    "name12" => Swp08Names.For(output, 12, output.Label),
                    "ndi" => output.NdiName,
                    "number" => (context.OutputIndex + 1).ToString(CultureInfo.InvariantCulture),
                    _ => string.Empty
                };
            }

            default:
                return string.Empty;
        }
    }

    // ---------------------------------------------------------------- fitting
    // The same rules as NMOS UMD, so the two tools cut a long name the same way.

    public static string Fit(string text, int maxLength, FitMode mode)
    {
        var clean = WhitespacePattern.Replace(text ?? string.Empty, " ").Trim();
        if (maxLength <= 0 || clean.Length <= maxLength) return clean;

        return mode switch
        {
            FitMode.KeepEnd => KeepEnd(clean, maxLength),
            FitMode.Squeeze => Squeeze(clean, maxLength),
            _ => clean[..maxLength]
        };
    }

    /// <summary>Keeps the tail, then drops a leading part-word.</summary>
    private static string KeepEnd(string text, int maxLength)
    {
        var tail = text[^maxLength..];
        if (text[^(maxLength + 1)] == ' ') return tail;

        var space = tail.IndexOf(' ');
        return space >= 0 && space < tail.Length - 1 ? tail[(space + 1)..] : tail;
    }

    /// <summary>
    /// Drops vowels from the longest word first, then closes up the spaces, then trims letters
    /// off the end, and only then cuts. Digits are never dropped, nor letters touching a digit:
    /// "1080i25" must not become "108025".
    /// </summary>
    private static string Squeeze(string text, int maxLength)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(w => new StringBuilder(w)).ToList();
        if (words.Count == 0) return text[..maxLength];

        while (Length(words) > maxLength)
        {
            var target = -1;
            for (var i = 0; i < words.Count; i++)
            {
                if (!HasRemovableVowel(words[i])) continue;
                if (target < 0 || words[i].Length > words[target].Length) target = i;
            }

            if (target < 0) break;
            RemoveLastVowel(words[target]);
        }

        var squeezed = string.Join(" ", words.Select(w => w.ToString()));
        if (squeezed.Length <= maxLength) return squeezed;

        var packed = new StringBuilder(squeezed.Replace(" ", string.Empty));
        while (packed.Length > maxLength)
        {
            var last = -1;
            for (var i = packed.Length - 1; i >= 0; i--)
            {
                if (!char.IsLetter(packed[i]) || TouchesDigit(packed, i)) continue;
                last = i;
                break;
            }

            if (last < 0) break;
            packed.Remove(last, 1);
        }

        var result = packed.ToString();
        return result.Length <= maxLength ? result : result[..maxLength];
    }

    private static int Length(List<StringBuilder> words) => words.Sum(w => w.Length) + Math.Max(0, words.Count - 1);

    private static bool HasRemovableVowel(StringBuilder word)
    {
        for (var i = word.Length - 1; i >= 1; i--)
            if (IsRemovable(word, i)) return true;
        return false;
    }

    private static void RemoveLastVowel(StringBuilder word)
    {
        for (var i = word.Length - 1; i >= 1; i--)
        {
            if (!IsRemovable(word, i)) continue;
            word.Remove(i, 1);
            return;
        }
    }

    private static bool IsRemovable(StringBuilder word, int index) => IsVowel(word[index]) && !TouchesDigit(word, index);

    private static bool TouchesDigit(StringBuilder word, int index) =>
        (index > 0 && char.IsDigit(word[index - 1])) ||
        (index + 1 < word.Length && char.IsDigit(word[index + 1]));

    private static bool IsVowel(char c) => "aeiouAEIOU".IndexOf(c) >= 0;
}
