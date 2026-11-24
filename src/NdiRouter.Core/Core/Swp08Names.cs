using System.Text;
using System.Text.RegularExpressions;

namespace NdiRouter.Core;

/// <summary>Something with a label and the three fixed-length names SW-P-08 controllers ask for.</summary>
public interface ISwp08Named
{
    string Label { get; }

    /// <summary>The single SW-P-08 name of earlier versions, while a saved file is being upgraded.</summary>
    string? LegacyShortName { get; set; }

    /// <summary>4-character name; blank means generated from the label.</summary>
    string Name4 { get; set; }

    /// <summary>8-character name; blank means generated from the label.</summary>
    string Name8 { get; set; }

    /// <summary>12-character name; blank means generated from the label.</summary>
    string Name12 { get; set; }
}

/// <summary>
/// The names sent to SW-P-08 controllers. Controllers ask for names at a fixed length - 4, 8 or
/// 12 characters - and router control panels show whichever length fits their buttons, so each
/// input and output has all three. Each is generated from the label unless it has been typed in.
/// </summary>
public static class Swp08Names
{
    /// <summary>The lengths that are stored and editable.</summary>
    public static readonly int[] Lengths = { 4, 8, 12 };

    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    /// <summary>The name a controller is sent at <paramref name="length"/> characters.</summary>
    public static string For(ISwp08Named item, int length, string fallbackLabel)
    {
        var typed = Override(item, length).Trim();
        if (typed.Length > 0) return typed.Length <= length ? typed : typed[..length];

        var label = string.IsNullOrWhiteSpace(item.Label) ? fallbackLabel : item.Label;
        return Generate(label, length);
    }

    /// <summary>The typed-in name at a stored length, or blank when it is generated.</summary>
    public static string Override(ISwp08Named item, int length) => length switch
    {
        4 => item.Name4,
        8 => item.Name8,
        12 => item.Name12,
        _ => string.Empty // 16 and 32 are always generated
    } ?? string.Empty;

    public static bool IsGenerated(ISwp08Named item, int length) => string.IsNullOrWhiteSpace(Override(item, length));

    /// <summary>Types in a name at a stored length; blank goes back to generating it.</summary>
    public static void Set(ISwp08Named item, int length, string value)
    {
        value = (value ?? string.Empty).Trim();
        if (value.Length > length) value = value[..length];
        switch (length)
        {
            case 4: item.Name4 = value; break;
            case 8: item.Name8 = value; break;
            case 12: item.Name12 = value; break;
        }
    }

    /// <summary>Goes back to generating all three names from the label.</summary>
    public static void Reset(ISwp08Named item) => item.Name4 = item.Name8 = item.Name12 = string.Empty;

    /// <summary>
    /// What to store for a name the operator has just entered: blank (generated) when it is what
    /// would be generated anyway, so it keeps following the label; otherwise the name itself.
    /// </summary>
    public static string ToStore(ISwp08Named item, int length, string entered, string fallbackLabel)
    {
        entered = (entered ?? string.Empty).Trim();
        var label = string.IsNullOrWhiteSpace(item.Label) ? fallbackLabel : item.Label;
        return entered.Length == 0 || entered == Generate(label, length) ? string.Empty : entered;
    }

    /// <summary>
    /// Abbreviates a label to fit, in steps that each lose a little more, stopping as soon as it
    /// fits:
    /// <list type="number">
    /// <item>close up the spaces and punctuation ("Cam 1" -> "Cam1");</item>
    /// <item>drop a word's vowels, longest word first, keeping the spaces ("Std Camera 1");</item>
    /// <item>then close up the spaces and punctuation;</item>
    /// <item>shorten words from their end, longest first, down to their first letter ("SCm1");</item>
    /// <item>cut, keeping a trailing number.</item>
    /// </list>
    /// A word's first letter is never dropped, nor a digit or a letter touching one: the digits are
    /// what tell "CAM 1" from "CAM 11", and "1080i25" must not become "108025". Two numbers are
    /// never run together, since "CAM 3 10" packed to "CAM310" reads as one number.
    /// </summary>
    public static string Generate(string label, int length)
    {
        var text = Whitespace.Replace(label ?? string.Empty, " ").Trim();
        text = new string(text.Select(c => c is >= ' ' and <= '~' ? c : '?').ToArray());
        if (length <= 0 || text.Length <= length) return text;

        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(w => new StringBuilder(w)).ToList();

        // 1. Close up.
        var closed = Pack(Stripped(words));
        if (closed.Length <= length) return closed;

        // 2. Vowels, a whole word at a time, longest word first.
        while (Spaced(words) > length)
        {
            var target = Longest(words, HasDroppableVowel);
            if (target < 0) break;
            DropVowels(words[target]);
        }
        if (Spaced(words) <= length) return string.Join(" ", words);

        // 3. Close up what is left.
        words = Stripped(words);
        var packed = Pack(words);
        if (packed.Length <= length) return packed;

        // 4. Shorten words from their end, longest first.
        while (Pack(words).Length > length)
        {
            var target = Longest(words, w => LastDroppable(w) >= 0);
            if (target < 0) break;
            words[target].Remove(LastDroppable(words[target]), 1);
        }

        // 5. Cut, keeping a trailing number when there is room for it.
        packed = Pack(words);
        if (packed.Length <= length) return packed;
        var last = words[^1].ToString();
        if (!last.All(char.IsDigit) || last.Length >= length) return Cut(packed, length, string.Empty);
        return Cut(Pack(words[..^1]), length, last);
    }

    /// <summary>
    /// The longest start of <paramref name="text"/> that fits in <paramref name="length"/> with
    /// <paramref name="tail"/> after it. The cut never falls inside a number, and a number before a
    /// numeric tail keeps a space between them, so "RTX 2070 SUPER 1" never becomes "R20701".
    /// </summary>
    private static string Cut(string text, int length, string tail)
    {
        for (var keep = Math.Min(text.Length, length - tail.Length); keep > 0; keep--)
        {
            if (keep < text.Length && char.IsDigit(text[keep - 1]) && char.IsDigit(text[keep])) continue; // inside a number

            var head = text[..keep].TrimEnd();
            if (head.Length == 0) break;
            var gap = tail.Length > 0 && char.IsDigit(head[^1]) && char.IsDigit(tail[0]) ? " " : string.Empty;
            if (head.Length + gap.Length + tail.Length <= length) return head + gap + tail;
        }

        // Nothing fits whole: the tail alone, or failing that the text cut short.
        return tail.Length > 0 ? tail : text[..length];
    }

    /// <summary>Words with their punctuation removed; a word that is all punctuation goes.</summary>
    private static List<StringBuilder> Stripped(List<StringBuilder> words) => words
        .Select(w => new StringBuilder(new string(w.ToString().Where(char.IsLetterOrDigit).ToArray())))
        .Where(w => w.Length > 0)
        .ToList();

    /// <summary>Words joined without spaces, except between two numbers.</summary>
    private static string Pack(List<StringBuilder> words)
    {
        var result = new StringBuilder();
        foreach (var word in words)
        {
            if (result.Length > 0 && word.Length > 0 && char.IsDigit(result[^1]) && char.IsDigit(word[0])) result.Append(' ');
            result.Append(word);
        }
        return result.ToString();
    }

    private static int Spaced(List<StringBuilder> words) => words.Sum(w => w.Length) + Math.Max(0, words.Count - 1);

    /// <summary>Index of the longest word that <paramref name="eligible"/> accepts; the earlier on a tie.</summary>
    private static int Longest(List<StringBuilder> words, Func<StringBuilder, bool> eligible)
    {
        var best = -1;
        for (var i = 0; i < words.Count; i++)
        {
            if (!eligible(words[i])) continue;
            if (best < 0 || words[i].Length > words[best].Length) best = i;
        }
        return best;
    }

    private static bool HasDroppableVowel(StringBuilder word)
    {
        for (var i = word.Length - 1; i >= 1; i--)
            if (IsVowel(word[i]) && !TouchesDigit(word, i)) return true;
        return false;
    }

    private static void DropVowels(StringBuilder word)
    {
        for (var i = word.Length - 1; i >= 1; i--)
            if (IsVowel(word[i]) && !TouchesDigit(word, i)) word.Remove(i, 1);
    }

    /// <summary>The last letter that can go: not the first character, not a digit, not touching one.</summary>
    private static int LastDroppable(StringBuilder word)
    {
        for (var i = word.Length - 1; i >= 1; i--)
            if (char.IsLetter(word[i]) && !TouchesDigit(word, i)) return i;
        return -1;
    }

    private static bool TouchesDigit(StringBuilder word, int index) =>
        (index > 0 && char.IsDigit(word[index - 1])) ||
        (index + 1 < word.Length && char.IsDigit(word[index + 1]));

    private static bool IsVowel(char c) => "aeiouAEIOU".IndexOf(c) >= 0;
}
