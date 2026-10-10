namespace CrystalCode.ApplyPatch.Patches;

/// <summary>
/// A verified Update section. <paramref name="Original"/> is the exact file
/// content read during verification and <paramref name="Updated"/> is the
/// derived text, including a leading BOM when the file had one.
/// </summary>
internal sealed record PreparedUpdate(
    string Path,
    string ShownPath,
    byte[] Original,
    string Updated) : PreparedChange(Path, ShownPath)
{
    /// <inheritdoc />
    public override char Letter => 'M';
}
