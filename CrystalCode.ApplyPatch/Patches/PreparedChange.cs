namespace CrystalCode.ApplyPatch.Patches;

/// <summary>
/// One verified file change ready to commit.
/// </summary>
internal abstract record PreparedChange(string Path, string ShownPath)
{
    /// <summary>
    /// Gets the patch summary letter: A, M, or D.
    /// </summary>
    public abstract char Letter { get; }
}
