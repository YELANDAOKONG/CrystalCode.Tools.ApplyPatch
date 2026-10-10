using System.Text.RegularExpressions;

namespace CrystalCode.ApplyPatch.Patches;

/// <summary>
/// Parses the file-oriented patch language into file sections.
/// </summary>
internal sealed class PatchParser
{
    private const string AddHeader = "*** Add File:";
    private const string DeleteHeader = "*** Delete File:";
    private const string UpdateHeader = "*** Update File:";
    private const string MoveHeader = "*** Move to:";

    private static readonly Regex HeredocPattern = new(
        "^(?:cat\\s+)?<<['\"]?([A-Za-z0-9_]+)['\"]?\\s*\\n([\\s\\S]*?)\\n\\1\\s*$",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    private readonly IReadOnlyList<string> _lines;
    private readonly int _end;
    private int _index;

    private PatchParser(IReadOnlyList<string> lines, int begin, int end)
    {
        _lines = lines;
        _index = begin;
        _end = end;
    }

    /// <summary>
    /// Parses one patch text. Outer whitespace, a heredoc wrapper, and CRLF
    /// line endings are accepted.
    /// </summary>
    public static bool TryParse(
        string patchText,
        out IReadOnlyList<PatchHunk> hunks,
        out string? error)
    {
        var text = StripHeredoc(patchText.Trim());
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var begin = FindMarker(lines, "*** Begin Patch");
        var end = FindMarker(lines, "*** End Patch");
        if (begin == -1 || end == -1 || begin >= end)
        {
            hunks = [];
            error = "Invalid patch format: missing Begin/End markers.";
            return false;
        }

        return new PatchParser(lines, begin + 1, end).TryReadHunks(out hunks, out error);
    }

    private static string StripHeredoc(string text) =>
        HeredocPattern.Match(text) is { Success: true } match
            ? match.Groups[2].Value
            : text;

    private static int FindMarker(IReadOnlyList<string> lines, string marker)
    {
        for (var index = 0; index < lines.Count; index++)
        {
            if (string.Equals(lines[index].Trim(), marker, StringComparison.Ordinal))
            {
                return index;
            }
        }

        return -1;
    }

    private bool TryReadHunks(out IReadOnlyList<PatchHunk> hunks, out string? error)
    {
        var result = new List<PatchHunk>();
        var ok = true;
        error = null;
        while (ok && _index < _end)
        {
            var line = _lines[_index];
            if (line.StartsWith(AddHeader, StringComparison.Ordinal))
            {
                ok = TryReadAddSection(result, out error);
            }
            else if (line.StartsWith(DeleteHeader, StringComparison.Ordinal))
            {
                ok = TryReadDeleteSection(result, out error);
            }
            else if (line.StartsWith(UpdateHeader, StringComparison.Ordinal))
            {
                ok = TryReadUpdateSection(result, out error);
            }
            else
            {
                error = $"Invalid patch line: {line}";
                ok = false;
            }
        }

        hunks = result;
        return ok;
    }

    private bool TryReadAddSection(List<PatchHunk> hunks, out string? error)
    {
        if (!TryReadPath(_lines[_index], AddHeader, "Invalid add file path.", out var path, out error))
        {
            return false;
        }

        _index++;
        var contents = new List<string>();
        while (_index < _end && !_lines[_index].StartsWith("***", StringComparison.Ordinal))
        {
            var line = _lines[_index];
            if (!line.StartsWith('+'))
            {
                error = $"Invalid add file line: {line}";
                return false;
            }

            contents.Add(line[1..]);
            _index++;
        }

        hunks.Add(new PatchAdd(path, string.Join("\n", contents)));
        return true;
    }

    private bool TryReadDeleteSection(List<PatchHunk> hunks, out string? error)
    {
        if (!TryReadPath(_lines[_index], DeleteHeader, "Invalid delete file path.", out var path, out error))
        {
            return false;
        }

        hunks.Add(new PatchDelete(path));
        _index++;
        return true;
    }

    private bool TryReadUpdateSection(List<PatchHunk> hunks, out string? error)
    {
        if (!TryReadPath(_lines[_index], UpdateHeader, "Invalid update file path.", out var path, out error))
        {
            return false;
        }

        _index++;
        string? movePath = null;
        if (_index < _end && _lines[_index].StartsWith(MoveHeader, StringComparison.Ordinal))
        {
            if (!TryReadPath(_lines[_index], MoveHeader, "Invalid move file path.", out var moved, out error))
            {
                return false;
            }

            movePath = moved;
            _index++;
        }

        if (!TryReadChunks(out var chunks, out error))
        {
            return false;
        }

        if (chunks.Count == 0)
        {
            error = $"Expected at least one @@ chunk for {path}.";
            return false;
        }

        hunks.Add(new PatchUpdate(path, movePath, chunks));
        return true;
    }

    private bool TryReadChunks(out List<PatchChunk> chunks, out string? error)
    {
        chunks = [];
        error = null;
        while (_index < _end && !_lines[_index].StartsWith("***", StringComparison.Ordinal))
        {
            var line = _lines[_index];
            if (!line.StartsWith("@@", StringComparison.Ordinal))
            {
                error = $"Invalid update file line: {line}";
                return false;
            }

            if (!TryReadChunk(line[2..].Trim(), out var chunk, out error))
            {
                return false;
            }

            chunks.Add(chunk);
        }

        return true;
    }

    private bool TryReadChunk(
        string changeContext,
        out PatchChunk chunk,
        out string? error)
    {
        chunk = null!;
        error = null;
        var oldLines = new List<string>();
        var newLines = new List<string>();
        var endOfFile = false;
        _index++;
        while (_index < _end && !_lines[_index].StartsWith("@@", StringComparison.Ordinal))
        {
            var line = _lines[_index];
            if (line == "*** End of File")
            {
                endOfFile = true;
                _index++;
                break;
            }

            if (line.StartsWith("***", StringComparison.Ordinal))
            {
                break;
            }

            if (line.StartsWith(' '))
            {
                oldLines.Add(line[1..]);
                newLines.Add(line[1..]);
            }
            else if (line.StartsWith('-'))
            {
                oldLines.Add(line[1..]);
            }
            else if (line.StartsWith('+'))
            {
                newLines.Add(line[1..]);
            }
            else
            {
                error = $"Invalid update chunk line: {line}";
                return false;
            }

            _index++;
        }

        chunk = new PatchChunk(
            oldLines,
            newLines,
            changeContext.Length == 0 ? null : changeContext,
            endOfFile);
        return true;
    }

    private static bool TryReadPath(
        string line,
        string header,
        string emptyError,
        out string path,
        out string? error)
    {
        path = line[header.Length..].Trim();
        if (path.Length == 0)
        {
            error = emptyError;
            return false;
        }

        error = null;
        return true;
    }
}
