using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace GitHubBackup.App;

internal sealed class OperationJob : IDisposable
{
    internal object Gate { get; } = new();
    private readonly HashSet<RequestJobLease> active = [];
    private bool closing;
    private readonly TaskCompletionSource closingSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal Task Closing => closingSignal.Task;
    internal static OperationJob Create() => new();
    internal bool IsCancellationRequested { get { lock (Gate) return closing; } }
    internal int ActiveLeaseCount { get { lock (Gate) return active.Count; } }
    internal RequestJobLease CreateRequestJob()
    {
        lock (Gate)
        {
            if (closing) throw new OperationCanceledException();
            var lease = new RequestJobLease(this); active.Add(lease); return lease;
        }
    }
    internal void BeginCancellation() { lock (Gate) { closing = true; closingSignal.TrySetResult(); } }
    internal async Task CancelAllAsync(TimeSpan timeout)
    {
        RequestJobLease[] leases;
        lock (Gate) { BeginCancellation(); leases = active.ToArray(); foreach (var lease in leases) lease.Terminate(); }
        await Task.WhenAll(leases.Select(x => x.WaitEmptyAsync(timeout))).ConfigureAwait(false);
    }
    internal void Unregister(RequestJobLease lease) { lock (Gate) active.Remove(lease); }
    public void Dispose() { CancelAllAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult(); }
}

internal sealed class RequestJobLease : IDisposable
{
    private readonly OperationJob operation;
    private readonly SafeJobHandle handle;
    private bool disposed;
    private bool assigned;
    internal RequestJobLease(OperationJob operation)
    {
        this.operation = operation;
        handle = JobNative.CreateJobObject(IntPtr.Zero, null);
        if (handle.IsInvalid) { handle.Dispose(); throw new Win32Exception(Marshal.GetLastWin32Error(), "PROCESS_JOB_CREATE_FAILED"); }
        var limits = new JobNative.ExtendedLimits { Basic = new() { LimitFlags = 0x2000 } };
        if (!JobNative.SetInformationJobObject(handle, 9, ref limits, Marshal.SizeOf<JobNative.ExtendedLimits>()))
        { int error = Marshal.GetLastWin32Error(); handle.Dispose(); throw new Win32Exception(error, "PROCESS_JOB_CONFIG_FAILED"); }
    }
    internal void Assign(SafeProcessHandle process)
    {
        lock (operation.Gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (operation.IsCancellationRequested) throw new OperationCanceledException();
            if (!JobNative.AssignProcessToJobObject(handle, process)) throw new Win32Exception(Marshal.GetLastWin32Error(), "PROCESS_JOB_ASSIGN_FAILED");
            assigned = true;
        }
    }
    internal bool ResumeAssignedOrCancel(SafeThreadHandle primaryThread, CancellationToken cancellationToken)
    {
        lock (operation.Gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!assigned) throw new InvalidOperationException("PROCESS_JOB_NOT_ASSIGNED");
            if (operation.IsCancellationRequested || cancellationToken.IsCancellationRequested) return false;
            if (ProcessNative.ResumeThread(primaryThread) == uint.MaxValue) throw new Win32Exception(Marshal.GetLastWin32Error(), "PROCESS_RESUME_FAILED");
            return true;
        }
    }
    internal void Terminate()
    {
        lock (operation.Gate)
        {
            if (!disposed && !JobNative.TerminateJobObject(handle, 1)) throw new Win32Exception(Marshal.GetLastWin32Error(), "PROCESS_TERMINATION_FAILED");
        }
    }
    internal async Task TerminateAndWaitAsync(SafeProcessHandle process, TimeSpan timeout)
    {
        var watch = Stopwatch.StartNew();
        Terminate();
        await WaitEmptyAsync(timeout).ConfigureAwait(false);
        uint remaining = (uint)Math.Max(0, (timeout - watch.Elapsed).TotalMilliseconds);
        if (ProcessNative.WaitForSingleObject(process, remaining) != 0) throw new IOException("PROCESS_TERMINATION_FAILED");
    }
    internal async Task WaitEmptyAsync(TimeSpan timeout)
    {
        var watch = Stopwatch.StartNew();
        while (true)
        {
            lock (operation.Gate)
            {
                if (disposed) return;
                if (!JobNative.QueryInformationJobObject(handle, 1, out JobNative.Accounting accounting, Marshal.SizeOf<JobNative.Accounting>(), IntPtr.Zero))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "PROCESS_JOB_QUERY_FAILED");
                if (accounting.ActiveProcesses == 0) return;
            }
            if (watch.Elapsed >= timeout) throw new IOException("PROCESS_TERMINATION_FAILED");
            await Task.Delay(10).ConfigureAwait(false);
        }
    }
    public void Dispose()
    {
        lock (operation.Gate)
        {
            if (disposed) return;
            handle.Dispose(); disposed = true; operation.Unregister(this);
        }
    }
}

internal sealed class SafeJobHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public SafeJobHandle() : base(true) { }
    protected override bool ReleaseHandle() => ProcessNative.CloseHandle(handle);
}
internal sealed class SafeThreadHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    internal SafeThreadHandle(IntPtr value) : base(true) => SetHandle(value);
    protected override bool ReleaseHandle() => ProcessNative.CloseHandle(handle);
}
internal static class JobNative
{
    [StructLayout(LayoutKind.Sequential)] internal struct BasicLimits { internal long PerProcessUserTime, PerJobUserTime; internal uint LimitFlags; internal UIntPtr MinimumWorkingSet, MaximumWorkingSet; internal uint ActiveProcessLimit; internal UIntPtr Affinity; internal uint PriorityClass, SchedulingClass; }
    [StructLayout(LayoutKind.Sequential)] internal struct ExtendedLimits { internal BasicLimits Basic; internal ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; internal UIntPtr ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory; }
    [StructLayout(LayoutKind.Sequential)] internal struct Accounting { internal long TotalUserTime, TotalKernelTime, ThisPeriodUserTime, ThisPeriodKernelTime; internal uint PageFaultCount, TotalProcesses, ActiveProcesses, TerminatedProcesses; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern SafeJobHandle CreateJobObject(IntPtr security, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetInformationJobObject(SafeJobHandle job, int infoClass, ref ExtendedLimits information, int size);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool QueryInformationJobObject(SafeJobHandle job, int infoClass, out Accounting information, int size, IntPtr returned);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool AssignProcessToJobObject(SafeJobHandle job, SafeProcessHandle process);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool TerminateJobObject(SafeJobHandle job, uint code);
}
