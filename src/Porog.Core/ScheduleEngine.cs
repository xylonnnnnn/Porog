namespace Porog.Core;

public sealed record ScheduleSnapshot(Schedule? Active, Schedule? Next, Schedule[] Warnings);

public static class ScheduleEngine
{
    public static string Key(Schedule schedule) =>
        $"{schedule.Id:N}:{schedule.StartAt.UtcTicks}:{schedule.EndAt.UtcTicks}";

    public static ScheduleSnapshot Evaluate(AppConfig config, DateTimeOffset now, IEnumerable<string> warned)
    {
        var all = config.Schedules.Where(s => s.Enabled).OrderBy(s => s.StartAt).ToArray();
        var keys = warned.ToHashSet(StringComparer.Ordinal);
        return new(all.SingleOrDefault(s => s.StartAt <= now && now < s.EndAt),
            all.FirstOrDefault(s => s.StartAt > now),
            all.Where(s => now >= s.StartAt.AddSeconds(-config.Settings.WarningSeconds) &&
                now < s.StartAt && !keys.Contains(Key(s))).ToArray());
    }
}
