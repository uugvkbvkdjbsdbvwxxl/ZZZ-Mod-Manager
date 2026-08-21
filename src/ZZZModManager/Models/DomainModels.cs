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
    public override string ToString() => $"[{Timestamp:HH:mm:ss}] [{Level}] {Message}";
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
    public int SchemaVersion { get; set; } = 2;
    public string Id { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string SourcePath { get; init; } = string.Empty;
    public string SourceSha256 { get; init; } = string.Empty;
    public string InstalledDirectory { get; set; } = string.Empty;
    public DateTimeOffset ImportedAt { get; init; } = DateTimeOffset.UtcNow;
    public bool Enabled { get; set; }
    public ImportStatus ImportStatus { get; init; }
    public HashSet<string> Hashes { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Dependencies { get; init; } = [];
    public List<AppliedFix> AppliedFixes { get; init; } = [];
    public string ReportFile { get; init; } = "import-report.json";

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
}

public sealed record ModStateRequest(string ModId, bool Enabled);

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
