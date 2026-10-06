namespace GitHubBackup.App.Tests;

[TestClass]
public sealed class SetupCommandTests
{
    private const string Manifest = @"C:\synthetic-payload\manifest.json";
    private static readonly string Digest = new('A', 64);

    [TestMethod]
    public void Install_accepts_only_a_manifest_path_and_its_sha256()
    {
        var parsed = SetupCommand.ParseArguments(["--setup-install", Manifest, Digest.ToLowerInvariant()]);
        Assert.IsTrue(parsed.Install);
        Assert.AreEqual(Manifest, parsed.ManifestPath);
        string expectedHash = Digest;
        Assert.AreEqual(expectedHash, parsed.ExpectedHash);
    }

    [TestMethod]
    public void Uninstall_has_no_caller_selected_paths()
    {
        var parsed = SetupCommand.ParseArguments(["--setup-uninstall"]);
        Assert.IsFalse(parsed.Install);
        Assert.IsNull(parsed.ManifestPath);
        Assert.IsNull(parsed.ExpectedHash);
    }

    [TestMethod]
    public void Missing_desktop_fallback_is_limited_to_the_registered_default_user_desktop()
    {
        Assert.AreEqual(@"C:\Users\Example\Desktop",
            SetupCommand.ResolveDefaultDesktopPath(@"C:\Users\Example", @"C:\Users\Example\Desktop"));
        Assert.Throws<IOException>(() => SetupCommand.ResolveDefaultDesktopPath(
            @"C:\Users\Example", @"D:\Shared\Desktop"));
        Assert.Throws<IOException>(() => SetupCommand.ResolveDefaultDesktopPath(
            @"C:\Users\Example", @"\\server\share\Desktop"));
    }

    [TestMethod]
    [DataRow("--setup")]
    [DataRow("--setup-upgrade")]
    [DataRow("--SETUP-INSTALL")]
    [DataRow("--setup-install=manifest.json")]
    [DataRow("/setup")]
    [DataRow("/setup-uninstall")]
    [DataRow("-setup-install")]
    [DataRow(" --setup-install")]
    public void Malformed_setup_switches_are_routed_to_rejection_not_normal_startup(string value)
    {
        Assert.IsTrue(SetupCommand.IsSetupRequest([value]));
        Assert.IsTrue(SetupCommand.IsSetupRequest(["ordinary-argument", value]));
        Assert.Throws<ArgumentException>(() => SetupCommand.ParseArguments([value]));
    }

    [TestMethod]
    public void Ordinary_arguments_do_not_enter_setup()
    {
        Assert.IsFalse(SetupCommand.IsSetupRequest([]));
        Assert.IsFalse(SetupCommand.IsSetupRequest(["--help"]));
        Assert.IsFalse(SetupCommand.IsSetupRequest([@"C:\documents\setup-notes.txt"]));
    }

    [TestMethod]
    public void Extra_missing_or_mixed_mode_arguments_cannot_select_an_installation_root()
    {
        string[][] invalid =
        [
            [], ["--setup-install"], ["--setup-install", Manifest],
            ["--setup-install", Manifest, Digest, @"C:\arbitrary-root"],
            ["--setup-install", Manifest, Digest, "--root", @"C:\arbitrary-root"],
            ["--setup-uninstall", @"C:\arbitrary-root"],
            ["--setup-uninstall", "--root", @"C:\arbitrary-root"],
            ["--setup-install", Manifest, "--setup-uninstall"],
            ["ordinary-argument", "--setup-uninstall"],
            ["--SETUP-UNINSTALL"], ["--setup-install ", Manifest, Digest]
        ];
        foreach (string[] args in invalid)
            Assert.Throws<ArgumentException>(() => SetupCommand.ParseArguments(args));
    }

    [TestMethod]
    [DataRow("manifest.json")]
    [DataRow("C:manifest.json")]
    [DataRow("\\\\server\\share\\manifest.json")]
    [DataRow("\\\\?\\C:\\payload\\manifest.json")]
    [DataRow("C:/payload/manifest.json")]
    [DataRow("C:\\payload\\..\\manifest.json")]
    [DataRow("C:\\payload\\manifest.json:stream")]
    [DataRow("C:\\payload\\manifest.json ")]
    [DataRow("C:\\payload\\")]
    [DataRow("C:\\")]
    public void Manifest_paths_must_be_canonical_local_absolute_file_paths(string path) =>
        Assert.Throws<ArgumentException>(() => SetupCommand.ParseArguments(["--setup-install", path, Digest]));

    [TestMethod]
    [DataRow("")]
    [DataRow("sha256:0123456789")]
    [DataRow("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [DataRow("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [DataRow("GAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public void Invalid_manifest_digests_are_rejected_before_any_files_are_opened(string digest) =>
        Assert.Throws<ArgumentException>(() => SetupCommand.ParseArguments(["--setup-install", Manifest, digest]));
}
