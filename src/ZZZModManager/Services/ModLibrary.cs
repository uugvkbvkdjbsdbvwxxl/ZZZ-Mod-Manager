using ZZZModManager.Infrastructure;
using ZZZModManager.Models;

namespace ZZZModManager.Services;

public interface IConflictDetector
{
    IReadOnlyList<ModManifest> FindConflicts(ModManifest candidate, IEnumerable<ModManifest> enabledMods);
}

public sealed class ConflictDetector : IConflictDetector
{
    public IReadOnlyList<ModManifest> FindConflicts(ModManifest candidate, IEnumerable<ModManifest> enabledMods) =>
        enabledMods
            .Where(mod => !string.Equals(mod.Id, candidate.Id, StringComparison.OrdinalIgnoreCase))
            .Where(mod => mod.Enabled)
            .Where(mod => mod.Hashes.Overlaps(candidate.Hashes))
            .ToList();
}

public interface IModLibrary
{
    IReadOnlyList<ModManifest> GetAll();
    IReadOnlyList<SplitModPackage> FindSplitPackages();
    IReadOnlyList<CharacterGroupInfo> GetAvailableCharacterGroups();
    CharacterGroupInfo DetectCharacterGroup(ModManifest manifest);
    void RegisterCustomCharacterGroup(CharacterGroupInfo group);
    ModManifest Install(ImportCandidate candidate, ImportReport report);
    IReadOnlyList<UnmanagedDirectoryChange> QuarantineActiveUnmanagedDirectories();
    ModLibraryBatchResult ApplyStateBatch(string id, bool enabled, bool keepLoaded);
    ModLibraryBatchResult ApplyStateBatch(IEnumerable<ModStateRequest> requests, bool keepLoaded);
    bool PreloadForLiveSwitch(IEnumerable<string> modIds);
    bool NormalizeDisabledDirectories();
    void SetEnabled(string id, bool enabled, bool keepLoaded = false);
    void SetCharacterGroupOverride(string id, string? groupKey);
    void Delete(string id);
    void SaveReport(ModManifest manifest, ImportReport report);
    void SaveChanges();
    string GetAbsolutePath(ModManifest manifest);
    IReadOnlyList<ModManifest> FindConflicts(ModManifest candidate);
}

public sealed class ModLibrary : IModLibrary
{
    private readonly AppPaths _paths;
    private readonly JsonFileStore _store;
    private readonly IConflictDetector _conflictDetector;
    private readonly LibraryState _state;
    private bool _discoveredGroupsRefreshed;

    public ModLibrary(AppPaths paths, JsonFileStore store, IConflictDetector conflictDetector)
    {
        _paths = paths;
        _store = store;
        _conflictDetector = conflictDetector;
        _paths.Ensure();
        _state = _store.Load(_paths.LibraryFile, () => new LibraryState());
        _state.Mods ??= [];
        _state.DiscoveredCharacterGroups ??= [];
        _state.CustomCharacterGroups ??= [];
        MigrateSchema();
        ReconcileMissingDirectories();
        RefreshDiscoveredCharacterGroups();
    }

    public IReadOnlyList<ModManifest> GetAll() => _state.Mods
        .OrderBy(mod => mod.DisplayName, StringComparer.CurrentCultureIgnoreCase)
        .ToList();

    public IReadOnlyList<SplitModPackage> FindSplitPackages() => _state.Mods
        .Select(manifest => new
        {
            Manifest = manifest,
            Family = GetSplitPackageFamily(manifest)
        })
        .Where(item => !string.IsNullOrWhiteSpace(item.Family)
                       && !string.IsNullOrWhiteSpace(item.Manifest.SourceSha256))
        .GroupBy(item => $"{item.Manifest.SourceSha256}|{item.Family}", StringComparer.OrdinalIgnoreCase)
        .Where(group => group.Count() > 1)
        .Select(group => new SplitModPackage
        {
            Key = group.Key,
            SourcePath = group.First().Manifest.SourcePath,
            SourceSha256 = group.First().Manifest.SourceSha256,
            Mods = group.Select(item => item.Manifest).ToList()
        })
        .OrderBy(package => package.SourcePath, StringComparer.OrdinalIgnoreCase)
        .ToList();

    public IReadOnlyList<CharacterGroupInfo> GetAvailableCharacterGroups()
    {
        RefreshDiscoveredCharacterGroups();
        return CharacterGroupDetector.KnownGroups
            .Concat(_state.DiscoveredCharacterGroups)
            .Concat(_state.CustomCharacterGroups)
            .Where(group => CharacterGroupDetector.IsRoleGroup(group.Kind)
                            || group.Kind == CharacterGroupKind.Framework)
            .DistinctBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Kind == CharacterGroupKind.Framework ? 1 : 0)
            .ThenBy(group => group.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public CharacterGroupInfo DetectCharacterGroup(ModManifest manifest)
    {
        RefreshDiscoveredCharacterGroups();
        return CharacterGroupDetector.DetectInfo(manifest, GetAbsolutePath(manifest), GetAdditionalGroups());
    }

    public void RegisterCustomCharacterGroup(CharacterGroupInfo group)
    {
        if (group.Kind != CharacterGroupKind.Custom)
        {
            throw new ArgumentException("只能注册自定义角色分组。", nameof(group));
        }

        var canonical = CharacterGroupDetector.CreateCustomGroup(group.DisplayName);
        if (_state.CustomCharacterGroups.Any(item =>
                string.Equals(item.Key, canonical.Key, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        _state.CustomCharacterGroups.Add(canonical);
        SaveState();
    }

    public ModManifest Install(ImportCandidate candidate, ImportReport report)
    {
        if (report.Status == ImportStatus.Blocked)
        {
            throw new InvalidOperationException("被阻止的 Mod 不能安装。");
        }

        var baseId = FileSystemSafety.SanitizeDirectoryName(candidate.DisplayName).Replace(' ', '_');
        var id = EnsureUniqueId(baseId);
        var tempDirectory = Path.Combine(_paths.ModsRoot, $".installing-{id}-{Guid.NewGuid():N}");
        var finalDirectory = Path.Combine(_paths.ModsRoot, "DISABLED_" + id);
        ModManifest? addedManifest = null;

        try
        {
            FileSystemSafety.CopyDirectory(candidate.StagedPath, tempDirectory);
            _store.Save(Path.Combine(tempDirectory, "import-report.json"), report);
            Directory.Move(tempDirectory, finalDirectory);

            var manifest = new ModManifest
            {
                SchemaVersion = 2,
                Id = id,
                DisplayName = candidate.DisplayName,
                SourcePath = candidate.SourcePath,
                SourceSha256 = candidate.SourceSha256,
                InstalledDirectory = Path.GetFileName(finalDirectory),
                ImportedAt = DateTimeOffset.UtcNow,
                Enabled = false,
                ImportStatus = report.Status,
                Hashes = new HashSet<string>(report.Hashes, StringComparer.OrdinalIgnoreCase),
                Dependencies = [.. report.Dependencies],
                AppliedFixes = [.. report.Fixes],
                ReportFile = "import-report.json",
                PreviewFile = ModPreviewLocator.Find(finalDirectory)
            };

            _state.Mods.Add(manifest);
            _discoveredGroupsRefreshed = false;
            addedManifest = manifest;
            SaveState();
            return manifest;
        }
        catch
        {
            if (addedManifest is not null)
            {
                _state.Mods.Remove(addedManifest);
            }

            SafeDelete(tempDirectory);
            SafeDelete(finalDirectory);
            throw;
        }
    }

    /// <summary>
    /// 3DMigoto recursively loads every directory that is not prefixed with
    /// DISABLED_. A folder copied directly into the library, or selected as an
    /// import source from inside the library, would otherwise bypass manifests
    /// and make a disabled card appear active in game. Preserve such folders
    /// in place, but move them behind a recoverable DISABLED_UNMANAGED_ prefix.
    /// </summary>
    public IReadOnlyList<UnmanagedDirectoryChange> QuarantineActiveUnmanagedDirectories()
    {
        var managedNames = _state.Mods
            .Select(manifest => Path.GetFileName(manifest.InstalledDirectory))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var candidates = Directory.EnumerateDirectories(_paths.ModsRoot, "*", SearchOption.TopDirectoryOnly)
            .Where(path =>
            {
                var name = Path.GetFileName(path);
                return !managedNames.Contains(name)
                       && !name.StartsWith("DISABLED_", StringComparison.OrdinalIgnoreCase)
                       && !name.StartsWith(".", StringComparison.Ordinal)
                       && ContainsIni(path);
            })
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (candidates.Count == 0)
        {
            return [];
        }

        var reservedNames = Directory.EnumerateFileSystemEntries(_paths.ModsRoot, "*", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var planned = new List<(string Source, string Target, UnmanagedDirectoryChange Change)>();
        foreach (var source in candidates)
        {
            var originalName = Path.GetFileName(source);
            var baseName = "DISABLED_UNMANAGED_" + FileSystemSafety.SanitizeDirectoryName(originalName);
            var targetName = baseName;
            var index = 2;
            while (!reservedNames.Add(targetName))
            {
                targetName = $"{baseName}_{index++}";
            }

            var target = Path.Combine(_paths.ModsRoot, targetName);
            planned.Add((source, target, new UnmanagedDirectoryChange(originalName, targetName)));
        }
        var moved = new List<(string Source, string Target)>();
        try
        {
            foreach (var item in planned)
            {
                Directory.Move(item.Source, item.Target);
                moved.Add((item.Source, item.Target));
            }

            return planned.Select(item => item.Change).ToList();
        }
        catch
        {
            foreach (var item in moved.AsEnumerable().Reverse())
            {
                if (Directory.Exists(item.Target) && !Directory.Exists(item.Source))
                {
                    Directory.Move(item.Target, item.Source);
                }
            }

            throw;
        }
    }

    /// <summary>
    /// Applies the user's target plus same-character single select and hash conflicts
    /// as one transaction. A failed directory move or library save is rolled back.
    /// </summary>
    public ModLibraryBatchResult ApplyStateBatch(string id, bool enabled, bool keepLoaded)
    {
        var target = Find(id) ?? throw new InvalidOperationException("找不到该 Mod。");
        var requests = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
        {
            [target.Id] = enabled
        };
        var disabledByCharacter = new List<ModManifest>();
        var disabledByConflict = new List<ModManifest>();

        if (enabled)
        {
            var targetGroup = DetectCharacterGroup(target);
            if (CharacterGroupDetector.IsRoleGroup(targetGroup.Kind))
            {
                foreach (var other in _state.Mods.Where(mod => mod.Enabled && !SameId(mod, target)))
                {
                    var otherGroup = DetectCharacterGroup(other);
                    if (CharacterGroupDetector.IsRoleGroup(otherGroup.Kind)
                        && string.Equals(otherGroup.Key, targetGroup.Key, StringComparison.OrdinalIgnoreCase))
                    {
                        requests[other.Id] = false;
                        disabledByCharacter.Add(other);
                    }
                }
            }

            foreach (var conflict in _conflictDetector.FindConflicts(target, _state.Mods))
            {
                requests[conflict.Id] = false;
                if (!disabledByCharacter.Any(mod => SameId(mod, conflict)))
                {
                    disabledByConflict.Add(conflict);
                }
            }
        }

        var result = ApplyRequests(requests.Select(pair => new ModStateRequest(pair.Key, pair.Value)), keepLoaded);
        return new ModLibraryBatchResult
        {
            ChangedMods = result.ChangedMods,
            IncludeTreeChanged = result.IncludeTreeChanged,
            DisabledByCharacter = disabledByCharacter,
            DisabledByConflict = disabledByConflict
        };
    }

    public ModLibraryBatchResult ApplyStateBatch(IEnumerable<ModStateRequest> requests, bool keepLoaded) =>
        ApplyRequests(requests, keepLoaded);

    public void SetEnabled(string id, bool enabled, bool keepLoaded = false) =>
        ApplyRequests([new ModStateRequest(id, enabled)], keepLoaded);

    /// <summary>
    /// Keeps selected, live-switchable mods in the recursive include tree while
    /// preserving their desired disabled state. This is used before process
    /// startup so static vertex metadata is registered once and later toggles do
    /// not require a directory move or an F10 reload.
    /// </summary>
    public bool PreloadForLiveSwitch(IEnumerable<string> modIds)
    {
        var ids = modIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (ids.Count == 0)
        {
            return false;
        }

        var plans = new List<PreloadPlan>();
        var reconciledDirectory = false;
        foreach (var manifest in _state.Mods.Where(item => ids.Contains(item.Id)))
        {
            var resolution = ResolveExistingDirectory(manifest);
            reconciledDirectory |= resolution.ManifestChanged;
            var currentPath = resolution.Path;
            var currentName = Path.GetFileName(currentPath);
            var targetName = manifest.Id;
            var targetPath = Path.Combine(_paths.ModsRoot, targetName);
            var requiresMove = !string.Equals(currentName, targetName, StringComparison.OrdinalIgnoreCase);
            if (requiresMove && (File.Exists(targetPath) || Directory.Exists(targetPath)))
            {
                throw new IOException($"目标目录已存在：{targetPath}");
            }

            if (requiresMove)
            {
                plans.Add(new PreloadPlan(manifest, manifest.InstalledDirectory, currentPath, targetName, targetPath));
            }
        }

        if (plans.Count == 0)
        {
            if (reconciledDirectory)
            {
                SaveState();
            }

            return false;
        }

        var moved = new List<PreloadPlan>();
        try
        {
            foreach (var plan in plans)
            {
                Directory.Move(plan.SourcePath, plan.TargetPath);
                moved.Add(plan);
                plan.Manifest.InstalledDirectory = plan.TargetDirectory;
            }

            SaveState();
            return true;
        }
        catch
        {
            foreach (var plan in moved.AsEnumerable().Reverse())
            {
                try
                {
                    if (Directory.Exists(plan.TargetPath) && !Directory.Exists(plan.SourcePath))
                    {
                        Directory.Move(plan.TargetPath, plan.SourcePath);
                    }
                }
                catch
                {
                    // Preserve the first failure; the source remains recoverable.
                }
            }

            foreach (var plan in plans)
            {
                plan.Manifest.InstalledDirectory = plan.OriginalDirectory;
            }

            try
            {
                SaveState();
            }
            catch
            {
                // Preserve the original transaction failure.
            }

            throw;
        }
    }

    public bool NormalizeDisabledDirectories()
    {
        var requests = _state.Mods
            .Where(manifest => !manifest.Enabled
                               && !Path.GetFileName(manifest.InstalledDirectory).StartsWith("DISABLED_", StringComparison.OrdinalIgnoreCase))
            .Select(manifest => new ModStateRequest(manifest.Id, false))
            .ToList();
        return requests.Count > 0 && ApplyRequests(requests, keepLoaded: false).IncludeTreeChanged;
    }

    public void SetCharacterGroupOverride(string id, string? groupKey)
    {
        var manifest = Find(id) ?? throw new InvalidOperationException("找不到该 Mod。");
        if (!string.IsNullOrWhiteSpace(groupKey)
            && CharacterGroupDetector.FindGroup(groupKey, GetAdditionalGroups()) is null)
        {
            throw new InvalidOperationException("未知的角色分组。");
        }

        manifest.CharacterGroupOverrideKey = string.IsNullOrWhiteSpace(groupKey) ? null : groupKey;
        _discoveredGroupsRefreshed = false;
        SaveState();
    }

    public void Delete(string id)
    {
        var manifest = Find(id) ?? throw new InvalidOperationException("找不到该 Mod。");
        var path = GetAbsolutePath(manifest);
        if (!FileSystemSafety.IsWithin(_paths.ModsRoot, path))
        {
            throw new InvalidOperationException("拒绝删除库目录之外的路径。");
        }

        var deleteDirectory = Path.Combine(
            _paths.ModsRoot,
            "DISABLED_DELETING_" + FileSystemSafety.SanitizeDirectoryName(manifest.Id) + "-" + Guid.NewGuid().ToString("N"));
        var originalIndex = _state.Mods.IndexOf(manifest);
        var moved = false;
        var removed = false;
        try
        {
            Directory.Move(path, deleteDirectory);
            moved = true;
            _state.Mods.Remove(manifest);
            _discoveredGroupsRefreshed = false;
            removed = true;
            SaveState();
            SafeDelete(deleteDirectory);
        }
        catch
        {
            if (removed && !_state.Mods.Contains(manifest))
            {
                _state.Mods.Insert(Math.Clamp(originalIndex, 0, _state.Mods.Count), manifest);
            }

            if (moved && !Directory.Exists(path) && Directory.Exists(deleteDirectory))
            {
                try
                {
                    Directory.Move(deleteDirectory, path);
                }
                catch
                {
                    // The DISABLED_DELETING_ prefix keeps a failed cleanup out of ZZMI.
                }
            }

            try
            {
                SaveState();
            }
            catch
            {
                // Preserve the original failure while keeping the current memory state.
            }

            throw;
        }
    }

    public void SaveReport(ModManifest manifest, ImportReport report) =>
        _store.Save(Path.Combine(GetAbsolutePath(manifest), manifest.ReportFile), report);

    public void SaveChanges() => SaveState();

    public string GetAbsolutePath(ModManifest manifest)
    {
        var path = Path.GetFullPath(Path.Combine(_paths.ModsRoot, manifest.InstalledDirectory));
        if (!FileSystemSafety.IsWithin(_paths.ModsRoot, path))
        {
            throw new InvalidOperationException("Mod 清单包含不安全路径。");
        }

        return path;
    }

    public IReadOnlyList<ModManifest> FindConflicts(ModManifest candidate) =>
        _conflictDetector.FindConflicts(candidate, _state.Mods);

    private ModLibraryBatchResult ApplyRequests(IEnumerable<ModStateRequest> requests, bool keepLoaded)
    {
        var desired = requests
            .GroupBy(request => request.ModId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Last().Enabled, StringComparer.OrdinalIgnoreCase);
        var plans = new List<StatePlan>();
        var reconciledDirectory = false;

        foreach (var pair in desired)
        {
            var manifest = Find(pair.Key) ?? throw new InvalidOperationException($"找不到 Mod：{pair.Key}");
            var resolution = ResolveExistingDirectory(manifest);
            var currentPath = resolution.Path;
            reconciledDirectory |= resolution.ManifestChanged;

            var currentName = Path.GetFileName(currentPath);
            var targetName = pair.Value ? manifest.Id : "DISABLED_" + manifest.Id;
            var keepActiveDirectory = keepLoaded
                                      && !pair.Value
                                      && string.Equals(currentName, manifest.Id, StringComparison.OrdinalIgnoreCase);
            var targetPath = keepActiveDirectory
                ? currentPath
                : Path.Combine(_paths.ModsRoot, targetName);
            var requiresMove = !string.Equals(currentPath, targetPath, StringComparison.OrdinalIgnoreCase);
            if (requiresMove && (File.Exists(targetPath) || Directory.Exists(targetPath)))
            {
                throw new IOException($"目标目录已存在：{targetPath}");
            }

            if (manifest.Enabled != pair.Value || requiresMove)
            {
                plans.Add(new StatePlan(manifest, manifest.Enabled, manifest.InstalledDirectory, pair.Value, targetName, currentPath, targetPath, requiresMove));
            }
        }

        if (plans.Count == 0)
        {
            if (reconciledDirectory)
            {
                SaveState();
            }

            return new ModLibraryBatchResult();
        }

        var moved = new List<StatePlan>();
        try
        {
            foreach (var plan in plans)
            {
                if (plan.RequiresMove)
                {
                    Directory.Move(plan.SourcePath, plan.TargetPath);
                    moved.Add(plan);
                    plan.Manifest.InstalledDirectory = plan.TargetDirectory;
                }

                plan.Manifest.Enabled = plan.TargetEnabled;
            }

            SaveState();
            return new ModLibraryBatchResult
            {
                ChangedMods = plans.Select(plan => plan.Manifest).ToList(),
                IncludeTreeChanged = moved.Count > 0
            };
        }
        catch
        {
            foreach (var plan in moved.AsEnumerable().Reverse())
            {
                try
                {
                    if (Directory.Exists(plan.TargetPath) && !Directory.Exists(plan.SourcePath))
                    {
                        Directory.Move(plan.TargetPath, plan.SourcePath);
                    }
                }
                catch
                {
                    // The original failure is more actionable; memory is still restored below.
                }
            }

            foreach (var plan in plans)
            {
                plan.Manifest.Enabled = plan.OriginalEnabled;
                plan.Manifest.InstalledDirectory = plan.OriginalDirectory;
            }

            try
            {
                SaveState();
            }
            catch
            {
                // Preserve and rethrow the first transaction failure.
            }

            throw;
        }
    }

    private ModManifest? Find(string id) => _state.Mods.FirstOrDefault(
        mod => string.Equals(mod.Id, id, StringComparison.OrdinalIgnoreCase));

    private static string? GetSplitPackageFamily(ModManifest manifest)
    {
        var suffixes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "misc", "soundwave", "optional", "extra", "addon", "add-on"
        };

        foreach (var value in new[] { manifest.DisplayName, manifest.Id })
        {
            foreach (var separator in new[] { " - ", "_-_" })
            {
                var separatorIndex = value.LastIndexOf(separator, StringComparison.Ordinal);
                if (separatorIndex <= 0)
                {
                    continue;
                }

                var suffix = value[(separatorIndex + separator.Length)..].Trim();
                if (!suffixes.Contains(suffix))
                {
                    continue;
                }

                var family = value[..separatorIndex].Trim();
                return string.IsNullOrWhiteSpace(family) ? null : family.ToLowerInvariant();
            }
        }

        return null;
    }

    private string EnsureUniqueId(string baseId)
    {
        var id = baseId;
        var index = 2;
        while (_state.Mods.Any(mod => string.Equals(mod.Id, id, StringComparison.OrdinalIgnoreCase))
               || Directory.Exists(Path.Combine(_paths.ModsRoot, id))
               || Directory.Exists(Path.Combine(_paths.ModsRoot, "DISABLED_" + id)))
        {
            id = $"{baseId}_{index++}";
        }

        return id;
    }

    private static bool ContainsIni(string directory)
    {
        try
        {
            return Directory.EnumerateFiles(directory, "*.ini", SearchOption.AllDirectories).Any();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private void MigrateSchema()
    {
        var changed = _state.SchemaVersion < 3;
        _state.SchemaVersion = 3;
        _state.DiscoveredCharacterGroups = NormalizeGroups(
            _state.DiscoveredCharacterGroups,
            CharacterGroupKind.Discovered,
            "discovered:",
            ref changed);
        _state.CustomCharacterGroups = NormalizeGroups(
            _state.CustomCharacterGroups,
            CharacterGroupKind.Custom,
            "custom:",
            ref changed);
        foreach (var manifest in _state.Mods)
        {
            if (manifest.SchemaVersion < 2)
            {
                manifest.SchemaVersion = 2;
                changed = true;
            }

            var preview = ModPreviewLocator.Find(GetAbsolutePath(manifest));
            if (!string.Equals(preview, manifest.PreviewFile, StringComparison.Ordinal))
            {
                manifest.PreviewFile = preview;
                changed = true;
            }
        }

        if (changed)
        {
            SaveState();
        }
    }

    private void RefreshDiscoveredCharacterGroups()
    {
        if (_discoveredGroupsRefreshed)
        {
            return;
        }

        // Rebuild discovered groups from the current manifests. This also
        // removes stale labels created by an older, less selective scanner
        // (for example "TextureOverride" or a mesh part such as "头发").
        var previous = _state.DiscoveredCharacterGroups.ToList();
        var customGroups = _state.CustomCharacterGroups.ToList();
        var discovered = new Dictionary<string, CharacterGroupInfo>(StringComparer.OrdinalIgnoreCase);
        var changed = false;
        foreach (var manifest in _state.Mods)
        {
            var previousOverride = previous.FirstOrDefault(group =>
                string.Equals(group.Key, manifest.CharacterGroupOverrideKey, StringComparison.OrdinalIgnoreCase));
            if (previousOverride is not null)
            {
                discovered[previousOverride.Key] = previousOverride;
                continue;
            }

            var detected = CharacterGroupDetector.DetectInfo(
                manifest,
                GetAbsolutePath(manifest),
                customGroups);
            if (detected.Kind != CharacterGroupKind.Discovered)
            {
                continue;
            }

            discovered[detected.Key] = detected;
        }

        if (previous.Count != discovered.Count
            || previous.Any(group => !discovered.ContainsKey(group.Key))
            || previous.Any(group => discovered.TryGetValue(group.Key, out var current)
                                     && !string.Equals(group.DisplayName, current.DisplayName, StringComparison.Ordinal)))
        {
            _state.DiscoveredCharacterGroups = discovered.Values.ToList();
            changed = true;
        }

        if (changed)
        {
            SaveState();
        }

        _discoveredGroupsRefreshed = true;
    }

    private IReadOnlyList<CharacterGroupInfo> GetAdditionalGroups() =>
        _state.DiscoveredCharacterGroups
            .Concat(_state.CustomCharacterGroups)
            .ToList();

    private static List<CharacterGroupInfo> NormalizeGroups(
        IEnumerable<CharacterGroupInfo>? source,
        CharacterGroupKind kind,
        string keyPrefix,
        ref bool changed)
    {
        var result = new List<CharacterGroupInfo>();
        foreach (var group in source ?? [])
        {
            if (group is null
                || group.Kind != kind
                || string.IsNullOrWhiteSpace(group.Key)
                || string.IsNullOrWhiteSpace(group.DisplayName)
                || !group.Key.StartsWith(keyPrefix, StringComparison.OrdinalIgnoreCase)
                || result.Any(item => string.Equals(item.Key, group.Key, StringComparison.OrdinalIgnoreCase)))
            {
                changed = true;
                continue;
            }

            var normalized = new CharacterGroupInfo(group.Key.Trim(), group.DisplayName.Trim(), kind);
            if (!string.Equals(normalized.Key, group.Key, StringComparison.Ordinal)
                || !string.Equals(normalized.DisplayName, group.DisplayName, StringComparison.Ordinal))
            {
                changed = true;
            }

            result.Add(normalized);
        }

        return result;
    }

    private void ReconcileMissingDirectories()
    {
        var changed = false;
        foreach (var manifest in _state.Mods)
        {
            if (Directory.Exists(GetAbsolutePath(manifest)))
            {
                continue;
            }

            var resolution = TryResolveExistingDirectory(manifest);
            if (resolution is not null)
            {
                manifest.InstalledDirectory = Path.GetFileName(resolution);
                manifest.PreviewFile = ModPreviewLocator.Find(resolution);
                changed = true;
            }
            else if (manifest.Enabled)
            {
                manifest.Enabled = false;
                changed = true;
            }
        }

        if (changed)
        {
            SaveState();
        }
    }

    private DirectoryResolution ResolveExistingDirectory(ModManifest manifest)
    {
        var configuredPath = GetAbsolutePath(manifest);
        if (Directory.Exists(configuredPath))
        {
            return new DirectoryResolution(configuredPath, ManifestChanged: false);
        }

        var recoveredPath = TryResolveExistingDirectory(manifest);
        if (recoveredPath is null)
        {
            throw new DirectoryNotFoundException($"找不到 Mod 目录：{configuredPath}");
        }

        manifest.InstalledDirectory = Path.GetFileName(recoveredPath);
        manifest.PreviewFile = ModPreviewLocator.Find(recoveredPath);
        return new DirectoryResolution(recoveredPath, ManifestChanged: true);
    }

    private string? TryResolveExistingDirectory(ModManifest manifest)
    {
        var candidates = new[]
            {
                Path.Combine(_paths.ModsRoot, manifest.Id),
                Path.Combine(_paths.ModsRoot, "DISABLED_" + manifest.Id)
            }
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(Directory.Exists)
            .ToList();

        return candidates.Count switch
        {
            0 => null,
            1 => candidates[0],
            _ => throw new IOException($"Mod 目录状态不明确，同时存在启用和禁用副本：{manifest.Id}")
        };
    }

    private void SaveState() => _store.Save(_paths.LibraryFile, _state);

    private static bool SameId(ModManifest left, ModManifest right) =>
        string.Equals(left.Id, right.Id, StringComparison.OrdinalIgnoreCase);

    private void SafeDelete(string path)
    {
        if (!FileSystemSafety.IsWithin(_paths.ModsRoot, path))
        {
            return;
        }

        if (Directory.Exists(path))
        {
            Directory.Delete(path, true);
        }
    }

    private sealed record StatePlan(
        ModManifest Manifest,
        bool OriginalEnabled,
        string OriginalDirectory,
        bool TargetEnabled,
        string TargetDirectory,
        string SourcePath,
        string TargetPath,
        bool RequiresMove);

    private sealed record PreloadPlan(
        ModManifest Manifest,
        string OriginalDirectory,
        string SourcePath,
        string TargetDirectory,
        string TargetPath);

    private sealed record DirectoryResolution(string Path, bool ManifestChanged);
}
