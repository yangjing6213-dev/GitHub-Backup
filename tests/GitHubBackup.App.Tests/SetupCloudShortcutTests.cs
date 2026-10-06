using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using GitHubBackup.App;

namespace GitHubBackup.App.Tests;

[TestClass]
public sealed class SetupCloudShortcutTests
{
    [TestMethod]
    public void Shortcut_parent_policy_accepts_only_the_exact_Microsoft_cloud_tag_family()
    {
        for (uint variant = 0; variant < 16; variant++)
            Assert.IsTrue(NativeFileSystem.IsSetupShortcutCloudTag(0x9000001a | (variant << 12)));
        foreach (uint tag in new uint[] { 0, 0xA0000003, 0xA000000C, 0x8000001B, 0x80000017,
                     0x8000001a, 0x9001001a, 0x9000001b, 0xb000001a, 0x9000001c })
            Assert.IsFalse(NativeFileSystem.IsSetupShortcutCloudTag(tag));
    }

    [TestMethod]
    public void Ordinary_shortcut_parent_retains_identity_path_type_and_no_delete_sharing_checks()
    {
        using var root = new StorageTestRoot();
        string directory = root.Child("shortcut-parent");
        AclPolicy.CreateRestrictedDirectory(directory, root.User);
        using (var handle = NativeFileSystem.Open(directory))
        {
            Assert.AreEqual(NativeFileSystem.Inspect(handle, directory, true),
                NativeFileSystem.InspectSetupShortcutParent(handle, directory));
            Assert.ThrowsExactly<PathBoundaryException>(() =>
                NativeFileSystem.InspectSetupShortcutParent(handle, root.Child("wrong-path")));
            Assert.ThrowsExactly<IOException>(() => Directory.Move(directory, root.Child("moved")));
            SetupNative.VerifySharedParent(handle, root.User);
        }
        string file = root.Child("file");
        using (var output = AclPolicy.CreateRestrictedFile(file, root.User)) { }
        using var fileHandle = NativeFileSystem.Open(file);
        Assert.ThrowsExactly<PathBoundaryException>(() => NativeFileSystem.InspectSetupShortcutParent(fileHandle, file));
    }

    [TestMethod]
    public void Junction_shortcut_parent_is_rejected_before_installation_writes()
    {
        var fixture = new SetupLifecycleTests.Fixture();
        string desktop = Path.GetDirectoryName(fixture.Context.DesktopShortcut)!;
        string target = Path.Combine(Path.GetDirectoryName(desktop)!, "external-desktop");
        Directory.CreateDirectory(target);
        Assert.IsEmpty(Directory.GetFileSystemEntries(desktop));
        Directory.Delete(desktop);
        StorageTestRoot.CreateJunction(desktop, target);
        try
        {
            using (var handle = NativeFileSystem.Open(desktop))
            {
                Assert.ThrowsExactly<PathBoundaryException>(() => NativeFileSystem.InspectSetupShortcutParent(handle, desktop));
                Assert.ThrowsExactly<PathBoundaryException>(() => NativeFileSystem.Inspect(handle, desktop, true));
            }
            Assert.Throws<IOException>(() => SetupLifecycle.Install(fixture.Context, fixture.Payload, fixture.Manifest));
            Assert.IsFalse(Directory.Exists(fixture.Context.Root));
            Assert.IsEmpty(Directory.GetFileSystemEntries(target));
            Assert.HasCount(0, fixture.Registry.Entries);
        }
        finally { Directory.Delete(desktop); }
    }

    [TestMethod]
    [TestCategory("LiveCloudReadOnly")]
    public void Actual_cloud_desktop_parent_can_be_pinned_without_creating_a_shortcut()
    {
        string desktop = Environment.GetEnvironmentVariable("GITHUB_BACKUP_CLOUD_PARENT")
            ?? Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (string.IsNullOrEmpty(desktop) || !Directory.Exists(desktop))
            Assert.Inconclusive("LIVE_CLOUD_PARENT_UNAVAILABLE");
        bool cloudFound = false;
        foreach (string segment in NativeFileSystem.Segments(desktop))
        {
            using var handle = NativeFileSystem.Open(segment);
            if (!GetFileInformationByHandleEx(handle, 9, out FileAttributeTagInformation tag, 8))
                NativeFileSystem.ThrowLastError();
            if ((tag.Attributes & FileAttributes.ReparsePoint) == 0) continue;
            Assert.IsTrue(NativeFileSystem.IsSetupShortcutCloudTag(tag.ReparseTag), "LIVE_SHORTCUT_PARENT_UNKNOWN_TAG");
            Assert.ThrowsExactly<PathBoundaryException>(() => NativeFileSystem.Inspect(handle, segment, true));
            NativeFileSystem.InspectSetupShortcutParent(handle, segment);
            cloudFound = true;
        }
        if (!cloudFound) Assert.Inconclusive("LIVE_CLOUD_PARENT_UNAVAILABLE");

        var fixture = new SetupLifecycleTests.Fixture();
        var context = fixture.Context with { DesktopShortcut = Path.Combine(desktop, SetupLifecycle.ShortcutName) };
        Type treeType = typeof(SetupLifecycle).GetNestedType("Tree", BindingFlags.NonPublic)!;
        ConstructorInfo constructor = treeType.GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic,
            null, [typeof(SetupContext)], null)!;

        // Tree only pins existing shortcut parents. It never opens or writes the
        // actual Desktop shortcut; all installation/root paths are synthetic.
        using var tree = (IDisposable)constructor.Invoke([context]);
        Assert.IsFalse(Directory.Exists(context.Root));
        Assert.HasCount(0, fixture.Registry.Entries);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileAttributeTagInformation
    {
        internal FileAttributes Attributes;
        internal uint ReparseTag;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int informationClass,
        out FileAttributeTagInformation information, uint size);
}
