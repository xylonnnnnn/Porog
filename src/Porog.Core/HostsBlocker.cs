using System.Text;

namespace Porog.Core;

public interface IWebsiteBlocker
{
    void Apply(IReadOnlyCollection<string> domains);
    bool CanWrite { get; }
}

/// <summary>Preserves bytes outside our marker block; refuses malformed markers and concurrent changes.</summary>
public sealed class HostsBlocker(string path) : IWebsiteBlocker
{
    public const string Begin = "# BEGIN POROG V1";
    public const string End = "# END POROG V1";
    private static readonly Encoding ByteEncoding = Encoding.Latin1;
    public bool CanWrite
    {
        get
        {
            try { using var stream = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read); return true; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
        }
    }

    public static byte[] Render(byte[] original, IReadOnlyCollection<string> domains)
    {
        var text = ByteEncoding.GetString(original);
        var start = text.IndexOf(Begin, StringComparison.Ordinal);
        var end = text.IndexOf(End, StringComparison.Ordinal);
        if ((start < 0) != (end < 0) || (start >= 0 && (end < start ||
            text.IndexOf(Begin, start + Begin.Length, StringComparison.Ordinal) >= 0 ||
            text.IndexOf(End, end + End.Length, StringComparison.Ordinal) >= 0)))
            throw new PorogException("HOSTS_WRITE_FAILED", "Маркеры Porog в hosts повреждены. Требуется ручная проверка.");
        if (start >= 0)
        {
            if ((start > 0 && text[start - 1] != '\n') ||
                (end > 0 && text[end - 1] != '\n'))
                throw new PorogException("HOSTS_WRITE_FAILED", "Маркер Porog должен занимать отдельную строку.");
            var finish = end + End.Length;
            if (finish < text.Length && text[finish] == '\r') finish++;
            if (finish < text.Length && text[finish] == '\n') finish++;
            else if (finish < text.Length)
                throw new PorogException("HOSTS_WRITE_FAILED", "После маркера Porog обнаружены посторонние данные.");
            text = text.Remove(start, finish - start);
        }
        var normalized = domains.Select(Validation.Domain).Distinct().Order().ToArray();
        if (normalized.Length == 0) return ByteEncoding.GetBytes(text);
        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        // Any separator added here becomes an ordinary trailing newline and is never removed later.
        if (text.Length > 0 && !text.EndsWith('\n')) text += newline;
        var block = new StringBuilder(Begin).Append(newline);
        foreach (var domain in normalized)
            block.Append("0.0.0.0 ").Append(domain).Append(newline)
                .Append(":: ").Append(domain).Append(newline);
        block.Append(End).Append(newline);
        return ByteEncoding.GetBytes(text + block);
    }

    public void Apply(IReadOnlyCollection<string> domains)
    {
        try
        {
            // Deny concurrent writers for the complete read/modify/write transaction.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
            if (stream.Length > 8 * 1024 * 1024) throw new IOException("hosts exceeds 8 MiB");
            var original = new byte[(int)stream.Length];
            stream.ReadExactly(original);
            var changed = Render(original, domains);
            if (original.AsSpan().SequenceEqual(changed)) return;
            // A separate backup is diagnostic only, never used to overwrite external modifications.
            File.WriteAllBytes(path + ".porog.bak", original);
            stream.Position = 0;
            try
            {
                stream.Write(changed);
                stream.SetLength(changed.Length);
                stream.Flush(true);
            }
            catch
            {
                stream.Position = 0;
                stream.Write(original);
                stream.SetLength(original.Length);
                stream.Flush(true);
                throw;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new PorogException("HOSTS_WRITE_FAILED", "Не удалось изменить hosts. Проверьте права и доступ к файлу.", retryable: true, inner: e);
        }
    }
}
