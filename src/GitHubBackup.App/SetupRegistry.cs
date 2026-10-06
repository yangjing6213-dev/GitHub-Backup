using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace GitHubBackup.App;

internal sealed record SetupRegistryEntry(string InstallId, IReadOnlyDictionary<string, object> Values)
{
    internal const string InstallIdValueName = "GitHubBackupToolInstallId";

    internal static SetupRegistryEntry ForInstall(SetupContext context, SetupManifest manifest, string installId)
        => ForInstall(context.Root, manifest.Version, installId);

    internal static SetupRegistryEntry ForInstall(string root, string version, string installId)
    {
        if (!Guid.TryParseExact(installId, "N", out var parsed) || parsed.ToString("N") != installId)
            throw new IOException("SETUP_REGISTRY_ID_INVALID");
        string app = Path.Combine(root, SetupLifecycle.AppName);
        string uninstaller = Path.Combine(root, SetupLifecycle.UninstallerName);
        var values = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
        {
            [InstallIdValueName] = installId,
            ["DisplayVersion"] = version,
            ["InstallLocation"] = root,
            ["DisplayIcon"] = "\"" + app + "\",0",
            ["UninstallString"] = "\"" + uninstaller + "\"",
            ["NoModify"] = 1,
            ["NoRepair"] = 1,
            // Windows shows an entry only after the final DisplayName value is written.
            ["DisplayName"] = "GitHub 备份工具"
        };
        return new(installId, values);
    }
}

internal interface ISetupRegistryStore
{
    void EnsureAbsent(string installId);
    void CreateNew(SetupRegistryEntry entry);
    void VerifyExact(SetupRegistryEntry entry);
    void RemoveOwned(SetupRegistryEntry entry, bool allowPartial);
}

// The application writes only its own current-user uninstall subkey. Unknown
// values/subkeys are preserved and make uninstall fail closed.
internal sealed class WindowsSetupRegistryStore : ISetupRegistryStore
{
    private const string UninstallRoot = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";
    private const int KeyReadWrite = 0x2001F;
    private const int CreatedNewKey = 1;

    public void EnsureAbsent(string installId)
    {
        ValidateId(installId);
        using var root = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        using var existing = root.OpenSubKey(KeyName(installId), writable: false);
        if (existing is not null) throw new IOException("SETUP_REGISTRY_ENTRY_OCCUPIED");
    }

    public void CreateNew(SetupRegistryEntry entry)
    {
        ValidateEntry(entry);
        using var root = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        int error = RegCreateKeyEx(root.Handle, KeyName(entry.InstallId), 0, null, 0, KeyReadWrite,
            IntPtr.Zero, out SafeRegistryHandle handle, out int disposition);
        if (error != 0)
        {
            handle.Dispose();
            throw new IOException("SETUP_REGISTRY_CREATE_FAILED", new Win32Exception(error));
        }
        using (handle)
        {
            EnsureNewKeyDisposition(disposition);
            using var key = RegistryKey.FromHandle(handle, RegistryView.Registry64);
            // This atomic disposition check prevents adopting a pre-existing empty key.
            if (key.ValueCount != 0 || key.SubKeyCount != 0) throw new IOException("SETUP_REGISTRY_ENTRY_OCCUPIED");
            foreach (var pair in entry.Values.Where(p => !p.Key.Equals("DisplayName", StringComparison.OrdinalIgnoreCase)))
                SetValue(key, pair.Key, pair.Value);
            key.Flush();
            SetValue(key, "DisplayName", entry.Values["DisplayName"]);
            key.Flush();
        }
    }

    public void VerifyExact(SetupRegistryEntry entry)
    {
        ValidateEntry(entry);
        using var root = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        using var key = root.OpenSubKey(KeyName(entry.InstallId), writable: false)
            ?? throw new IOException("SETUP_REGISTRY_ENTRY_MISSING");
        VerifyContents(key, entry, allowPartial: false);
    }

    public void RemoveOwned(SetupRegistryEntry entry, bool allowPartial)
    {
        ValidateEntry(entry);
        using var root = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        using var key = root.OpenSubKey(KeyName(entry.InstallId), writable: true);
        if (key is null)
        {
            if (allowPartial) return;
            throw new IOException("SETUP_REGISTRY_ENTRY_MISSING");
        }
        // A crash may occur between atomic key creation and writing its first
        // ownership marker. An empty key is ambiguous, so leave it untouched.
        if (allowPartial && key.ValueCount == 0 && key.SubKeyCount == 0) return;
        string[] names = VerifyContents(key, entry, allowPartial);
        foreach (string name in names) key.DeleteValue(name, throwOnMissingValue: false);
        key.Flush();
        bool empty = key.ValueCount == 0 && key.SubKeyCount == 0;
        key.Dispose();
        if (empty) root.DeleteSubKey(KeyName(entry.InstallId), throwOnMissingSubKey: false);
    }

    private static string[] VerifyContents(RegistryKey key, SetupRegistryEntry entry, bool allowPartial)
    {
        if (key.SubKeyCount != 0) throw new IOException("SETUP_REGISTRY_ENTRY_CONFLICT");
        string[] names = key.GetValueNames();
        foreach (string name in names)
        {
            if (!entry.Values.TryGetValue(name, out object? expected))
                throw new IOException("SETUP_REGISTRY_ENTRY_CONFLICT");
            RegistryValueKind kind = key.GetValueKind(name);
            object? actual = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            if (expected is int integer)
            {
                if (kind != RegistryValueKind.DWord || actual is not int actualInteger || actualInteger != integer)
                    throw new IOException("SETUP_REGISTRY_ENTRY_CHANGED");
            }
            else if (expected is string text)
            {
                if (kind != RegistryValueKind.String || actual is not string actualText
                    || !string.Equals(actualText, text, StringComparison.Ordinal))
                    throw new IOException("SETUP_REGISTRY_ENTRY_CHANGED");
            }
            else throw new IOException("SETUP_REGISTRY_VALUE_INVALID");
        }
        if (!allowPartial && names.Length != entry.Values.Count)
            throw new IOException("SETUP_REGISTRY_ENTRY_INCOMPLETE");
        return names;
    }

    private static void SetValue(RegistryKey key, string name, object value)
    {
        if (value is string text) key.SetValue(name, text, RegistryValueKind.String);
        else if (value is int integer) key.SetValue(name, integer, RegistryValueKind.DWord);
        else throw new IOException("SETUP_REGISTRY_VALUE_INVALID");
    }

    private static string KeyName(string installId) => UninstallRoot + "\\GitHubBackupTool-" + installId;
    internal static void EnsureNewKeyDisposition(int disposition)
    {
        if (disposition != CreatedNewKey) throw new IOException("SETUP_REGISTRY_ENTRY_OCCUPIED");
    }

    [DllImport("advapi32.dll", EntryPoint = "RegCreateKeyExW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int RegCreateKeyEx(SafeRegistryHandle key, string subKey, int reserved, string? keyClass,
        int options, int desiredAccess, IntPtr securityAttributes, out SafeRegistryHandle result, out int disposition);

    private static void ValidateEntry(SetupRegistryEntry entry)
    {
        ValidateId(entry.InstallId);
        SetupRegistryEntry expected = SetupRegistryEntry.ForInstall(
            entry.Values["InstallLocation"] as string ?? throw new IOException("SETUP_REGISTRY_VALUE_INVALID"),
            entry.Values["DisplayVersion"] as string ?? throw new IOException("SETUP_REGISTRY_VALUE_INVALID"), entry.InstallId);
        if (entry.Values.Count != expected.Values.Count || expected.Values.Any(pair =>
                !entry.Values.TryGetValue(pair.Key, out object? actual) || actual.GetType() != pair.Value.GetType() || !Equals(actual, pair.Value)))
            throw new IOException("SETUP_REGISTRY_VALUE_INVALID");
    }
    private static void ValidateId(string installId)
    {
        if (!Guid.TryParseExact(installId, "N", out var parsed) || parsed.ToString("N") != installId)
            throw new IOException("SETUP_REGISTRY_ID_INVALID");
    }
}
