using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace GitHubBackup.App;

// Setup is dispatched before ordinary application initialization. Callers supply
// only the packaged manifest; Windows determines every installation destination.
internal static class SetupCommand
{
    internal sealed record Arguments(bool Install, string? ManifestPath, string? ExpectedHash);

    internal static bool IsSetupRequest(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        return args.Any(value => value is not null &&
            (value.TrimStart().StartsWith("--setup", StringComparison.OrdinalIgnoreCase)
            || value.TrimStart().StartsWith("-setup", StringComparison.OrdinalIgnoreCase)
            || value.TrimStart().StartsWith("/setup", StringComparison.OrdinalIgnoreCase)));
    }

    internal static Arguments ParseArguments(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Length == 1 && args[0] == "--setup-uninstall") return new(false, null, null);
        if (args.Length != 3 || args[0] != "--setup-install" || string.IsNullOrWhiteSpace(args[1])
            || args[2] is null || args[2].Length != 64 || !args[2].All(Uri.IsHexDigit))
            throw new ArgumentException("SETUP_ARGUMENTS_INVALID");
        string path = NativeFileSystem.CanonicalPath(args[1]);
        if (!string.Equals(path, args[1], StringComparison.OrdinalIgnoreCase) || Path.GetFileName(path).Length == 0)
            throw new ArgumentException("SETUP_MANIFEST_PATH_INVALID");
        return new(true, path, args[2].ToUpperInvariant());
    }

    internal static int Execute(string[] args)
    {
        try
        {
            Arguments request = ParseArguments(args);
            using var identity = WindowsIdentity.GetCurrent();
            RequireHost(identity.AccessToken);
            SecurityIdentifier user = identity.User ?? throw new IOException("SETUP_USER_MISSING");
            SetupContext context = ResolveContext(user);
            if (!request.Install)
            {
                SetupLifecycle.Uninstall(context);
                return 0;
            }

            string manifestPath = request.ManifestPath!;
            string payload = Path.GetDirectoryName(manifestPath)!;
            using var ancestors = NativeFileSystem.PinDirectories(payload);
            using var parent = NativeFileSystem.Open(payload);
            NativeFileSystem.Inspect(parent, payload, directory: true);
            AclPolicy.VerifyRestricted(parent, user);
            using var manifestFile = NativeFileSystem.Open(manifestPath,
                0x80000000 | NativeFileSystem.ReadControl | NativeFileSystem.ReadAttributes, shareWrite: false);
            NativeFileSystem.Inspect(manifestFile, manifestPath, directory: false);
            if (AclPolicy.Evaluate(AclPolicy.ReadDescriptor(manifestFile), user, true) != AclRisk.Safe)
                throw new IOException("SETUP_MANIFEST_ACL_INVALID");
            byte[] bytes = SetupNative.Read(manifestFile, 65536);
            if (Convert.ToHexString(SHA256.HashData(bytes)) != request.ExpectedHash)
                throw new IOException("SETUP_MANIFEST_HASH_MISMATCH");
            SetupManifest manifest = SetupLifecycle.Parse<SetupManifest>(bytes);
            SetupLifecycle.ValidateManifest(manifest);
            SetupLifecycle.Install(context, payload, manifest);
            return 0;
        }
        catch (IOException ex) when (ex.Message == "SETUP_OLD_VERSION_MUST_BE_UNINSTALLED") { return 12; }
        catch (ArgumentException) { return 11; }
        catch (PlatformNotSupportedException) { return 10; }
        catch (Exception) { return 13; }
        // The installer owns the failure UI. Never write exception messages or
        // private profile/payload paths into ordinary application diagnostics.
    }

    private static void RequireHost(SafeAccessTokenHandle token)
    {
        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64
            || RuntimeInformation.OSArchitecture != Architecture.X64)
            throw new PlatformNotSupportedException("SETUP_HOST_UNSUPPORTED");
        var version = new VersionInfo { Size = (uint)Marshal.SizeOf<VersionInfo>(), ServicePack = "" };
        if (RtlGetVersion(ref version) != 0 || version.Major != 10 || version.Minor != 0
            || version.Build < 26200 || version.ProductType != 1
            || !GetTokenInformation(token, 20, out int elevated, sizeof(int), out int returned)
            || returned != sizeof(int) || elevated != 0)
            throw new PlatformNotSupportedException("SETUP_HOST_UNSUPPORTED");
    }

    private static SetupContext ResolveContext(SecurityIdentifier user)
    {
        const int changedApartment = unchecked((int)0x80010106);
        int initialized = CoInitializeEx(IntPtr.Zero, 2);
        if (initialized < 0 && initialized != changedApartment) Marshal.ThrowExceptionForHR(initialized);
        try
        {
            string local = KnownFolder(new("F1B32785-6FBA-4FCF-9D55-7B8E7F157091"));
            string menu = KnownFolder(new("A77F5D77-2E2B-44C3-A6A2-ABA601054A51"));
            string desktop = KnownFolder(new("B4BFCC3A-DB2C-424C-B029-7FE99A87C641"));
            return new(NativeFileSystem.CanonicalPath(Path.Combine(local, "Programs", "GitHubBackupTool")),
                NativeFileSystem.CanonicalPath(Path.Combine(menu, SetupLifecycle.ShortcutName)),
                NativeFileSystem.CanonicalPath(Path.Combine(desktop, SetupLifecycle.ShortcutName)),
                new WindowsSetupRegistryStore(), user);
        }
        finally { if (initialized >= 0) CoUninitialize(); }
    }

    private static string KnownFolder(Guid id)
    {
        IntPtr path = IntPtr.Zero;
        try
        {
            Marshal.ThrowExceptionForHR(SHGetKnownFolderPath(ref id, 0, IntPtr.Zero, out path));
            return NativeFileSystem.CanonicalPath(Marshal.PtrToStringUni(path)
                ?? throw new IOException("SETUP_KNOWN_FOLDER_MISSING"));
        }
        finally { if (path != IntPtr.Zero) Marshal.FreeCoTaskMem(path); }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct VersionInfo
    {
        internal uint Size, Major, Minor, Build, Platform;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] internal string ServicePack;
        internal ushort ServicePackMajor, ServicePackMinor, SuiteMask;
        internal byte ProductType, Reserved;
    }

    [DllImport("ntdll.dll", CharSet = CharSet.Unicode)]
    private static extern int RtlGetVersion(ref VersionInfo version);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int kind,
        out int information, int size, out int returned);
    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath(ref Guid id, uint flags, IntPtr token, out IntPtr path);
    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(IntPtr reserved, uint concurrency);
    [DllImport("ole32.dll")]
    private static extern void CoUninitialize();
}
