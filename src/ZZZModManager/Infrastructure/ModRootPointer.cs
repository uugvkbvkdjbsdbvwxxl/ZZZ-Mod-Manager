namespace ZZZModManager.Infrastructure;

/// <summary>
/// Resolves and persists the Mod library root.
/// </summary>
/// <remarks>
/// The root cannot be stored in config.json, because config.json itself lives
/// inside the root. Only the pointer to the root is kept outside it - a single
/// line of text under LocalAppData. Configuration, logs and runtime files stay
/// where they always were, inside the chosen root.
/// </remarks>
public static class ModRootPointer
{
    public const string DefaultRoot = @"D:\ZZZMod";

    private const string PointerDirectoryName = "ZZZModManager";
    private const string PointerFileName = "mod-root.txt";

    public static string PointerFile { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        PointerDirectoryName,
        PointerFileName);

    public static string Resolve()
    {
        return Resolve(PointerFile);
    }

    public static string Resolve(string pointerFile)
    {
        try
        {
            if (!File.Exists(pointerFile))
            {
                return DefaultRoot;
            }

            var stored = File.ReadAllText(pointerFile);
            return TryNormalize(stored, out var normalized) ? normalized : DefaultRoot;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return DefaultRoot;
        }
    }

    public static bool TrySave(string pointerFile, string? candidate, out string normalized)
    {
        if (!TryNormalize(candidate, out normalized))
        {
            return false;
        }

        try
        {
            // Proving the directory can be created here keeps a bad choice from
            // turning into a silent startup failure on the next launch.
            Directory.CreateDirectory(normalized);
            var directory = Path.GetDirectoryName(pointerFile);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(pointerFile, normalized);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            normalized = string.Empty;
            return false;
        }
    }

    public static bool TryNormalize(string? candidate, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        try
        {
            // The check has to happen before GetFullPath, which resolves a relative
            // path against the current directory and would make every input look
            // fully qualified - silently planting the library wherever the process
            // happened to start.
            var trimmed = candidate.Trim();
            if (!Path.IsPathFullyQualified(trimmed))
            {
                return false;
            }

            var full = Path.GetFullPath(trimmed);
            // A drive root would scatter Mods, Logs, Backups and Staging across the
            // whole volume, so the root always has to be a directory of its own.
            if (string.Equals(full, Path.GetPathRoot(full), StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            normalized = full.TrimEnd(Path.DirectorySeparatorChar);
            return normalized.Length > 0;
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            return false;
        }
    }
}
