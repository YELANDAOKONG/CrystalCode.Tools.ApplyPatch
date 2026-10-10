using CrystalCode.ApplyPatch.Workspace;

namespace CrystalCode.ApplyPatch.Tests.Workspace;

public sealed class WorkspaceFenceTests
{
    [Fact]
    public void ResolvesRelativeAndAbsolutePathsInsideTheRoot()
    {
        UsingRoot(root =>
        {
            Directory.CreateDirectory(Path.Combine(root, "nested"));
            File.WriteAllText(Path.Combine(root, "nested", "file.txt"), "text");
            var fence = Fence(root);

            Assert.True(fence.TryResolve("nested/file.txt", out var relative, out _));
            Assert.Equal("nested/file.txt", fence.ToRelative(relative));
            Assert.True(fence.TryResolve(relative, out var absolute, out _));
            Assert.Equal(relative, absolute);
        });
    }

    [Fact]
    public void RefusesPathsOutsideTheRoot()
    {
        UsingRoot(root =>
        {
            var fence = Fence(root);

            Assert.False(fence.TryResolve("../escape.txt", out _, out var parentError));
            Assert.Equal("the path is outside the workspace.", parentError);

            var outside = Path.Combine(
                Path.GetTempPath(),
                "crystal-applypatch-outside-" + Guid.NewGuid().ToString("N") + ".txt");
            Assert.False(fence.TryResolve(outside, out _, out var absoluteError));
            Assert.Equal("the path is outside the workspace.", absoluteError);
        });
    }

    [Fact]
    public void RefusesCredentialPaths()
    {
        UsingRoot(root =>
        {
            var fence = Fence(root);

            Assert.False(fence.TryResolve("keys/.ssh/id_rsa", out _, out var sshError));
            Assert.Equal("editing credential paths is not allowed.", sshError);

            Assert.False(fence.TryResolve("nested/.gnupg/key", out _, out var gnupgError));
            Assert.Equal("editing credential paths is not allowed.", gnupgError);

            Assert.False(fence.TryResolve(".crystal/credentials.json", out _, out var crystalError));
            Assert.Equal("editing credential paths is not allowed.", crystalError);
        });
    }

    [Fact]
    public void RefusesASymbolicLinkThatLeavesTheRoot()
    {
        UsingRoot(root =>
        {
            var outside = Path.Combine(
                Path.GetTempPath(),
                "crystal-applypatch-target-" + Guid.NewGuid().ToString("N") + ".txt");
            File.WriteAllText(outside, "secret");
            File.CreateSymbolicLink(Path.Combine(root, "link.txt"), outside);
            var fence = Fence(root);

            try
            {
                Assert.False(fence.TryResolve("link.txt", out _, out var error));
                Assert.Equal("the path is outside the workspace.", error);
                Assert.Equal("secret", File.ReadAllText(outside));
            }
            finally
            {
                File.Delete(outside);
            }
        });
    }

    [Fact]
    public void ResolvesASymbolicLinkInsideTheRoot()
    {
        UsingRoot(root =>
        {
            var target = Path.Combine(root, "real.txt");
            File.WriteAllText(target, "text");
            File.CreateSymbolicLink(Path.Combine(root, "link.txt"), target);
            var fence = Fence(root);

            Assert.True(fence.TryResolve("link.txt", out var fullPath, out _));
            Assert.Equal("real.txt", fence.ToRelative(fullPath));
        });
    }

    [Fact]
    public void ReportsAnUnavailableWorkspace()
    {
        Assert.False(WorkspaceFence.TryCreate("", out _, out var emptyError));
        Assert.Equal("The workspace is unavailable in this host.", emptyError);

        var missing = Path.Combine(
            Path.GetTempPath(),
            "crystal-applypatch-missing-" + Guid.NewGuid().ToString("N"));
        Assert.False(WorkspaceFence.TryCreate(missing, out _, out var missingError));
        Assert.Equal("The workspace is unavailable in this host.", missingError);
    }

    private static WorkspaceFence Fence(string root)
    {
        Assert.True(WorkspaceFence.TryCreate(root, out var fence, out var error), error);
        return fence;
    }

    private static void UsingRoot(Action<string> action)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "crystal-applypatch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            action(root);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
