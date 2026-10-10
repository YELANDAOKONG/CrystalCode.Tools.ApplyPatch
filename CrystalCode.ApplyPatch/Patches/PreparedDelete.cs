namespace CrystalCode.ApplyPatch.Patches;

/// <summary>
/// A verified Delete section.
/// </summary>
internal sealed record PreparedDelete(string Path, string ShownPath)
    : PreparedChange(Path, ShownPath)
{
    /// <inheritdoc />
    public override char Letter => 'D';
}
