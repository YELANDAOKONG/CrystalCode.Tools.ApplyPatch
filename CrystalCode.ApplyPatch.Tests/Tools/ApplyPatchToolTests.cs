using System.Text;
using System.Text.Json;

using Crystal.Tools;

using CrystalCode.ApplyPatch.Tools;
using CrystalCode.Tools;

namespace CrystalCode.ApplyPatch.Tests.Tools;

public sealed class ApplyPatchToolTests
{
    [Fact]
    public void ReportsItsDefinition()
    {
        var definition = new ApplyPatchTool().Definition;

        Assert.Equal("apply_patch", definition.Name);
        Assert.Contains(
            definition.InputSchema.GetProperty("required").EnumerateArray(),
            item => item.GetString() == "patchText");
        Assert.NotNull(definition.Description);
        Assert.Contains("*** Begin Patch", definition.Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ClassifyApprovalAsync_ClassifiesThroughHostInterface()
    {
        IHostToolApprovalClassifier classifier = new ApplyPatchTool();
        var patchText = "*** Begin Patch\n*** Add File: nested/new.txt\n+created\n*** End Patch";
        var arguments = JsonSerializer.Serialize(new { patchText });

        var classification = await classifier.ClassifyApprovalAsync(
            new ToolCall("1", "apply_patch", arguments),
            Context(Path.GetTempPath()));

        Assert.Equal("write", classification.Risk);
        Assert.Equal("workspace", classification.Authority);
        Assert.False(classification.RequirePrompt);
    }

    [Fact]
    public async Task ClassifyApprovalAsync_CanceledToken_ThrowsOperationCanceledException()
    {
        IHostToolApprovalClassifier classifier = new ApplyPatchTool();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await classifier.ClassifyApprovalAsync(
                new ToolCall("1", "apply_patch", "{}"),
                Context(Path.GetTempPath()),
                cancellation.Token));
    }

    [Fact]
    public void ClassifiesWorkspacePatchAsWorkspaceWrite()
    {
        var patchText = "*** Begin Patch\n*** Add File: nested/new.txt\n+created\n*** End Patch";
        var arguments = JsonSerializer.Serialize(new { patchText });
        var classification = new ApplyPatchTool().ClassifyApproval(
            new ToolCall("1", "apply_patch", arguments),
            Context(Path.GetTempPath()));

        Assert.Equal("write", classification.Risk);
        Assert.Equal("workspace", classification.Authority);
        Assert.False(classification.RequirePrompt);
    }

    [Fact]
    public void ClassifiesInputPatchAsWorkspaceWrite()
    {
        var input = "*** Begin Patch\n*** Add File: nested/new.txt\n+created\n*** End Patch";
        var arguments = JsonSerializer.Serialize(new { input });
        var classification = new ApplyPatchTool().ClassifyApproval(
            new ToolCall("1", "apply_patch", arguments),
            Context(Path.GetTempPath()));

        Assert.Equal("write", classification.Risk);
        Assert.Equal("workspace", classification.Authority);
        Assert.False(classification.RequirePrompt);
    }

    [Fact]
    public void ClassifiesOutsidePatchAsOutsideWorkspaceWrite()
    {
        var patchText = "*** Begin Patch\n*** Add File: ../outside.txt\n+created\n*** End Patch";
        var arguments = JsonSerializer.Serialize(new { patchText });
        var classification = new ApplyPatchTool().ClassifyApproval(
            new ToolCall("1", "apply_patch", arguments),
            Context(Path.GetTempPath()));

        Assert.Equal("write", classification.Risk);
        Assert.Equal("outsideWorkspace", classification.Authority);
    }

    [Fact]
    public void ClassifiesASymlinkToACredentialPathAsForbidden()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "crystal-applypatch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var ssh = Path.Combine(root, ".ssh");
        Directory.CreateDirectory(ssh);
        var secret = Path.Combine(ssh, "id_rsa");
        File.WriteAllText(secret, "secret");
        File.CreateSymbolicLink(Path.Combine(root, "key-link"), secret);
        try
        {
            var patchText = "*** Begin Patch\n*** Update File: key-link\n@@\n-secret\n+other\n*** End Patch";
            var arguments = JsonSerializer.Serialize(new { patchText });
            var classification = new ApplyPatchTool().ClassifyApproval(
                new ToolCall("1", "apply_patch", arguments),
                Context(root));

            Assert.Equal("forbidden", classification.Risk);
            Assert.Equal("privilegedEscalation", classification.Authority);
            Assert.Equal("secret", File.ReadAllText(secret));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ClassifiesCredentialPatchAsForbidden()
    {
        var patchText = "*** Begin Patch\n*** Add File: .ssh/id_rsa\n+secret\n*** End Patch";
        var arguments = JsonSerializer.Serialize(new { patchText });
        var classification = new ApplyPatchTool().ClassifyApproval(
            new ToolCall("1", "apply_patch", arguments),
            Context(Path.GetTempPath()));

        Assert.Equal("forbidden", classification.Risk);
        Assert.Equal("privilegedEscalation", classification.Authority);
    }

    [Fact]
    public void MalformedPatchRequiresPromptAndFailsClosed()
    {
        var arguments = JsonSerializer.Serialize(new { patchText = "not a patch" });
        var classification = new ApplyPatchTool().ClassifyApproval(
            new ToolCall("1", "apply_patch", arguments),
            Context(Path.GetTempPath()));

        Assert.Equal("privileged", classification.Risk);
        Assert.Equal("privilegedEscalation", classification.Authority);
        Assert.True(classification.RequirePrompt);
    }

    [Fact]
    public async Task AppliesAddUpdateAndDeleteSections()
    {
        await UsingRoot(async root =>
        {
            await File.WriteAllTextAsync(Path.Combine(root, "update.txt"), "before\n");
            await File.WriteAllTextAsync(Path.Combine(root, "remove.txt"), "remove\n");

            var output = await Invoke(
                root,
                """
                *** Begin Patch
                *** Add File: nested/new.txt
                +created
                *** Update File: update.txt
                @@
                -before
                +after
                *** Delete File: remove.txt
                *** End Patch
                """);

            Assert.Equal(ToolResultStatus.Success, output.Status);
            Assert.Equal(
                "Applied patch sequentially:\nA nested/new.txt\nM update.txt\nD remove.txt",
                output.Text);
            Assert.Equal(
                "created\n",
                await File.ReadAllTextAsync(Path.Combine(root, "nested", "new.txt")));
            Assert.Equal("after\n", await File.ReadAllTextAsync(Path.Combine(root, "update.txt")));
            Assert.False(File.Exists(Path.Combine(root, "remove.txt")));
        });
    }

    [Fact]
    public async Task AppliesAPatchPassedAsInput()
    {
        await UsingRoot(async root =>
        {
            var arguments = JsonSerializer.Serialize(
                new { input = "*** Begin Patch\n*** Add File: input.txt\n+created\n*** End Patch" });

            var output = await new ApplyPatchTool().InvokeAsync(
                new ToolCall("1", "apply_patch", arguments),
                Context(root));

            Assert.Equal(ToolResultStatus.Success, output.Status);
            Assert.Equal("Applied patch sequentially:\nA input.txt", output.Text);
            Assert.Equal(
                "created\n",
                await File.ReadAllTextAsync(Path.Combine(root, "input.txt")));
        });
    }

    [Fact]
    public async Task RejectsMovesBeforeAnyChange()
    {
        await UsingRoot(async root =>
        {
            await File.WriteAllTextAsync(Path.Combine(root, "old.txt"), "before\n");

            var output = await Invoke(
                root,
                """
                *** Begin Patch
                *** Add File: created.txt
                +created
                *** Update File: old.txt
                *** Move to: moved.txt
                @@
                -before
                +after
                *** End Patch
                """);

            Assert.Equal(ToolResultStatus.Failure, output.Status);
            Assert.Equal("apply_patch moves are not supported yet.", output.Text);
            Assert.False(File.Exists(Path.Combine(root, "created.txt")));
            Assert.False(File.Exists(Path.Combine(root, "moved.txt")));
            Assert.Equal("before\n", await File.ReadAllTextAsync(Path.Combine(root, "old.txt")));
        });
    }

    [Fact]
    public async Task RejectsALaterVerificationFailureBeforeApplyingAnEarlierAdd()
    {
        await UsingRoot(async root =>
        {
            var output = await Invoke(
                root,
                """
                *** Begin Patch
                *** Add File: created.txt
                +created
                *** Update File: missing.txt
                @@
                -before
                +after
                *** End Patch
                """);

            Assert.Equal(ToolResultStatus.Failure, output.Status);
            Assert.Equal(
                "Unable to apply patch at missing.txt: the file was not found.",
                output.Text);
            Assert.False(File.Exists(Path.Combine(root, "created.txt")));
        });
    }

    [Fact]
    public async Task RejectsAnAddOfAnExistingFile()
    {
        await UsingRoot(async root =>
        {
            var path = Path.Combine(root, "existing.txt");
            await File.WriteAllTextAsync(path, "sentinel\n");

            var output = await Invoke(
                root,
                "*** Begin Patch\n*** Add File: existing.txt\n+replacement\n*** End Patch");

            Assert.Equal(ToolResultStatus.Failure, output.Status);
            Assert.Equal(
                "Unable to apply patch at existing.txt: the file already exists.",
                output.Text);
            Assert.Equal("sentinel\n", await File.ReadAllTextAsync(path));
        });
    }

    [Fact]
    public async Task ReportsPartialApplicationAfterAnEarlierAdd()
    {
        await UsingRoot(async root =>
        {
            await File.WriteAllTextAsync(Path.Combine(root, "existing.txt"), "sentinel\n");

            var output = await Invoke(
                root,
                """
                *** Begin Patch
                *** Add File: new.txt
                +created
                *** Add File: existing.txt
                +replacement
                *** End Patch
                """);

            Assert.Equal(ToolResultStatus.Failure, output.Status);
            Assert.Equal(
                "Patch partially applied before failing at existing.txt: the file already exists. "
                + "Applied: new.txt.",
                output.Text);
            Assert.Equal(
                "created\n",
                await File.ReadAllTextAsync(Path.Combine(root, "new.txt")));
        });
    }

    [Fact]
    public async Task RejectsAnEmptyPatch()
    {
        await UsingRoot(async root =>
        {
            var output = await Invoke(root, "*** Begin Patch\n*** End Patch");

            Assert.Equal(ToolResultStatus.Failure, output.Status);
            Assert.Equal("patch rejected: empty patch.", output.Text);
        });
    }

    [Fact]
    public async Task RejectsAMalformedPatch()
    {
        await UsingRoot(async root =>
        {
            var output = await Invoke(root, "*** Add File: add.txt\n+added\n");

            Assert.Equal(ToolResultStatus.Failure, output.Status);
            Assert.Equal(
                "apply_patch verification failed: Invalid patch format: missing Begin/End markers.",
                output.Text);
        });
    }

    [Fact]
    public async Task RejectsAMismatchedUpdate()
    {
        await UsingRoot(async root =>
        {
            await File.WriteAllTextAsync(Path.Combine(root, "update.txt"), "other\n");

            var output = await Invoke(
                root,
                "*** Begin Patch\n*** Update File: update.txt\n@@\n-before\n+after\n*** End Patch");

            Assert.Equal(ToolResultStatus.Failure, output.Status);
            Assert.Equal(
                "Unable to apply patch at update.txt: the expected lines were not found:\nbefore",
                output.Text);
        });
    }

    [Fact]
    public async Task RejectsAnUpdateOfADirectory()
    {
        await UsingRoot(async root =>
        {
            Directory.CreateDirectory(Path.Combine(root, "folder"));

            var output = await Invoke(
                root,
                "*** Begin Patch\n*** Update File: folder\n@@\n-before\n+after\n*** End Patch");

            Assert.Equal(ToolResultStatus.Failure, output.Status);
            Assert.Equal("Unable to apply patch at folder: the path is a directory.", output.Text);
        });
    }

    [Fact]
    public async Task RejectsADeleteOfAMissingFile()
    {
        await UsingRoot(async root =>
        {
            var output = await Invoke(
                root,
                "*** Begin Patch\n*** Delete File: gone.txt\n*** End Patch");

            Assert.Equal(ToolResultStatus.Failure, output.Status);
            Assert.Equal("Unable to apply patch at gone.txt: the file was not found.", output.Text);
        });
    }

    [Fact]
    public async Task RejectsABinaryFileUpdate()
    {
        await UsingRoot(async root =>
        {
            await File.WriteAllBytesAsync(Path.Combine(root, "binary.bin"), [0x00, 0x01, 0x02]);

            var output = await Invoke(
                root,
                "*** Begin Patch\n*** Update File: binary.bin\n@@\n-before\n+after\n*** End Patch");

            Assert.Equal(ToolResultStatus.Failure, output.Status);
            Assert.Equal(
                "Unable to apply patch at binary.bin: the file looks binary and will not be edited.",
                output.Text);
        });
    }

    [Fact]
    public async Task RejectsAnUpdateThatIsNotValidUtf8()
    {
        await UsingRoot(async root =>
        {
            var path = Path.Combine(root, "latin1.txt");
            var original = new byte[] { 0x68, 0xFF, 0x69 };
            await File.WriteAllBytesAsync(path, original);

            var output = await Invoke(
                root,
                "*** Begin Patch\n*** Update File: latin1.txt\n@@\n-hi\n+hello\n*** End Patch");

            Assert.Equal(ToolResultStatus.Failure, output.Status);
            Assert.Equal(
                "Unable to apply patch at latin1.txt: the file is not valid UTF-8 and will not be edited.",
                output.Text);
            Assert.Equal(original, await File.ReadAllBytesAsync(path));
        });
    }

    [Fact]
    public async Task PreservesCrlfLineEndings()
    {
        await UsingRoot(async root =>
        {
            var path = Path.Combine(root, "windows.txt");
            await File.WriteAllBytesAsync(
                path,
                Encoding.UTF8.GetBytes("line 1\r\nline 2\r\n"));

            var output = await Invoke(
                root,
                "*** Begin Patch\n*** Update File: windows.txt\n@@\n-line 2\n+line 2 updated\n*** End Patch");

            Assert.Equal(ToolResultStatus.Success, output.Status);
            Assert.Equal(
                "line 1\r\nline 2 updated\r\n",
                Encoding.UTF8.GetString(await File.ReadAllBytesAsync(path)));
        });
    }

    [Fact]
    public async Task RejectsASecondUpdateOfTheSameFile()
    {
        await UsingRoot(async root =>
        {
            var path = Path.Combine(root, "twice.txt");
            await File.WriteAllTextAsync(path, "old\n");

            var output = await Invoke(
                root,
                """
                *** Begin Patch
                *** Update File: twice.txt
                @@
                -old
                +new
                *** Update File: twice.txt
                @@
                -old
                +newer
                *** End Patch
                """);

            Assert.Equal(ToolResultStatus.Failure, output.Status);
            Assert.Equal(
                "Unable to apply patch at twice.txt: the file is updated more than once in this patch.",
                output.Text);
            Assert.Equal("old\n", await File.ReadAllTextAsync(path));
        });
    }

    [Fact]
    public async Task DeletesABinaryFileWithoutInspectingIt()
    {
        await UsingRoot(async root =>
        {
            var path = Path.Combine(root, "image.bin");
            await File.WriteAllBytesAsync(path, [0x00, 0x89, 0x50]);

            var output = await Invoke(
                root,
                "*** Begin Patch\n*** Delete File: image.bin\n*** End Patch");

            Assert.Equal(ToolResultStatus.Success, output.Status);
            Assert.Equal("Applied patch sequentially:\nD image.bin", output.Text);
            Assert.False(File.Exists(path));
        });
    }

    [Fact]
    public async Task AppliesAPathOutsideTheWorkspace()
    {
        await UsingRoot(async root =>
        {
            var outside = Path.Combine(
                Path.GetTempPath(),
                "crystal-applypatch-outside-" + Guid.NewGuid().ToString("N") + ".txt");
            await File.WriteAllTextAsync(outside, "before\n");
            try
            {
                var output = await Invoke(
                    root,
                    $"*** Begin Patch\n*** Update File: {outside}\n@@\n-before\n+after\n*** End Patch");

                Assert.Equal(ToolResultStatus.Success, output.Status);
                Assert.Contains(outside, output.Text, StringComparison.Ordinal);
                Assert.Equal("after\n", await File.ReadAllTextAsync(outside));
            }
            finally
            {
                File.Delete(outside);
            }
        });
    }

    [Fact]
    public async Task RefusesCredentialPaths()
    {
        await UsingRoot(async root =>
        {
            var output = await Invoke(
                root,
                """
                *** Begin Patch
                *** Update File: nested/.ssh/id_rsa
                @@
                -before
                +after
                *** End Patch
                """);

            Assert.Equal(ToolResultStatus.Failure, output.Status);
            Assert.Equal(
                "Unable to apply patch at nested/.ssh/id_rsa: editing credential paths is not allowed.",
                output.Text);
        });
    }

    [Fact]
    public async Task UpdatesAFileWithoutATrailingNewline()
    {
        await UsingRoot(async root =>
        {
            var path = Path.Combine(root, "plain.txt");
            await File.WriteAllTextAsync(path, "no newline");

            var output = await Invoke(
                root,
                "*** Begin Patch\n*** Update File: plain.txt\n@@\n-no newline\n+has newline now\n*** End Patch");

            Assert.Equal(ToolResultStatus.Success, output.Status);
            Assert.Equal("Applied patch sequentially:\nM plain.txt", output.Text);
            Assert.Equal("has newline now\n", await File.ReadAllTextAsync(path));
        });
    }

    [Fact]
    public async Task PreservesTheBom()
    {
        await UsingRoot(async root =>
        {
            var path = Path.Combine(root, "bom.txt");
            await File.WriteAllTextAsync(path, "\uFEFFold\n");

            var output = await Invoke(
                root,
                "*** Begin Patch\n*** Update File: bom.txt\n@@\n-old\n+new\n*** End Patch");

            Assert.Equal(ToolResultStatus.Success, output.Status);
            Assert.Equal("\uFEFFnew\n", Encoding.UTF8.GetString(await File.ReadAllBytesAsync(path)));
        });
    }

    [Fact]
    public async Task ReportsAnUnavailableWorkspace()
    {
        var arguments = JsonSerializer.Serialize(
            new { patchText = "*** Begin Patch\n*** Delete File: gone.txt\n*** End Patch" });

        var output = await new ApplyPatchTool().InvokeAsync(
            new ToolCall("1", "apply_patch", arguments),
            new ToolHostContext("", "session", "default"));

        Assert.Equal(ToolResultStatus.Failure, output.Status);
        Assert.Equal("The workspace is unavailable in this host.", output.Text);
    }

    [Fact]
    public async Task RejectsInvalidJson()
    {
        await UsingRoot(async root =>
        {
            var output = await new ApplyPatchTool().InvokeAsync(
                new ToolCall("1", "apply_patch", "{oops"),
                Context(root));

            Assert.Equal(ToolResultStatus.Failure, output.Status);
            Assert.Equal("Tool arguments are not valid JSON.", output.Text);
        });
    }

    [Fact]
    public async Task RejectsAnUnknownArgument()
    {
        await UsingRoot(async root =>
        {
            var output = await new ApplyPatchTool().InvokeAsync(
                new ToolCall(
                    "1",
                    "apply_patch",
                    "{\"patchText\":\"*** Begin Patch\\n*** End Patch\",\"path\":\"/tmp\"}"),
                Context(root));

            Assert.Equal(ToolResultStatus.Failure, output.Status);
            Assert.Equal("Tool arguments must contain only patchText or input.", output.Text);
        });
    }

    [Fact]
    public async Task RejectsBothPatchTextAndInput()
    {
        await UsingRoot(async root =>
        {
            var output = await new ApplyPatchTool().InvokeAsync(
                new ToolCall(
                    "1",
                    "apply_patch",
                    "{\"patchText\":\"*** Begin Patch\\n*** End Patch\","
                    + "\"input\":\"*** Begin Patch\\n*** End Patch\"}"),
                Context(root));

            Assert.Equal(ToolResultStatus.Failure, output.Status);
            Assert.Equal("Provide only one of patchText or input.", output.Text);
        });
    }

    [Fact]
    public async Task RejectsAMissingPatchText()
    {
        await UsingRoot(async root =>
        {
            var output = await new ApplyPatchTool().InvokeAsync(
                new ToolCall("1", "apply_patch", "{}"),
                Context(root));

            Assert.Equal(ToolResultStatus.Failure, output.Status);
            Assert.Equal("Argument 'patchText' must be a non-empty string.", output.Text);
        });
    }

    [Fact]
    public async Task RejectsABlankPatchText()
    {
        await UsingRoot(async root =>
        {
            var output = await new ApplyPatchTool().InvokeAsync(
                new ToolCall("1", "apply_patch", "{\"patchText\":\"   \"}"),
                Context(root));

            Assert.Equal(ToolResultStatus.Failure, output.Status);
            Assert.Equal("Argument 'patchText' must be a non-empty string.", output.Text);
        });
    }

    private static async Task<ToolOutput> Invoke(string root, string patchText)
    {
        var arguments = JsonSerializer.Serialize(new { patchText });
        return await new ApplyPatchTool().InvokeAsync(
            new ToolCall("1", "apply_patch", arguments),
            Context(root));
    }

    private static ToolHostContext Context(string root) =>
        new(root, "session", "default");

    private static async Task UsingRoot(Func<string, Task> action)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "crystal-applypatch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await action(root);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
