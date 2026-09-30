using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using GitHubBackup.App;
using Microsoft.Win32.SafeHandles;

namespace GitHubBackup.App.Tests;

[TestClass]
[DoNotParallelize]
public sealed class SingleInstanceCoordinatorTests
{
    private static string CurrentSid { get { using var identity = WindowsIdentity.GetCurrent(); return identity.User!.Value; } }

    [TestMethod]
    public void Names_are_stable_bounded_and_separate_windows_users()
    {
        var first = SingleInstanceCoordinator.CreateNames("S-1-5-21-111-222-333-1001");
        Assert.AreEqual(first, SingleInstanceCoordinator.CreateNames("S-1-5-21-111-222-333-1001"));
        var other = SingleInstanceCoordinator.CreateNames("S-1-5-21-111-222-333-1002");
        Assert.AreNotEqual(first.MutexName, other.MutexName); Assert.AreNotEqual(first.PipeName, other.PipeName);
        Assert.StartsWith(@"Local\GitHubBackupTool-", first.MutexName);
        Assert.IsLessThan(200, first.MutexName.Length); Assert.IsLessThan(200, first.PipeName.Length);
        Assert.DoesNotContain("S-1-5-", first.MutexName); Assert.DoesNotContain("\\", first.PipeName);
        Assert.ThrowsExactly<ArgumentException>(() => SingleInstanceCoordinator.CreateNames("not a SID"));
    }

    [TestMethod]
    public void Caller_supplied_sid_cannot_grant_access_as_another_user() =>
        Assert.ThrowsExactly<UnauthorizedAccessException>(() => SingleInstanceCoordinator.Start("S-1-5-21-111-222-333-9999", Guid.NewGuid()));

    [TestMethod]
    public async Task Secondary_activates_primary_once_with_acknowledgement()
    {
        Guid scope = Guid.NewGuid(); int activations = 0;
        using var primary = SingleInstanceCoordinator.Start(CurrentSid, scope);
        using var secondary = SingleInstanceCoordinator.Start(CurrentSid, scope);
        Assert.IsTrue(primary.IsPrimary); Assert.IsFalse(secondary.IsPrimary);
        primary.BindActivation(() => Interlocked.Increment(ref activations));
        Assert.IsTrue(await secondary.ActivatePrimaryAsync(CancellationToken.None));
        Assert.AreEqual(1, activations);
        Assert.IsTrue(await secondary.ActivatePrimaryAsync(CancellationToken.None));
        Assert.AreEqual(2, activations);
    }

    [TestMethod]
    public async Task Activation_waits_for_binding_and_never_acknowledges_a_missing_callback()
    {
        Guid scope = Guid.NewGuid(); int activations = 0;
        using var primary = SingleInstanceCoordinator.Start(CurrentSid, scope);
        using var secondary = SingleInstanceCoordinator.Start(CurrentSid, scope);
        var activation = secondary.ActivatePrimaryAsync(CancellationToken.None);
        Assert.IsFalse(activation.IsCompleted);
        primary.BindActivation(() => Interlocked.Increment(ref activations));
        Assert.IsTrue(await activation); Assert.AreEqual(1, activations);
    }

    [TestMethod]
    public async Task Missing_binding_times_out_without_retaining_a_late_activation()
    {
        Guid scope = Guid.NewGuid(); int activations = 0;
        using var primary = SingleInstanceCoordinator.Start(CurrentSid, scope);
        using var secondary = SingleInstanceCoordinator.Start(CurrentSid, scope);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        Assert.IsFalse(await secondary.ActivatePrimaryAsync(CancellationToken.None).WaitAsync(timeout.Token));
        primary.BindActivation(() => Interlocked.Increment(ref activations));
        Assert.IsTrue(await secondary.ActivatePrimaryAsync(timeout.Token));
        Assert.AreEqual(1, activations);
    }

    [TestMethod]
    public async Task Actual_mutex_and_pipe_have_protected_explicit_approved_dacls()
    {
        Guid scope = Guid.NewGuid(); var names = Names(scope);
        using var primary = SingleInstanceCoordinator.Start(CurrentSid, scope);
        using var mutex = OpenMutex(0x20000, false, names.MutexName);
        Assert.IsFalse(mutex.IsInvalid);
        AssertDescriptor(NativeFileSystem.ReadSecurity(mutex), 0x120001);
        using var client = Client(names.PipeName);
        await client.ConnectAsync(3000);
        AssertDescriptor(new RawSecurityDescriptor(client.GetAccessControl().GetSecurityDescriptorBinaryForm(), 0), 0x12019b);
    }

    [TestMethod]
    [DataRow(false)] [DataRow(true)]
    public void Existing_mutex_with_broad_or_unprotected_dacl_is_rejected(bool broad)
    {
        Guid scope = Guid.NewGuid(); var names = Names(scope);
        using var mutex = CreateMutex(names.MutexName, broad
            ? $"O:{CurrentSid}D:P(A;;0x120001;;;{CurrentSid})(A;;0x120001;;;SY)(A;;0x120001;;;BA)(A;;0x120001;;;WD)"
            : $"O:{CurrentSid}D:(A;;0x120001;;;{CurrentSid})(A;;0x120001;;;SY)(A;;0x120001;;;BA)");
        Assert.ThrowsExactly<UnauthorizedAccessException>(() => SingleInstanceCoordinator.Start(CurrentSid, scope));
    }

    [TestMethod]
    public void Preexisting_pipe_is_rejected_without_leaving_a_primary_mutex()
    {
        Guid scope = Guid.NewGuid(); var names = Names(scope);
        using (var occupied = new NamedPipeServerStream(names.PipeName))
            Assert.Throws<IOException>(() => SingleInstanceCoordinator.Start(CurrentSid, scope));
        using var recovered = SingleInstanceCoordinator.Start(CurrentSid, scope);
        Assert.IsTrue(recovered.IsPrimary);
    }

    [TestMethod]
    [DataRow("ACTIVAT")] [DataRow("ACTIVATE\n")] [DataRow("OTHER")]
    [DataRow("ACTIVATE trailing data")] [DataRow("oversized")]
    public async Task Invalid_or_oversized_messages_are_rejected_without_activation_and_listener_recovers(string message)
    {
        Guid scope = Guid.NewGuid(); int activations = 0;
        using var primary = SingleInstanceCoordinator.Start(CurrentSid, scope);
        primary.BindActivation(() => Interlocked.Increment(ref activations));
        using (var client = Client(Names(scope).PipeName))
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
        {
            await client.ConnectAsync(timeout.Token);
            try
            {
                await client.WriteAsync(Encoding.ASCII.GetBytes(message == "oversized" ? new string('X', 4096) : message), timeout.Token);
                Assert.AreEqual(0, await client.ReadAsync(new byte[4], timeout.Token));
            }
            catch (IOException) { }
        }
        Assert.AreEqual(0, activations);
        using var secondary = SingleInstanceCoordinator.Start(CurrentSid, scope);
        Assert.IsTrue(await secondary.ActivatePrimaryAsync(CancellationToken.None));
        Assert.AreEqual(1, activations);
    }

    [TestMethod]
    public async Task Silent_connected_client_times_out_and_does_not_block_next_activation()
    {
        Guid scope = Guid.NewGuid(); int activations = 0;
        using var primary = SingleInstanceCoordinator.Start(CurrentSid, scope);
        primary.BindActivation(() => Interlocked.Increment(ref activations));
        using var client = Client(Names(scope).PipeName);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        await client.ConnectAsync(timeout.Token);
        try { Assert.AreEqual(0, await client.ReadAsync(new byte[4], timeout.Token)); }
        catch (IOException) { }
        using var secondary = SingleInstanceCoordinator.Start(CurrentSid, scope);
        Assert.IsTrue(await secondary.ActivatePrimaryAsync(timeout.Token));
        Assert.AreEqual(1, activations);
    }

    [TestMethod]
    public async Task Anonymous_client_cannot_activate_and_does_not_stop_the_listener()
    {
        Guid scope = Guid.NewGuid(); int activations = 0;
        using var primary = SingleInstanceCoordinator.Start(CurrentSid, scope);
        primary.BindActivation(() => Interlocked.Increment(ref activations));
        using (var client = new NamedPipeClientStream(".", Names(scope).PipeName,
            PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize, PipeOptions.Asynchronous,
            TokenImpersonationLevel.Anonymous, HandleInheritability.None))
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
        {
            await client.ConnectAsync(timeout.Token);
            try
            {
                await client.WriteAsync("ACTIVATE"u8.ToArray(), timeout.Token);
                Assert.AreEqual(0, await client.ReadAsync(new byte[4], timeout.Token));
            }
            catch (IOException) { }
        }
        Assert.AreEqual(0, activations);
        using var secondary = SingleInstanceCoordinator.Start(CurrentSid, scope);
        Assert.IsTrue(await secondary.ActivatePrimaryAsync(CancellationToken.None));
        Assert.AreEqual(1, activations);
    }

    [TestMethod]
    public async Task Acknowledgement_remains_readable_until_client_closes_or_exchange_times_out()
    {
        Guid scope = Guid.NewGuid(); int activations = 0;
        using var primary = SingleInstanceCoordinator.Start(CurrentSid, scope);
        primary.BindActivation(() => Interlocked.Increment(ref activations));
        using var client = Client(Names(scope).PipeName);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await client.ConnectAsync(timeout.Token);
        await client.WriteAsync("ACTIVATE"u8.ToArray(), timeout.Token);
        // Delayed reading exposes Disconnect discarding an ACK still buffered in the native pipe.
        await Task.Delay(100, timeout.Token);
        var reply = new byte[4]; int count = await client.ReadAsync(reply, timeout.Token);
        Assert.AreEqual("ACK", Encoding.ASCII.GetString(reply, 0, count));
        Assert.AreEqual(1, activations);
    }

    [TestMethod]
    [DataRow("NACK")] [DataRow("ACK trailing data")] [DataRow("silent")]
    public async Task Failed_or_missing_ack_is_not_success_and_request_is_the_fixed_activate_frame(string reply)
    {
        Guid scope = Guid.NewGuid(); var names = Names(scope);
        using var mutex = SafeMutex(names.MutexName);
        using var server = ProtectedServer(names.PipeName);
        using var secondary = SingleInstanceCoordinator.Start(CurrentSid, scope);
        Assert.IsFalse(secondary.IsPrimary);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        Task<bool> activation = secondary.ActivatePrimaryAsync(CancellationToken.None);
        await server.WaitForConnectionAsync(timeout.Token);
        var request = new byte[9]; int count = await server.ReadAsync(request, timeout.Token);
        Assert.AreEqual("ACTIVATE", Encoding.ASCII.GetString(request, 0, count));
        Assert.IsTrue(server.IsMessageComplete);
        if (reply != "silent") await server.WriteAsync(Encoding.ASCII.GetBytes(reply), timeout.Token);
        Assert.IsFalse(await activation.WaitAsync(timeout.Token));
    }

    [TestMethod]
    public async Task Unsafe_existing_pipe_is_rejected_before_sending_any_command()
    {
        Guid scope = Guid.NewGuid(); var names = Names(scope);
        using var mutex = SafeMutex(names.MutexName);
        using var server = ProtectedServer(names.PipeName, broad: true);
        using var secondary = SingleInstanceCoordinator.Start(CurrentSid, scope);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Task<bool> activation = secondary.ActivatePrimaryAsync(CancellationToken.None);
        await server.WaitForConnectionAsync(timeout.Token);
        Assert.IsFalse(await activation);
        Assert.AreEqual(0, await server.ReadAsync(new byte[9], timeout.Token));
    }

    [TestMethod]
    public async Task Unavailable_primary_is_bounded_and_caller_cancellation_is_observed()
    {
        Guid scope = Guid.NewGuid();
        using var mutex = SafeMutex(Names(scope).MutexName);
        using var secondary = SingleInstanceCoordinator.Start(CurrentSid, scope);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        Assert.IsFalse(await secondary.ActivatePrimaryAsync(CancellationToken.None).WaitAsync(timeout.Token));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => secondary.ActivatePrimaryAsync(cancelled.Token));
    }

    [TestMethod]
    [DataRow(false)] [DataRow(true)]
    public async Task Dispose_awaits_listener_shutdown_and_releases_owned_names(bool connected)
    {
        Guid scope = Guid.NewGuid();
        var primary = SingleInstanceCoordinator.Start(CurrentSid, scope);
        using var client = Client(Names(scope).PipeName);
        if (connected) await client.ConnectAsync(3000);
        await Task.Run(primary.Dispose).WaitAsync(TimeSpan.FromSeconds(5));
        primary.Dispose();
        using var next = SingleInstanceCoordinator.Start(CurrentSid, scope);
        Assert.IsTrue(next.IsPrimary);
    }

    [TestMethod]
    [DataRow(false)] [DataRow(true)]
    public async Task Dispose_cancels_waiting_for_binding_or_acknowledgement_read_completion(bool bound)
    {
        Guid scope = Guid.NewGuid();
        using var primary = SingleInstanceCoordinator.Start(CurrentSid, scope);
        if (bound) primary.BindActivation(() => { });
        using var client = Client(Names(scope).PipeName);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await client.ConnectAsync(timeout.Token);
        await client.WriteAsync("ACTIVATE"u8.ToArray(), timeout.Token);
        if (bound)
        {
            var ack = new byte[4]; int count = await client.ReadAsync(ack, timeout.Token);
            Assert.AreEqual("ACK", Encoding.ASCII.GetString(ack, 0, count));
        }
        await Task.Run(primary.Dispose).WaitAsync(timeout.Token);
        using var next = SingleInstanceCoordinator.Start(CurrentSid, scope);
        Assert.IsTrue(next.IsPrimary);
    }

    private static SingleInstanceNames Names(Guid scope)
    {
        var names = SingleInstanceCoordinator.CreateNames(CurrentSid);
        return new(names.MutexName + "-" + scope.ToString("N"), names.PipeName + "-" + scope.ToString("N"));
    }

    private static NamedPipeClientStream Client(string name) => new(".", name,
        PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly,
        TokenImpersonationLevel.Identification, HandleInheritability.None);

    private static void AssertDescriptor(RawSecurityDescriptor raw, int rights)
    {
        Assert.AreEqual(CurrentSid, raw.Owner!.Value);
        Assert.IsTrue(raw.ControlFlags.HasFlag(ControlFlags.DiscretionaryAclPresent));
        Assert.IsTrue(raw.ControlFlags.HasFlag(ControlFlags.DiscretionaryAclProtected));
        Assert.IsNotNull(raw.DiscretionaryAcl); Assert.HasCount(3, raw.DiscretionaryAcl);
        var allowed = new HashSet<string> { CurrentSid, "S-1-5-18", "S-1-5-32-544" };
        foreach (var ace in raw.DiscretionaryAcl.Cast<CommonAce>())
        {
            Assert.AreEqual(AceQualifier.AccessAllowed, ace.AceQualifier); Assert.IsFalse(ace.IsCallback);
            Assert.AreEqual(AceFlags.None, ace.AceFlags); Assert.AreEqual(rights, ace.AccessMask);
            Assert.IsTrue(allowed.Remove(ace.SecurityIdentifier.Value));
        }
        Assert.IsEmpty(allowed);
    }

    private static NamedPipeServerStream ProtectedServer(string name, bool broad = false)
    {
        var security = new PipeSecurity();
        security.SetSecurityDescriptorSddlForm($"O:{CurrentSid}D:P(A;;0x12019b;;;{CurrentSid})(A;;0x12019b;;;SY)(A;;0x12019b;;;BA)" + (broad ? "(A;;0x12019b;;;WD)" : ""));
        return NamedPipeServerStreamAcl.Create(name, PipeDirection.InOut, 1, PipeTransmissionMode.Message,
            PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance, 64, 64, security);
    }

    private static SafeFileHandle SafeMutex(string name) => CreateMutex(name,
        $"O:{CurrentSid}D:P(A;;0x120001;;;{CurrentSid})(A;;0x120001;;;SY)(A;;0x120001;;;BA)");

    private static SafeFileHandle CreateMutex(string name, string sddl)
    {
        var raw = new RawSecurityDescriptor(sddl); var bytes = new byte[raw.BinaryLength]; raw.GetBinaryForm(bytes, 0);
        var pinned = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try
        {
            var attributes = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), Descriptor = pinned.AddrOfPinnedObject() };
            var mutex = CreateMutexEx(ref attributes, name, 0, 0x120001);
            Assert.IsFalse(mutex.IsInvalid); return mutex;
        }
        finally { pinned.Free(); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes { internal int Length; internal IntPtr Descriptor; internal int Inherit; }
    [DllImport("kernel32.dll", EntryPoint = "CreateMutexExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateMutexEx(ref SecurityAttributes attributes, string name, uint flags, uint desiredAccess);
    [DllImport("kernel32.dll", EntryPoint = "OpenMutexW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle OpenMutex(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inherit, string name);
}
