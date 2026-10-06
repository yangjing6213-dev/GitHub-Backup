using GitHubBackup.App;
using System.Reflection;
using System.Runtime.InteropServices;

namespace GitHubBackup.App.Tests;

[TestClass]
public sealed class SetupRegistryTests
{
    [TestMethod]
    public void Only_a_key_created_by_this_atomic_registry_call_is_owned()
    {
        WindowsSetupRegistryStore.EnsureNewKeyDisposition(1);
        Assert.Throws<IOException>(() => WindowsSetupRegistryStore.EnsureNewKeyDisposition(2));
        Assert.Throws<IOException>(() => WindowsSetupRegistryStore.EnsureNewKeyDisposition(0));
    }

    [TestMethod]
    public void Native_registry_create_binding_names_the_unicode_windows_entry_point()
    {
        MethodInfo method = typeof(WindowsSetupRegistryStore).GetMethod("RegCreateKeyEx", BindingFlags.NonPublic | BindingFlags.Static)!;
        var import = method.GetCustomAttribute<DllImportAttribute>();

        Assert.IsNotNull(import);
        Assert.AreEqual("RegCreateKeyExW", import.EntryPoint);
        Assert.IsTrue(import.ExactSpelling);
        Assert.AreEqual(CharSet.Unicode, import.CharSet);
    }
}
