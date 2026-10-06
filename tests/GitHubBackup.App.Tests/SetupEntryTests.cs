using System.Runtime.CompilerServices;

namespace GitHubBackup.App.Tests;

[TestClass]
public sealed class SetupEntryTests
{
    [TestMethod]
    public void Setup_dispatch_precedes_all_normal_profile_and_application_initialization()
    {
        string entry = ReadEntry();
        int dispatch = entry.IndexOf("SetupCommand.Execute(args)", StringComparison.Ordinal);
        Assert.IsGreaterThan(0, dispatch);
        foreach (string initialization in new[] { "ApplicationConfiguration.Initialize()", "SingleInstanceCoordinator.Start", "AppPaths.Create" })
            Assert.IsGreaterThan(dispatch, entry.IndexOf(initialization, StringComparison.Ordinal));
        StringAssert.Contains(entry, "Environment.ExitCode = SetupCommand.Execute(args);");
    }

    private static string ReadEntry([CallerFilePath] string source = "")
    {
        string root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "..", ".."));
        return File.ReadAllText(Path.Combine(root, "src", "GitHubBackup.App", "Program.cs"));
    }

    [TestMethod]
    public void Invalid_setup_command_returns_failure_without_resolving_an_installation()
    {
        Assert.AreEqual(11, SetupCommand.Execute(["--setup-install"]));
        Assert.AreEqual(11, SetupCommand.Execute(["--setup-uninstall", "F:\\arbitrary-target"]));
    }
}
