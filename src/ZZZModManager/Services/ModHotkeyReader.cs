namespace ZZZModManager.Services;

public sealed record ModHotkey(
    string File,
    string Section,
    IReadOnlyList<string> Keys,
    string? Type,
    IReadOnlyList<string> Variables,
    string? Condition)
{
    public string DisplayName => Section.StartsWith("Key", StringComparison.OrdinalIgnoreCase)
        ? Section[3..]
        : Section;
}

/// <summary>
/// Reads 3DMigoto/ZZMI [Key...] sections without changing the Mod files.
/// </summary>
public static class ModHotkeyReader
{
    public static IReadOnlyList<ModHotkey> Read(string modRoot)
    {
        if (!Directory.Exists(modRoot))
        {
            return [];
        }

        var result = new List<ModHotkey>();
        foreach (var path in Directory.EnumerateFiles(modRoot, "*.ini", SearchOption.AllDirectories)
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            if (Path.GetFileName(path).Equals("zzzmod-live.ini", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            ReadFile(modRoot, path, result);
        }

        return result;
    }

    private static void ReadFile(string modRoot, string path, ICollection<ModHotkey> result)
    {
        string? section = null;
        string? type = null;
        string? condition = null;
        var keys = new List<string>();
        var variables = new List<string>();

        void Flush()
        {
            if (section is not null
                && section.StartsWith("Key", StringComparison.OrdinalIgnoreCase)
                && keys.Count > 0)
            {
                result.Add(new ModHotkey(
                    Path.GetRelativePath(modRoot, path),
                    section,
                    keys.ToArray(),
                    type,
                    variables.ToArray(),
                    condition));
            }

            keys.Clear();
            variables.Clear();
            type = null;
            condition = null;
        }

        foreach (var rawLine in File.ReadLines(path))
        {
            var line = rawLine.Trim();
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                Flush();
                section = line[1..^1].Trim();
                continue;
            }

            if (section is null || line.StartsWith(';') || line.Length == 0)
            {
                continue;
            }

            if (TryReadValue(line, "key", out var key))
            {
                keys.Add(key);
            }
            else if (TryReadValue(line, "type", out var parsedType))
            {
                type = parsedType;
            }
            else if (TryReadValue(line, "condition", out var parsedCondition))
            {
                condition = parsedCondition;
            }
            else if (line.StartsWith('$') && line.Contains('='))
            {
                variables.Add(line);
            }
        }

        Flush();
    }

    private static bool TryReadValue(string line, string key, out string value)
    {
        var separator = line.IndexOf('=');
        if (separator <= 0 || !line[..separator].Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
        {
            value = string.Empty;
            return false;
        }

        value = line[(separator + 1)..].Trim();
        return value.Length > 0;
    }
}
