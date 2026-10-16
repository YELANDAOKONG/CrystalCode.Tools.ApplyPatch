using System.Text;
using System.Text.Json;

using Crystal.Tools;

using CrystalCode.ApplyPatch.Patches;
using CrystalCode.ApplyPatch.Workspace;
using CrystalCode.Tools;

namespace CrystalCode.ApplyPatch.Tools;

/// <summary>
/// Applies one file-oriented patch inside the workspace.
/// </summary>
public sealed class ApplyPatchTool : IHostTool, IHostToolApprovalClassifier
{
    private const string ToolName = "apply_patch";

    private static readonly Encoding StrictUtf8 = new UTF8Encoding(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private const string ToolDescription =
        "Apply one patch containing add, update, and delete file operations. "
        + "The patch text starts with *** Begin Patch and ends with *** End Patch; "
        + "each section starts with *** Add File: <path>, *** Update File: <path>, or *** Delete File: <path>. "
        + "An Add section lists the initial contents as + lines. "
        + "An Update section contains @@ chunks; context lines start with a space, removed lines with -, and added lines with +. "
        + "A chunk's context and removed lines are matched exactly first, then ignoring trailing whitespace, then trimmed, then ignoring Unicode quote, dash, and ellipsis differences. "
        + "A Delete section removes one existing file. "
        + "Paths are workspace-relative or absolute. "
        + "A path inside the workspace is a workspace write. "
        + "A path outside it is an outside-workspace write and follows host approval. "
        + "Credential paths are refused. "
        + "Update and Delete require an existing regular file; Add refuses an existing file and creates parent directories. "
        + "The whole patch is verified before any file changes; sections then apply in order, and a failure reports the sections already applied. "
        + "Moves are not supported. Example:\n"
        + "*** Begin Patch\n"
        + "*** Add File: hello.txt\n"
        + "+Hello world\n"
        + "*** Update File: src/app.py\n"
        + "@@ def greet():\n"
        + "-print(\"Hi\")\n"
        + "+print(\"Hello, world!\")\n"
        + "*** Delete File: obsolete.txt\n"
        + "*** End Patch";

    private static readonly ToolDefinition Tool = CreateTool();

    /// <inheritdoc />
    public ToolDefinition Definition => Tool;

    /// <inheritdoc />
    public ValueTask<HostToolApprovalClassification> ClassifyApprovalAsync(
        ToolCall call,
        ToolHostContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(ClassifyApproval(call, context));
    }

    /// <inheritdoc />
    public HostToolApprovalClassification ClassifyApproval(
        ToolCall call,
        ToolHostContext context)
    {
        ArgumentNullException.ThrowIfNull(call);
        ArgumentNullException.ThrowIfNull(context);

        if (!ApplyPatchArguments.TryRead(call.Arguments, out var patchText, out _)
            || !PatchParser.TryParse(patchText, out var hunks, out _)
            || hunks.Count == 0)
        {
            return new HostToolApprovalClassification(
                "privileged",
                "privilegedEscalation",
                "apply_patch could not classify the patch",
                RequirePrompt: true);
        }

        if (!WorkspaceFence.TryCreate(context.WorkspaceRoot, out var fence, out _))
        {
            return new HostToolApprovalClassification(
                "privileged",
                "privilegedEscalation",
                "apply_patch workspace could not be classified",
                RequirePrompt: true);
        }

        foreach (var hunk in hunks)
        {
            var pathClassification = ClassifyPath(fence, hunk.Path);
            if (pathClassification is not null)
            {
                return pathClassification;
            }

            if (hunk is PatchUpdate { MovePath: not null } move)
            {
                pathClassification = ClassifyPath(fence, move.MovePath);
                if (pathClassification is not null)
                {
                    return pathClassification;
                }
            }
        }

        return new HostToolApprovalClassification(
            "write",
            "workspace",
            "Apply patch inside workspace");
    }

    /// <inheritdoc />
    public async ValueTask<ToolOutput> InvokeAsync(
        ToolCall call,
        ToolHostContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(call);
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        if (!ApplyPatchArguments.TryRead(call.Arguments, out var patchText, out var error))
        {
            return Failure(error ?? "Tool arguments are not valid.");
        }

        if (!PatchParser.TryParse(patchText, out var hunks, out error))
        {
            return Failure($"apply_patch verification failed: {error}");
        }

        if (hunks.Count == 0)
        {
            return Failure("patch rejected: empty patch.");
        }

        foreach (var hunk in hunks)
        {
            if (hunk is PatchUpdate { MovePath: not null })
            {
                return Failure("apply_patch moves are not supported yet.");
            }
        }

        if (!WorkspaceFence.TryCreate(context.WorkspaceRoot, out var fence, out error))
        {
            return Failure(error ?? "The workspace is unavailable in this host.");
        }

        var changes = new List<PreparedChange>(hunks.Count);
        var updatedPaths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var hunk in hunks)
        {
            var (change, prepareError) = await PrepareAsync(fence, hunk, cancellationToken)
                .ConfigureAwait(false);
            if (prepareError is not null)
            {
                return Failure(prepareError);
            }

            if (change is PreparedUpdate && !updatedPaths.Add(change.Path))
            {
                return Failure(
                    $"Unable to apply patch at {change.ShownPath}: the file is updated more than once in this patch.");
            }

            changes.Add(change!);
        }

        return await CommitAsync(changes, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<(PreparedChange? Change, string? Error)> PrepareAsync(
        WorkspaceFence fence,
        PatchHunk hunk,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var shown = hunk.Path.Trim();
        if (!fence.TryResolve(hunk.Path, out var path, out var pathError))
        {
            return (null, $"Unable to apply patch at {shown}: {pathError}");
        }

        shown = fence.Contains(path) ? fence.ToRelative(path) : path;
        switch (hunk)
        {
            case PatchAdd add:
            {
                var contents = add.Contents.Length == 0 || add.Contents.EndsWith('\n')
                    ? add.Contents
                    : add.Contents + "\n";
                if (Encoding.UTF8.GetByteCount(contents) > PatchLimits.MaximumAddedBytes)
                {
                    return (
                        null,
                        $"Unable to apply patch at {shown}: the added contents exceed {PatchLimits.MaximumAddedBytes} bytes.");
                }

                return (new PreparedAdd(path, shown, contents), null);
            }

            case PatchDelete:
            {
                if (Directory.Exists(path))
                {
                    return (null, $"Unable to apply patch at {shown}: the path is a directory.");
                }

                if (!File.Exists(path))
                {
                    return (null, $"Unable to apply patch at {shown}: the file was not found.");
                }

                return (new PreparedDelete(path, shown), null);
            }

            case PatchUpdate update:
            {
                if (Directory.Exists(path))
                {
                    return (null, $"Unable to apply patch at {shown}: the path is a directory.");
                }

                if (!File.Exists(path))
                {
                    return (null, $"Unable to apply patch at {shown}: the file was not found.");
                }

                byte[] original;
                try
                {
                    original = await File.ReadAllBytesAsync(path, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is IOException
                    or UnauthorizedAccessException)
                {
                    return (null, $"Unable to apply patch at {shown}: the file could not be read.");
                }

                if (LooksBinary(original))
                {
                    return (null, $"Unable to apply patch at {shown}: the file looks binary and will not be edited.");
                }

                string decoded;
                try
                {
                    decoded = StrictUtf8.GetString(original);
                }
                catch (DecoderFallbackException)
                {
                    return (
                        null,
                        $"Unable to apply patch at {shown}: the file is not valid UTF-8 and will not be edited.");
                }

                if (!PatchDeriver.TryDerive(
                        update.Chunks,
                        decoded,
                        out var updated,
                        out var deriveError))
                {
                    return (null, $"Unable to apply patch at {shown}: {deriveError}");
                }

                return (new PreparedUpdate(path, shown, original, updated), null);
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(hunk));
        }
    }

    private static HostToolApprovalClassification? ClassifyPath(
        WorkspaceFence fence,
        string path)
    {
        if (WorkspaceFence.IsCredentialPath(path))
        {
            return CredentialClassification();
        }

        if (!fence.TryResolve(path, out var fullPath, out var error))
        {
            return error == WorkspaceFence.CredentialPathError
                ? CredentialClassification()
                : OutsideClassification();
        }

        return fence.Contains(fullPath) ? null : OutsideClassification();
    }

    private static HostToolApprovalClassification OutsideClassification() =>
        new(
            "write",
            "outsideWorkspace",
            "Apply patch outside workspace");

    private static async Task<ToolOutput> CommitAsync(
        IReadOnlyList<PreparedChange> changes,
        CancellationToken cancellationToken)
    {
        var applied = new List<string>(changes.Count);
        foreach (var change in changes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            switch (change)
            {
                case PreparedAdd add:
                {
                    if (File.Exists(add.Path))
                    {
                        return Partial(add, applied, "the file already exists.");
                    }

                    try
                    {
                        var directory = Path.GetDirectoryName(add.Path);
                        if (!string.IsNullOrEmpty(directory))
                        {
                            Directory.CreateDirectory(directory);
                        }

                        await WriteNewAsync(
                                add.Path,
                                Encoding.UTF8.GetBytes(add.Contents),
                                cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (Exception exception) when (exception is IOException
                        or UnauthorizedAccessException)
                    {
                        return Partial(add, applied, "the file could not be written.");
                    }

                    applied.Add(add.ShownPath);
                    break;
                }

                case PreparedUpdate update:
                {
                    byte[] current;
                    try
                    {
                        current = await File.ReadAllBytesAsync(update.Path, cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (Exception exception) when (exception is IOException
                        or UnauthorizedAccessException)
                    {
                        return Partial(update, applied, "the file could not be read.");
                    }

                    if (!current.AsSpan().SequenceEqual(update.Original))
                    {
                        return Partial(update, applied, "the file changed while the patch was being applied.");
                    }

                    try
                    {
                        await File.WriteAllBytesAsync(
                                update.Path,
                                Encoding.UTF8.GetBytes(update.Updated),
                                cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (Exception exception) when (exception is IOException
                        or UnauthorizedAccessException)
                    {
                        return Partial(update, applied, "the file could not be written.");
                    }

                    applied.Add(update.ShownPath);
                    break;
                }

                case PreparedDelete delete:
                {
                    try
                    {
                        File.Delete(delete.Path);
                    }
                    catch (Exception exception) when (exception is IOException
                        or UnauthorizedAccessException)
                    {
                        return Partial(delete, applied, "the file could not be deleted.");
                    }

                    applied.Add(delete.ShownPath);
                    break;
                }

                default:
                    throw new ArgumentOutOfRangeException(nameof(changes));
            }
        }

        var builder = new StringBuilder("Applied patch sequentially:");
        foreach (var change in changes)
        {
            builder.Append('\n').Append(change.Letter).Append(' ').Append(change.ShownPath);
        }

        return new ToolOutput(builder.ToString());
    }

    private static async Task WriteNewAsync(
        string path,
        byte[] bytes,
        CancellationToken cancellationToken)
    {
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            Options = FileOptions.Asynchronous
        };
        await using var stream = new FileStream(path, options);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
    }

    private static HostToolApprovalClassification CredentialClassification() =>
        new(
            "forbidden",
            "privilegedEscalation",
            "apply_patch credential path");

    private static bool LooksBinary(byte[] bytes)
    {
        var length = Math.Min(bytes.Length, PatchLimits.BinaryProbeBytes);
        for (var index = 0; index < length; index++)
        {
            if (bytes[index] == 0)
            {
                return true;
            }
        }

        return false;
    }

    private static ToolOutput Partial(
        PreparedChange change,
        IReadOnlyList<string> applied,
        string detail) =>
        applied.Count == 0
            ? Failure($"Unable to apply patch at {change.ShownPath}: {detail}")
            : Failure(
                $"Patch partially applied before failing at {change.ShownPath}: {detail} "
                + $"Applied: {string.Join(", ", applied)}.");

    private static ToolOutput Failure(string message) =>
        new(message, ToolResultStatus.Failure);

    private static ToolDefinition CreateTool()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "type": "object",
              "properties": {
                "patchText": {
                  "type": "string",
                  "description": "The full patch text describing Add, Update, and Delete file operations."
                }
              },
              "required": ["patchText"],
              "additionalProperties": false
            }
            """);
        return new ToolDefinition(
            ToolName,
            document.RootElement.Clone(),
            ToolDescription);
    }
}
