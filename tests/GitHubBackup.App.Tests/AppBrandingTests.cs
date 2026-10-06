using System.Drawing;
using System.Reflection;

namespace GitHubBackup.App.Tests;

[TestClass]
public sealed class AppBrandingTests
{
    [TestMethod]
    public void Windows_icon_contains_expected_sizes_and_is_embedded_in_the_application()
    {
        string root = FindRepositoryRoot();
        string iconPath = Path.Combine(root, "src", "GitHubBackup.App", "Assets", "enhe-app-icon.ico");
        byte[] iconBytes = File.ReadAllBytes(iconPath);
        Assert.IsGreaterThanOrEqualTo(6, iconBytes.Length);
        Assert.AreEqual((ushort)0, BitConverter.ToUInt16(iconBytes, 0));
        Assert.AreEqual((ushort)1, BitConverter.ToUInt16(iconBytes, 2));
        ushort count = BitConverter.ToUInt16(iconBytes, 4);
        Assert.AreEqual((ushort)6, count);
        var sizes = Enumerable.Range(0, count).Select(index =>
        {
            int offset = 6 + index * 16;
            int width = iconBytes[offset] == 0 ? 256 : iconBytes[offset];
            int height = iconBytes[offset + 1] == 0 ? 256 : iconBytes[offset + 1];
            return width == height ? width : -1;
        }).Order().ToArray();
        CollectionAssert.AreEqual(new[] { 16, 32, 48, 64, 128, 256 }, sizes);
        using var icon = new Icon(iconPath);
        Assert.AreEqual(32, icon.Width);
        Assert.AreEqual(32, icon.Height);

        string project = File.ReadAllText(Path.Combine(root, "src", "GitHubBackup.App", "GitHubBackup.App.csproj"));
        StringAssert.Contains(project, "<ApplicationIcon>Assets\\enhe-app-icon.ico</ApplicationIcon>");
        Assert.IsTrue(typeof(MainForm).Assembly.GetManifestResourceNames().Any(name => name.EndsWith(".author-avatar.png", StringComparison.Ordinal)));
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "GitHubBackupTool.sln"))) return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Repository root not found from test output.");
    }
}
