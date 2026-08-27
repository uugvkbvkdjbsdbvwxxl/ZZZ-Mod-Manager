namespace ZZZModManager.Infrastructure;

public sealed class AppPaths
{
    public string Root { get; }
    public string ModsRoot => Path.Combine(Root, "Mods");
    public string StagingRoot => Path.Combine(Root, "Staging");
    public string BackupsRoot => Path.Combine(Root, "Backups");
    public string ModBackupsRoot => Path.Combine(BackupsRoot, "Mods");
    public string CacheRoot => Path.Combine(Root, "Cache");
    public string CharacterFaceCacheRoot => Path.Combine(CacheRoot, "BaseCharacters");
    public string LogsRoot => Path.Combine(Root, "Logs");
    public string DependenciesRoot => Path.Combine(Root, "Dependencies");
    public string RuntimeRoot => Path.Combine(Root, "Runtime", "ZZMI");
    public string UiRoot => Path.Combine(Root, "UI");
    public string ConfigFile => Path.Combine(Root, "config.json");
    public string LibraryFile => Path.Combine(Root, "library.json");
    public string RuntimeManifestFile => Path.Combine(Root, "runtime-manifest.json");
    public string PresetsFile => Path.Combine(Root, "presets.json");
    // The character roster is data, not code: keeping it beside the library lets
    // users add new characters without waiting for a manager release.
    public string CharacterTableFile => Path.Combine(Root, "characters.json");

    public AppPaths(string? root = null)
    {
        Root = root ?? ResolveDefaultRoot();
    }

    private static string ResolveDefaultRoot()
    {
        // The root is user-configurable through an external pointer, because
        // config.json lives inside the root itself. Everything else stays
        // self-contained under it: do not silently recreate configuration, logs
        // or runtime files under LocalAppData.
        return ModRootPointer.Resolve();
    }

    public void Ensure()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(ModsRoot);
        Directory.CreateDirectory(StagingRoot);
        Directory.CreateDirectory(BackupsRoot);
        Directory.CreateDirectory(ModBackupsRoot);
        Directory.CreateDirectory(CacheRoot);
        Directory.CreateDirectory(CharacterFaceCacheRoot);
        Directory.CreateDirectory(LogsRoot);
        Directory.CreateDirectory(DependenciesRoot);
        Directory.CreateDirectory(RuntimeRoot);
        Directory.CreateDirectory(UiRoot);
    }

    public string CreateStagingDirectory()
    {
        Ensure();
        var path = Path.Combine(StagingRoot, $"import-{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
