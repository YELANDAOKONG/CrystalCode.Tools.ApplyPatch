namespace CrystalCode.ApplyPatch.Patches;

/// <summary>
/// A section that updates one file, optionally with a rename.
/// </summary>
internal sealed record PatchUpdate(
    string Path,
    string? MovePath,
    IReadOnlyList<PatchChunk> Chunks) : PatchHunk(Path);
