using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;

namespace Porog.Core;

public static partial class Validation
{
    // Whole-browser and operating system targets are deliberately outside this MVP.
    private static readonly HashSet<string> ForbiddenProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "porog.desktop.exe", "porog.service.exe", "system.exe", "idle.exe", "registry.exe",
        "smss.exe", "csrss.exe", "wininit.exe", "services.exe", "lsass.exe", "svchost.exe",
        "winlogon.exe", "explorer.exe", "dwm.exe", "sihost.exe", "fontdrvhost.exe",
        "conhost.exe", "taskhostw.exe", "runtimebroker.exe", "logonui.exe", "securityhealthservice.exe",
        "chrome.exe", "msedge.exe", "firefox.exe", "opera.exe", "brave.exe", "browser.exe"
    };

    [GeneratedRegex(@"^[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?$")]
    private static partial Regex LabelPattern();
    [GeneratedRegex(@"^[\p{L}\p{N}][\p{L}\p{N} _().-]{0,119}\.exe$", RegexOptions.IgnoreCase)]
    private static partial Regex ProcessPattern();

    public static string Domain(string input)
    {
        var value = (input ?? "").Trim().TrimEnd('.').ToLowerInvariant();
        try { value = new IdnMapping().GetAscii(value); }
        catch (ArgumentException) { throw Invalid("domainTargets", "Некорректный домен."); }
        var labels = value.Split('.');
        if (value.Length > 253 || labels.Length < 2 || IPAddress.TryParse(value, out _) ||
            labels.Any(l => !LabelPattern().IsMatch(l)) || labels[^1].All(char.IsDigit))
            throw Invalid("domainTargets", "Укажите домен без https://, пути, порта и пробелов.");
        return value;
    }

    public static string ProcessName(string input)
    {
        var value = (input ?? "").Trim();
        if (!ProcessPattern().IsMatch(value) || ForbiddenProcesses.Contains(value))
            throw Invalid("processTargets", "Укажите имя .exe. Системные процессы и браузеры целиком блокировать нельзя.");
        return value.ToLowerInvariant();
    }

    public static void BreakDuration(int minutes)
    {
        if (minutes is < 3 or > 12)
            throw Invalid("durationMinutes", "Длительность «Отхода» должна быть от 3 до 12 минут.");
    }

    public static AppConfig Config(AppConfig config)
    {
        if (config.SchemaVersion != 1 || config.AppId != "Porog" || config.ConfigVersion < 0)
            throw new PorogException("CONFIG_CORRUPTED", "Неизвестная версия или формат конфигурации.");
        if (config.Settings is null || config.Schedules is null || config.Schedules.Length > 200)
            throw Invalid("schedules", "Допускается до 200 промежутков.");
        BreakDuration(config.Settings.BreakDurationMinutes);
        if (config.Settings.WarningSeconds != 60)
            throw Invalid("warningSeconds", "Предупреждение MVP фиксировано за 60 секунд.");
        var schedules = config.Schedules.Select(Normalize).ToArray();
        if (schedules.Select(s => s.Id).Distinct().Count() != schedules.Length)
            throw Invalid("id", "ID промежутков должны быть уникальными.");
        var enabled = schedules.Where(s => s.Enabled).OrderBy(s => s.StartAt).ToArray();
        for (var i = 1; i < enabled.Length; i++)
            if (enabled[i].StartAt < enabled[i - 1].EndAt)
                throw new PorogException("SCHEDULE_CONFLICT", $"Пересекаются «{enabled[i - 1].Name}» и «{enabled[i].Name}».",
                    [new("SCHEDULE_CONFLICT", "Промежутки не должны пересекаться.", "startAt")]);
        return config with { Schedules = schedules };
    }

    private static Schedule Normalize(Schedule s)
    {
        if (s is null || s.Id == Guid.Empty || string.IsNullOrWhiteSpace(s.Name) || s.Name.Length > 80)
            throw Invalid("name", "Укажите название до 80 символов и непустой UUID.");
        if (s.StartAt == default || s.EndAt <= s.StartAt)
            throw Invalid("endAt", "Окончание должно быть позже начала; время содержит смещение UTC.");
        if (s.DomainTargets is null || s.ProcessTargets is null || s.DomainTargets.Length > 300 || s.ProcessTargets.Length > 100)
            throw Invalid("targets", "Допускается до 300 доменов и 100 приложений.");
        var domains = s.DomainTargets.Select(Domain).ToArray();
        var processes = s.ProcessTargets.Select(ProcessName).ToArray();
        if (domains.Length + processes.Length == 0)
            throw Invalid("targets", "Добавьте хотя бы один сайт или приложение.");
        if (domains.Distinct().Count() != domains.Length || processes.Distinct().Count() != processes.Length)
            throw Invalid("targets", "Удалите повторяющиеся сайты или приложения.");
        if (s.Intention is null || s.Intention.Length > 300)
            throw Invalid("intention", "Намерение должно быть не длиннее 300 символов.");
        return s with { Name = s.Name.Trim(), DomainTargets = domains, ProcessTargets = processes };
    }

    public static void GuardActive(AppConfig before, AppConfig after, DateTimeOffset now)
    {
        foreach (var s in before.Schedules.Where(s => s.Enabled && s.StartAt <= now && now < s.EndAt))
        {
            var replacement = after.Schedules.SingleOrDefault(x => x.Id == s.Id);
            if (replacement is null || System.Text.Json.JsonSerializer.Serialize(s, Wire.Json) !=
                System.Text.Json.JsonSerializer.Serialize(replacement, Wire.Json))
                throw new PorogException("ACTIVE_SESSION_LOCKED", "Начатый промежуток нельзя изменить или удалить до окончания.");
        }
    }

    public static PorogException Invalid(string field, string message) =>
        new("VALIDATION_ERROR", message, [new("VALIDATION_ERROR", message, field)]);
}
