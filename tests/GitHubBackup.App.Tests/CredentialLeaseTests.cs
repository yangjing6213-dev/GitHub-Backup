using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using GitHubBackup.App;

namespace GitHubBackup.App.Tests;

[TestClass]
public sealed class CredentialLeaseTests
{
    [TestMethod]
    public void Exact_read_uses_only_selected_generic_target_and_frees_native_memory()
    {
        using var native = new FakeNative("TEST_CANARY");
        using var lease = new GitHubCredentialReader(native).ReadExact("Fixture-User");
        Assert.AreEqual("Fixture-User", lease.Login);
        Assert.AreEqual("gh:github.com:Fixture-User", native.Target);
        Assert.AreEqual(1u, native.Type);
        Assert.AreEqual(0u, native.Flags);
        Assert.AreEqual(1, native.ReadCount);
        Assert.AreEqual(1, native.FreeCount);
        Assert.IsFalse(native.Enumerated);
    }

    [TestMethod]
    [DataRow(1)] [DataRow(2560)]
    public void Inclusive_blob_boundaries_are_accepted(int size)
    {
        using var native = new FakeNative(new string('A', size));
        using var lease = new GitHubCredentialReader(native).ReadExact("fixture-user");
        Assert.AreEqual("fixture-user", lease.Login);
        Assert.AreEqual(1, native.FreeCount);
    }

    [TestMethod]
    [DataRow(0)] [DataRow(2561)] [DataRow(-1)]
    public void Out_of_range_or_missing_blob_is_rejected_with_fixed_error(int size)
    {
        using var native = new FakeNative("TEST_CANARY") { SizeOverride = size };
        AuthBoundaryException error = Assert.ThrowsExactly<AuthBoundaryException>(() => new GitHubCredentialReader(native).ReadExact("fixture-user"));
        Assert.AreEqual("AUTH_API_CREDENTIAL_FORMAT_UNSUPPORTED", error.Code);
        Assert.AreEqual(1, native.FreeCount);
    }

    [TestMethod]
    [DataRow("BAD CANARY")] [DataRow("BAD\nCANARY")] [DataRow("BAD\0CANARY")] [DataRow("CAFÉ")]
    public void Invalid_credential_bytes_are_rejected_and_native_memory_freed(string value)
    {
        using var native = new FakeNative(value);
        AuthBoundaryException error = Assert.ThrowsExactly<AuthBoundaryException>(() => new GitHubCredentialReader(native).ReadExact("fixture-user"));
        Assert.AreEqual("AUTH_API_CREDENTIAL_FORMAT_UNSUPPORTED", error.Code);
        Assert.AreEqual(1, native.FreeCount);
        Assert.IsFalse(error.ToString().Contains(value, StringComparison.Ordinal));
    }

    [TestMethod]
    public void Missing_exact_target_does_not_try_shared_slot()
    {
        using var native = new FakeNative("TEST_CANARY") { ReadError = 1168 };
        AuthBoundaryException error = Assert.ThrowsExactly<AuthBoundaryException>(() => new GitHubCredentialReader(native).ReadExact("fixture-user"));
        Assert.AreEqual("AUTH_API_CREDENTIAL_MISSING", error.Code);
        Assert.AreEqual(1, native.ReadCount);
        Assert.AreEqual("gh:github.com:fixture-user", native.Target);
        Assert.IsFalse(native.Enumerated);
    }

    [TestMethod]
    public void Native_access_denial_and_null_allocation_return_fixed_failures()
    {
        using var denied = new FakeNative("TEST_CANARY") { ReadError = 5 };
        AuthBoundaryException deniedError = Assert.ThrowsExactly<AuthBoundaryException>(() => new GitHubCredentialReader(denied).ReadExact("fixture-user"));
        Assert.AreEqual("AUTH_API_CREDENTIAL_READ_FAILED", deniedError.Code);
        Assert.IsFalse(deniedError.ToString().Contains("TEST_CANARY", StringComparison.Ordinal));
        using var empty = new FakeNative("TEST_CANARY") { ReturnNullBuffer = true };
        Assert.AreEqual("AUTH_API_CREDENTIAL_READ_FAILED",
            Assert.ThrowsExactly<AuthBoundaryException>(() => new GitHubCredentialReader(empty).ReadExact("fixture-user")).Code);
        Assert.AreEqual(0, empty.FreeCount);
    }

    [TestMethod]
    public void Incorrect_native_type_or_target_is_rejected()
    {
        using var native = new FakeNative("TEST_CANARY") { ReturnedType = 2 };
        Assert.AreEqual("AUTH_API_CREDENTIAL_FORMAT_UNSUPPORTED",
            Assert.ThrowsExactly<AuthBoundaryException>(() => new GitHubCredentialReader(native).ReadExact("fixture-user")).Code);
        native.ReturnedType = 1;
        native.ReturnedTarget = "gh:github.com:other-user";
        Assert.AreEqual("AUTH_API_CREDENTIAL_FORMAT_UNSUPPORTED",
            Assert.ThrowsExactly<AuthBoundaryException>(() => new GitHubCredentialReader(native).ReadExact("fixture-user")).Code);
        Assert.AreEqual(2, native.FreeCount);
        native.ReturnedTarget = null;
        native.NullTarget = true;
        Assert.AreEqual("AUTH_API_CREDENTIAL_FORMAT_UNSUPPORTED",
            Assert.ThrowsExactly<AuthBoundaryException>(() => new GitHubCredentialReader(native).ReadExact("fixture-user")).Code);
        Assert.AreEqual(3, native.FreeCount);
    }

    [TestMethod]
    public void Native_free_failure_keeps_error_fixed_and_cannot_return_a_lease()
    {
        using var native = new FakeNative("TEST_CANARY") { FreeFault = true };
        Assert.AreEqual("AUTH_API_CREDENTIAL_READ_FAILED",
            Assert.ThrowsExactly<AuthBoundaryException>(() => new GitHubCredentialReader(native).ReadExact("fixture-user")).Code);
        Assert.AreEqual(1, native.FreeCount);
    }

    [TestMethod]
    public void Lease_attaches_only_to_exact_api_origin_and_clears_owned_bytes()
    {
        byte[] secret = Encoding.ASCII.GetBytes("TEST_CANARY");
        var lease = new GitHubCredentialLease("fixture-user", secret);
        foreach (string uri in new[] { "https://api.github.com.evil.test/user", "https://api.github.com.:443/user",
            "https://api.github.com:444/user", "https://name@api.github.com/user", "https://api.github.com/user#fragment",
            "https://github.com/user", "http://api.github.com/user" })
        {
            using var rejected = new HttpRequestMessage(HttpMethod.Get, uri);
            Assert.ThrowsExactly<AuthBoundaryException>(() => lease.AttachAuthorization(rejected));
            Assert.IsNull(rejected.Headers.Authorization);
        }
        using var accepted = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com:443/user");
        lease.AttachAuthorization(accepted);
        Assert.AreEqual(new AuthenticationHeaderValue("Bearer", "TEST_CANARY"), accepted.Headers.Authorization);
        lease.Dispose();
        Assert.IsTrue(secret.All(value => value == 0));
        using var after = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/user");
        Assert.ThrowsExactly<ObjectDisposedException>(() => lease.AttachAuthorization(after));
        Assert.IsNull(after.Headers.Authorization);
    }

    [TestMethod]
    public void Lease_rejects_relative_or_missing_uri_and_existing_authorization()
    {
        byte[] secret = "TEST_CANARY"u8.ToArray();
        using var lease = new GitHubCredentialLease("fixture-user", secret);
        using var relative = new HttpRequestMessage(HttpMethod.Get, new Uri("/user", UriKind.Relative));
        Assert.ThrowsExactly<AuthBoundaryException>(() => lease.AttachAuthorization(relative));
        using var missing = new HttpRequestMessage(HttpMethod.Get, (Uri?)null);
        Assert.ThrowsExactly<AuthBoundaryException>(() => lease.AttachAuthorization(missing));
        using var existing = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/user");
        existing.Headers.Authorization = new AuthenticationHeaderValue("Basic", "sentinel");
        Assert.ThrowsExactly<AuthBoundaryException>(() => lease.AttachAuthorization(existing));
        Assert.AreEqual("Basic sentinel", existing.Headers.Authorization!.ToString());
        lease.Dispose();
        Assert.IsTrue(secret.All(value => value == 0));
    }

    private sealed class FakeNative(string value) : ICredentialNative, IDisposable
    {
        internal string Target = "";
        internal uint Type, Flags;
        internal int ReadCount, FreeCount;
        internal bool Enumerated;
        internal int SizeOverride = int.MinValue;
        internal int ReadError;
        internal uint ReturnedType = 1;
        internal string? ReturnedTarget;
        internal bool FreeFault;
        internal bool ReturnNullBuffer, NullTarget;
        private readonly Dictionary<nint, nint[]> allocations = [];
        public int Read(string target, uint type, uint flags, out nint buffer)
        {
            Target = target; Type = type; Flags = flags; ReadCount++; buffer = 0;
            if (ReadError != 0) return ReadError;
            if (ReturnNullBuffer) return 0;
            byte[] bytes = Encoding.UTF8.GetBytes(value);
            nint name = NullTarget ? 0 : Marshal.StringToHGlobalUni(ReturnedTarget ?? target);
            nint blob = Marshal.AllocHGlobal(Math.Max(1, bytes.Length));
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            buffer = Marshal.AllocHGlobal(Marshal.SizeOf<NativeCredential>());
            Marshal.StructureToPtr(new NativeCredential { Type = ReturnedType, TargetName = name,
                CredentialBlob = SizeOverride == -1 ? 0 : blob,
                CredentialBlobSize = SizeOverride < 0 ? (uint)bytes.Length : (uint)SizeOverride }, buffer, false);
            allocations[buffer] = name == 0 ? [blob] : [name, blob];
            Array.Clear(bytes);
            return 0;
        }
        public void Free(nint buffer)
        {
            FreeCount++;
            foreach (nint item in allocations[buffer]) Marshal.FreeHGlobal(item);
            allocations.Remove(buffer); Marshal.FreeHGlobal(buffer);
            if (FreeFault) throw new InvalidOperationException("synthetic native release failure");
        }
        public int Enumerate(string filter, uint flags, out uint count, out nint buffer)
        { Enumerated = true; throw new AssertFailedException("Enumeration is forbidden for API credentials."); }
        public int Write(ref NativeCredential credential) => throw new AssertFailedException("Write is forbidden.");
        public int Delete(string target, uint type, uint flags) => throw new AssertFailedException("Delete is forbidden.");
        public void Dispose() => Assert.HasCount(0, allocations);
    }
}
