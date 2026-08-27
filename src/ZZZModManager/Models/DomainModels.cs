using System.Globalization;
using System.Text.Json.Serialization;

namespace ZZZModManager.Models;

public enum ImportStatus
{
    Ready,
    ReadyWithFixes,
    NeedsDependency,
    Blocked
}

public enum IssueSeverity
{
    Info,
    Warning,
    Error
}

public enum CharacterGroupKind
{
    Character,
    Framework,
    Unknown,
    Discovered,
    Custom
}

public sealed record CharacterGroupInfo(string Key, string DisplayName, CharacterGroupKind Kind);

public enum LiveSwitchCapability
{
    RequiresReload,
    Immediate,
    SlotUnavailable,
    Unsupported,
    RequiresRestart
}

public enum ModStateApplication
{
    Immediate,
    Reloaded,
    Pending,
    Failed
}

public enum AppLogLevel
{
    Info,
    Warning,
    Error
}

public sealed record LogEntry(DateTimeOffset Timestamp, AppLogLevel Level, string Message)
{
    // The date is part of the persisted format so that entries reloaded on a later
    // day keep the moment they were actually written.
    public const string TimestampFormat = "yyyy-MM-dd HH:mm:ss";

    public override string ToString() => $"[{Timestamp.ToString(TimestampFormat, CultureInfo.InvariantCulture)}] [{Level}] {Message}";
}

public sealed class ValidationIssue
{
    public IssueSeverity Severity { get; init; }
    public string Code { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public string? File { get; init; }
    public int? Line { get; init; }
    public bool Fixable { get; init; }

    public override string ToString()
    {
        var location = File is null ? string.Empty : $" [{File}{(Line is null ? string.Empty : $":{Line}")}]";
        return $"{Severity}: {Message}{location}";
    }
}

public sealed class AppliedFix
{
    public string RuleId { get; init; } = string.Empty;
    public string File { get; init; } = string.Empty;
    public int Line { get; init; }
    public string Before { get; init; } = string.Empty;
    public string After { get; init; } = string.Empty;
    public string RuleVersion { get; init; } = "1";
}

public sealed class ImportReport
{
    public string SourcePath { get; init; } = string.Empty;
    public string SourceSha256 { get; init; } = string.Empty;
    public string CandidateRoot { get; init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public ImportStatus Status { get; set; }
    public List<ValidationIssue> Issues { get; init; } = [];
    public List<AppliedFix> Fixes { get; init; } = [];
    public List<string> Dependencies { get; init; } = [];
    public HashSet<string> Hashes { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class ModManifest
{
    public int SchemaVersion { get; set; } = 3;
    public string Id { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string SourcePath { get; set; } = string.Empty;
    public string SourceSha256 { get; set; } = string.Empty;
    public string InstalledDirectory { get; set; } = string.Empty;
    public DateTimeOffset ImportedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? UpdatedAt { get; set; }
    public int VersionRevision { get; set; } = 1;
    public bool Enabled { get; set; }
    public ImportStatus ImportStatus { get; set; }
    public HashSet<string> Hashes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Dependencies { get; set; } = [];
    public List<AppliedFix> AppliedFixes { get; set; } = [];
    public string ReportFile { get; set; } = "import-report.json";

    // A stable, manager-owned hotkey used for live enable/disable while the
    // game is running.  These fields are deliberately separate from the
    // author's namespaces and are safe to regenerate for older manifests.
    public string LiveSwitchKey { get; set; } = string.Empty;
    public string LiveSwitchVariable { get; set; } = string.Empty;
    public bool LiveSwitchPrepared { get; set; }
    public int? LiveSwitchSlot { get; set; }
    public string LiveSwitchRuleVersion { get; set; } = string.Empty;
    public LiveSwitchCapability LiveSwitchCapability { get; set; } = LiveSwitchCapability.RequiresReload;
    public string? LiveSwitchBlockReason { get; set; }
    public string? PreviewFile { get; set; }
    public string? CharacterGroupOverrideKey { get; set; }
}

public enum ModFileDifferenceKind
{
    Added,
    Modified,
    Removed,
    Unchanged
}

public sealed record ModFileDifference(
    string RelativePath,
    ModFileDifferenceKind Kind,
    long PreviousBytes,
    long NewBytes);

public sealed class ModUpdatePreview
{
    public IReadOnlyList<ModFileDifference> Files { get; init; } = [];
    public int AddedCount => Files.Count(file => file.Kind == ModFileDifferenceKind.Added);
    public int ModifiedCount => Files.Count(file => file.Kind == ModFileDifferenceKind.Modified);
    public int RemovedCount => Files.Count(file => file.Kind == ModFileDifferenceKind.Removed);
    public int UnchangedCount => Files.Count(file => file.Kind == ModFileDifferenceKind.Unchanged);
    public bool HasChanges => AddedCount + ModifiedCount + RemovedCount > 0;
}

public sealed class ModVersionSnapshot
{
    public string SourcePath { get; init; } = string.Empty;
    public string SourceSha256 { get; init; } = string.Empty;
    public ImportStatus ImportStatus { get; init; }
    public HashSet<string> Hashes { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Dependencies { get; init; } = [];
    public List<AppliedFix> AppliedFixes { get; init; } = [];
    public string ReportFile { get; init; } = "import-report.json";
    public string? PreviewFile { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }
    public int VersionRevision { get; init; } = 1;
}

public sealed class ModVersionBackup
{
    public int SchemaVersion { get; init; } = 1;
    public string BackupId { get; init; } = string.Empty;
    public string ModId { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public string Reason { get; init; } = string.Empty;
    public int FileCount { get; init; }
    public long TotalBytes { get; init; }
    public ModVersionSnapshot Snapshot { get; init; } = new();
}

public sealed class LibraryState
{
    public int SchemaVersion { get; set; } = 3;
    public List<ModManifest> Mods { get; set; } = [];
    public List<CharacterGroupInfo> DiscoveredCharacterGroups { get; set; } = [];
    public List<CharacterGroupInfo> CustomCharacterGroups { get; set; } = [];
}

public sealed class AppConfig
{
    public int SchemaVersion { get; set; } = 3;
    public string? GameExecutablePath { get; set; }
    public string? RuntimePath { get; set; }
    public string? BackgroundImagePath { get; set; }
    public bool ConfigureGameSettings { get; set; } = true;
    public bool AutoDisableConflicts { get; set; } = true;
    public bool AutoReloadOnModChange { get; set; } = true;
    public bool AutoHideAfterLiveSwitch { get; set; } = true;
    public bool ReloadWhenRequired { get; set; } = true;
    // Missing values in pre-v3 config files deserialize to Exit, preserving
    // the historical close-button behavior for existing installations.
    public WindowCloseBehavior CloseBehavior { get; set; } = WindowCloseBehavior.Exit;
    public int InjectionTimeoutSeconds { get; set; } = 30;
    // Appearance defaults reproduce the previous look for existing installations:
    // dark palette, near-opaque chrome, and a background image that is finally
    // visible instead of being buried under a fixed veil.
    public AppTheme Theme { get; set; } = AppTheme.Dark;
    public double SidebarOpacity { get; set; } = AppearancePolicy.DefaultSidebarOpacity;
    public double PanelOpacity { get; set; } = AppearancePolicy.DefaultPanelOpacity;
    public double BackgroundOpacity { get; set; } = AppearancePolicy.DefaultBackgroundOpacity;
}

public sealed record ModStateRequest(string ModId, bool Enabled);

/// <summary>
/// A named snapshot of which mods were enabled. Presets deliberately store only
/// the enable flag: character group overrides, conflicts and same-character
/// exclusivity stay owned by the library rules, so applying an old preset after
/// the library changed cannot resurrect a stale grouping decision.
/// </summary>
public sealed class ModPreset
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<string> EnabledModIds { get; set; } = [];
}

public sealed class ModPresetState
{
    public int SchemaVersion { get; set; } = 1;
    public List<ModPreset> Presets { get; set; } = [];
}

public sealed record UnmanagedDirectoryChange(
    string OriginalDirectory,
    string QuarantinedDirectory);

public sealed class ModLibraryBatchResult
{
    public IReadOnlyList<ModManifest> ChangedMods { get; init; } = [];
    public IReadOnlyList<ModManifest> DisabledByCharacter { get; init; } = [];
    public IReadOnlyList<ModManifest> DisabledByConflict { get; init; } = [];
    public bool IncludeTreeChanged { get; init; }
}

public sealed class ModStateChangeResult
{
    public ModStateApplication Application { get; init; }
    public bool DesiredStateSaved { get; init; }
    public bool GameRunning { get; init; }
    public string Message { get; init; } = string.Empty;
    public IReadOnlyList<ModManifest> ChangedMods { get; init; } = [];
    public IReadOnlyList<ModManifest> AutomaticallyDisabled { get; init; } = [];

    public bool Succeeded => Application is ModStateApplication.Immediate or ModStateApplication.Reloaded;
}

public sealed class RuntimeManifest
{
    public int SchemaVersion { get; init; } = 1;
    public string PackageName { get; init; } = "ZZMI";
    public string ExpectedVersion { get; init; } = "1.4.3";
    public string RuntimeDirectory { get; init; } = string.Empty;
    public DateTimeOffset ImportedAt { get; init; } = DateTimeOffset.UtcNow;
    public Dictionary<string, string> FileSha256 { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class ImportCandidate
{
    [JsonIgnore]
    public string StagedPath { get; init; } = string.Empty;

    public string DisplayName { get; init; } = string.Empty;
    public string RelativeRoot { get; init; } = string.Empty;
    public string SourcePath { get; init; } = string.Empty;
    public string SourceSha256 { get; init; } = string.Empty;
    public ImportReport? Report { get; set; }
}

public sealed class ImportSession
{
    [JsonIgnore]
    public string StagingPath { get; init; } = string.Empty;

    public string SourcePath { get; init; } = string.Empty;
    public string SourceSha256 { get; init; } = string.Empty;
    public List<ImportCandidate> Candidates { get; init; } = [];
}

public sealed class SplitModPackage
{
    public string Key { get; init; } = string.Empty;
    public string SourcePath { get; init; } = string.Empty;
    public string SourceSha256 { get; init; } = string.Empty;
    public IReadOnlyList<ModManifest> Mods { get; init; } = [];
}

public sealed class ConflictResult
{
    public ModManifest Mod { get; init; } = new();
    public List<ModManifest> Conflicts { get; init; } = [];
}
