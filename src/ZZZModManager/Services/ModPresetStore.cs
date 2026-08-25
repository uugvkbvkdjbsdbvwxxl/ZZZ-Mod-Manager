using ZZZModManager.Infrastructure;
using ZZZModManager.Models;

namespace ZZZModManager.Services;

public interface IModPresetStore
{
    IReadOnlyList<ModPreset> GetAll();
    ModPreset? Find(string id);
    ModPreset Save(string name, IEnumerable<string> enabledModIds);
    bool Delete(string id);
    IReadOnlyList<ModStateRequest> BuildRequests(ModPreset preset, IEnumerable<ModManifest> installedMods);
}

/// <summary>
/// Persists named enable-state snapshots in the library root. A preset only
/// records the ids that were enabled, so it stays valid while mods are added or
/// removed: <see cref="BuildRequests"/> resolves it against the mods that are
/// actually installed at apply time and silently drops ids that vanished.
/// </summary>
public sealed class ModPresetStore : IModPresetStore
{
    private readonly AppPaths _paths;
    private readonly JsonFileStore _store;
    private readonly ModPresetState _state;

    public ModPresetStore(AppPaths paths, JsonFileStore store)
    {
        _paths = paths;
        _store = store;
        _state = store.Load(paths.PresetsFile, () => new ModPresetState());
        _state.SchemaVersion = 1;
        _state.Presets = _state.Presets
            .Where(preset => !string.IsNullOrWhiteSpace(preset.Name))
            .DistinctBy(preset => preset.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public IReadOnlyList<ModPreset> GetAll() =>
        _state.Presets
            .OrderBy(preset => preset.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

    public ModPreset? Find(string id) =>
        _state.Presets.FirstOrDefault(preset => string.Equals(preset.Id, id, StringComparison.OrdinalIgnoreCase));

    public ModPreset Save(string name, IEnumerable<string> enabledModIds)
    {
        var trimmed = (name ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            throw new ArgumentException("预设名称不能为空。", nameof(name));
        }

        var ids = enabledModIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Saving under an existing name overwrites it: the toolbar exposes one
        // "保存为预设" action, and users expect a re-save to update the entry
        // instead of accumulating duplicates that only differ by timestamp.
        var existing = _state.Presets
            .FirstOrDefault(preset => string.Equals(preset.Name, trimmed, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            existing.Name = trimmed;
            existing.EnabledModIds = ids;
            existing.UpdatedAt = DateTimeOffset.UtcNow;
            Persist();
            return existing;
        }

        var created = new ModPreset
        {
            Name = trimmed,
            EnabledModIds = ids
        };
        _state.Presets.Add(created);
        Persist();
        return created;
    }

    public bool Delete(string id)
    {
        var removed = _state.Presets
            .RemoveAll(preset => string.Equals(preset.Id, id, StringComparison.OrdinalIgnoreCase)) > 0;
        if (removed)
        {
            Persist();
        }

        return removed;
    }

    public IReadOnlyList<ModStateRequest> BuildRequests(ModPreset preset, IEnumerable<ModManifest> installedMods)
    {
        var wanted = new HashSet<string>(preset.EnabledModIds, StringComparer.OrdinalIgnoreCase);
        return installedMods
            .Select(manifest => new ModStateRequest(manifest.Id, wanted.Contains(manifest.Id)))
            .ToList();
    }

    private void Persist() => _store.Save(_paths.PresetsFile, _state);
}
