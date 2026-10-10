namespace CrystalCode.ApplyPatch;

/// <summary>
/// Bounds for one patch application.
/// </summary>
internal static class PatchLimits
{
    /// <summary>
    /// Gets the maximum bytes of contents one Add section may create. It
    /// matches the host write limit.
    /// </summary>
    public const int MaximumAddedBytes = 2 * 1024 * 1024;

    /// <summary>
    /// Gets the number of leading bytes probed for a null byte before a file
    /// counts as binary.
    /// </summary>
    public const int BinaryProbeBytes = 8192;
}
