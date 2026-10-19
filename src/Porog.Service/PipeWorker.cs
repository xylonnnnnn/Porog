using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Porog.Core;

namespace Porog.Service;

public sealed class PipeWorker(Controller controller, Owner owner, ILogger<PipeWorker> logger) : BackgroundService
{
    private NamedPipeServerStream CreatePipe()
    {
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new(new SecurityIdentifier(owner.Sid), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(Wire.PipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.WriteThrough, 4096, 4096, security);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var pipe = CreatePipe();
                await pipe.WaitForConnectionAsync(stoppingToken);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(2));
                // One bounded request per connection. Never trust a SID supplied in JSON.
                using var reader = new StreamReader(pipe, new UTF8Encoding(false, true), false, 4096, true);
                var line = new StringBuilder();
                var character = new char[1];
                while (await reader.ReadAsync(character.AsMemory(), timeout.Token) != 0 && character[0] != '\n')
                {
                    if (line.Length >= Wire.MaxMessageLength) throw new InvalidDataException("IPC message too large");
                    line.Append(character[0]);
                }
                string? sid = null;
                pipe.RunAsClient(() => sid = WindowsIdentity.GetCurrent().User?.Value);
                if (sid != owner.Sid) throw new UnauthorizedAccessException("Unexpected IPC identity");
                Response response;
                try
                {
                    var request = JsonSerializer.Deserialize<Request>(line.ToString(), Wire.Json)
                        ?? throw new JsonException("Null request");
                    response = controller.Handle(request);
                }
                catch (JsonException)
                { response = Response.Failure(Guid.Empty, Validation.Invalid("request", "Некорректный JSON IPC.")); }
                using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true);
                await writer.WriteLineAsync(JsonSerializer.Serialize(response, Wire.Json).AsMemory(), timeout.Token);
                await writer.FlushAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or OperationCanceledException or DecoderFallbackException)
            { logger.LogDebug("IPC connection rejected: {ErrorType}", e.GetType().Name); }
        }
    }
}
