using System.Text;

namespace NdiRouter.Videohub;

/// <summary>One block: its header line ("VIDEO OUTPUT ROUTING:") and the lines after it.</summary>
/// <param name="TooLong">More lines arrived than a block may hold; the extra ones were dropped.</param>
public sealed record VideohubBlock(string Header, IReadOnlyList<string> Lines, bool TooLong = false);

/// <summary>
/// Turns a Videohub TCP stream into blocks. A block is a header line, any number of lines, then
/// a blank line. Lines end in a line feed, a carriage return, or both (the spec allows either),
/// and are UTF-8.
/// </summary>
public sealed class VideohubReader
{
    /// <summary>A line longer than this is dropped; no real line comes close.</summary>
    public const int MaxLineBytes = 16 * 1024;

    /// <summary>A block with more lines than this is cut short; the router's largest has about 1000.</summary>
    public const int MaxLines = 4096;

    private readonly List<byte> _line = new();
    private readonly List<string> _lines = new();
    private bool _afterCarriageReturn;
    private bool _lineTooLong;
    private bool _blockTooLong;
    private string? _header;

    public List<VideohubBlock> Feed(ReadOnlySpan<byte> data)
    {
        var blocks = new List<VideohubBlock>();
        foreach (var b in data)
        {
            // CR LF is one line end, not a line end and then an empty line.
            if (b == '\n' && _afterCarriageReturn)
            {
                _afterCarriageReturn = false;
                continue;
            }
            _afterCarriageReturn = b == '\r';

            if (b is (byte)'\r' or (byte)'\n')
            {
                var text = _lineTooLong ? null : Encoding.UTF8.GetString(_line.ToArray());
                _line.Clear();
                _lineTooLong = false;
                if (text is not null && OnLine(text) is { } block) blocks.Add(block);
                continue;
            }

            if (_line.Count < MaxLineBytes) _line.Add(b);
            else _lineTooLong = true;
        }

        return blocks;
    }

    private VideohubBlock? OnLine(string line)
    {
        if (line.Trim().Length == 0)
        {
            if (_header is null) return null; // blank lines between blocks
            var block = new VideohubBlock(_header, _lines.ToList(), _blockTooLong);
            _header = null;
            _lines.Clear();
            _blockTooLong = false;
            return block;
        }

        if (_header is null)
            _header = line.Trim();
        else if (_lines.Count < MaxLines)
            _lines.Add(line);
        else
            _blockTooLong = true;

        return null;
    }
}
