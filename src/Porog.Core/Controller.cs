namespace Porog.Core;

public sealed record ProcessResult(int Terminated, Issue[] Issues);
public interface IProcessBlocker
{
    ProcessResult Apply(IReadOnlyCollection<string> executableNames);
}

/// <summary>Serialized service-owned state. UI never writes configuration or decides when protection ends.</summary>
public sealed class Controller
{
    private readonly object gate = new();
    private readonly AtomicJsonStore<AppConfig> configStore;
    private readonly AtomicJsonStore<RuntimeState> runtimeStore;
    private readonly IWebsiteBlocker websites;
    private readonly IProcessBlocker processes;
    private readonly TimeProvider clock;
    private readonly string dataDirectory;
    private AppConfig config = AppConfig.Empty;
    private RuntimeState runtime = RuntimeState.Empty;
    private Issue? fatal;
    private Issue[] issues = [];
    private bool hostsApplied;
    private readonly Dictionary<Guid, (string Fingerprint, Response Response)> replies = [];

    public Controller(string dataDirectory, IWebsiteBlocker websites, IProcessBlocker processes,
        TimeProvider? clock = null)
    {
        this.dataDirectory = dataDirectory;
        this.websites = websites;
        this.processes = processes;
        this.clock = clock ?? TimeProvider.System;
        configStore = new(Path.Combine(dataDirectory, "config.json"), Validation.Config);
        runtimeStore = new(Path.Combine(dataDirectory, "runtime.json"), ValidateRuntime);
        try
        {
            config = configStore.Load() ?? MissingPrimary(configStore.Path, AppConfig.Empty);
            runtime = runtimeStore.Load() ?? MissingPrimary(runtimeStore.Path, RuntimeState.Empty);
        }
        catch (Exception e) when (e is PorogException or IOException or UnauthorizedAccessException)
        {
            fatal = new(e is PorogException p ? p.Code : "PERMISSION_DENIED", e.Message);
        }
    }

    private static T MissingPrimary<T>(string path, T fallback)
    {
        if (File.Exists(path + ".bak"))
            throw new PorogException("CONFIG_CORRUPTED", "Основной файл отсутствует, но найдена резервная копия. Требуется явное восстановление.");
        return fallback;
    }

    public static RuntimeState ValidateRuntime(RuntimeState state)
    {
        if (state.SchemaVersion != 1 || state.Warnings is null || state.Warnings.Length > 2000 ||
            state.Warnings.Any(s => string.IsNullOrWhiteSpace(s)))
            throw new PorogException("CONFIG_CORRUPTED", "Некорректный формат состояния службы.");
        foreach (var session in new[] { state.WorkSession, state.BreakSession, state.LastCompleted }.OfType<Session>())
            if (session.SessionId == Guid.Empty || session.EndsAt <= session.StartedAt ||
                session.Mode is not ("Work" or "Break") || session.Attempts < 0 ||
                (session.Mode == "Break" && (session.EndsAt - session.StartedAt).TotalMinutes is < 3 or > 12))
                throw new PorogException("CONFIG_CORRUPTED", "Некорректная сохранённая сессия.");
        return state;
    }

    public void Tick()
    {
        lock (gate)
        {
            if (fatal is not null) return;
            try
            {
                var now = clock.GetUtcNow();
                var evaluation = ScheduleEngine.Evaluate(config, now, runtime.Warnings);
                var next = runtime;
                var active = evaluation.Active;
                if (next.WorkSession is { } work && (active is null || work.ScheduleId != active.Id || work.EndsAt != active.EndAt))
                    next = next with { LastCompleted = work, WorkSession = null };
                if (active is not null && next.WorkSession is null)
                    next = next with { WorkSession = new(active.Id, active.Id, "Work", active.StartAt, active.EndAt, Name: active.Name) };
                if (next.BreakSession is { } rest && now >= rest.EndsAt)
                    next = next with { BreakSession = null, LastCompleted = rest };
                if (evaluation.Warnings.Length > 0)
                    next = next with { Warnings = next.Warnings.Concat(evaluation.Warnings.Select(ScheduleEngine.Key)).TakeLast(2000).ToArray() };
                Persist(next); // Durable deadlines and warning decisions precede notification/enforcement.
                var problems = new List<Issue>();
                try { websites.Apply(active?.DomainTargets ?? []); hostsApplied = true; }
                catch (PorogException e) { hostsApplied = false; problems.Add(new(e.Code, e.Message)); }
                var result = processes.Apply(active?.ProcessTargets ?? []);
                problems.AddRange(result.Issues);
                if (result.Terminated > 0 && runtime.WorkSession is { } session)
                    Persist(runtime with { WorkSession = session with { Attempts = checked(session.Attempts + result.Terminated) } });
                issues = problems.ToArray();
            }
            catch (Exception e) when (e is PorogException or IOException or UnauthorizedAccessException or OverflowException)
            {
                fatal = new(e is PorogException p ? p.Code : "INTERNAL_ERROR", e.Message);
            }
        }
    }

    private void Persist(RuntimeState next)
    {
        if (System.Text.Json.JsonSerializer.Serialize(runtime, Wire.Json) ==
            System.Text.Json.JsonSerializer.Serialize(next, Wire.Json)) return;
        runtimeStore.Save(next);
        runtime = next;
    }

    public ServiceStatus Status()
    {
        lock (gate)
        {
            var now = clock.GetUtcNow();
            var allIssues = fatal is null ? issues : issues.Append(fatal).ToArray();
            return new(fatal is not null ? "Error" : allIssues.Length > 0 || !hostsApplied ? "Degraded" : "Running", now,
                runtime.WorkSession, runtime.BreakSession, runtime.LastCompleted,
                ScheduleEngine.Evaluate(config, now, runtime.Warnings).Next, runtime.Warnings,
                allIssues, [new("hosts", hostsApplied, "Только указанные домены. Secure DNS / DoH может обходить hosts."),
                    new("processControl", fatal is null && !issues.Any(i => i.Code == "PROCESS_CONTROL_FAILED"),
                        "Только процессы владельца установки. Счётчик учитывает успешные завершения."),
                    new("break", fatal is null, "Полноэкранный режим требует работающего UI; системные сочетания доступны.")], config.ConfigVersion);
        }
    }

    public Response Handle(Request request)
    {
        lock (gate)
        {
            try
            {
                if (request.RequestId == Guid.Empty) throw Validation.Invalid("requestId", "Требуется непустой UUID запроса.");
                var mutation = request.Type is "SaveConfig" or "StartBreak";
                var fingerprint = request.Type + ":" + request.Payload.GetRawText();
                if (mutation && replies.TryGetValue(request.RequestId, out var cached))
                {
                    if (cached.Fingerprint != fingerprint) throw Validation.Invalid("requestId", "UUID уже использован для другого запроса.");
                    return cached.Response;
                }
                if (mutation && fatal is not null) throw new PorogException(fatal.Code, fatal.Message);
                object result = request.Type switch
                {
                    "GetStatus" => Status(),
                    "GetConfig" => fatal is null ? config : throw new PorogException(fatal.Code, fatal.Message),
                    "SaveConfig" => Save(request.Payload.DeserializeRequired<SaveConfigRequest>()),
                    "StartBreak" => StartBreak(request.RequestId, request.Payload.DeserializeRequired<StartBreakRequest>()),
                    "GetSession" => GetSession(request.Payload.GetProperty("sessionId").GetGuid()),
                    "GetDiagnostics" => new Diagnostics("1.0.0", config.ConfigVersion, dataDirectory, Status().Capabilities, Status().Issues),
                    _ => throw Validation.Invalid("type", "Неизвестная команда IPC.")
                };
                var response = Response.Success(request.RequestId, result);
                if (mutation)
                {
                    if (replies.Count >= 256) replies.Remove(replies.Keys.First());
                    replies[request.RequestId] = (fingerprint, response);
                }
                return response;
            }
            catch (PorogException e) { return Response.Failure(request.RequestId, e); }
            catch (Exception e) when (e is System.Text.Json.JsonException or InvalidOperationException or FormatException or KeyNotFoundException or ArgumentException)
            { return Response.Failure(request.RequestId, Validation.Invalid("payload", "Некорректное тело команды.")); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            { return Response.Failure(request.RequestId, new("PERMISSION_DENIED", "Не удалось записать локальные данные. Проверьте диагностику.", inner: e)); }
        }
    }

    private AppConfig Save(SaveConfigRequest input)
    {
        if (input.ExpectedVersion != config.ConfigVersion)
            throw new PorogException("CONFIG_VERSION_CONFLICT", "Настройки изменились. Загрузите расписание заново.");
        var next = Validation.Config(config with { Schedules = input.Schedules, Settings = input.Settings,
            ConfigVersion = checked(config.ConfigVersion + 1), UpdatedAt = clock.GetUtcNow() });
        Validation.GuardActive(config, next, clock.GetUtcNow());
        configStore.Save(next);
        config = next;
        Tick();
        return config;
    }

    private Session StartBreak(Guid requestId, StartBreakRequest input)
    {
        Validation.BreakDuration(input.DurationMinutes);
        var now = clock.GetUtcNow();
        if (runtime.BreakSession is { } existing && existing.EndsAt > now)
        {
            if (existing.SessionId == requestId) return existing;
            throw new PorogException("ACTIVE_SESSION_LOCKED", "«Отход» уже начат. Дождитесь окончания.");
        }
        var session = new Session(requestId, null, "Break", now, now.AddMinutes(input.DurationMinutes), Name: "Отход");
        Persist(runtime with { BreakSession = session });
        return session;
    }

    private Session GetSession(Guid id) =>
        new[] { runtime.WorkSession, runtime.BreakSession, runtime.LastCompleted }.OfType<Session>()
            .FirstOrDefault(s => s.SessionId == id) ?? throw new PorogException("SESSION_NOT_FOUND", "Сессия не найдена.");
}

public static class JsonExtensions
{
    public static T DeserializeRequired<T>(this System.Text.Json.JsonElement element) =>
        System.Text.Json.JsonSerializer.Deserialize<T>(element, Wire.Json)
            ?? throw Validation.Invalid("payload", "Тело команды не может быть null.");
}
