using System.Text.Json;
using System.Text.Json.Serialization;

namespace Porog.Core;

public static class Wire
{
    public const string PipeName = "Porog.v1";
    public const int MaxMessageLength = 262144;
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() }
    };
    public static JsonElement Element<T>(T value) => JsonSerializer.SerializeToElement(value, Json);
}

public sealed record Settings(int BreakDurationMinutes = 5, int WarningSeconds = 60);
public sealed record Schedule(Guid Id, string Name, bool Enabled, DateTimeOffset StartAt,
    DateTimeOffset EndAt, string[] DomainTargets, string[] ProcessTargets, string Intention = "");
public sealed record AppConfig(int SchemaVersion, string AppId, long ConfigVersion,
    Schedule[] Schedules, Settings Settings, DateTimeOffset UpdatedAt)
{
    public static AppConfig Empty => new(1, "Porog", 0, [], new(), DateTimeOffset.UtcNow);
}
public sealed record SaveConfigRequest(long ExpectedVersion, Schedule[] Schedules, Settings Settings);
public sealed record StartBreakRequest(int DurationMinutes);
public sealed record Session(Guid SessionId, Guid? ScheduleId, string Mode,
    DateTimeOffset StartedAt, DateTimeOffset EndsAt, int Attempts = 0, string Name = "");
public sealed record RuntimeState(int SchemaVersion, string[] Warnings, Session? BreakSession,
    Session? WorkSession, Session? LastCompleted)
{
    public static RuntimeState Empty => new(1, [], null, null, null);
}
public sealed record Issue(string Code, string Message, string? Field = null);
public sealed record Capability(string Name, bool Available, string Detail);
public sealed record ServiceStatus(string ServiceState, DateTimeOffset Now, Session? ActiveSession,
    Session? BreakSession, Session? LastCompleted, Schedule? NextSchedule, string[] WarningKeys,
    Issue[] Issues, Capability[] Capabilities, long ConfigVersion);
public sealed record Diagnostics(string ServiceVersion, long ConfigVersion, string DataDirectory,
    Capability[] Capabilities, Issue[] Issues);
public sealed record Request(string Type, Guid RequestId, JsonElement Payload);
public sealed record ErrorBody(string Code, string Message, Issue[] Details, bool Retryable);
public sealed record Response(Guid RequestId, bool Ok, JsonElement? Payload, ErrorBody? Error)
{
    public static Response Success(Guid id, object value) => new(id, true, Wire.Element(value), null);
    public static Response Failure(Guid id, PorogException e) => new(id, false, null,
        new(e.Code, e.Message, e.Details, e.Retryable));
}
public sealed class PorogException(string code, string message, Issue[]? details = null,
    bool retryable = false, Exception? inner = null) : Exception(message, inner)
{
    public string Code { get; } = code;
    public Issue[] Details { get; } = details ?? [];
    public bool Retryable { get; } = retryable;
}
