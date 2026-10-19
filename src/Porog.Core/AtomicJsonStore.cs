using System.Text.Json;

namespace Porog.Core;

/// <summary>The service is the only writer. A corrupt primary is never silently replaced by backup.</summary>
public sealed class AtomicJsonStore<T>(string path, Func<T, T> validate) where T : class
{
    public string Path { get; } = System.IO.Path.GetFullPath(path);

    public T? Load()
    {
        if (!File.Exists(Path)) return null;
        try
        {
            var value = JsonSerializer.Deserialize<T>(File.ReadAllBytes(Path), Wire.Json)
                ?? throw new JsonException("Null document");
            return validate(value);
        }
        catch (Exception e) when (e is JsonException or NotSupportedException or ArgumentException or PorogException)
        {
            throw new PorogException("CONFIG_CORRUPTED", $"Файл {System.IO.Path.GetFileName(Path)} повреждён или несовместим. Исходный файл сохранён; восстановите резервную копию.", inner: e);
        }
    }

    public void Save(T value)
    {
        value = validate(value);
        // Do not overwrite a corrupt file, including one changed by an administrator at runtime.
        _ = Load();
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        var temp = Path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       4096, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, value, Wire.Json);
                stream.Flush(true);
            }
            if (File.Exists(Path)) File.Replace(temp, Path, Path + ".bak", true);
            else File.Move(temp, Path);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
