using System.Text.Json;

using CrystalCode.ApplyPatch.Tools;

namespace CrystalCode.ApplyPatch.Tests;

public sealed class ManifestTests
{
    [Fact]
    public void ShipsAManifestThatMatchesTheTool()
    {
        var path = Path.Combine(FindRepositoryRoot(), "tools.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;

        Assert.Equal("dotnet", root.GetProperty("runner").GetString());
        Assert.Equal("CrystalCode.ApplyPatch.dll", root.GetProperty("assembly").GetString());
        Assert.Equal(
            [typeof(ApplyPatchTool).FullName!],
            root.GetProperty("types").EnumerateArray().Select(item => item.GetString()!).ToArray());
        Assert.Equal(
            ["work"],
            root.GetProperty("catalogs").EnumerateArray().Select(item => item.GetString()!).ToArray());
        Assert.Equal("inherit", root.GetProperty("approval").GetString());
        Assert.True(root.GetProperty("timeoutSeconds").GetInt32() > 0);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null
            && !(File.Exists(Path.Combine(directory.FullName, "tools.json"))
                && File.Exists(Path.Combine(directory.FullName, "CrystalCode.Tools.ApplyPatch.sln"))))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory!.FullName;
    }
}
