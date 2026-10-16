using System.Text;

namespace CrystalCode.ApplyPatch.Patches;

/// <summary>
/// Derives updated file text from update chunks. Lines are matched exactly
/// first, then ignoring trailing whitespace, then trimmed, then ignoring
/// Unicode quote, dash, space, and ellipsis differences.
/// </summary>
internal static class PatchDeriver
{
    private static readonly Func<string, string, bool>[] Comparers =
    [
        static (left, right) => left == right,
        static (left, right) => left.TrimEnd() == right.TrimEnd(),
        static (left, right) => left.Trim() == right.Trim(),
        static (left, right) => Normalize(left.Trim()) == Normalize(right.Trim())
    ];

    /// <summary>
    /// Applies every chunk to <paramref name="original"/>. The result keeps a
    /// leading BOM, keeps CRLF when the file uses it, and ends with a newline
    /// unless the file is empty.
    /// </summary>
    public static bool TryDerive(
        IReadOnlyList<PatchChunk> chunks,
        string original,
        out string updated,
        out string? error)
    {
        updated = "";
        var bom = original.StartsWith('\uFEFF');
        var body = bom ? original[1..] : original;
        // TrimEnd treats CR as whitespace, so matching on raw CRLF lines would
        // rewrite only the edited region as LF. Strip CR first, then join with
        // the file's own newline.
        var newline = body.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var normalized = body.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var lines = normalized.Split('\n').ToList();
        if (lines.Count > 0 && lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        if (!TryComputeReplacements(lines, chunks, out var replacements, out error))
        {
            return false;
        }

        var ordered = replacements.OrderBy(static replacement => replacement.Start).ToList();
        for (var index = ordered.Count - 1; index >= 0; index--)
        {
            var replacement = ordered[index];
            lines.RemoveRange(replacement.Start, replacement.Remove);
            lines.InsertRange(replacement.Start, replacement.Insert);
        }

        if (lines.Count == 0 || lines[^1].Length != 0)
        {
            lines.Add("");
        }

        var text = string.Join(newline, lines);
        var nextBom = text.StartsWith('\uFEFF');
        updated = bom || nextBom
            ? "\uFEFF" + (nextBom ? text[1..] : text)
            : text;
        error = null;
        return true;
    }

    private static bool TryComputeReplacements(
        List<string> lines,
        IReadOnlyList<PatchChunk> chunks,
        out List<Replacement> replacements,
        out string? error)
    {
        replacements = [];
        error = null;
        var lineIndex = 0;
        foreach (var chunk in chunks)
        {
            if (chunk.ChangeContext is { } context)
            {
                var contextIndex = Seek(lines, [context], lineIndex, endOfFile: false);
                if (contextIndex == -1)
                {
                    error = $"the change context '{context}' was not found.";
                    return false;
                }

                lineIndex = contextIndex + 1;
            }

            if (chunk.OldLines.Count == 0)
            {
                replacements.Add(new Replacement(lines.Count, 0, chunk.NewLines));
                continue;
            }

            var oldLines = chunk.OldLines;
            var newLines = chunk.NewLines;
            var found = Seek(lines, oldLines, lineIndex, chunk.EndOfFile);
            if (found == -1 && oldLines[^1].Length == 0)
            {
                oldLines = oldLines.Take(oldLines.Count - 1).ToArray();
                if (newLines.Count > 0 && newLines[^1].Length == 0)
                {
                    newLines = newLines.Take(newLines.Count - 1).ToArray();
                }

                found = Seek(lines, oldLines, lineIndex, chunk.EndOfFile);
            }

            if (found == -1)
            {
                error = $"the expected lines were not found:\n{string.Join("\n", chunk.OldLines)}";
                return false;
            }

            replacements.Add(new Replacement(found, oldLines.Count, newLines));
            lineIndex = found + oldLines.Count;
        }

        return true;
    }

    private static int Seek(
        List<string> lines,
        IReadOnlyList<string> pattern,
        int start,
        bool endOfFile)
    {
        if (pattern.Count == 0)
        {
            return -1;
        }

        foreach (var compare in Comparers)
        {
            if (endOfFile)
            {
                var offset = lines.Count - pattern.Count;
                if (offset >= start && Matches(lines, pattern, offset, compare))
                {
                    return offset;
                }

                // An end-of-file chunk is anchored. A looser match at the end
                // must win over an exact match earlier in the file.
                continue;
            }

            for (var offset = start; offset <= lines.Count - pattern.Count; offset++)
            {
                if (Matches(lines, pattern, offset, compare))
                {
                    return offset;
                }
            }
        }

        return -1;
    }

    private static bool Matches(
        List<string> lines,
        IReadOnlyList<string> pattern,
        int offset,
        Func<string, string, bool> compare)
    {
        for (var index = 0; index < pattern.Count; index++)
        {
            if (!compare(lines[offset + index], pattern[index]))
            {
                return false;
            }
        }

        return true;
    }

    private static string Normalize(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            switch (character)
            {
                case '\u2018' or '\u2019' or '\u201a' or '\u201b':
                    builder.Append('\'');
                    break;
                case '\u201c' or '\u201d' or '\u201e' or '\u201f':
                    builder.Append('"');
                    break;
                case '\u2010' or '\u2011' or '\u2012' or '\u2013' or '\u2014' or '\u2015':
                    builder.Append('-');
                    break;
                case '\u2212':
                    builder.Append('-');
                    break;
                case '\u2026':
                    builder.Append("...");
                    break;
                case '\u00a0'
                    or '\u2002' or '\u2003' or '\u2004' or '\u2005' or '\u2006'
                    or '\u2007' or '\u2008' or '\u2009' or '\u200a'
                    or '\u202f' or '\u205f' or '\u3000':
                    builder.Append(' ');
                    break;
                default:
                    builder.Append(character);
                    break;
            }
        }

        return builder.ToString();
    }

    private sealed record Replacement(int Start, int Remove, IReadOnlyList<string> Insert);
}
