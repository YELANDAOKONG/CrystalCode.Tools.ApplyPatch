namespace CrystalCode.ApplyPatch.Patches;

/// <summary>
/// A section that removes one file.
/// </summary>
internal sealed record PatchDelete(string Path) : PatchHunk(Path);
