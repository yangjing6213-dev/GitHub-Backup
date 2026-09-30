using GitHubBackup.App;
namespace GitHubBackup.App.Tests;

internal sealed class ScriptedProcessRunner : IProcessRunner
{
    internal List<ProcessRequest> Requests { get; } = [];
    internal Queue<Func<ProcessRequest,ProcessResult>> Results { get; } = new();
    public Task<ProcessResult> RunAsync(ProcessRequest request, OperationJob job, IProgress<string>? progress, CancellationToken cancellationToken)
    { cancellationToken.ThrowIfCancellationRequested(); Requests.Add(request); return Task.FromResult(Results.Dequeue()(request)); }
}
