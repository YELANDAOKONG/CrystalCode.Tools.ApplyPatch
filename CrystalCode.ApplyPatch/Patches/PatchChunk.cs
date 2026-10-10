namespace CrystalCode.ApplyPatch.Patches;

/// <summary>
/// One @@ chunk of an update section.
/// </summary>
internal sealed record PatchChunk(
    IReadOnlyList<string> OldLines,
    IReadOnlyList<string> NewLines,
    string? ChangeContext,
    bool EndOfFile);
