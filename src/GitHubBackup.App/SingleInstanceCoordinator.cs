using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace GitHubBackup.App;

internal sealed record SingleInstanceNames(string MutexName, string PipeName);

internal sealed class SingleInstanceCoordinator : IDisposable
{
    private const int MutexRights = 0x120001; // SYNCHRONIZE, MUTEX_MODIFY_STATE, READ_CONTROL.
    private const PipeAccessRights ClientRights = PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize;
    private static readonly TimeSpan ExchangeTimeout = TimeSpan.FromSeconds(2);
    private readonly SafeFileHandle mutex;
    private readonly NamedPipeServerStream? server;
    private readonly SingleInstanceNames names;
    private readonly SecurityIdentifier user;
    private readonly CancellationTokenSource stopping = new();
    private readonly TaskCompletionSource<Action> activation = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task listener;
    private int disposed;

    private SingleInstanceCoordinator(SafeFileHandle mutex, NamedPipeServerStream? server, SingleInstanceNames names, SecurityIdentifier user)
    {
        this.mutex = mutex; this.server = server; this.names = names; this.user = user;
        listener = server is null ? Task.CompletedTask : Task.Run(ListenAsync);
    }

    internal static SingleInstanceNames CreateNames(string windowsSid)
    {
        string sid = new SecurityIdentifier(windowsSid).Value;
        string suffix = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sid)));
        return new(@"Local\GitHubBackupTool-" + suffix, "GitHubBackupTool-" + suffix);
    }

    internal static SingleInstanceCoordinator Start(string windowsSid) => Start(windowsSid, Guid.Empty);

    // Tests may isolate names, but cannot supply another authority or bypass any native security check.
    internal static SingleInstanceCoordinator Start(string windowsSid, Guid instanceNamespace)
    {
        using var identity = WindowsIdentity.GetCurrent();
        SecurityIdentifier user = identity.User ?? throw new UnauthorizedAccessException("INSTANCE_USER_UNAVAILABLE");
        if (!user.Equals(new SecurityIdentifier(windowsSid))) throw new UnauthorizedAccessException("INSTANCE_USER_MISMATCH");
        SingleInstanceNames names = CreateNames(user.Value);
        if (instanceNamespace != Guid.Empty)
            names = new(names.MutexName + "-" + instanceNamespace.ToString("N"), names.PipeName + "-" + instanceNamespace.ToString("N"));

        var descriptor = new RawSecurityDescriptor(Descriptor(user, MutexRights));
        var bytes = new byte[descriptor.BinaryLength]; descriptor.GetBinaryForm(bytes, 0);
        var pinned = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        SafeFileHandle mutex;
        bool existing;
        try
        {
            var attributes = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), Descriptor = pinned.AddrOfPinnedObject() };
            mutex = CreateMutexEx(ref attributes, names.MutexName, 0, MutexRights);
            int error = Marshal.GetLastWin32Error();
            if (mutex.IsInvalid) { mutex.Dispose(); throw new Win32Exception(error, "INSTANCE_MUTEX_UNAVAILABLE"); }
            existing = error == 183; // ERROR_ALREADY_EXISTS. Never treat an existing object as a new primary.
        }
        finally { pinned.Free(); }

        NamedPipeServerStream? server = null;
        try
        {
            VerifyDescriptor(NativeFileSystem.ReadSecurity(mutex), user, MutexRights);
            if (!existing)
            {
                // CurrentUserOnly ignores a supplied PipeSecurity. Start restrictive, then apply the
                // explicit protected DACL while holding the sole server instance, before listening.
                server = NamedPipeServerStreamAcl.Create(names.PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Message,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly | PipeOptions.FirstPipeInstance, 64, 64, null,
                    HandleInheritability.None, PipeAccessRights.ChangePermissions);
                var security = new PipeSecurity();
                security.SetSecurityDescriptorSddlForm(Descriptor(user, (int)ClientRights), AccessControlSections.Access);
                server.SetAccessControl(security);
                VerifyPipe(server, user);
            }
            return new(mutex, server, names, user);
        }
        catch { server?.Dispose(); mutex.Dispose(); throw; }
    }

    internal bool IsPrimary => server is not null;

    internal void BindActivation(Action activate)
    {
        ArgumentNullException.ThrowIfNull(activate);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if (!IsPrimary || !activation.TrySetResult(activate)) throw new InvalidOperationException("INSTANCE_BIND_UNAVAILABLE");
    }

    internal async Task<bool> ActivatePrimaryAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if (IsPrimary) throw new InvalidOperationException("INSTANCE_ALREADY_PRIMARY");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, stopping.Token);
        timeout.CancelAfter(ExchangeTimeout);
        try
        {
            using var client = new NamedPipeClientStream(".", names.PipeName, ClientRights,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, TokenImpersonationLevel.Identification, HandleInheritability.None);
            await client.ConnectAsync(timeout.Token).ConfigureAwait(false);
            VerifyPipe(client, user);
            client.ReadMode = PipeTransmissionMode.Message;
            await client.WriteAsync("ACTIVATE"u8.ToArray(), timeout.Token).ConfigureAwait(false);
            return await ReadFrameAsync(client, "ACK"u8.ToArray(), timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return false; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private async Task ListenAsync()
    {
        try
        {
            while (!stopping.IsCancellationRequested)
            {
                await server!.WaitForConnectionAsync(stopping.Token).ConfigureAwait(false);
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stopping.Token);
                    timeout.CancelAfter(ExchangeTimeout);
                    if (!await ReadFrameAsync(server, "ACTIVATE"u8.ToArray(), timeout.Token).ConfigureAwait(false)) continue;
                    bool sameUser = false;
                    server.RunAsClient(() =>
                    {
                        using var client = WindowsIdentity.GetCurrent(true);
                        sameUser = client?.User is { } clientSid && user.Equals(clientSid);
                    });
                    if (!sameUser) continue;
                    Action activate = await activation.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
                    timeout.Token.ThrowIfCancellationRequested();
                    activate(); // MainForm posts to its UI thread; never waits for that thread here.
                    await server.WriteAsync("ACK"u8.ToArray(), timeout.Token).ConfigureAwait(false);
                    // Disconnect discards unread output. Let the client read ACK and close; a silent
                    // client still cannot occupy the listener beyond the same exchange deadline.
                    await server.ReadAsync(new byte[1], timeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                catch (SecurityException) { } // Anonymous/unqueryable client token is never an authorized SID.
                catch (InvalidOperationException) { } // Window/pipe can close while a request is in flight.
                finally { server.Disconnect(); } // Reset even after EOF changes IsConnected to false.
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
        catch (IOException) { } // Keep the mutex: a failed listener must never create a competing window.
    }

    private static async Task<bool> ReadFrameAsync(PipeStream pipe, byte[] expected, CancellationToken token)
    {
        // One message only; one excess byte detects an oversized frame without unbounded buffering.
        var buffer = new byte[expected.Length + 1];
        int count = await pipe.ReadAsync(buffer, token).ConfigureAwait(false);
        return count == expected.Length && pipe.IsMessageComplete && buffer.AsSpan(0, count).SequenceEqual(expected);
    }

    private static string Descriptor(SecurityIdentifier user, int rights) =>
        $"O:{user.Value}D:P(A;;0x{rights:x};;;{user.Value})(A;;0x{rights:x};;;SY)(A;;0x{rights:x};;;BA)";

    private static void VerifyPipe(PipeStream pipe, SecurityIdentifier user) =>
        VerifyDescriptor(new RawSecurityDescriptor(pipe.GetAccessControl().GetSecurityDescriptorBinaryForm(), 0), user, (int)ClientRights);

    private static void VerifyDescriptor(RawSecurityDescriptor raw, SecurityIdentifier user, int rights)
    {
        var allowed = new HashSet<string> { user.Value, "S-1-5-18", "S-1-5-32-544" };
        if (raw.Owner is null || !user.Equals(raw.Owner) || !raw.ControlFlags.HasFlag(ControlFlags.DiscretionaryAclPresent)
            || !raw.ControlFlags.HasFlag(ControlFlags.DiscretionaryAclProtected)
            || raw.DiscretionaryAcl is null || raw.DiscretionaryAcl.Count != allowed.Count)
            throw new UnauthorizedAccessException("INSTANCE_SECURITY_REJECTED");
        foreach (GenericAce ace in raw.DiscretionaryAcl)
            if (ace is not CommonAce { AceQualifier: AceQualifier.AccessAllowed, IsCallback: false, AceFlags: AceFlags.None } common
                || common.AccessMask != rights || !allowed.Remove(common.SecurityIdentifier.Value))
                throw new UnauthorizedAccessException("INSTANCE_SECURITY_REJECTED");
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        stopping.Cancel();
        try { listener.GetAwaiter().GetResult(); }
        finally { server?.Dispose(); mutex.Dispose(); stopping.Dispose(); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes { internal int Length; internal IntPtr Descriptor; internal int Inherit; }
    [DllImport("kernel32.dll", EntryPoint = "CreateMutexExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateMutexEx(ref SecurityAttributes attributes, string name, uint flags, uint desiredAccess);
}
