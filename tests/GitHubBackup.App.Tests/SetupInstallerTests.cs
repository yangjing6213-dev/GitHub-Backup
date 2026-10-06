using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Win32;

namespace GitHubBackup.App.Tests;

// The native harness prepares foundation + ownership primitive checks, with no
// product mutation. G1/G2/G3 must independently be satisfied before it runs.
// Complete product authorization and the lifecycle matrix are not implemented.
[TestClass]
[DoNotParallelize]
[TestCategory("SetupIsolated")]
public sealed class SetupInstallerTests
{
    [TestMethod]
    public async Task NativeGuards_CheckIdentityOwnershipHashReceiptMutexAndCapacity()
    {
        // No fixture constructor, directory, registry or process action precedes this.
        var authorization = SetupIsolationGate.Require(
            Environment.GetEnvironmentVariable("GITHUB_BACKUP_SETUP_TEST_PROFILE"),
            ReadAuthorizationRecord,
            () =>
            {
                using var identity = WindowsIdentity.GetCurrent();
                return (identity.User?.Value ?? "", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            }, ReadRegisteredProfile);

        // This record indexes reviewed gate evidence. Creating a JSON record or
        // setting an environment variable does not itself authorize an execution.
        string harness = Path.Combine(RepoRoot(), "artifacts", "setup", "isolated-foundation", "SetupHarness.exe");
        Assert.IsTrue(File.Exists(harness), "SETUP_FOUNDATION_HARNESS_NOT_BUILT");
        using var parents = NativeFileSystem.PinDirectories(Path.GetDirectoryName(harness)!);
        using var handle = NativeFileSystem.Open(harness, shareWrite: false);
        NativeFileSystem.Inspect(handle, harness, directory: false);
        using var harnessLease = new FileStream(handle, FileAccess.Read);
        string harnessHash = Convert.ToHexString(SHA256.HashData(harnessLease));
        Assert.AreEqual(authorization.HarnessSha256, harnessHash, ignoreCase: true,
            "SETUP_FOUNDATION_HARNESS_HASH_MISMATCH");
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs", "GitHubBackupTool");
        bool existed = Directory.Exists(root);
        DateTime? writeTime = existed ? Directory.GetLastWriteTimeUtc(root) : null;

        var start = new ProcessStartInfo(harness) { UseShellExecute = false, CreateNoWindow = true };
        using var process = Process.Start(start)!;
        // Never kill a process as cleanup; a hung native check must be diagnosed.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await process.WaitForExitAsync(timeout.Token);
        Assert.AreEqual(0, process.ExitCode, "SETUP_NATIVE_FOUNDATION_REJECTED");
        Assert.AreEqual(existed, Directory.Exists(root));
        Assert.AreEqual(writeTime, existed ? Directory.GetLastWriteTimeUtc(root) : null);
    }

    private static string RepoRoot([CallerFilePath] string source = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "..", ".."));

    private static SetupIsolationRecord? ReadAuthorizationRecord()
    {
        string path = Path.Combine(RepoRoot(), "artifacts", "setup", "authorized-isolated-profile.json");
        using var parents = NativeFileSystem.PinDirectories(Path.GetDirectoryName(path)!);
        using var handle = NativeFileSystem.Open(path, shareWrite: false);
        NativeFileSystem.Inspect(handle, path, directory: false);
        using var stream = new FileStream(handle, FileAccess.Read);
        if (stream.Length > 8192) throw new IOException("SETUP_ISOLATED_RECORD_TOO_LARGE");
        return JsonSerializer.Deserialize<SetupIsolationRecord>(stream);
    }

    private static string? ReadRegisteredProfile(string sid)
    {
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var profile = machine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\" + sid);
        return profile?.GetValue("ProfileImagePath", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
    }
}

// Ordinary tests below verify the test runner's refusal gate, not NSIS behavior.
[TestClass]
public sealed class SetupIsolationGateTests
{
    [TestMethod]
    public void Missing_opt_in_refuses_before_record_or_profile_access()
    {
        bool accessed = false;
        var error = Assert.ThrowsExactly<AssertInconclusiveException>(() => SetupIsolationGate.Require(null,
            () => { accessed = true; return null; },
            () => { accessed = true; return ("", ""); },
            _ => { accessed = true; return null; }));
        StringAssert.Contains(error.Message, "SETUP_ISOLATED_ENV_NOT_AUTHORIZED");
        Assert.IsFalse(accessed);
    }

    [TestMethod]
    public void Mismatched_selection_refuses_before_actual_profile_access()
    {
        bool accessed = false;
        Assert.ThrowsExactly<AssertInconclusiveException>(() => SetupIsolationGate.Require("other-profile",
            ApprovedSyntheticRecord, () => { accessed = true; return ("", ""); }, _ => { accessed = true; return null; }));
        Assert.IsFalse(accessed);
    }

    [TestMethod]
    public void Matching_opt_in_still_requires_actual_sid_profile_and_gate_evidence()
    {
        const string selected = @"S-1-5-21-100-200-300-1001|C:\Users\SetupOnly";
        Assert.ThrowsExactly<AssertInconclusiveException>(() => SetupIsolationGate.Require(selected,
            ApprovedSyntheticRecord, () => ("S-1-5-21-100-200-300-1002", @"C:\Users\SetupOnly"), _ => @"C:\Users\SetupOnly"));
        Assert.ThrowsExactly<AssertInconclusiveException>(() => SetupIsolationGate.Require(selected,
            ApprovedSyntheticRecord, () => ("S-1-5-21-100-200-300-1001", @"C:\Users\SomeoneElse"), _ => @"C:\Users\SetupOnly"));
        Assert.ThrowsExactly<AssertInconclusiveException>(() => SetupIsolationGate.Require(selected,
            () => ApprovedSyntheticRecord() with { ExecutionResolutionReference = "" },
            () => ("S-1-5-21-100-200-300-1001", @"C:\Users\SetupOnly"), _ => @"C:\Users\SetupOnly"));
        var accepted = SetupIsolationGate.Require(selected, ApprovedSyntheticRecord,
            () => ("S-1-5-21-100-200-300-1001", @"C:\Users\SetupOnly"), _ => @"C:\Users\SetupOnly");
        Assert.AreEqual(@"C:\Users\SetupOnly", accepted.Profile);
    }

    [TestMethod]
    public void Matching_record_without_a_registered_Windows_profile_is_refused()
    {
        const string selected = @"S-1-5-21-100-200-300-1001|C:\Users\SetupOnly";
        Assert.ThrowsExactly<AssertInconclusiveException>(() => SetupIsolationGate.Require(selected,
            ApprovedSyntheticRecord, () => ("S-1-5-21-100-200-300-1001", @"C:\Users\SetupOnly"), _ => null));
    }

    [TestMethod]
    public void Missing_or_malformed_record_is_refused_before_identity_access()
    {
        const string selected = @"S-1-5-21-100-200-300-1001|C:\Users\SetupOnly";
        bool accessed = false;
        Func<(string Sid, string Profile)> actual = () => { accessed = true; return ("", ""); };
        Func<string, string?> registered = _ => { accessed = true; return null; };
        Assert.ThrowsExactly<AssertInconclusiveException>(() => SetupIsolationGate.Require(selected, () => null, actual, registered));
        Assert.ThrowsExactly<AssertInconclusiveException>(() => SetupIsolationGate.Require(selected,
            () => throw new JsonException("malformed record"), actual, registered));
        Assert.ThrowsExactly<AssertInconclusiveException>(() => SetupIsolationGate.Require(selected,
            () => ApprovedSyntheticRecord() with { Schema = 2 }, actual, registered));
        Assert.IsFalse(accessed);
    }

    [TestMethod]
    public void Missing_each_independent_gate_reference_is_refused()
    {
        const string selected = @"S-1-5-21-100-200-300-1001|C:\Users\SetupOnly";
        var valid = ApprovedSyntheticRecord();
        foreach (var record in new[]
        {
            valid with { ExecutionResolutionReference = "" },
            valid with { ToolVerificationReference = "" },
            valid with { IsolationApprovalReference = "" }
        })
            Assert.ThrowsExactly<AssertInconclusiveException>(() => SetupIsolationGate.Require(selected,
                () => record, () => (valid.Sid, valid.Profile), _ => valid.Profile));
    }

    [TestMethod]
    public void Invalid_harness_hash_is_refused()
    {
        const string selected = @"S-1-5-21-100-200-300-1001|C:\Users\SetupOnly";
        var valid = ApprovedSyntheticRecord();
        foreach (string hash in new[] { "A", new string('G', 64) })
            Assert.ThrowsExactly<AssertInconclusiveException>(() => SetupIsolationGate.Require(selected,
                () => valid with { HarnessSha256 = hash }, () => (valid.Sid, valid.Profile), _ => valid.Profile));
    }

    [TestMethod]
    public void Registered_profile_must_match_record_and_current_identity()
    {
        const string selected = @"S-1-5-21-100-200-300-1001|C:\Users\SetupOnly";
        var valid = ApprovedSyntheticRecord();
        Assert.ThrowsExactly<AssertInconclusiveException>(() => SetupIsolationGate.Require(selected,
            ApprovedSyntheticRecord, () => (valid.Sid, valid.Profile), _ => @"C:\Users\SomeoneElse"));
        Assert.ThrowsExactly<AssertInconclusiveException>(() => SetupIsolationGate.Require(selected,
            ApprovedSyntheticRecord, () => (valid.Sid, valid.Profile), _ => throw new UnauthorizedAccessException()));
    }

    [TestMethod]
    public void Raw_registered_profile_is_expanded_before_comparison()
    {
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.AreEqual(profile, Environment.ExpandEnvironmentVariables("%USERPROFILE%"), ignoreCase: true);
        var record = ApprovedSyntheticRecord() with { Profile = profile };
        try
        {
            var accepted = SetupIsolationGate.Require(record.Sid + "|" + profile,
                () => record, () => (record.Sid, profile), _ => "%USERPROFILE%");
            Assert.AreEqual(profile, accepted.Profile);
        }
        catch (AssertInconclusiveException error)
        {
            Assert.Fail("REGISTERED_PROFILE_EXPANSION_REJECTED: " + error.Message);
        }
    }

    [TestMethod]
    public void Different_sid_cannot_use_current_users_profile_path()
    {
        const string selected = @"S-1-5-21-100-200-300-1002|C:\Users\FixtureOwner";
        var record = ApprovedSyntheticRecord() with { Sid = "S-1-5-21-100-200-300-1002", Profile = @"C:\Users\FixtureOwner" };
        var error = Assert.ThrowsExactly<AssertInconclusiveException>(() => SetupIsolationGate.Require(selected,
            () => record, () => (record.Sid, record.Profile), _ => null));
        StringAssert.Contains(error.Message, "SETUP_ISOLATED_REGISTERED_PROFILE_NOT_MATCHED");
    }

    private static SetupIsolationRecord ApprovedSyntheticRecord() => new(1,
        "S-1-5-21-100-200-300-1001", @"C:\Users\SetupOnly", "synthetic G1", "synthetic G2",
        "synthetic G3", new string('A', 64));
}

internal sealed record SetupIsolationRecord(int Schema, string Sid, string Profile,
    string ExecutionResolutionReference, string ToolVerificationReference,
    string IsolationApprovalReference, string HarnessSha256);

internal static class SetupIsolationGate
{
    internal static SetupIsolationRecord Require(string? selected,
        Func<SetupIsolationRecord?> readRecord, Func<(string Sid, string Profile)> readActual,
        Func<string, string?> readRegisteredProfile)
    {
        if (string.IsNullOrWhiteSpace(selected))
            Assert.Inconclusive("SETUP_ISOLATED_ENV_NOT_AUTHORIZED");
        SetupIsolationRecord? record;
        try { record = readRecord(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            Assert.Inconclusive("SETUP_ISOLATED_RECORD_UNAVAILABLE");
            throw; // Assert.Inconclusive always throws.
        }
        if (record is null || record.Schema != 1 ||
            string.IsNullOrWhiteSpace(record.Sid) || string.IsNullOrWhiteSpace(record.Profile) ||
            !Path.IsPathFullyQualified(record.Profile) ||
            string.IsNullOrWhiteSpace(record.ExecutionResolutionReference) ||
            string.IsNullOrWhiteSpace(record.ToolVerificationReference) ||
            string.IsNullOrWhiteSpace(record.IsolationApprovalReference) ||
            record.HarnessSha256 is not { Length: 64 } || !record.HarnessSha256.All(Uri.IsHexDigit) ||
            !string.Equals(selected, record.Sid + "|" + record.Profile, StringComparison.OrdinalIgnoreCase))
            Assert.Inconclusive("SETUP_ISOLATED_RECORD_NOT_MATCHED");
        var actual = readActual();
        if (!string.Equals(actual.Sid, record.Sid, StringComparison.Ordinal) ||
            !string.Equals(actual.Profile, record.Profile, StringComparison.OrdinalIgnoreCase))
            Assert.Inconclusive("SETUP_ISOLATED_ACTUAL_PROFILE_NOT_MATCHED");
        string? registeredProfile;
        try { registeredProfile = readRegisteredProfile(actual.Sid); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException)
        {
            Assert.Inconclusive("SETUP_ISOLATED_REGISTERED_PROFILE_UNAVAILABLE");
            throw;
        }
        if (registeredProfile is not null)
            registeredProfile = Environment.ExpandEnvironmentVariables(registeredProfile);
        if (string.IsNullOrWhiteSpace(registeredProfile) || !Path.IsPathFullyQualified(registeredProfile) ||
            !string.Equals(registeredProfile, record.Profile, StringComparison.OrdinalIgnoreCase))
            Assert.Inconclusive("SETUP_ISOLATED_REGISTERED_PROFILE_NOT_MATCHED");
        return record;
    }
}
