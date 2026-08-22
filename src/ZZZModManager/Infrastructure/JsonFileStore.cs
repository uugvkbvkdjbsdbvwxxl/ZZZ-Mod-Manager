using System.Text.Json;
using System.Text.Json.Serialization;

namespace ZZZModManager.Infrastructure;

public class JsonFileStore
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.General)
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    public virtual T Load<T>(string path, Func<T> factory)
    {
        try
        {
            if (!File.Exists(path))
            {
                return factory();
            }

            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<T>(json, Options) ?? factory();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return factory();
        }
    }

    // A failed save must never take the process down: state is written from the
    // MainWindow constructor, and an unwritable root (locked file, read-only or
    // missing volume) used to surface as a silent 0xE0434352 crash at startup.
    // The write is still atomic, and the temp file is removed on every failure.
    public virtual void Save<T>(string path, T value)
    {
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(temp, JsonSerializer.Serialize(value, Options));
            File.Move(temp, path, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            TryDelete(temp);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nothing left to do; the orphaned temp file is harmless.
        }
    }
}
