using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using ZZZModManager.Infrastructure;
using ZZZModManager.Models;

namespace ZZZModManager.Services;

public sealed record LivePreparationSummary(
    bool ControlFilesChanged,
    int ImmediateCount,
    int ReloadOnlyCount,
    int SlotUnavailableCount,
    int RestartOnlyCount);

public sealed record LiveGateAuditResult(bool IsSafe, IReadOnlyList<string> Issues);

public interface ILiveModSwitchService
{
    LivePreparationSummary PrepareAll(IEnumerable<ModManifest> manifests);
    LivePreparationSummary PrepareForStartup(IEnumerable<ModManifest> manifests);
    bool Prepare(ModManifest manifest);
    void SetDefault(ModManifest manifest, bool enabled);
    LiveGateAuditResult Audit(ModManifest manifest);
    bool RequiresStartupPreload(ModManifest manifest);
    GameKeyChord GetStateChord(ModManifest manifest, bool enabled);
    string GetDisplayBinding(ModManifest manifest, bool enabled);
}

/// <summary>
/// Keeps every installed mod parsed by ZZMI and owns one global gate variable per
/// mod. Schema v2 uses two absolute commands for that variable, so a dropped or
/// duplicated key event can never invert an unknown state. Rule v3 gives the
/// controller an explicit namespace and uses fully-qualified references from mod
/// INIs; 3DMigoto otherwise resolves the same short variable name separately in
/// every included INI file. Rule v4 keeps all match_* selectors outside the runtime
/// guard. Rule v5 keeps vertex-count and byte-stride overrides outside it. Rule
/// v6 removes empty guards from metadata-only sections. Rule v7 removes all manager
/// gates from mods that use vertex-count overrides. Rule v8 treated those mods as
/// restart-only. Rule v10 keeps their static metadata loaded at process startup and
/// gates only the replacement actions, allowing safe absolute live switching after
/// the initial launch. It follows XXMI's same-hash capacity aggregation:
/// different static capacities under one hash remain in the startup include tree
/// instead of being downgraded by the manager to restart-only.
/// </summary>
public sealed class LiveModSwitchService : ILiveModSwitchService
{
    public const int MaximumSlots = 48;
    public const string RuleVersion = "10";
    private const string ControlFileName = "zzzmod-live.ini";
    private const string GuardMarker = "; ZZZMOD-LIVE-GUARD";
    private static readonly Regex SectionRegex = new(@"^\s*\[(?<name>[^\]]+)\]\s*$", RegexOptions.Compiled);
    private static readonly Regex ConditionRegex = new(@"^(?<indent>\s*)condition\s*=\s*(?<value>.*)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex MetadataRegex = new(
        @"^\s*(hash|match_[A-Za-z0-9_]+|override_(?:vertex_count|byte_stride)|filter_index|allow_duplicate_hash|shader_model|namespace)\s*=",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex RestartOnlyMetadataRegex = new(
        @"^\s*override_(?:vertex_count|byte_stride)\s*=",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex HandlingRegex = new(@"^\s*handling\s*=", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex PresentResetRegex = new(@"^\s*post\s+\$[A-Za-z0-9_]+\s*=", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex LiveVariableRegex = new(
        @"\$(?:\\ZZZModManager\\[A-Za-z0-9_]+\\enabled|zzzmgr_enabled_[A-Za-z0-9_]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private readonly AppPaths _paths;

    public LiveModSwitchService(AppPaths paths)
    {
        _paths = paths;
    }

    public LivePreparationSummary PrepareAll(IEnumerable<ModManifest> manifests)
    {
        var ordered = manifests
            .OrderBy(manifest => manifest.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(manifest => manifest.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var restartOnly = ordered
            .Where(NeedsStaticPreparation)
            .Select(manifest => manifest.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var used = new HashSet<int>();
        foreach (var manifest in ordered)
        {
            if (restartOnly.Contains(manifest.Id))
            {
                manifest.LiveSwitchSlot = null;
                continue;
            }

            if (manifest.LiveSwitchSlot is not int slot || slot is < 0 or >= MaximumSlots || !used.Add(slot))
            {
                manifest.LiveSwitchSlot = null;
            }
        }

        foreach (var manifest in ordered.Where(manifest => manifest.LiveSwitchSlot is null && !restartOnly.Contains(manifest.Id)))
        {
            var free = Enumerable.Range(0, MaximumSlots).FirstOrDefault(slot => !used.Contains(slot), -1);
            if (free >= 0)
            {
                manifest.LiveSwitchSlot = free;
                used.Add(free);
            }
        }

        var changed = false;
        foreach (var manifest in ordered)
        {
            manifest.SchemaVersion = 2;
            manifest.LiveSwitchVariable = string.IsNullOrWhiteSpace(manifest.LiveSwitchVariable)
                ? BuildVariableName(manifest.Id)
                : manifest.LiveSwitchVariable;
            manifest.LiveSwitchKey = manifest.LiveSwitchSlot is int slot
                ? GetBaseKeyName(slot)
                : string.Empty;
            changed |= Prepare(manifest);
        }

        return new LivePreparationSummary(
            changed,
            ordered.Count(manifest => manifest.LiveSwitchCapability == LiveSwitchCapability.Immediate),
            ordered.Count(manifest => manifest.LiveSwitchCapability is LiveSwitchCapability.RequiresReload or LiveSwitchCapability.Unsupported),
            ordered.Count(manifest => manifest.LiveSwitchCapability == LiveSwitchCapability.SlotUnavailable),
            ordered.Count(manifest => manifest.LiveSwitchCapability == LiveSwitchCapability.RequiresRestart));
    }

    /// <summary>
    /// Performs the one-time process-start preparation required by static vertex
    /// metadata. The metadata remains outside the live gate so ZZMI can allocate
    /// the correct buffer capacity during DLL initialization; all replacement
    /// actions are still gated by the manager-owned variable.
    /// </summary>
    public LivePreparationSummary PrepareForStartup(IEnumerable<ModManifest> manifests)
    {
        var ordered = manifests
            .OrderBy(manifest => manifest.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(manifest => manifest.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var initial = PrepareAll(ordered);
        var used = ordered
            .Where(manifest => manifest.LiveSwitchSlot is int slot && slot is >= 0 and < MaximumSlots)
            .Select(manifest => manifest.LiveSwitchSlot!.Value)
            .ToHashSet();
        var changed = initial.ControlFilesChanged;

        // XXMI aggregates override_byte_width per hash after parsing the full
        // include tree.  Keep every static-capacity variant in that tree and
        // let the runtime's maximum-width rule apply; the manager must not turn
        // same-hash differences into a false restart requirement.
        foreach (var manifest in ordered.Where(RequiresStartupPreload))
        {
            if (manifest.LiveSwitchSlot is not int slot || slot is < 0 or >= MaximumSlots)
            {
                var free = Enumerable.Range(0, MaximumSlots).FirstOrDefault(candidate => !used.Contains(candidate), -1);
                if (free >= 0)
                {
                    manifest.LiveSwitchSlot = free;
                    manifest.LiveSwitchKey = GetBaseKeyName(free);
                    used.Add(free);
                }
            }

            changed |= PrepareStaticForStartup(manifest);
        }

        return new LivePreparationSummary(
            changed,
            ordered.Count(manifest => manifest.LiveSwitchCapability == LiveSwitchCapability.Immediate),
            ordered.Count(manifest => manifest.LiveSwitchCapability is LiveSwitchCapability.RequiresReload or LiveSwitchCapability.Unsupported),
            ordered.Count(manifest => manifest.LiveSwitchCapability == LiveSwitchCapability.SlotUnavailable),
            ordered.Count(manifest => manifest.LiveSwitchCapability == LiveSwitchCapability.RequiresRestart));
    }

    public bool Prepare(ModManifest manifest)
    {
        var root = GetModPath(manifest);
        if (!Directory.Exists(root))
        {
            manifest.LiveSwitchPrepared = false;
            manifest.LiveSwitchCapability = LiveSwitchCapability.Unsupported;
            manifest.LiveSwitchBlockReason = "Mod 目录不存在";
            return false;
        }

        manifest.LiveSwitchVariable = string.IsNullOrWhiteSpace(manifest.LiveSwitchVariable)
            ? BuildVariableName(manifest.Id)
            : manifest.LiveSwitchVariable;

        var iniPaths = Directory.EnumerateFiles(root, "*.ini", SearchOption.AllDirectories)
            .Where(path => !string.Equals(Path.GetFileName(path), ControlFileName, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var requiresRestart = iniPaths.Any(path => ReadLines(path).Any(line => RestartOnlyMetadataRegex.IsMatch(line)))
                              && !HasLiveInstrumentation(manifest);
        var changed = false;
        foreach (var path in iniPaths)
        {
            changed |= requiresRestart
                ? RemoveLiveInstrumentation(path)
                : PatchIni(path, GetQualifiedVariable(manifest));
        }

        changed |= requiresRestart ? DeleteControlFile(manifest) : WriteControlFile(manifest, allowLiveKeys: true);
        manifest.LiveSwitchRuleVersion = RuleVersion;
        if (requiresRestart)
        {
            manifest.LiveSwitchPrepared = true;
            manifest.LiveSwitchCapability = LiveSwitchCapability.RequiresRestart;
            manifest.LiveSwitchBlockReason = "静态顶点限制 · 启动预加载后可实时切换";
        }
        else
        {
            var audit = Audit(manifest);
            manifest.LiveSwitchPrepared = audit.IsSafe;
            manifest.LiveSwitchCapability = !audit.IsSafe
                ? LiveSwitchCapability.Unsupported
                : manifest.LiveSwitchSlot is null
                    ? LiveSwitchCapability.SlotUnavailable
                    : LiveSwitchCapability.Immediate;
            manifest.LiveSwitchBlockReason = manifest.LiveSwitchCapability switch
            {
                LiveSwitchCapability.SlotUnavailable => "实时槽位已满 · 需要安全重载",
                LiveSwitchCapability.Unsupported => "门控审计未通过 · 需要安全重载",
                _ => null
            };
        }

        manifest.PreviewFile = FindRootPreview(root);
        return changed;
    }

    public bool RequiresStartupPreload(ModManifest manifest)
    {
        var root = GetModPath(manifest);
        return Directory.Exists(root)
               && Directory.EnumerateFiles(root, "*.ini", SearchOption.AllDirectories)
                   .Where(path => !string.Equals(Path.GetFileName(path), ControlFileName, StringComparison.OrdinalIgnoreCase))
                   .Any(path => ReadLines(path).Any(line => RestartOnlyMetadataRegex.IsMatch(line)));
    }

    private bool PrepareStaticForStartup(ModManifest manifest)
    {
        var root = GetModPath(manifest);
        if (!Directory.Exists(root))
        {
            manifest.LiveSwitchPrepared = false;
            manifest.LiveSwitchCapability = LiveSwitchCapability.Unsupported;
            manifest.LiveSwitchBlockReason = "Mod 目录不存在";
            return false;
        }

        manifest.LiveSwitchVariable = string.IsNullOrWhiteSpace(manifest.LiveSwitchVariable)
            ? BuildVariableName(manifest.Id)
            : manifest.LiveSwitchVariable;
        var iniPaths = Directory.EnumerateFiles(root, "*.ini", SearchOption.AllDirectories)
            .Where(path => !string.Equals(Path.GetFileName(path), ControlFileName, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var changed = false;
        foreach (var path in iniPaths)
        {
            changed |= PatchIni(path, GetQualifiedVariable(manifest));
        }

        changed |= WriteControlFile(manifest, allowLiveKeys: manifest.LiveSwitchSlot is not null);
        manifest.LiveSwitchRuleVersion = RuleVersion;
        var audit = Audit(manifest);
        manifest.LiveSwitchPrepared = audit.IsSafe;
        manifest.LiveSwitchCapability = !audit.IsSafe
            ? LiveSwitchCapability.Unsupported
            : manifest.LiveSwitchSlot is null
                ? LiveSwitchCapability.SlotUnavailable
                : LiveSwitchCapability.Immediate;
        manifest.LiveSwitchBlockReason = manifest.LiveSwitchCapability switch
        {
            LiveSwitchCapability.SlotUnavailable => "实时槽位已满 · 需要安全重载",
            LiveSwitchCapability.Unsupported => "门控审计未通过 · 需要安全重载",
            _ => null
        };
        manifest.PreviewFile = FindRootPreview(root);
        return changed;
    }

    public void SetDefault(ModManifest manifest, bool enabled)
    {
        if (manifest.LiveSwitchCapability == LiveSwitchCapability.RequiresRestart
            || (RequiresStartupPreload(manifest) && !HasLiveInstrumentation(manifest)))
        {
            return;
        }

        var path = Path.Combine(GetModPath(manifest), ControlFileName);
        if (!File.Exists(path))
        {
            Prepare(manifest);
        }

        var lines = ReadLines(path);
        const string variable = "$enabled";
        var changed = false;
        for (var index = 0; index < lines.Count; index++)
        {
            if (!lines[index].Contains("global " + variable + " =", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var replacement = $"global {variable} = {(enabled ? 1 : 0)}";
            if (!string.Equals(lines[index].Trim(), replacement, StringComparison.Ordinal))
            {
                lines[index] = replacement;
                changed = true;
            }
        }

        if (changed)
        {
            WriteLines(path, lines);
        }
    }

    public LiveGateAuditResult Audit(ModManifest manifest)
    {
        var root = GetModPath(manifest);
        var issues = new List<string>();
        if (!Directory.Exists(root))
        {
            issues.Add("Mod 目录不存在。");
            return new LiveGateAuditResult(false, issues);
        }

        var variable = GetQualifiedVariable(manifest);
        foreach (var path in Directory.EnumerateFiles(root, "*.ini", SearchOption.AllDirectories)
                     .Where(path => !string.Equals(Path.GetFileName(path), ControlFileName, StringComparison.OrdinalIgnoreCase)))
        {
            AuditIni(path, variable, issues);
        }

        return new LiveGateAuditResult(issues.Count == 0, issues);
    }

    public GameKeyChord GetStateChord(ModManifest manifest, bool enabled)
        => BuildStateChord(manifest, enabled);

    // Kept for source compatibility with the previous UI while existing
    // installations migrate. It now returns the manifest's absolute state.
    public static GameKeyChord GetKeyChord(ModManifest manifest) =>
        BuildStateChord(manifest, manifest.Enabled);

    private static GameKeyChord BuildStateChord(ModManifest manifest, bool enabled)
    {
        if (manifest.LiveSwitchSlot is not int slot || slot is < 0 or >= MaximumSlots)
        {
            throw new InvalidOperationException("该 Mod 没有可用的实时槽位，需要安全重载。");
        }

        var modifiers = GameKeyModifiers.Control | GameKeyModifiers.Alt;
        if (enabled)
        {
            modifiers |= GameKeyModifiers.Shift;
        }

        return new GameKeyChord(GetVirtualKey(slot), modifiers);
    }

    public string GetDisplayBinding(ModManifest manifest, bool enabled)
    {
        if (manifest.LiveSwitchCapability != LiveSwitchCapability.Immediate || manifest.LiveSwitchSlot is not int slot)
        {
            return manifest.LiveSwitchCapability == LiveSwitchCapability.RequiresRestart ? "需重启游戏" : "需安全重载";
        }

        // F13-F24 (and the numeric/letter fallback slots) are manager-owned
        // virtual channels.  They are sent programmatically to ZZMI; showing
        // the chord as a physical keyboard shortcut made users look for keys
        // such as F20/F21 that most keyboards do not have.
        _ = enabled;
        _ = slot;
        return "管理器内部控制（无需物理按键）";
    }

    public string GetModPath(ModManifest manifest)
    {
        var path = Path.GetFullPath(Path.Combine(_paths.ModsRoot, manifest.InstalledDirectory));
        if (!FileSystemSafety.IsWithin(_paths.ModsRoot, path))
        {
            throw new InvalidOperationException("Mod 路径不在独立 Mod 库内。");
        }

        return path;
    }

    private bool WriteControlFile(ModManifest manifest, bool allowLiveKeys)
    {
        var path = Path.Combine(GetModPath(manifest), ControlFileName);
        const string variable = "$enabled";
        var controlNamespace = GetControlNamespace(manifest);
        var lines = new List<string>
        {
            $"namespace = {controlNamespace}",
            "; Generated by ZZZ Mod Manager. Do not edit.",
            "; Rule v10 uses absolute state commands; static vertex metadata stays loaded at startup.",
            "[Constants]",
            $"global {variable} = {(manifest.Enabled ? 1 : 0)}",
            ""
        };

        if (allowLiveKeys && manifest.LiveSwitchSlot is int slot)
        {
            var baseKey = FormatIniBaseKey(slot);
            lines.AddRange(
            [
                $"[KeyZZZModDisable_{manifest.LiveSwitchVariable}]",
                "condition = 1",
                $"key = ctrl alt no_shift {baseKey}",
                $"{variable} = 0",
                "",
                $"[KeyZZZModEnable_{manifest.LiveSwitchVariable}]",
                "condition = 1",
                $"key = ctrl alt shift {baseKey}",
                $"{variable} = 1",
                ""
            ]);
        }
        else
        {
            lines.Add("; Saved state is applied by the manager reload command or on the next launch.");
            lines.Add(string.Empty);
        }

        var text = string.Join(Environment.NewLine, lines);
        var before = File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : null;
        if (string.Equals(before, text, StringComparison.Ordinal))
        {
            return false;
        }

        WriteText(path, text);
        return true;
    }

    private static bool PatchIni(string path, string variable)
    {
        var lines = ReadLines(path);
        var changed = StripForeignLiveGates(lines, variable);
        var sections = FindSections(lines);

        foreach (var section in sections.OrderByDescending(section => section.Start))
        {
            var end = section.End;
            if (section.Name.StartsWith("Key", StringComparison.OrdinalIgnoreCase))
            {
                changed |= PatchKeySection(lines, section.Start, end, variable);
                continue;
            }

            if (!IsGuardable(section.Name))
            {
                continue;
            }

            if (lines.Skip(section.Start).Take(Math.Max(0, end - section.Start))
                .Any(line => line.Contains(GuardMarker, StringComparison.OrdinalIgnoreCase)))
            {
                changed |= MoveActionLinesInsideGuard(lines, section.Start, end);
                changed |= MoveMetadataLinesOutsideGuard(lines, section.Start, end);
                changed |= RemoveEmptyGuard(lines, section.Start, end);
                continue;
            }

            var insertion = FindBodyInsertion(lines, section.Start, end, section.Name);
            if (insertion < 0)
            {
                continue;
            }

            lines.Insert(insertion, $"{GuardMarker}-BEGIN {variable}");
            lines.Insert(insertion + 1, $"if {variable}");
            var shiftedEnd = end + 2;
            lines.Insert(shiftedEnd, $"{GuardMarker}-END {variable}");
            lines.Insert(shiftedEnd + 1, "endif");
            changed = true;
        }

        if (changed)
        {
            WriteLines(path, lines);
        }

        return changed;
    }

    private static bool RemoveLiveInstrumentation(string path)
    {
        var lines = ReadLines(path);
        var changed = false;
        for (var index = lines.Count - 1; index >= 0; index--)
        {
            if (!lines[index].Contains(GuardMarker + "-BEGIN", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var guardEnd = -1;
            for (var candidate = index + 1; candidate < lines.Count; candidate++)
            {
                if (lines[candidate].Contains(GuardMarker + "-END", StringComparison.OrdinalIgnoreCase))
                {
                    guardEnd = candidate;
                    break;
                }
            }

            if (guardEnd < 0)
            {
                continue;
            }

            if (guardEnd + 1 < lines.Count
                && lines[guardEnd + 1].Trim().Equals("endif", StringComparison.OrdinalIgnoreCase))
            {
                lines.RemoveAt(guardEnd + 1);
            }

            lines.RemoveAt(guardEnd);
            if (index + 1 < lines.Count
                && lines[index + 1].TrimStart().StartsWith("if ", StringComparison.OrdinalIgnoreCase)
                && LiveVariableRegex.IsMatch(lines[index + 1]))
            {
                lines.RemoveAt(index + 1);
            }

            lines.RemoveAt(index);
            changed = true;
        }

        for (var index = 0; index < lines.Count; index++)
        {
            var match = ConditionRegex.Match(lines[index]);
            if (!match.Success)
            {
                continue;
            }

            var value = match.Groups["value"].Value;
            var liveVariables = LiveVariableRegex.Matches(value)
                .Select(item => item.Value)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            foreach (var variable in liveVariables)
            {
                value = Regex.Replace(value, $@"\s*&&\s*{Regex.Escape(variable)}", string.Empty, RegexOptions.IgnoreCase);
                value = Regex.Replace(value, $@"{Regex.Escape(variable)}\s*&&\s*", string.Empty, RegexOptions.IgnoreCase);
                value = Regex.Replace(value, Regex.Escape(variable), "1", RegexOptions.IgnoreCase);
            }

            if (liveVariables.Count > 0)
            {
                lines[index] = $"{match.Groups["indent"].Value}condition = {value.Trim()}";
                changed = true;
            }
        }

        if (changed)
        {
            WriteLines(path, lines);
        }

        return changed;
    }

    private bool RequiresRestartForStaticMetadata(ModManifest manifest)
    {
        var root = GetModPath(manifest);
        return Directory.Exists(root)
               && Directory.EnumerateFiles(root, "*.ini", SearchOption.AllDirectories)
                   .Where(path => !string.Equals(Path.GetFileName(path), ControlFileName, StringComparison.OrdinalIgnoreCase))
                    .Any(path => ReadLines(path).Any(line => RestartOnlyMetadataRegex.IsMatch(line)));
    }

    private bool NeedsStaticPreparation(ModManifest manifest) =>
        RequiresRestartForStaticMetadata(manifest) && !HasLiveInstrumentation(manifest);

    private bool HasLiveInstrumentation(ModManifest manifest)
    {
        var root = GetModPath(manifest);
        var controlPath = Path.Combine(root, ControlFileName);
        if (!File.Exists(controlPath))
        {
            return false;
        }

        try
        {
            return Directory.EnumerateFiles(root, "*.ini", SearchOption.AllDirectories)
                .Where(path => !string.Equals(Path.GetFileName(path), ControlFileName, StringComparison.OrdinalIgnoreCase))
                .Any(path => ReadLines(path).Any(line => line.Contains(GuardMarker + "-BEGIN", StringComparison.OrdinalIgnoreCase)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private bool DeleteControlFile(ModManifest manifest)
    {
        var path = Path.Combine(GetModPath(manifest), ControlFileName);
        if (!File.Exists(path))
        {
            return false;
        }

        File.Delete(path);
        return true;
    }

    private static bool MoveActionLinesInsideGuard(List<string> lines, int start, int end)
    {
        var begin = -1;
        var guardEnd = -1;
        for (var index = start + 1; index < end; index++)
        {
            if (lines[index].Contains(GuardMarker + "-BEGIN", StringComparison.OrdinalIgnoreCase))
            {
                begin = index;
            }
            else if (begin >= 0 && lines[index].Contains(GuardMarker + "-END", StringComparison.OrdinalIgnoreCase))
            {
                guardEnd = index;
                break;
            }
        }

        if (begin < 0 || guardEnd < 0)
        {
            return false;
        }

        var movable = new List<string>();
        for (var index = begin - 1; index > start; index--)
        {
            if (!HandlingRegex.IsMatch(lines[index]))
            {
                continue;
            }

            movable.Insert(0, lines[index]);
            lines.RemoveAt(index);
            begin--;
            guardEnd--;
        }

        if (movable.Count == 0)
        {
            return false;
        }

        lines.InsertRange(begin + 2, movable);
        return true;
    }

    private static bool MoveMetadataLinesOutsideGuard(List<string> lines, int start, int end)
    {
        var begin = -1;
        var guardEnd = -1;
        for (var index = start + 1; index < end; index++)
        {
            if (lines[index].Contains(GuardMarker + "-BEGIN", StringComparison.OrdinalIgnoreCase))
            {
                begin = index;
            }
            else if (begin >= 0 && lines[index].Contains(GuardMarker + "-END", StringComparison.OrdinalIgnoreCase))
            {
                guardEnd = index;
                break;
            }
        }

        if (begin < 0 || guardEnd < 0)
        {
            return false;
        }

        var metadata = new List<string>();
        for (var index = guardEnd - 1; index > begin; index--)
        {
            if (!MetadataRegex.IsMatch(lines[index]))
            {
                continue;
            }

            metadata.Insert(0, lines[index]);
            lines.RemoveAt(index);
        }

        if (metadata.Count == 0)
        {
            return false;
        }

        lines.InsertRange(begin, metadata);
        return true;
    }

    private static bool RemoveEmptyGuard(List<string> lines, int start, int end)
    {
        var begin = -1;
        var guardEnd = -1;
        for (var index = start + 1; index < Math.Min(end, lines.Count); index++)
        {
            if (lines[index].Contains(GuardMarker + "-BEGIN", StringComparison.OrdinalIgnoreCase))
            {
                begin = index;
            }
            else if (begin >= 0 && lines[index].Contains(GuardMarker + "-END", StringComparison.OrdinalIgnoreCase))
            {
                guardEnd = index;
                break;
            }
        }

        if (begin < 0 || guardEnd < 0)
        {
            return false;
        }

        var bodyStart = begin + 2;
        var hasBody = lines.Skip(bodyStart).Take(Math.Max(0, guardEnd - bodyStart))
            .Any(line => !string.IsNullOrWhiteSpace(line)
                         && !line.TrimStart().StartsWith(';')
                         && !line.TrimStart().StartsWith('#'));
        if (hasBody)
        {
            return false;
        }

        if (guardEnd + 1 < lines.Count
            && lines[guardEnd + 1].Trim().Equals("endif", StringComparison.OrdinalIgnoreCase))
        {
            lines.RemoveAt(guardEnd + 1);
        }

        lines.RemoveAt(guardEnd);
        if (begin + 1 < lines.Count && lines[begin + 1].TrimStart().StartsWith("if ", StringComparison.OrdinalIgnoreCase))
        {
            lines.RemoveAt(begin + 1);
        }

        lines.RemoveAt(begin);
        return true;
    }

    private static bool PatchKeySection(List<string> lines, int start, int end, string variable)
    {
        if (lines.Skip(start).Take(Math.Max(0, end - start))
            .Any(line => line.Contains(variable, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        for (var index = start + 1; index < end; index++)
        {
            var match = ConditionRegex.Match(lines[index]);
            if (!match.Success)
            {
                continue;
            }

            lines[index] = $"{match.Groups["indent"].Value}condition = ({match.Groups["value"].Value.Trim()}) && {variable}";
            return true;
        }

        lines.Insert(start + 1, $"condition = {variable}");
        return true;
    }

    private static bool StripForeignLiveGates(List<string> lines, string currentVariable)
    {
        var changed = false;
        for (var index = lines.Count - 1; index >= 0; index--)
        {
            if (!lines[index].Contains(GuardMarker + "-BEGIN", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var foreign = LiveVariableRegex.Match(lines[index]).Value;
            if (string.IsNullOrWhiteSpace(foreign)
                || string.Equals(foreign, currentVariable, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var end = -1;
            for (var candidate = index + 1; candidate < lines.Count; candidate++)
            {
                if (lines[candidate].Contains(GuardMarker + "-END", StringComparison.OrdinalIgnoreCase)
                    && lines[candidate].Contains(foreign, StringComparison.OrdinalIgnoreCase))
                {
                    end = candidate;
                    break;
                }
            }

            if (end < 0)
            {
                continue;
            }

            if (end + 1 < lines.Count && lines[end + 1].Trim().Equals("endif", StringComparison.OrdinalIgnoreCase))
            {
                lines.RemoveAt(end + 1);
            }

            lines.RemoveAt(end);
            if (index + 1 < lines.Count
                && Regex.IsMatch(lines[index + 1], $@"^\s*if\s+{Regex.Escape(foreign)}\s*$", RegexOptions.IgnoreCase))
            {
                lines.RemoveAt(index + 1);
            }

            lines.RemoveAt(index);
            changed = true;
        }

        for (var index = 0; index < lines.Count; index++)
        {
            var match = ConditionRegex.Match(lines[index]);
            if (!match.Success)
            {
                continue;
            }

            var value = match.Groups["value"].Value;
            var foreignVariables = LiveVariableRegex.Matches(value)
                .Select(item => item.Value)
                .Where(item => !string.Equals(item, currentVariable, StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            foreach (var foreign in foreignVariables)
            {
                value = Regex.Replace(value, $@"\s*&&\s*{Regex.Escape(foreign)}", string.Empty, RegexOptions.IgnoreCase);
                value = Regex.Replace(value, $@"{Regex.Escape(foreign)}\s*&&\s*", string.Empty, RegexOptions.IgnoreCase);
                value = Regex.Replace(value, Regex.Escape(foreign), "1", RegexOptions.IgnoreCase);
            }

            if (foreignVariables.Count > 0)
            {
                lines[index] = $"{match.Groups["indent"].Value}condition = {value.Trim()}";
                changed = true;
            }
        }

        return changed;
    }

    private static void AuditIni(string path, string variable, List<string> issues)
    {
        var lines = ReadLines(path);
        var foreignVariables = lines.SelectMany(line => LiveVariableRegex.Matches(line).Select(match => match.Value))
            .Where(item => !string.Equals(item, variable, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (foreignVariables.Count > 0)
        {
            issues.Add($"{Path.GetFileName(path)} 仍引用其他 Mod 的管理器门控变量：{string.Join("、", foreignVariables)}。");
        }

        foreach (var section in FindSections(lines))
        {
            if (section.Name.StartsWith("Key", StringComparison.OrdinalIgnoreCase))
            {
                var hasKey = lines.Skip(section.Start + 1).Take(Math.Max(0, section.End - section.Start - 1))
                    .Any(line => Regex.IsMatch(line, @"^\s*key\s*=", RegexOptions.IgnoreCase));
                var gated = lines.Skip(section.Start + 1).Take(Math.Max(0, section.End - section.Start - 1))
                    .Any(line => ConditionRegex.IsMatch(line) && line.Contains(variable, StringComparison.OrdinalIgnoreCase));
                if (hasKey && !gated)
                {
                    issues.Add($"{Path.GetFileName(path)} [{section.Name}] 快捷键未受禁用条件控制。");
                }

                continue;
            }

            if (!IsGuardable(section.Name))
            {
                continue;
            }

            var begin = -1;
            var end = -1;
            for (var index = section.Start + 1; index < section.End; index++)
            {
                if (lines[index].Contains(GuardMarker + "-BEGIN", StringComparison.OrdinalIgnoreCase))
                {
                    if (begin >= 0)
                    {
                        issues.Add($"{Path.GetFileName(path)} [{section.Name}] 存在重复门控标记。");
                    }

                    begin = index;
                }
                else if (lines[index].Contains(GuardMarker + "-END", StringComparison.OrdinalIgnoreCase))
                {
                    end = index;
                }
            }

            var actionable = Enumerable.Range(section.Start + 1, Math.Max(0, section.End - section.Start - 1))
                .Where(index => IsActionLine(lines[index], section.Name))
                .ToList();
            if (actionable.Count == 0)
            {
                continue;
            }

            if (begin < 0 || end <= begin)
            {
                issues.Add($"{Path.GetFileName(path)} [{section.Name}] 缺少完整门控。");
                continue;
            }

            if (actionable.Any(index => index <= begin || index >= end))
            {
                issues.Add($"{Path.GetFileName(path)} [{section.Name}] 仍有绘制或资源命令位于门控之外。");
            }
        }
    }

    private static bool IsActionLine(string line, string sectionName)
    {
        var trimmed = line.Trim();
        if (trimmed.Length == 0 || trimmed.StartsWith(';') || trimmed.StartsWith('#')
            || trimmed.StartsWith("if ", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("else", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("endif", StringComparison.OrdinalIgnoreCase)
            || MetadataRegex.IsMatch(line))
        {
            return false;
        }

        if (sectionName.Equals("Present", StringComparison.OrdinalIgnoreCase) && PresentResetRegex.IsMatch(line))
        {
            return false;
        }

        return true;
    }

    private static List<(int Start, int End, string Name)> FindSections(IReadOnlyList<string> lines)
    {
        var sections = new List<(int Start, int End, string Name)>();
        for (var index = 0; index < lines.Count; index++)
        {
            var match = SectionRegex.Match(lines[index]);
            if (!match.Success)
            {
                continue;
            }

            if (sections.Count > 0)
            {
                var previous = sections[^1];
                sections[^1] = (previous.Start, index, previous.Name);
            }

            sections.Add((index, lines.Count, match.Groups["name"].Value.Trim()));
        }

        return sections;
    }

    private static bool IsGuardable(string name) =>
        name.Equals("Present", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("TextureOverride", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("ShaderOverride", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("ShaderRegex", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("CommandList", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("CustomShader", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("BuiltInCommandList", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("BuiltInCustomShader", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("ClearRenderTargetView", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("ClearDepthStencilView", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("ClearUnorderedAccessView", StringComparison.OrdinalIgnoreCase);

    private static int FindBodyInsertion(IReadOnlyList<string> lines, int start, int end, string section)
    {
        for (var index = start + 1; index < end; index++)
        {
            var line = lines[index];
            if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith(';') || line.TrimStart().StartsWith('#'))
            {
                continue;
            }

            if (section.Equals("Present", StringComparison.OrdinalIgnoreCase) && PresentResetRegex.IsMatch(line))
            {
                continue;
            }

            if (MetadataRegex.IsMatch(line))
            {
                continue;
            }

            return index;
        }

        return -1;
    }

    private static ushort GetVirtualKey(int slot)
    {
        if (slot < 12)
        {
            return (ushort)(0x7C + slot); // F13..F24
        }

        if (slot < 22)
        {
            return (ushort)(0x30 + slot - 12); // 0..9
        }

        return (ushort)(0x41 + slot - 22); // A..Z
    }

    private static string GetBaseKeyName(int slot)
    {
        if (slot < 12)
        {
            return "F" + (13 + slot);
        }

        if (slot < 22)
        {
            return ((char)('0' + slot - 12)).ToString();
        }

        return ((char)('A' + slot - 22)).ToString();
    }

    private static string FormatIniBaseKey(int slot) =>
        slot < 12 ? "VK_" + GetBaseKeyName(slot) : GetBaseKeyName(slot);

    private static string BuildVariableName(string id)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id))).ToLowerInvariant();
        return "zzzmgr_enabled_" + hash[..12];
    }

    private static string GetControlNamespace(ModManifest manifest) =>
        "ZZZModManager\\" + manifest.LiveSwitchVariable;

    private static string GetQualifiedVariable(ModManifest manifest) =>
        "$\\" + GetControlNamespace(manifest) + "\\enabled";

    private static string? FindRootPreview(string root)
    {
        try
        {
            return Directory.EnumerateFiles(root, "*", SearchOption.TopDirectoryOnly)
                .FirstOrDefault(path => string.Equals(Path.GetFileName(path), "preview.png", StringComparison.OrdinalIgnoreCase)) is { } preview
                ? Path.GetFileName(preview)
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static List<string> ReadLines(string path)
    {
        try
        {
            return File.ReadAllLines(path, new UTF8Encoding(false, true)).ToList();
        }
        catch (DecoderFallbackException)
        {
            return File.ReadAllLines(path, Encoding.Default).ToList();
        }
    }

    private static void WriteLines(string path, IReadOnlyList<string> lines) =>
        WriteText(path, string.Join(Environment.NewLine, lines) + Environment.NewLine);

    private static void WriteText(string path, string text)
    {
        var temp = path + ".zzzmod.tmp";
        File.WriteAllText(temp, text, new UTF8Encoding(false));
        File.Move(temp, path, true);
    }
}
