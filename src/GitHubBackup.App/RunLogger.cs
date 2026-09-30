using System.Text;
using System.Text.Json;
using System.Security.Principal;

namespace GitHubBackup.App;

internal enum LogLevel { Info, Warning, Error }

internal sealed class RunLogger : IAsyncDisposable
{
    private const int TailLimit = 2000;
    private const int LineLimit = 8192;
    private readonly StreamWriter writer;
    private readonly Queue<string> tail = new();
    private readonly SemaphoreSlim gate = new(1, 1);
    private bool disposed;

    private RunLogger(StreamWriter writer) => this.writer = writer;

    internal static Task<RunLogger> CreateAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string canonical = NativeFileSystem.CanonicalPath(path);
        string name = Path.GetFileName(canonical);
        if (!name.StartsWith("backup-", StringComparison.Ordinal) || !name.EndsWith(".log", StringComparison.Ordinal)
            || name.Length <= "backup-.log".Length || !name[7..^4].All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))
            throw new UnauthorizedAccessException("UNAPPROVED_RUN_LOG_PATH");
        string logs = Path.GetDirectoryName(canonical)!;
        if (!string.Equals(Path.GetFileName(logs), "logs", StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("UNAPPROVED_RUN_LOG_PATH");
        using PathLease ownerLease = SummaryStore.RequirePrivateDirectory(Path.GetDirectoryName(logs)!);
        using PathLease logLease = SummaryStore.RequirePrivateDirectory(logs);
        FileStream stream = AclPolicy.CreateRestrictedFile(canonical, WindowsIdentity.GetCurrent().User!);
        return Task.FromResult(new RunLogger(new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true }));
    }

    internal async Task WriteAsync(LogLevel level, string phase, string? repository, string message,
        CancellationToken cancellationToken)
    {
        string safePhase = Clean(phase, 128);
        string? safeRepository = repository is null ? null : Clean(repository, 256);
        string safeMessage = Clean(message, 7800);
        string Serialize() => JsonSerializer.Serialize(new
        {
            timestamp = DateTimeOffset.UtcNow,
            level = level.ToString().ToUpperInvariant(),
            phase = safePhase,
            repository = safeRepository,
            message = safeMessage
        });
        string line = Serialize();
        if (line.Length > LineLimit)
        {
            safeMessage = safeMessage[..Math.Max(0, safeMessage.Length - (line.Length - LineLimit) - 11)] + "[TRUNCATED]";
            line = Serialize();
        }
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await writer.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
            lock (tail)
            {
                tail.Enqueue(line);
                if (tail.Count > TailLimit) tail.Dequeue();
            }
        }
        finally { gate.Release(); }
    }

    internal IReadOnlyList<string> GetTail()
    {
        lock (tail) return tail.ToArray();
    }

    private static string Clean(string value, int limit)
    {
        var stream = new SafeTextStream();
        string safe = stream.Push(value) + stream.Complete();
        return safe.Length <= limit ? safe : safe[..(limit - 11)] + "[TRUNCATED]";
    }

    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (disposed) return;
            await writer.DisposeAsync().ConfigureAwait(false); disposed = true;
        }
        finally { gate.Release(); }
    }
}
