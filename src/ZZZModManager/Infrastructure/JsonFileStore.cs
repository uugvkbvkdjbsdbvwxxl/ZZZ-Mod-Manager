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

    public virtual void Save<T>(string path, T value)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllText(temp, JsonSerializer.Serialize(value, Options));
        File.Move(temp, path, true);
    }
}
