using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Porog.Core;

namespace Porog.Service;

public sealed class EngineWorker(Controller controller, ILogger<EngineWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        string? lastError = null;
        do
        {
            controller.Tick();
            var status = controller.Status();
            var error = string.Join(",", status.Issues.Select(i => i.Code));
            if (error.Length > 0 && error != lastError)
                logger.LogWarning("Protection state {State}; codes {Codes}", status.ServiceState, error);
            lastError = error;
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
