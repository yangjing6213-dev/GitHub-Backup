using GitHubBackup.App;
namespace GitHubBackup.App.Tests;

[TestClass]
public sealed class OperationJobTests
{
    [TestMethod] public async Task Registration_cancellation_is_linearizable()
    {
        for (int i = 0; i < 100; i++)
        {
            using var operation = OperationJob.Create();
            var registration = Task.Run(() => { try { using var lease = operation.CreateRequestJob(); } catch (OperationCanceledException) { } });
            await operation.CancelAllAsync(TimeSpan.FromSeconds(5)); await registration;
            Assert.AreEqual(0, operation.ActiveLeaseCount); Assert.IsTrue(operation.IsCancellationRequested);
            Assert.ThrowsExactly<OperationCanceledException>(() => operation.CreateRequestJob());
        }
    }
}
