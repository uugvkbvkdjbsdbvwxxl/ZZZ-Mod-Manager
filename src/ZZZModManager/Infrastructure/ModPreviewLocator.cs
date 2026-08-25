namespace ZZZModManager.Infrastructure;

/// <summary>
/// Locates a mod's preview image. Mods in the wild ship their screenshot as
/// preview.jpg, Preview.webp or inside an images subfolder, so a single
/// hardcoded root-level "preview.png" left most cards blank.
/// </summary>
public static class ModPreviewLocator
{
    public static readonly string[] SupportedExtensions = [".png", ".jpg", ".jpeg", ".webp", ".bmp"];

    private const string PreferredName = "preview";

    // Mod folders can hold thousands of texture files. The search stays shallow and
    // bounded so a refresh never turns into a full-disk scan.
    private const int MaximumRelativeDepth = 3;
    private const int MaximumInspectedFiles = 4000;

    private static readonly EnumerationOptions Enumeration = new()
    {
        RecurseSubdirectories = true,
        MaxRecursionDepth = MaximumRelativeDepth,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System,
        ReturnSpecialDirectories = false
    };

    /// <summary>
    /// Returns the preview image path relative to <paramref name="root"/>, or null.
    /// The result is stable across refreshes so manifest change detection stays quiet.
    /// </summary>
    public static string? Find(string root)
    {
        if (!Directory.Exists(root))
        {
            return null;
        }

        try
        {
            string? best = null;
            var bestRank = int.MaxValue;
            var inspected = 0;
            foreach (var file in Directory.EnumerateFiles(root, "*", Enumeration))
            {
                if (++inspected > MaximumInspectedFiles)
                {
                    break;
                }

                if (!IsSupported(file))
                {
                    continue;
                }

                var relative = Path.GetRelativePath(root, file);
                var rank = Rank(relative);
                if (rank == int.MaxValue)
                {
                    continue;
                }

                // Ties are broken by path so two equally good candidates always
                // resolve to the same one, whatever order the file system reports.
                if (rank < bestRank
                    || (rank == bestRank && string.Compare(relative, best, StringComparison.OrdinalIgnoreCase) < 0))
                {
                    best = relative;
                    bestRank = rank;
                }
            }

            return best;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Resolves a stored relative preview path against its mod directory, refusing
    /// values that would escape it.
    /// </summary>
    public static string? Resolve(string modDirectory, string? previewFile)
    {
        if (string.IsNullOrWhiteSpace(previewFile))
        {
            return null;
        }

        try
        {
            var combined = Path.GetFullPath(Path.Combine(modDirectory, previewFile));
            return FileSystemSafety.IsWithin(modDirectory, combined) ? combined : null;
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            return null;
        }
    }

    private static bool IsSupported(string path)
    {
        var extension = Path.GetExtension(path);
        return SupportedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);
    }

    private static int Rank(string relative)
    {
        var depth = relative.Count(character => character is '\\' or '/');
        var name = Path.GetFileNameWithoutExtension(relative);
        if (string.Equals(name, PreferredName, StringComparison.OrdinalIgnoreCase))
        {
            return depth;
        }

        if (name.Contains(PreferredName, StringComparison.OrdinalIgnoreCase))
        {
            return 100 + depth;
        }

        // An unrelated image only speaks for the mod when it sits at the root.
        // Deep inside the folder it is far more likely to be a texture.
        return depth == 0 ? 200 : int.MaxValue;
    }
}
