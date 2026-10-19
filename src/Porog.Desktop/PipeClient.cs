using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Porog.Core;

namespace Porog.Desktop;

public sealed class PipeClient
{
    public async Task<T> Send<T>(string type, object payload, CancellationToken cancellation = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            await using var pipe = new NamedPipeClientStream(".", Wire.PipeName, PipeDirection.InOut,
                PipeOptions.Asynchronous, TokenImpersonationLevel.Impersonation);
            await pipe.ConnectAsync(timeout.Token);
            var id = Guid.NewGuid();
            var request = new Request(type, id, Wire.Element(payload));
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true);
            await writer.WriteLineAsync(JsonSerializer.Serialize(request, Wire.Json).AsMemory(), timeout.Token);
            await writer.FlushAsync(timeout.Token);
            using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, true);
            var line = new StringBuilder();
            var ch = new char[1];
            while (await reader.ReadAsync(ch.AsMemory(), timeout.Token) != 0 && ch[0] != '\n')
            {
                if (line.Length >= Wire.MaxMessageLength * 4) throw new IOException("Oversized response");
                line.Append(ch[0]);
            }
            var response = JsonSerializer.Deserialize<Response>(line.ToString(), Wire.Json)
                ?? throw new IOException("Empty response");
            if (response.RequestId != id) throw new IOException("Unexpected response ID");
            if (!response.Ok)
                throw new PorogException(response.Error?.Code ?? "INTERNAL_ERROR",
                    response.Error?.Message ?? "Ошибка службы.", response.Error?.Details);
            return response.Payload!.Value.DeserializeRequired<T>();
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or UnauthorizedAccessException or JsonException)
        {
            throw new PorogException("SERVICE_UNAVAILABLE", "Служба не отвечает. Блокировка может не работать. Проверьте установку и повторите подключение.", retryable: true, inner: e);
        }
    }
}
