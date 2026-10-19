using System.Text;
using System.Text.Json;
using Porog.Core;

namespace Porog.Core.Tests;

public sealed class ControllerTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "PorogTests", Guid.NewGuid().ToString("N"));
    private readonly ManualClock clock = new();
    private readonly FakeWebsites websites = new();
    private readonly FakeProcesses processes = new();
    public ControllerTests() => Directory.CreateDirectory(directory);
    private Controller Create() => new(directory, websites, processes, clock);
    private static Request Request(string type, object payload, Guid? id = null) => new(type, id ?? Guid.NewGuid(), Wire.Element(payload));
    private static AppConfig Save(Controller c, Schedule[] schedules, long version = 0)
    {
        var result = c.Handle(Request("SaveConfig", new SaveConfigRequest(version, schedules, new())));
        Assert.True(result.Ok, result.Error?.Message);
        return result.Payload!.Value.DeserializeRequired<AppConfig>();
    }

    [Fact]
    public void ConfigPersistsAndReloadsAfterRestart()
    {
        var c = Create(); var saved = Save(c, [RulesTests.Make()]);
        var next = Create().Handle(Request("GetConfig", new { }));
        Assert.Equal(JsonSerializer.Serialize(saved, Wire.Json), next.Payload!.Value.GetRawText());
    }

    [Fact]
    public void FailedValidationDoesNotAlterConfigBytes()
    {
        var c = Create(); Save(c, [RulesTests.Make()]);
        var path = Path.Combine(directory, "config.json"); var original = File.ReadAllBytes(path);
        var result = c.Handle(Request("SaveConfig", new SaveConfigRequest(1, [RulesTests.Make() with { Name = "" }], new())));
        Assert.False(result.Ok); Assert.Equal(original, File.ReadAllBytes(path));
    }

    [Fact]
    public void StaleEditorCannotOverwriteNewerConfig()
    {
        var c = Create(); Save(c, [RulesTests.Make()]);
        var response = c.Handle(Request("SaveConfig", new SaveConfigRequest(0, [], new())));
        Assert.Equal("CONFIG_VERSION_CONFLICT", response.Error!.Code);
    }

    [Fact]
    public void MutationRetriesAreIdempotentAndCannotReuseIdsForDifferentContent()
    {
        var c = Create(); var request = Request("SaveConfig", new SaveConfigRequest(0, [RulesTests.Make()], new()));
        var a = c.Handle(request); var b = c.Handle(request); Assert.Equal(a, b);
        Assert.Equal(1, c.Status().ConfigVersion);
        Assert.Equal("VALIDATION_ERROR", c.Handle(request with { Payload = Wire.Element(new SaveConfigRequest(1, [], new())) }).Error!.Code);
    }

    [Fact]
    public void FullWorkLifecycleReappliesProcessesAndRemovesDomains()
    {
        var c = Create(); var s = RulesTests.Make(0, 2); Save(c, [s]);
        Assert.Equal("Running", c.Status().ServiceState);
        Assert.Equal(s.Id, c.Status().ActiveSession!.ScheduleId);
        Assert.Equal(["youtube.com"], websites.Domains);
        processes.NextKills = 2; clock.Advance(TimeSpan.FromSeconds(1)); c.Tick();
        Assert.Equal(2, c.Status().ActiveSession!.Attempts);
        clock.Set(s.EndAt); c.Tick();
        Assert.Null(c.Status().ActiveSession); Assert.Empty(websites.Domains);
        Assert.Equal(2, c.Status().LastCompleted!.Attempts); Assert.True(processes.Calls > 1);
    }

    [Fact]
    public void RestartDuringWorkRestoresSessionAndAttemptCount()
    {
        var c = Create(); Save(c, [RulesTests.Make(0, 5)]);
        processes.NextKills = 1; c.Tick();
        var restarted = Create(); restarted.Tick();
        Assert.Equal(1, restarted.Status().ActiveSession!.Attempts);
        Assert.Equal(["youtube.com"], websites.Domains);
    }

    [Fact]
    public void ActiveLockIsEnforcedThroughIpc()
    {
        var c = Create(); Save(c, [RulesTests.Make(0)]);
        Assert.Equal("ACTIVE_SESSION_LOCKED", c.Handle(Request("SaveConfig", new SaveConfigRequest(1, [], new()))).Error!.Code);
    }

    [Fact]
    public void WarningIsDurableAcrossServiceRestart()
    {
        var c = Create(); var s = RulesTests.Make(1); Save(c, [s]);
        Assert.Single(c.Status().WarningKeys);
        var restarted = Create(); restarted.Tick();
        Assert.Single(restarted.Status().WarningKeys);
        clock.Set(s.StartAt); restarted.Tick(); Assert.Single(restarted.Status().WarningKeys);
    }

    [Theory]
    [InlineData(3)][InlineData(12)]
    public void BreakPersistsAbsoluteDeadlineAndCompletesAtBoundary(int minutes)
    {
        var c = Create(); var request = Request("StartBreak", new StartBreakRequest(minutes));
        var response = c.Handle(request); Assert.True(response.Ok);
        var endsAt = response.Payload!.Value.DeserializeRequired<Session>().EndsAt;
        clock.Advance(TimeSpan.FromSeconds(10)); var restarted = Create(); restarted.Tick();
        Assert.Equal(endsAt, restarted.Status().BreakSession!.EndsAt);
        Assert.Equal("ACTIVE_SESSION_LOCKED", restarted.Handle(Request("StartBreak", new StartBreakRequest(5))).Error!.Code);
        Assert.True(restarted.Handle(request).Ok);
        clock.Set(endsAt); restarted.Tick(); Assert.Null(restarted.Status().BreakSession);
    }

    [Fact]
    public void InvalidBreakDoesNotCreateRuntimeFile()
    {
        var c = Create();
        Assert.Equal("VALIDATION_ERROR", c.Handle(Request("StartBreak", new StartBreakRequest(13))).Error!.Code);
        Assert.Null(c.Status().BreakSession); Assert.False(File.Exists(Path.Combine(directory, "runtime.json")));
    }

    [Fact]
    public void CorruptConfigIsPreservedAndNotSilentlyReplacedByBackup()
    {
        var c = Create(); Save(c, [RulesTests.Make()]); Save(c, [], 1);
        var path = Path.Combine(directory, "config.json"); File.WriteAllText(path, "{broken");
        var restarted = Create(); restarted.Tick();
        Assert.Equal("Error", restarted.Status().ServiceState);
        Assert.Contains(restarted.Status().Issues, i => i.Code == "CONFIG_CORRUPTED");
        Assert.False(restarted.Handle(Request("SaveConfig", new SaveConfigRequest(0, [], new()))).Ok);
        Assert.Equal("{broken", File.ReadAllText(path)); Assert.True(File.Exists(path + ".bak"));
    }

    [Fact]
    public void UnknownSchemaAndDeletedPrimaryDoNotResetConfiguration()
    {
        var store = new AtomicJsonStore<AppConfig>(Path.Combine(directory, "config.json"), Validation.Config);
        File.WriteAllText(store.Path, JsonSerializer.Serialize(AppConfig.Empty with { SchemaVersion = 99 }, Wire.Json));
        Assert.Equal("CONFIG_CORRUPTED", Assert.Throws<PorogException>(() => store.Load()).Code);
        File.Delete(store.Path); store.Save(AppConfig.Empty); store.Save(AppConfig.Empty);
        File.Delete(store.Path);
        Assert.Equal("Error", Create().Status().ServiceState);
    }

    [Fact]
    public void CorruptRuntimeIsPreserved()
    {
        var path = Path.Combine(directory, "runtime.json"); File.WriteAllText(path, "null");
        var c = Create(); c.Tick(); Assert.Equal("Error", c.Status().ServiceState);
        Assert.Equal("null", File.ReadAllText(path));
    }

    [Fact]
    public void PermissionErrorsReportPartialProtectionAndRecoverOnNextTick()
    {
        websites.Fail = true; processes.Fail = true;
        var c = Create(); Save(c, [RulesTests.Make(0)]);
        Assert.Equal("Degraded", c.Status().ServiceState);
        Assert.Contains(c.Status().Issues, i => i.Code == "HOSTS_WRITE_FAILED");
        Assert.Contains(c.Status().Issues, i => i.Code == "PROCESS_CONTROL_FAILED");
        Assert.Equal(0, c.Status().ActiveSession!.Attempts);
        websites.Fail = processes.Fail = false; c.Tick();
        Assert.Equal("Running", c.Status().ServiceState);
    }

    [Fact]
    public void RealHostsAdapterOnlyTouchesSandboxFileAndCreatesBackup()
    {
        var path = Path.Combine(directory, "hosts"); File.WriteAllBytes(path, Encoding.UTF8.GetBytes("127.0.0.1 localhost\n"));
        var adapter = new HostsBlocker(path); Assert.True(adapter.CanWrite);
        adapter.Apply(["youtube.com"]); Assert.True(File.Exists(path + ".porog.bak"));
        File.AppendAllText(path, "10.0.0.5 work.local\n"); adapter.Apply([]);
        Assert.Equal("127.0.0.1 localhost\n10.0.0.5 work.local\n", File.ReadAllText(path));
    }

    [Theory]
    [InlineData("NoSuchCommand", "{}")][InlineData("StartBreak", "null")]
    [InlineData("StartBreak", "{\"durationMinutes\":\"abc\"}")][InlineData("GetSession", "{}")]
    public void MalformedCommandsReturnStructuredError(string type, string json)
    {
        var request = new Request(type, Guid.NewGuid(), JsonDocument.Parse(json).RootElement.Clone());
        var result = Create().Handle(request); Assert.False(result.Ok);
        Assert.Equal(request.RequestId, result.RequestId); Assert.Equal("VALIDATION_ERROR", result.Error!.Code);
    }

    public void Dispose() => Directory.Delete(directory, true);
    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset now = RulesTests.Now;
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan value) => now += value;
        public void Set(DateTimeOffset value) => now = value;
    }
    private sealed class FakeWebsites : IWebsiteBlocker
    {
        public bool Fail { get; set; }
        public string[] Domains { get; private set; } = [];
        public bool CanWrite => !Fail;
        public void Apply(IReadOnlyCollection<string> domains)
        {
            if (Fail) throw new PorogException("HOSTS_WRITE_FAILED", "Test permission failure");
            Domains = domains.ToArray();
        }
    }
    private sealed class FakeProcesses : IProcessBlocker
    {
        public bool Fail { get; set; }
        public int NextKills { get; set; }
        public int Calls { get; private set; }
        public ProcessResult Apply(IReadOnlyCollection<string> names)
        {
            Calls++; var count = NextKills; NextKills = 0;
            return Fail ? new(0, [new("PROCESS_CONTROL_FAILED", "Test permission failure")]) : new(count, []);
        }
    }
}
