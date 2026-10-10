namespace CrystalCode.ApplyPatch.Patches;

/// <summary>
/// A verified Add section.
/// </summary>
internal sealed record PreparedAdd(string Path, string ShownPath, string Contents)
    : PreparedChange(Path, ShownPath)
{
    /// <inheritdoc />
    public override char Letter => 'A';
}
