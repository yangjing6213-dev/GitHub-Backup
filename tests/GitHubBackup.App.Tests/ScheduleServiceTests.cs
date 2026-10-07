using GitHubBackup.App;

namespace GitHubBackup.App.Tests;

[TestClass]
public sealed class ScheduleServiceTests
{
    [TestMethod]
    public void Daily_schedule_is_due_once_and_calculates_the_next_local_time()
    {
        var schedule = new BackupSchedule(true, BackupScheduleFrequency.Daily, 10, 30, DayOfWeek.Sunday);
        var now = new DateTimeOffset(new DateTime(2026, 10, 7, 11, 0, 0, DateTimeKind.Unspecified), TimeSpan.FromHours(8));

        Assert.IsTrue(ScheduleService.IsDue(schedule, now));
        Assert.IsFalse(ScheduleService.IsDue(schedule with { LastStartedAtUtc = now.ToUniversalTime() }, now));
        Assert.AreEqual("2026-10-08 02:30:00Z", ScheduleService.NextDue(schedule, now).ToString("yyyy-MM-dd HH:mm:ss'Z'"));
    }

    [TestMethod]
    public void Weekly_schedule_uses_the_selected_weekday()
    {
        var schedule = new BackupSchedule(true, BackupScheduleFrequency.Weekly, 9, 0, DayOfWeek.Monday);
        var now = new DateTimeOffset(new DateTime(2026, 10, 7, 8, 0, 0, DateTimeKind.Unspecified), TimeSpan.FromHours(8));

        Assert.IsTrue(ScheduleService.IsDue(schedule, now));
        Assert.AreEqual("2026-10-12 01:00:00Z", ScheduleService.NextDue(schedule, now).ToString("yyyy-MM-dd HH:mm:ss'Z'"));
    }

    [TestMethod]
    public async Task Schedule_store_roundtrips_and_keeps_disabled_default_for_missing_file()
    {
        using var root = new StorageTestRoot();
        var store = new ScheduleStore(AppPaths.Create(root.Path));
        Assert.AreEqual(BackupSchedule.Disabled, await store.LoadAsync(default));
        var value = new BackupSchedule(true, BackupScheduleFrequency.Weekly, 7, 15, DayOfWeek.Friday,
            DateTimeOffset.UtcNow, null, "COMPLETED", null);
        await store.SaveAsync(value, default);
        Assert.AreEqual(value, await store.LoadAsync(default));
    }
}
