using System.Security.Principal;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Porog.Core;
using Porog.Service;

if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Porog Service requires Windows.");
if (args.SequenceEqual(new[] { "--cleanup-hosts" }))
{
    if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
        throw new UnauthorizedAccessException("Administrator recovery requires elevation.");
    new HostsBlocker(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "drivers", "etc", "hosts")).Apply([]);
    return;
}
var data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Porog");
var ownerPath = Path.Combine(data, "owner.json");
if (!File.Exists(ownerPath)) throw new InvalidOperationException("Сначала запустите scripts/Install.ps1.");
var owner = JsonSerializer.Deserialize<Owner>(File.ReadAllText(ownerPath), Wire.Json)
    ?? throw new InvalidOperationException("Некорректный owner.json.");
_ = new SecurityIdentifier(owner.Sid);
var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(options => options.ServiceName = "Porog");
builder.Services.AddSingleton(new Controller(data,
    new HostsBlocker(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "drivers", "etc", "hosts")),
    new ProcessBlocker(owner.Sid)));
builder.Services.AddSingleton(owner);
builder.Services.AddHostedService<EngineWorker>();
builder.Services.AddHostedService<PipeWorker>();
await builder.Build().RunAsync();

namespace Porog.Service { public sealed record Owner(string Sid); }
