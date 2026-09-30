using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace GitHubBackup.App;

internal delegate RequestJobLease AttachProcessDelegate(OperationJob operation, SafeProcessHandle process);

internal sealed class ProcessRunner(AttachProcessDelegate? attachProcess = null, RunLogger? logger = null) : IProcessRunner
{
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(5);
    public async Task<ProcessResult> RunAsync(ProcessRequest request, OperationJob job, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var environment = request.Environment as RuntimeEnvironment;
        environment?.RevalidateAuthentication();
        try { return await RunCoreAsync(request,job,progress,cancellationToken).ConfigureAwait(false); }
        finally { environment?.RevalidateAuthentication(); }
    }
    private async Task<ProcessResult> RunCoreAsync(ProcessRequest request, OperationJob job, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        using var cancellation = cancellationToken.Register(job.BeginCancellation);
        if (cancellationToken.IsCancellationRequested || job.IsCancellationRequested) return new(null, false, true, [], []);
        using IDisposable? environmentUse = (request.Environment as RuntimeEnvironment)?.Owner?.AcquireRequest(job,request);
        if (request.Timeout <= TimeSpan.Zero || request.Timeout > TimeSpan.FromDays(30) || request.MaxStandardOutputBytes < 0)
            throw new ArgumentException("PROCESS_REQUEST_INVALID");
        using PathLease working = NativeFileSystem.PinDirectories(request.WorkingDirectory);
        using var pipes = NativePipeSet.Create();
        NativeStartResult start = StartSuspended(request, job, pipes, attachProcess ?? Attach, cancellationToken);
        if (start.CancelledBeforeResume) return new(null, false, true, [], []);
        using NativeProcessHandles handles = start.Handles!;
        pipes.CloseChildHandles();
        var stdout = new List<string>(); var stderr = new List<string>();
        FileStream? raw = null; NativeFileIdentity? rawIdentity = null; PathLease? rawParent = null;
        bool keepOutput = false;
        try
        {
            if (request.OutputMode is ProcessOutputMode.CapturedFile or ProcessOutputMode.BinaryFile)
            {
                if (request.StandardOutputFile is null) throw new ArgumentException("PROCESS_STDOUT_FILE_REQUIRED");
                string output = NativeFileSystem.CanonicalPath(request.StandardOutputFile);
                if (!string.Equals(Path.GetDirectoryName(output), NativeFileSystem.CanonicalPath(request.WorkingDirectory), StringComparison.OrdinalIgnoreCase))
                    throw new UnauthorizedAccessException("PROCESS_STDOUT_DIRECTORY_REJECTED");
                rawParent = SummaryStore.RequirePrivateDirectory(Path.GetDirectoryName(output)!);
                raw = AclPolicy.CreateRestrictedFile(output, WindowsIdentity.GetCurrent().User!);
                rawIdentity = NativeFileSystem.Inspect(raw.SafeFileHandle, output, false);
            }
            Task outputDrain = raw is null ? DrainTextAsync(pipes.Output, stdout, request.OutputMode, progress) : DrainRawAsync(pipes.Output, raw, request.MaxStandardOutputBytes);
            Task errorDrain = DrainTextAsync(pipes.Error, stderr, request.EphemeralStandardError || request.OutputMode == ProcessOutputMode.EphemeralText ? ProcessOutputMode.EphemeralText : ProcessOutputMode.TextTail, progress);
            Task drains = Task.WhenAll(outputDrain, errorDrain);
            Task exited = WaitExitAsync(handles.Process);
            // A process may exit before descendants close inherited pipe handles.
            // Timeout covers the root AND both drains; it is not disabled on root exit.
            Task completed = Task.WhenAll(exited, drains);
            using var timerStop = new CancellationTokenSource();
            Task timeout = Task.Delay(request.Timeout, timerStop.Token);
            Task fault = FirstDrainFailureAsync(outputDrain, errorDrain, timerStop.Token);
            Task terminal = await Task.WhenAny(completed, timeout, job.Closing, fault).ConfigureAwait(false);
            bool wasCancelled = job.IsCancellationRequested;
            bool timedOut = !wasCancelled && terminal == timeout;
            try
            {
                if (wasCancelled || timedOut || terminal == fault)
                {
                    if (wasCancelled) await job.CancelAllAsync(CleanupTimeout).ConfigureAwait(false);
                    else await handles.Job.TerminateAndWaitAsync(handles.Process, CleanupTimeout).ConfigureAwait(false);
                    if (await Task.WhenAny(drains, Task.Delay(CleanupTimeout)).ConfigureAwait(false) != drains)
                        throw new IOException("PROCESS_PIPE_DRAIN_TIMEOUT");
                    await drains.ConfigureAwait(false);
                    await exited.ConfigureAwait(false);
                    return new(null, timedOut, wasCancelled, stdout, stderr);
                }
                await completed.ConfigureAwait(false);
                // Finish any descendant that closed its pipes but stayed running.
                await handles.Job.TerminateAndWaitAsync(handles.Process, CleanupTimeout).ConfigureAwait(false);
                if (!ProcessNative.GetExitCodeProcess(handles.Process, out uint exitCode)) throw new Win32Exception(Marshal.GetLastWin32Error(), "PROCESS_EXIT_QUERY_FAILED");
                if (raw is not null) await raw.FlushAsync().ConfigureAwait(false);
                keepOutput = true;
                return new(unchecked((int)exitCode), false, false, stdout, stderr);
            }
            finally { timerStop.Cancel(); }
        }
        catch
        {
            await handles.Job.TerminateAndWaitAsync(handles.Process, CleanupTimeout).ConfigureAwait(false);
            throw;
        }
        finally
        {
            if (raw is not null) await raw.DisposeAsync().ConfigureAwait(false);
            try
            {
                if (!keepOutput && rawIdentity is not null) AtomicFile.DeleteOwnedFile(request.StandardOutputFile!, rawIdentity.Value);
            }
            finally { rawParent?.Dispose(); }
        }
    }

    private static async Task FirstDrainFailureAsync(Task first, Task second, CancellationToken stopped)
    {
        Task early = await Task.WhenAny(first, second).ConfigureAwait(false);
        if (early.IsFaulted || early.IsCanceled) return;
        Task late = early == first ? second : first;
        try { await late.ConfigureAwait(false); } catch { return; }
        try { await Task.Delay(Timeout.InfiniteTimeSpan, stopped).ConfigureAwait(false); } catch (OperationCanceledException) { }
    }

    private async Task DrainTextAsync(Stream input, List<string> lines, ProcessOutputMode mode, IProgress<string>? progress)
    {
        var safe = new SafeTextStream(); byte[] buffer = new byte[4096]; var line = new StringBuilder();
        async Task Emit(string chunk)
        {
            if (mode == ProcessOutputMode.EphemeralText) { if (chunk.Length != 0) progress?.Report(chunk); return; }
            foreach (char c in chunk)
            {
                if (c != '\n' && line.Length < 8192) line.Append(c);
                if (c == '\n' || line.Length == 8192)
                {
                    string value = line.ToString(); line.Clear();
                    lines.Add(value); if (lines.Count > 2000) lines.RemoveAt(0);
                    progress?.Report(value);
                    if (logger is not null) await logger.WriteAsync(LogLevel.Info, "process", null, value, default).ConfigureAwait(false);
                }
            }
        }
        int read;
        while ((read = await input.ReadAsync(buffer).ConfigureAwait(false)) != 0) await Emit(safe.Push(buffer.AsSpan(0, read))).ConfigureAwait(false);
        await Emit(safe.Complete()).ConfigureAwait(false);
        if (mode != ProcessOutputMode.EphemeralText && line.Length != 0) await Emit("\n").ConfigureAwait(false);
    }

    private static async Task DrainRawAsync(Stream input, Stream output, long ceiling)
    {
        byte[] buffer = new byte[65536]; long total = 0; int read;
        while ((read = await input.ReadAsync(buffer).ConfigureAwait(false)) != 0)
        {
            if (read > ceiling - total) throw new IOException("PROCESS_STDOUT_LIMIT_EXCEEDED");
            await output.WriteAsync(buffer.AsMemory(0, read)).ConfigureAwait(false); total += read;
        }
    }
    private static async Task WaitExitAsync(SafeProcessHandle process)
    {
        while (true)
        {
            uint state = ProcessNative.WaitForSingleObject(process, 0);
            if (state == 0) return;
            if (state != 258) throw new Win32Exception(Marshal.GetLastWin32Error(), "PROCESS_WAIT_FAILED");
            await Task.Delay(10).ConfigureAwait(false);
        }
    }
    private static RequestJobLease Attach(OperationJob operation, SafeProcessHandle process)
    {
        var lease = operation.CreateRequestJob();
        try { lease.Assign(process); return lease; } catch { lease.Dispose(); throw; }
    }

    internal static NativeStartResult StartSuspended(ProcessRequest request, OperationJob operation, NativePipeSet pipes, AttachProcessDelegate attach, CancellationToken token)
    {
        using ExecutableLease executable = ExecutableTrust.Acquire(request.FilePath, request.ExpectedExecutableIdentity ?? throw new InvalidOperationException("PROCESS_EXECUTABLE_IDENTITY_REQUIRED"));
        using var startup = StartupInfoBuffer.Create(pipes.ChildHandles);
        using var environment = EnvironmentBlock.Create(request.Environment);
        if (token.IsCancellationRequested || operation.IsCancellationRequested) return new(null, true);
        if (!ProcessNative.CreateProcess(request.FilePath, new StringBuilder(WindowsCommandLine.Serialize([request.FilePath, ..request.Arguments])), IntPtr.Zero, IntPtr.Zero, true,
            0x4 | 0x400 | 0x08000000 | 0x80000, environment.Pointer, request.WorkingDirectory, ref startup.Value, out ProcessNative.ProcessInformation info))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "PROCESS_CREATE_FAILED");
        var handles = new NativeProcessHandles(info.Process, info.Thread);
        RequestJobLease? lease = null;
        try
        {
            lease = attach(operation, handles.Process);
            if (!lease.ResumeAssignedOrCancel(handles.Thread, token))
            {
                lease.TerminateAndWaitAsync(handles.Process, CleanupTimeout).GetAwaiter().GetResult();
                lease.Dispose(); handles.Dispose(); return new(null, true);
            }
            handles.Attach(lease); return new(handles, false);
        }
        catch (Exception error)
        {
            try
            {
                if (lease is not null) lease.TerminateAndWaitAsync(handles.Process, CleanupTimeout).GetAwaiter().GetResult();
                else handles.TerminateUnassignedAndWait(CleanupTimeout);
            }
            finally { lease?.Dispose(); handles.Dispose(); }
            if (error is OperationCanceledException && (token.IsCancellationRequested || operation.IsCancellationRequested)) return new(null, true);
            throw;
        }
    }
}

internal sealed record NativeStartResult(NativeProcessHandles? Handles, bool CancelledBeforeResume);
internal sealed class NativeProcessHandles : IDisposable
{
    internal SafeProcessHandle Process { get; }
    internal SafeThreadHandle Thread { get; }
    internal RequestJobLease Job { get; private set; } = null!;
    internal NativeProcessHandles(IntPtr process, IntPtr thread) { Process = new(process, true); Thread = new(thread); }
    internal void Attach(RequestJobLease job) => Job = job;
    internal void TerminateUnassignedAndWait(TimeSpan timeout)
    {
        if (!ProcessNative.TerminateProcess(Process, 1) || ProcessNative.WaitForSingleObject(Process, (uint)timeout.TotalMilliseconds) != 0)
            throw new IOException("PROCESS_TERMINATION_FAILED");
    }
    public void Dispose() { Job?.Dispose(); Thread.Dispose(); Process.Dispose(); }
}

internal static class WindowsCommandLine
{
    internal static string Serialize(IEnumerable<string> arguments) => string.Join(' ', arguments.Select(Quote));
    private static string Quote(string value)
    {
        if (value.Contains('\0')) throw new ArgumentException("PROCESS_ARGUMENT_INVALID");
        var output = new StringBuilder("\""); int slashes = 0;
        foreach (char c in value)
        {
            if (c == '\\') { slashes++; continue; }
            output.Append('\\', c == '"' ? slashes * 2 + 1 : slashes).Append(c); slashes = 0;
        }
        return output.Append('\\', slashes * 2).Append('"').ToString();
    }
}
internal sealed class EnvironmentBlock : IDisposable
{
    internal IntPtr Pointer { get; private set; }
    internal static EnvironmentBlock Create(IReadOnlyDictionary<string, string?> environment)
    {
        ChildEnvironmentBuilder.ValidateKeys(environment);
        if (environment.Any(x => string.IsNullOrEmpty(x.Key) || x.Key.IndexOfAny(['=', '\0']) >= 0 || x.Value?.Contains('\0') == true)
            || environment.Keys.Distinct(StringComparer.OrdinalIgnoreCase).Count() != environment.Count)
            throw new ArgumentException("PROCESS_ENVIRONMENT_INVALID");
        string block = string.Join('\0', environment.Where(x => x.Value is not null).OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase).Select(x => x.Key + "=" + x.Value)) + "\0\0";
        return new() { Pointer = Marshal.StringToHGlobalUni(block) };
    }
    public void Dispose() { Marshal.FreeHGlobal(Pointer); Pointer = IntPtr.Zero; }
}
internal sealed class NativePipeSet : IDisposable
{
    internal FileStream Output { get; private set; } = null!;
    internal FileStream Error { get; private set; } = null!;
    private SafeFileHandle? outputWrite, errorWrite;
    internal IntPtr[] ChildHandles => [outputWrite!.DangerousGetHandle(), errorWrite!.DangerousGetHandle()];
    internal static NativePipeSet Create()
    {
        var pipes = new NativePipeSet();
        try { (pipes.Output, pipes.outputWrite) = CreatePipe(); (pipes.Error, pipes.errorWrite) = CreatePipe(); return pipes; }
        catch { pipes.Dispose(); throw; }
    }
    private static (FileStream, SafeFileHandle) CreatePipe()
    {
        var security = new ProcessNative.SecurityAttributes { Length = Marshal.SizeOf<ProcessNative.SecurityAttributes>(), Inherit = 1 };
        if (!ProcessNative.CreatePipe(out SafeFileHandle read, out SafeFileHandle write, ref security, 0)) throw new Win32Exception(Marshal.GetLastWin32Error(), "PROCESS_PIPE_CREATE_FAILED");
        try
        {
            if (!ProcessNative.SetHandleInformation(read, 1, 0)) throw new Win32Exception(Marshal.GetLastWin32Error(), "PROCESS_PIPE_CONFIG_FAILED");
            return (new FileStream(read, FileAccess.Read, 4096, false), write);
        }
        catch { read.Dispose(); write.Dispose(); throw; }
    }
    internal void CloseChildHandles() { outputWrite?.Dispose(); errorWrite?.Dispose(); }
    public void Dispose() { CloseChildHandles(); Output?.Dispose(); Error?.Dispose(); }
}
internal sealed class StartupInfoBuffer : IDisposable
{
    internal ProcessNative.StartupInfoEx Value;
    private IntPtr list, handles;
    private bool initialized;
    internal static StartupInfoBuffer Create(IntPtr[] childHandles)
    {
        var buffer = new StartupInfoBuffer();
        try
        {
            nuint size = 0; ProcessNative.InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
            buffer.list = Marshal.AllocHGlobal(checked((int)size));
            if (!ProcessNative.InitializeProcThreadAttributeList(buffer.list, 1, 0, ref size)) throw new Win32Exception(Marshal.GetLastWin32Error(), "PROCESS_ATTRIBUTES_FAILED");
            buffer.initialized = true;
            buffer.handles = Marshal.AllocHGlobal(childHandles.Length * IntPtr.Size); Marshal.Copy(childHandles, 0, buffer.handles, childHandles.Length);
            if (!ProcessNative.UpdateProcThreadAttribute(buffer.list, 0, (IntPtr)0x20002, buffer.handles, (nuint)(childHandles.Length * IntPtr.Size), IntPtr.Zero, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "PROCESS_HANDLE_LIST_FAILED");
            buffer.Value = new() { Attributes = buffer.list, Startup = new() { Size = Marshal.SizeOf<ProcessNative.StartupInfoEx>(), Flags = 0x100, StdOutput = childHandles[0], StdError = childHandles[1] } };
            return buffer;
        }
        catch { buffer.Dispose(); throw; }
    }
    public void Dispose() { if (initialized) ProcessNative.DeleteProcThreadAttributeList(list); Marshal.FreeHGlobal(handles); Marshal.FreeHGlobal(list); }
}
internal static class ProcessNative
{
    [StructLayout(LayoutKind.Sequential)] internal struct SecurityAttributes { internal int Length; internal IntPtr Descriptor; internal int Inherit; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] internal struct StartupInfo { internal int Size; internal IntPtr Reserved, Desktop, Title; internal uint X, Y, XSize, YSize, XChars, YChars, FillAttribute, Flags; internal ushort ShowWindow, ReservedSize; internal IntPtr ReservedBytes, StdInput, StdOutput, StdError; }
    [StructLayout(LayoutKind.Sequential)] internal struct StartupInfoEx { internal StartupInfo Startup; internal IntPtr Attributes; }
    [StructLayout(LayoutKind.Sequential)] internal struct ProcessInformation { internal IntPtr Process, Thread; internal uint ProcessId, ThreadId; }
    [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool CreateProcess(string application, StringBuilder command, IntPtr processSecurity, IntPtr threadSecurity, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint flags, IntPtr environment, string directory, ref StartupInfoEx startup, out ProcessInformation information);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern uint ResumeThread(SafeThreadHandle thread);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern uint WaitForSingleObject(SafeProcessHandle process, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool TerminateProcess(SafeProcessHandle process, uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetExitCodeProcess(SafeProcessHandle process, out uint code);
    [DllImport("kernel32.dll")] internal static extern int GetProcessId(SafeProcessHandle process);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, ref SecurityAttributes security, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetHandleInformation(SafeFileHandle handle, uint mask, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref nuint size);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value, nuint size, IntPtr previous, IntPtr returned);
    [DllImport("kernel32.dll")] internal static extern void DeleteProcThreadAttributeList(IntPtr list);
}
