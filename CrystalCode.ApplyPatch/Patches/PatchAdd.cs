namespace CrystalCode.ApplyPatch.Patches;

/// <summary>
/// A section that creates one file.
/// </summary>
internal sealed record PatchAdd(string Path, string Contents) : PatchHunk(Path);
