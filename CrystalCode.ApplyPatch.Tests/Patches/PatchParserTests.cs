using CrystalCode.ApplyPatch.Patches;

namespace CrystalCode.ApplyPatch.Tests.Patches;

public sealed class PatchParserTests
{
    [Fact]
    public void ParsesAddUpdateAndDeleteSections()
    {
        var hunks = Parse(
            """
            *** Begin Patch
            *** Add File: add.txt
            +added
            *** Update File: update.txt
            @@ section
            -old
            +new
            *** Delete File: delete.txt
            *** End Patch
            """);

        Assert.Collection(
            hunks,
            hunk =>
            {
                var add = Assert.IsType<PatchAdd>(hunk);
                Assert.Equal("add.txt", add.Path);
                Assert.Equal("added", add.Contents);
            },
            hunk =>
            {
                var update = Assert.IsType<PatchUpdate>(hunk);
                Assert.Equal("update.txt", update.Path);
                Assert.Null(update.MovePath);
                var chunk = Assert.Single(update.Chunks);
                Assert.Equal(["old"], chunk.OldLines);
                Assert.Equal(["new"], chunk.NewLines);
                Assert.Equal("section", chunk.ChangeContext);
                Assert.False(chunk.EndOfFile);
            },
            hunk => Assert.Equal("delete.txt", Assert.IsType<PatchDelete>(hunk).Path));
    }

    [Fact]
    public void StripsAHeredocWrapper()
    {
        var hunks = Parse("cat <<'EOF'\n*** Begin Patch\n*** Add File: add.txt\n+added\n*** End Patch\nEOF");

        var add = Assert.IsType<PatchAdd>(Assert.Single(hunks));
        Assert.Equal("added", add.Contents);
    }

    [Fact]
    public void AcceptsCrlfLineEndings()
    {
        var hunks = Parse("*** Begin Patch\r\n*** Add File: add.txt\r\n+first\r\n+second\r\n*** End Patch");

        var add = Assert.IsType<PatchAdd>(Assert.Single(hunks));
        Assert.Equal("first\nsecond", add.Contents);
    }

    [Fact]
    public void ParsesAMoveTarget()
    {
        var hunks = Parse(
            """
            *** Begin Patch
            *** Update File: old.txt
            *** Move to: new.txt
            @@
            -old
            +new
            *** End Patch
            """);

        var update = Assert.IsType<PatchUpdate>(Assert.Single(hunks));
        Assert.Equal("old.txt", update.Path);
        Assert.Equal("new.txt", update.MovePath);
    }

    [Fact]
    public void ParsesTheEndOfFileMarker()
    {
        var hunks = Parse(
            """
            *** Begin Patch
            *** Update File: update.txt
            @@
            -last
            +end
            *** End of File
            *** End Patch
            """);

        var update = Assert.IsType<PatchUpdate>(Assert.Single(hunks));
        var chunk = Assert.Single(update.Chunks);
        Assert.True(chunk.EndOfFile);
        Assert.Equal(["last"], chunk.OldLines);
        Assert.Equal(["end"], chunk.NewLines);
    }

    [Fact]
    public void ParsesMultipleChunksInOneSection()
    {
        var hunks = Parse(
            """
            *** Begin Patch
            *** Update File: update.txt
            @@ first
             line
            -old
            +new
            @@
            +appended
            *** End Patch
            """);

        var update = Assert.IsType<PatchUpdate>(Assert.Single(hunks));
        Assert.Equal(2, update.Chunks.Count);
        Assert.Equal("first", update.Chunks[0].ChangeContext);
        Assert.Equal(["line", "old"], update.Chunks[0].OldLines);
        Assert.Equal(["line", "new"], update.Chunks[0].NewLines);
        Assert.Null(update.Chunks[1].ChangeContext);
        Assert.Empty(update.Chunks[1].OldLines);
        Assert.Equal(["appended"], update.Chunks[1].NewLines);
    }

    [Fact]
    public void ParsesAnEmptyAddSection()
    {
        var hunks = Parse("*** Begin Patch\n*** Add File: empty.txt\n*** End Patch");

        var add = Assert.IsType<PatchAdd>(Assert.Single(hunks));
        Assert.Equal("", add.Contents);
    }

    [Fact]
    public void ParsesAnEmptyPatch()
    {
        Assert.True(
            PatchParser.TryParse("*** Begin Patch\n*** End Patch", out var hunks, out var error),
            error);
        Assert.Empty(hunks);
    }

    [Fact]
    public void RejectsMissingMarkers()
    {
        Assert.Equal(
            "Invalid patch format: missing Begin/End markers.",
            ParseError("*** Add File: add.txt\n+added"));
    }

    [Fact]
    public void RejectsAWhitespaceOnlyPatch()
    {
        Assert.Equal(
            "Invalid patch format: missing Begin/End markers.",
            ParseError("   "));
    }

    [Fact]
    public void RejectsAnAddLineWithoutPlus()
    {
        Assert.Equal(
            "Invalid add file line: missing plus",
            ParseError("*** Begin Patch\n*** Add File: add.txt\nmissing plus\n*** End Patch"));
    }

    [Fact]
    public void RejectsAnEmptyPath()
    {
        Assert.Equal(
            "Invalid add file path.",
            ParseError("*** Begin Patch\n*** Add File:   \n*** End Patch"));
    }

    [Fact]
    public void RejectsAnUpdateWithoutChunks()
    {
        Assert.Equal(
            "Expected at least one @@ chunk for update.txt.",
            ParseError("*** Begin Patch\n*** Update File: update.txt\n*** End Patch"));
    }

    [Fact]
    public void RejectsAnUnknownSection()
    {
        Assert.Equal(
            "Invalid patch line: *** Rename File: file.txt",
            ParseError("*** Begin Patch\n*** Rename File: file.txt\n*** End Patch"));
    }

    [Fact]
    public void RejectsABadUpdateLine()
    {
        Assert.Equal(
            "Invalid update file line: plain",
            ParseError("*** Begin Patch\n*** Update File: update.txt\nplain\n*** End Patch"));
    }

    [Fact]
    public void RejectsABadChunkLine()
    {
        Assert.Equal(
            "Invalid update chunk line: plain",
            ParseError("*** Begin Patch\n*** Update File: update.txt\n@@\nplain\n*** End Patch"));
    }

    private static IReadOnlyList<PatchHunk> Parse(string patchText)
    {
        Assert.True(PatchParser.TryParse(patchText, out var hunks, out var error), error);
        return hunks;
    }

    private static string ParseError(string patchText)
    {
        Assert.False(PatchParser.TryParse(patchText, out _, out var error));
        return error!;
    }
}
