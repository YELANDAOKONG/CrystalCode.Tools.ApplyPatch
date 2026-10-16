using CrystalCode.ApplyPatch.Patches;

namespace CrystalCode.ApplyPatch.Tests.Patches;

public sealed class PatchDeriverTests
{
    [Fact]
    public void ReplacesMatchedLines()
    {
        Assert.Equal(
            "line 1\nline 2 updated\nline 3\n",
            Derive("line 1\nline 2\nline 3\n", Chunk(["line 2"], ["line 2 updated"])));
    }

    [Fact]
    public void MatchesIgnoringTrailingWhitespace()
    {
        Assert.Equal("new\n", Derive("old   \n", Chunk(["old"], ["new"])));
    }

    [Fact]
    public void MatchesTrimmedLines()
    {
        Assert.Equal("new\n", Derive("  old  \n", Chunk(["old"], ["new"])));
    }

    [Fact]
    public void MatchesUnicodePunctuation()
    {
        Assert.Equal(
            "done\n",
            Derive(
                "he said \u201chi\u201d \u2014 okay\u2026\n",
                Chunk(["he said \"hi\" - okay..."], ["done"])));
    }

    [Fact]
    public void PreservesTheBom()
    {
        Assert.Equal("\uFEFFnew\n", Derive("\uFEFFold\n", Chunk(["  old   "], ["new"])));
    }

    [Fact]
    public void AddsATrailingNewline()
    {
        Assert.Equal(
            "has newline now\n",
            Derive("no newline", Chunk(["no newline"], ["has newline now"])));
    }

    [Fact]
    public void AppendsAnOnlyAddedChunk()
    {
        Assert.Equal("first\nsecond\n", Derive("first\n", Chunk([], ["second"])));
    }

    [Fact]
    public void KeepsAppendOnlyChunksInPatchOrder()
    {
        Assert.Equal(
            "first\na\nb\n",
            Derive(
                "first\n",
                Chunk([], ["a"]),
                Chunk([], ["b"])));
    }

    [Fact]
    public void AppendsToAnEmptyFile()
    {
        Assert.Equal("first line\n", Derive("", Chunk([], ["first line"])));
    }

    [Fact]
    public void PreservesCrlfLineEndings()
    {
        Assert.Equal(
            "line 1\r\nline 2 updated\r\nline 3\r\n",
            Derive(
                "line 1\r\nline 2\r\nline 3\r\n",
                Chunk(["line 2"], ["line 2 updated"])));
    }

    [Fact]
    public void MatchesEndOfFileChunksFromTheEnd()
    {
        Assert.Equal(
            "marker\nmiddle\nmarker changed\nend\n",
            Derive(
                "marker\nmiddle\nmarker\nend\n",
                Chunk(["marker", "end"], ["marker changed", "end"], endOfFile: true)));
    }

    [Fact]
    public void AnchorsEndOfFileChunksWhenTheEndMatchesLoosely()
    {
        Assert.Equal(
            "marker\nend\nmarker\nchanged\n",
            Derive(
                "marker\nend\nmarker\nend   \n",
                Chunk(["marker", "end"], ["marker", "changed"], endOfFile: true)));
    }

    [Fact]
    public void DoesNotUseAnEarlierMatchForAnEndOfFileChunk()
    {
        Assert.Equal(
            "the expected lines were not found:\nmarker\nend",
            DeriveError(
                "marker\nend\nother\n",
                Chunk(["marker", "end"], ["marker", "changed"], endOfFile: true)));
    }

    [Fact]
    public void AppliesChunksAfterTheirChangeContext()
    {
        Assert.Equal(
            "context\nnew\n",
            Derive("context\nold\n", Chunk(["old"], ["new"], context: "context")));
    }

    [Fact]
    public void AppliesMultipleChunksInOrder()
    {
        Assert.Equal(
            "line 1\nLINE 2\nline 3\nLINE 4\n",
            Derive(
                "line 1\nline 2\nline 3\nline 4\n",
                Chunk(["line 1", "line 2"], ["line 1", "LINE 2"]),
                Chunk(["line 3", "line 4"], ["line 3", "LINE 4"])));
    }

    [Fact]
    public void MatchesWhenTheChunkEndsWithAnEmptyOldLine()
    {
        Assert.Equal("b\n", Derive("a\n", Chunk(["a", ""], ["b"])));
    }

    [Fact]
    public void ReportsAMissingContext()
    {
        Assert.Equal(
            "the change context 'section' was not found.",
            DeriveError("old\n", Chunk(["old"], ["new"], context: "section")));
    }

    [Fact]
    public void ReportsMissingExpectedLines()
    {
        Assert.Equal(
            "the expected lines were not found:\nmissing",
            DeriveError("other\n", Chunk(["missing"], ["new"])));
    }

    private static string Derive(string original, params PatchChunk[] chunks)
    {
        Assert.True(PatchDeriver.TryDerive(chunks, original, out var updated, out var error), error);
        return updated;
    }

    private static string DeriveError(string original, params PatchChunk[] chunks)
    {
        Assert.False(PatchDeriver.TryDerive(chunks, original, out _, out var error));
        return error!;
    }

    private static PatchChunk Chunk(
        string[] oldLines,
        string[] newLines,
        string? context = null,
        bool endOfFile = false) =>
        new(oldLines, newLines, context, endOfFile);
}
