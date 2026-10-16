namespace CrystalCode.ApplyPatch.Workspace;

/// <summary>
/// Resolves patch paths against one workspace root. Resolution follows
/// symbolic links to their final target. Credential paths are refused. A
/// path outside the root stays resolved so the host can classify it as an
/// outside-workspace write.
/// </summary>
internal sealed class WorkspaceFence
{
    private const int MaximumLinkHops = 40;

    internal const string CredentialPathError = "editing credential paths is not allowed.";

    private readonly string _root;

    private WorkspaceFence(string root)
    {
        _root = root;
    }

    /// <summary>
    /// Creates a fence for one workspace root.
    /// </summary>
    public static bool TryCreate(
        string workspaceRoot,
        out WorkspaceFence fence,
        out string? error)
    {
        fence = null!;
        error = null;
        if (string.IsNullOrWhiteSpace(workspaceRoot) || !Directory.Exists(workspaceRoot))
        {
            error = "The workspace is unavailable in this host.";
            return false;
        }

        try
        {
            fence = new WorkspaceFence(Canonicalize(Path.GetFullPath(workspaceRoot)));
        }
        catch (Exception exception) when (exception is ArgumentException
            or NotSupportedException
            or IOException)
        {
            error = "The workspace is unavailable in this host.";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Resolves one patch path. A relative path is combined with the root;
    /// an absolute path is used as given.
    /// </summary>
    public bool TryResolve(string path, out string fullPath, out string? error)
    {
        fullPath = "";
        error = null;
        var trimmed = path.Trim();
        if (trimmed.Length == 0)
        {
            error = "the path is empty.";
            return false;
        }

        string candidate;
        try
        {
            var combined = Path.IsPathRooted(trimmed)
                ? trimmed
                : Path.Combine(_root, trimmed);
            candidate = Canonicalize(Path.GetFullPath(combined));
        }
        catch (IOException)
        {
            error = "the path could not be resolved.";
            return false;
        }
        catch (Exception exception) when (exception is ArgumentException
            or NotSupportedException)
        {
            error = "the path is not valid.";
            return false;
        }

        // Credential wins over the workspace boundary. A symlink can resolve
        // to a credential path outside the root, and that must stay forbidden.
        if (IsCredentialPath(trimmed) || IsCredentialPath(candidate))
        {
            error = CredentialPathError;
            return false;
        }

        fullPath = candidate;
        return true;
    }

    /// <summary>
    /// Reports whether a canonical path is inside the workspace root.
    /// </summary>
    public bool Contains(string fullPath) => IsInside(fullPath);

    /// <summary>
    /// Gets the workspace-relative display path with forward slashes.
    /// </summary>
    public string ToRelative(string fullPath)
    {
        var relative = Path.GetRelativePath(_root, fullPath).Replace('\\', '/');
        return relative.Length == 0 ? "." : relative;
    }

    /// <summary>
    /// Reports whether any path segment is <c>.ssh</c>, <c>.gnupg</c>, or a
    /// <c>.crystal/credentials.json</c> pair, matching the host rule.
    /// </summary>
    public static bool IsCredentialPath(string path)
    {
        var normalized = path.Replace('\\', '/').TrimEnd('/');
        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var index = 0; index < segments.Length; index++)
        {
            if (segments[index].Equals(".ssh", StringComparison.OrdinalIgnoreCase)
                || segments[index].Equals(".gnupg", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (index + 1 < segments.Length
                && segments[index].Equals(".crystal", StringComparison.OrdinalIgnoreCase)
                && segments[index + 1].Equals("credentials.json", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private bool IsInside(string fullPath)
    {
        var root = _root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var candidate = fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(candidate, root, StringComparison.Ordinal)
            || candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static string Canonicalize(string fullPath) => Canonicalize(fullPath, hops: 0);

    private static string Canonicalize(string fullPath, int hops)
    {
        var rooted = Path.GetFullPath(fullPath);
        var root = Path.GetPathRoot(rooted);
        if (string.IsNullOrEmpty(root))
        {
            return rooted;
        }

        var current = root;
        var relative = rooted[root.Length..];
        foreach (var segment in relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                var parent = Path.GetDirectoryName(TrimTrailingSeparator(current));
                current = string.IsNullOrEmpty(parent) ? root : parent;
                continue;
            }

            current = Path.Combine(TrimTrailingSeparator(current), segment);
            if (!IsReparsePoint(current))
            {
                continue;
            }

            if (hops >= MaximumLinkHops || !TryImmediateTarget(current, out var target))
            {
                throw new IOException($"Symbolic link could not be resolved: {current}");
            }

            current = Canonicalize(target, hops + 1);
        }

        return Path.GetFullPath(current);
    }

    private static string TrimTrailingSeparator(string path)
    {
        var root = Path.GetPathRoot(path);
        if (root is not null && string.Equals(path, root, StringComparison.Ordinal))
        {
            return path;
        }

        return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static bool IsReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException)
        {
            return false;
        }
    }

    private static bool TryImmediateTarget(string path, out string target)
    {
        target = "";
        try
        {
            var attributes = File.GetAttributes(path);
            var link = (attributes & FileAttributes.Directory) != 0
                ? Directory.ResolveLinkTarget(path, returnFinalTarget: false)
                : File.ResolveLinkTarget(path, returnFinalTarget: false);
            if (link is null)
            {
                return false;
            }

            target = link.FullName;
            return true;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException)
        {
            return false;
        }
    }
}
