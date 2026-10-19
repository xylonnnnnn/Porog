using System.Text;
using Porog.Core;

namespace Porog.Core.Tests;

public sealed class RulesTests
{
    internal static readonly DateTimeOffset Now = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);
    internal static Schedule Make(int start = 5, int duration = 30) => new(Guid.NewGuid(), "Диплом", true,
        Now.AddMinutes(start), Now.AddMinutes(start + duration), ["youtube.com"], ["Telegram.exe"]);

    [Theory]
    [InlineData(" YouTube.COM ", "youtube.com")]
    [InlineData("пример.рф", "xn--e1afmkfd.xn--p1ai")]
    [InlineData("www.youtube.com.", "www.youtube.com")]
    public void NormalizesDomains(string input, string expected) => Assert.Equal(expected, Validation.Domain(input));

    [Theory]
    [InlineData("")][InlineData("https://youtube.com")][InlineData("youtube.com/path")]
    [InlineData("you tube.com")][InlineData("localhost")][InlineData("127.0.0.1")]
    [InlineData("-bad.com")][InlineData("a..com")][InlineData("example.com:443")][InlineData("*.example.com")]
    [InlineData("a.com\n0.0.0.0 victim.com")]
    public void RejectsInvalidOrInjectedDomains(string input) => Assert.Throws<PorogException>(() => Validation.Domain(input));

    [Theory]
    [InlineData("Telegram.exe")][InlineData("GAME.EXE")][InlineData("Моя игра.exe")]
    public void AcceptsExecutableNames(string input) => Assert.EndsWith(".exe", Validation.ProcessName(input));

    [Theory]
    [InlineData("explorer.exe")][InlineData("CSRSS.EXE")][InlineData("chrome.exe")]
    [InlineData("Porog.Service.exe")][InlineData("C:\\Telegram.exe")][InlineData("../a.exe")]
    [InlineData("telegram")][InlineData("a.exe\n")]
    public void RejectsUnsafeExecutableNames(string input)
    {
        // Surrounding whitespace is normalized; embedded paths or forbidden names never pass.
        if (input == "a.exe\n") Assert.Equal("a.exe", Validation.ProcessName(input));
        else Assert.Throws<PorogException>(() => Validation.ProcessName(input));
    }

    [Theory]
    [InlineData(3)][InlineData(12)]
    public void AcceptsBreakEndpoints(int minutes) => Validation.BreakDuration(minutes);
    [Theory]
    [InlineData(2)][InlineData(13)][InlineData(0)][InlineData(-1)]
    public void RejectsBreakOutsideEndpoints(int minutes) => Assert.Equal("VALIDATION_ERROR", Assert.Throws<PorogException>(() => Validation.BreakDuration(minutes)).Code);

    [Fact]
    public void RejectsEndBeforeStartAndEmptyTargets()
    {
        var s = Make();
        Assert.Throws<PorogException>(() => Validation.Config(AppConfig.Empty with { Schedules = [s with { EndAt = s.StartAt }] }));
        Assert.Throws<PorogException>(() => Validation.Config(AppConfig.Empty with { Schedules = [s with { DomainTargets = [], ProcessTargets = [] }] }));
    }

    [Fact]
    public void RejectsOverlapButAllowsAdjacentAndDisabled()
    {
        var a = Make(0, 30); var b = Make(29, 30);
        Assert.Equal("SCHEDULE_CONFLICT", Assert.Throws<PorogException>(() => Validation.Config(AppConfig.Empty with { Schedules = [a, b] })).Code);
        Validation.Config(AppConfig.Empty with { Schedules = [a, b with { Enabled = false }] });
        Validation.Config(AppConfig.Empty with { Schedules = [a, b with { StartAt = a.EndAt }] });
    }

    [Fact]
    public void RejectsDuplicateIdsDomainsAndProcesses()
    {
        var a = Make();
        Assert.Throws<PorogException>(() => Validation.Config(AppConfig.Empty with { Schedules = [a, a with { Enabled = false }] }));
        Assert.Throws<PorogException>(() => Validation.Config(AppConfig.Empty with { Schedules = [a with { DomainTargets = ["YouTube.com", "youtube.com"] }] }));
        Assert.Throws<PorogException>(() => Validation.Config(AppConfig.Empty with { Schedules = [a with { ProcessTargets = ["Telegram.exe", "TELEGRAM.EXE"] }] }));
    }

    [Fact]
    public void ActiveScheduleCannotBeDisabledDeletedShortenedOrRetargeted()
    {
        var a = Make(-1, 30);
        var before = Validation.Config(AppConfig.Empty with { Schedules = [a] });
        foreach (var replacement in new Schedule[][] { [], [a with { Enabled = false }], [a with { EndAt = Now }], [a with { DomainTargets = ["vk.com"] }] })
            Assert.Equal("ACTIVE_SESSION_LOCKED", Assert.Throws<PorogException>(() =>
                Validation.GuardActive(before, before with { Schedules = replacement }, Now)).Code);
        Validation.GuardActive(before, before, Now);
        Validation.GuardActive(before, before with { Schedules = [] }, a.EndAt);
    }

    [Fact]
    public void SchedulerUsesHalfOpenIntervalsAndAbsoluteOffsets()
    {
        var a = Make(0, 30);
        var config = AppConfig.Empty with { Schedules = [a] };
        Assert.Null(ScheduleEngine.Evaluate(config, Now.AddTicks(-1), []).Active);
        Assert.Equal(a, ScheduleEngine.Evaluate(config, Now.ToOffset(TimeSpan.FromHours(7)), []).Active);
        Assert.Null(ScheduleEngine.Evaluate(config, a.EndAt, []).Active);
        Assert.Null(ScheduleEngine.Evaluate(config, a.EndAt.AddHours(2), []).Next);
    }

    [Fact]
    public void WarnsOnlyWithinWindowAndOnlyOncePerScheduleVersion()
    {
        var a = Make(5);
        var config = AppConfig.Empty with { Schedules = [a] };
        Assert.Empty(ScheduleEngine.Evaluate(config, a.StartAt.AddSeconds(-61), []).Warnings);
        Assert.Single(ScheduleEngine.Evaluate(config, a.StartAt.AddSeconds(-60), []).Warnings);
        Assert.Empty(ScheduleEngine.Evaluate(config, a.StartAt.AddSeconds(-30), [ScheduleEngine.Key(a)]).Warnings);
        Assert.Empty(ScheduleEngine.Evaluate(config, a.StartAt, []).Warnings);
    }

    [Fact]
    public void HostsPreservesExternalBytesAndEditsWhileRemovingOnlyOwnedBlock()
    {
        var original = Encoding.UTF8.GetBytes("\uFEFF# чужая запись\r\n127.0.0.1 localhost\r\n10.0.0.1 work.local\r\n");
        var blocked = HostsBlocker.Render(original, ["youtube.com"]);
        Assert.Equal(blocked, HostsBlocker.Render(blocked, ["youtube.com"]));
        var externalEdit = blocked.Concat(Encoding.UTF8.GetBytes("10.0.0.2 new.local\r\n")).ToArray();
        var restored = HostsBlocker.Render(externalEdit, []);
        Assert.Equal(original.Concat(Encoding.UTF8.GetBytes("10.0.0.2 new.local\r\n")), restored);
    }

    [Fact]
    public void HostsBlocksExactNamesOnIpv4AndIpv6()
    {
        var text = Encoding.Latin1.GetString(HostsBlocker.Render([], ["youtube.com", "www.youtube.com"]));
        Assert.Contains("0.0.0.0 youtube.com", text); Assert.Contains(":: youtube.com", text);
        Assert.Contains("www.youtube.com", text); Assert.DoesNotContain("m.youtube.com", text);
        Assert.DoesNotContain(HostsBlocker.Begin, Encoding.Latin1.GetString(HostsBlocker.Render(Encoding.Latin1.GetBytes(text), [])));
    }

    [Theory]
    [InlineData("# BEGIN POROG V1\na")][InlineData("# END POROG V1\n")]
    [InlineData("# END POROG V1\n# BEGIN POROG V1\n")]
    [InlineData("# BEGIN POROG V1\n# BEGIN POROG V1\n# END POROG V1\n")]
    [InlineData("# BEGIN POROG V1\n# END POROG V1 foreign\n")]
    public void HostsRefusesBrokenMarkers(string text) => Assert.Throws<PorogException>(() => HostsBlocker.Render(Encoding.Latin1.GetBytes(text), []));
}
