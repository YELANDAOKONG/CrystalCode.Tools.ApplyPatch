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
    public void ResolvesPathsOutsideTheRoot()
    {
        UsingRoot(root =>
        {
            var fence = Fence(root);

            Assert.True(fence.TryResolve("../escape.txt", out var parent, out var parentError), parentError);
            Assert.False(fence.Contains(parent));

            var outside = Path.Combine(
                Path.GetTempPath(),
                "crystal-applypatch-outside-" + Guid.NewGuid().ToString("N") + ".txt");
            Assert.True(fence.TryResolve(outside, out var absolute, out var absoluteError), absoluteError);
            Assert.False(fence.Contains(absolute));
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
    public void ResolvesASymbolicLinkThatLeavesTheRoot()
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
                Assert.True(fence.TryResolve("link.txt", out var fullPath, out var error), error);
                Assert.False(fence.Contains(fullPath));
                Assert.Equal("secret", File.ReadAllText(outside));
            }
            finally
            {
                File.Delete(outside);
            }
        });
    }

    [Fact]
    public void RefusesASymbolicLinkToAnOutsideCredentialPath()
    {
        UsingRoot(root =>
        {
            var outside = Path.Combine(
                Path.GetTempPath(),
                "crystal-applypatch-ssh-" + Guid.NewGuid().ToString("N"));
            var ssh = Path.Combine(outside, ".ssh");
            Directory.CreateDirectory(ssh);
            var secret = Path.Combine(ssh, "id_rsa");
            try
            {
                File.WriteAllText(secret, "secret");
                File.CreateSymbolicLink(Path.Combine(root, "link.txt"), secret);
                var fence = Fence(root);

                Assert.False(fence.TryResolve("link.txt", out _, out var error));
                Assert.Equal("editing credential paths is not allowed.", error);
                Assert.Equal("secret", File.ReadAllText(secret));
            }
            finally
            {
                Directory.Delete(outside, recursive: true);
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
