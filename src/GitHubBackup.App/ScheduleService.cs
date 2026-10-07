using System.Text.Json;

namespace GitHubBackup.App;

internal enum BackupScheduleFrequency { Disabled, Daily, Weekly }

internal sealed record BackupSchedule(
    bool Enabled,
    BackupScheduleFrequency Frequency,
    int Hour,
    int Minute,
    DayOfWeek DayOfWeek,
    DateTimeOffset? LastStartedAtUtc = null,
    DateTimeOffset? LastCompletedAtUtc = null,
    string? LastStatus = null,
    string? LastErrorCode = null)
{
    internal static BackupSchedule Disabled { get; } = new(false, BackupScheduleFrequency.Disabled, 2, 0, DayOfWeek.Sunday);
    internal bool IsValid => Hour is >= 0 and <= 23 && Minute is >= 0 and <= 59
        && (!Enabled || Frequency is BackupScheduleFrequency.Daily or BackupScheduleFrequency.Weekly);
}

internal sealed class ScheduleStore(AppPaths paths)
{
    internal async Task<BackupSchedule> LoadAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using AppDataPathLease parent = RequireDirectory();
        using AppDataPathLease file = RequireFile(allowMissing: true);
        if (file.Identity is null) return BackupSchedule.Disabled;
        try
        {
            using var stream = new FileStream(paths.ScheduleFile, FileMode.Open, FileAccess.Read, FileShare.Read);
            using JsonDocument document = await JsonDocument.ParseAsync(stream, cancellationToken: token).ConfigureAwait(false);
            JsonElement root = document.RootElement;
            bool enabled = root.TryGetProperty("enabled", out JsonElement enabledElement) && enabledElement.ValueKind == JsonValueKind.True;
            string frequency = root.TryGetProperty("frequency", out JsonElement frequencyElement) && frequencyElement.ValueKind == JsonValueKind.String
                ? frequencyElement.GetString() ?? "Disabled" : "Disabled";
            int hour = root.TryGetProperty("hour", out JsonElement hourElement) && hourElement.TryGetInt32(out int h) ? h : 2;
            int minute = root.TryGetProperty("minute", out JsonElement minuteElement) && minuteElement.TryGetInt32(out int m) ? m : 0;
            int day = root.TryGetProperty("dayOfWeek", out JsonElement dayElement) && dayElement.TryGetInt32(out int d) ? d : (int)DayOfWeek.Sunday;
            BackupScheduleFrequency kind = Enum.TryParse(frequency, ignoreCase: false, out BackupScheduleFrequency parsed) ? parsed : BackupScheduleFrequency.Disabled;
            DateTimeOffset? started = ReadDate(root, "lastStartedAtUtc");
            DateTimeOffset? completed = ReadDate(root, "lastCompletedAtUtc");
            string? status = ReadString(root, "lastStatus");
            string? error = ReadString(root, "lastErrorCode");
            var value = new BackupSchedule(enabled, kind, hour, minute, Enum.IsDefined((DayOfWeek)day) ? (DayOfWeek)day : DayOfWeek.Sunday,
                started, completed, status, error);
            return value.IsValid ? value : BackupSchedule.Disabled;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        { return BackupSchedule.Disabled; }
    }

    internal async Task SaveAsync(BackupSchedule schedule, CancellationToken token)
    {
        if (!schedule.IsValid) throw new ArgumentException("SCHEDULE_INVALID", nameof(schedule));
        token.ThrowIfCancellationRequested();
        using AppDataPathLease parent = RequireDirectory();
        using (RequireFile(allowMissing: true)) { }
        await AtomicFile.WriteAsync(paths.ScheduleFile, (stream, cancellationToken) => JsonSerializer.SerializeAsync(stream, new
        {
            enabled = schedule.Enabled,
            frequency = schedule.Frequency.ToString(),
            hour = schedule.Hour,
            minute = schedule.Minute,
            dayOfWeek = (int)schedule.DayOfWeek,
            lastStartedAtUtc = schedule.LastStartedAtUtc,
            lastCompletedAtUtc = schedule.LastCompletedAtUtc,
            lastStatus = schedule.LastStatus,
            lastErrorCode = schedule.LastErrorCode
        }, MetadataJson.Options, cancellationToken), token).ConfigureAwait(false);
        using (RequireFile(allowMissing: false)) { }
    }

    private AppDataPathLease RequireDirectory()
    {
        try { return AppDataPathPolicy.Acquire(paths, paths.LocalAppDataRoot, AppDataEntryKind.Directory, createMissingDirectories: true); }
        catch (PathBoundaryException ex) { throw new UnauthorizedAccessException("APPDATA_PATH_" + ex.Code); }
    }

    private AppDataPathLease RequireFile(bool allowMissing)
    {
        try { return AppDataPathPolicy.Acquire(paths, paths.ScheduleFile, AppDataEntryKind.File, allowMissingFile: allowMissing); }
        catch (PathBoundaryException ex) { throw new UnauthorizedAccessException("APPDATA_PATH_" + ex.Code); }
    }

    private static DateTimeOffset? ReadDate(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(value.GetString(), out DateTimeOffset date) ? date : null;

    private static string? ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}

internal static class ScheduleService
{
    internal static bool IsDue(BackupSchedule schedule, DateTimeOffset now)
    {
        if (!schedule.Enabled || !schedule.IsValid || schedule.Frequency == BackupScheduleFrequency.Disabled) return false;
        DateTimeOffset due = LatestDue(schedule, now);
        return now >= due && (schedule.LastStartedAtUtc is null || schedule.LastStartedAtUtc < due);
    }

    internal static DateTimeOffset NextDue(BackupSchedule schedule, DateTimeOffset now)
    {
        if (!schedule.Enabled || schedule.Frequency == BackupScheduleFrequency.Disabled) return DateTimeOffset.MaxValue;
        DateTime local = now.ToLocalTime().DateTime;
        DateTime candidate = local.Date.AddHours(schedule.Hour).AddMinutes(schedule.Minute);
        if (schedule.Frequency == BackupScheduleFrequency.Weekly)
        {
            int delta = ((int)local.DayOfWeek - (int)schedule.DayOfWeek + 7) % 7;
            candidate = local.Date.AddDays(-delta).AddHours(schedule.Hour).AddMinutes(schedule.Minute);
        }
        if (candidate <= local) candidate = candidate.AddDays(schedule.Frequency == BackupScheduleFrequency.Daily ? 1 : 7);
        return new DateTimeOffset(candidate, now.Offset).ToUniversalTime();
    }

    private static DateTimeOffset LatestDue(BackupSchedule schedule, DateTimeOffset now)
    {
        DateTime local = now.ToLocalTime().DateTime;
        DateTime candidate = local.Date.AddHours(schedule.Hour).AddMinutes(schedule.Minute);
        if (schedule.Frequency == BackupScheduleFrequency.Weekly)
        {
            int delta = ((int)local.DayOfWeek - (int)schedule.DayOfWeek + 7) % 7;
            candidate = local.Date.AddDays(-delta).AddHours(schedule.Hour).AddMinutes(schedule.Minute);
        }
        return new DateTimeOffset(candidate, now.Offset).ToUniversalTime();
    }
}
