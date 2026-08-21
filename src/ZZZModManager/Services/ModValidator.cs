using System.Text;
using System.Text.RegularExpressions;
using ZZZModManager.Infrastructure;
using ZZZModManager.Models;

namespace ZZZModManager.Services;

public interface IModValidator
{
    ImportReport ValidateAndRepair(ImportCandidate candidate);
}

public sealed class ModValidator : IModValidator
{
    private const string RuleVersion = "1";
    private static readonly Regex SectionRegex = new(@"^\s*\[(?<name>[^\]]+)\]\s*$", RegexOptions.Compiled);
    private static readonly Regex FilenameRegex = new(@"^\s*filename\s*=\s*(?<value>[^;#]+?)\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex HashRegex = new(@"^\s*hash\s*=\s*(?<value>[0-9a-fA-F]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex TrailingCycleRegex = new(@"^(?<indent>\s*\$[A-Za-z0-9_]+\s*=\s*[^#;]*?\d)\s*,\s*$", RegexOptions.Compiled);
    private static readonly Regex ResourceTokenRegex = new(@"\bResource[A-Za-z0-9_]+\b", RegexOptions.Compiled);
    private static readonly Regex CommandTokenRegex = new(
        @"(?<![A-Za-z0-9_])(?<value>CommandList(?:\\[A-Za-z0-9_\\]+|[A-Za-z0-9_]+))(?![A-Za-z0-9_])",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ResourceReferenceAssignmentRegex = new(
        @"^\s*Resource\\[^=]+\s*=\s*ref\s+(?<name>Resource[A-Za-z0-9_]+)\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex NamespaceRegex = new(
        @"^\s*namespace\s*=\s*(?<value>[^;#]+?)\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex IfRegex = new(@"^\s*if(?:\s|$)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex EndIfRegex = new(@"^\s*endif(?:\s|$)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly AppPaths _paths;

    public ModValidator(AppPaths paths)
    {
        _paths = paths;
    }

    public ImportReport ValidateAndRepair(ImportCandidate candidate)
    {
        var report = new ImportReport
        {
            SourcePath = candidate.SourcePath,
            SourceSha256 = candidate.SourceSha256,
            CandidateRoot = candidate.RelativeRoot
        };

        var iniFiles = Directory.EnumerateFiles(candidate.StagedPath, "*.ini", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (iniFiles.Count == 0)
        {
            report.Issues.Add(new ValidationIssue
            {
                Severity = IssueSeverity.Error,
                Code = "NO_INI",
                Message = "候选目录不包含 INI 文件。"
            });
            report.Status = ImportStatus.Blocked;
            return report;
        }

        foreach (var iniPath in iniFiles)
        {
            ApplyTrailingCycleFixes(iniPath, candidate.StagedPath, report);
            ApplyLegacyRabbitFxFix(iniPath, candidate.StagedPath, report);
            ApplyLegacyModManagerGuardFix(iniPath, candidate.StagedPath, report);
        }

        // Some older XXMI exports keep a "vanilla" override section with
        // texture bindings for files that were never shipped.  The section
        // has no draw operation, so those bindings can be removed
        // deterministically; otherwise a valid merged package would be
        // blocked even though the optional back-hair textures are unused.
        ApplyInactiveMissingResourceBindingFixes(iniFiles, candidate.StagedPath, report);
        ApplyUnusedMissingResourceFixes(iniFiles, candidate.StagedPath, report);

        var contexts = iniFiles.Select(path => ReadContext(path, candidate.StagedPath)).ToList();
        var declaredResources = contexts.SelectMany(context => context.Resources)
            .Select(resource => resource.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var declaredCommandLists = CollectDeclaredCommandLists(contexts);

        foreach (var context in contexts)
        {
            foreach (var resource in context.Resources)
            {
                if (resource.Filename is null)
                {
                    continue;
                }

                var resolved = Path.GetFullPath(Path.Combine(
                    Path.GetDirectoryName(context.Path)!,
                    resource.Filename.Replace('/', Path.DirectorySeparatorChar)));
                if (!FileSystemSafety.IsWithin(candidate.StagedPath, resolved))
                {
                    report.Issues.Add(new ValidationIssue
                    {
                        Severity = IssueSeverity.Error,
                        Code = "PATH_OUTSIDE_ROOT",
                        Message = $"资源 {resource.Name} 指向候选目录之外的路径：{resource.Filename}",
                        File = Path.GetRelativePath(candidate.StagedPath, context.Path),
                        Line = resource.FilenameLine + 1,
                        Fixable = false
                    });
                    continue;
                }

                if (File.Exists(resolved))
                {
                    continue;
                }

                var used = CountResourceUse(contexts, resource.Name);
                if (used > 0)
                {
                    report.Issues.Add(new ValidationIssue
                    {
                        Severity = IssueSeverity.Error,
                        Code = "MISSING_USED_FILE",
                        Message = $"资源 {resource.Name} 引用的文件不存在：{resource.Filename}",
                        File = Path.GetRelativePath(context.Root, context.Path),
                        Line = resource.FilenameLine + 1,
                        Fixable = false
                    });
                }
            }

            foreach (var line in context.Lines.Select((value, index) => (value, index)))
            {
                if (IsComment(line.value))
                {
                    continue;
                }

                foreach (Match token in ResourceTokenRegex.Matches(line.value))
                {
                    if (!declaredResources.Contains(token.Value)
                        && !token.Value.Equals("ResourceEngineRGB", StringComparison.OrdinalIgnoreCase))
                    {
                        report.Issues.Add(new ValidationIssue
                        {
                            Severity = IssueSeverity.Warning,
                            Code = "UNDEFINED_RESOURCE",
                            Message = $"INI 引用了未定义的资源：{token.Value}",
                            File = Path.GetRelativePath(context.Root, context.Path),
                            Line = line.index + 1,
                            Fixable = false
                        });
                    }
                }
            }

            ValidateConditionalBalance(context, report);
            foreach (var line in context.Lines.Select((value, index) => (value, index)))
            {
                if (IsComment(line.value) || SectionRegex.IsMatch(line.value))
                {
                    continue;
                }

                foreach (Match tokenMatch in CommandTokenRegex.Matches(line.value))
                {
                    var token = tokenMatch.Groups["value"].Value;
                    if (declaredCommandLists.Contains(token)
                        || token.Contains(@"CommandList\RabbitFX\", StringComparison.OrdinalIgnoreCase)
                        || token.Contains(@"CommandList\ZZMI\", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    report.Issues.Add(new ValidationIssue
                    {
                        Severity = IssueSeverity.Warning,
                        Code = "UNDEFINED_COMMAND_LIST",
                        Message = $"INI 引用了未定义的 CommandList：{token}",
                        File = Path.GetRelativePath(context.Root, context.Path),
                        Line = line.index + 1,
                        Fixable = false
                    });
                }
            }

            foreach (var line in context.Lines)
            {
                foreach (Match hash in HashRegex.Matches(line))
                {
                    report.Hashes.Add(hash.Groups["value"].Value.ToLowerInvariant());
                }
            }
        }

        // A RabbitFX package declares its own namespace. Only mods that call the
        // exported RabbitFX command list should receive RabbitFX as a dependency;
        // otherwise RabbitFX would incorrectly depend on itself.
        var definesRabbitFxNamespace = contexts.Any(context =>
            context.Lines.Any(line => Regex.IsMatch(
                line,
                @"^\s*namespace\s*=\s*RabbitFX\s*$",
                RegexOptions.IgnoreCase)));
        var requiresRabbitFx = !definesRabbitFxNamespace && contexts.Any(context =>
            context.Lines.Any(line => line.Contains(@"CommandList\RabbitFX", StringComparison.OrdinalIgnoreCase)));
        if (requiresRabbitFx)
        {
            report.Dependencies.Add("RabbitFX");
            if (!IsDependencyAvailable("RabbitFX"))
            {
                report.Issues.Add(new ValidationIssue
                {
                    Severity = IssueSeverity.Warning,
                    Code = "MISSING_DEPENDENCY",
                    Message = "需要 RabbitFX。请将已授权的 RabbitFX Mod 压缩包或文件夹拖入管理器后再启用。",
                    Fixable = false
                });
            }
        }

        var hasErrors = report.Issues.Any(issue => issue.Severity == IssueSeverity.Error);
        var hasMissingDependency = report.Issues.Any(issue => issue.Code == "MISSING_DEPENDENCY");
        report.Status = hasErrors
            ? ImportStatus.Blocked
            : hasMissingDependency
                ? ImportStatus.NeedsDependency
                : report.Fixes.Count > 0
                    ? ImportStatus.ReadyWithFixes
                    : ImportStatus.Ready;
        return report;
    }

    private void ApplyTrailingCycleFixes(string path, string root, ImportReport report)
    {
        var lines = ReadLines(path);
        var changed = false;
        for (var index = 0; index < lines.Count; index++)
        {
            var match = TrailingCycleRegex.Match(lines[index]);
            if (!match.Success)
            {
                continue;
            }

            var before = lines[index];
            lines[index] = match.Groups["indent"].Value;
            changed = true;
            report.Fixes.Add(new AppliedFix
            {
                RuleId = "normalize-trailing-cycle-value",
                RuleVersion = RuleVersion,
                File = Path.GetRelativePath(root, path),
                Line = index + 1,
                Before = before,
                After = lines[index]
            });
        }

        if (changed)
        {
            WriteLines(path, lines);
        }
    }

    private void ApplyLegacyRabbitFxFix(string path, string root, ImportReport report)
    {
        var lines = ReadLines(path);
        var text = string.Join('\n', lines);
        if (!text.Contains("RabbitFX", StringComparison.OrdinalIgnoreCase)
            || !text.Contains("CommandList\\RabbitFX\\Run", StringComparison.OrdinalIgnoreCase)
            || text.Contains("[ResourceEngineRGB]", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var changed = false;
        for (var index = lines.Count - 1; index >= 0; index--)
        {
            if (!lines[index].Contains("ps-u4 = ResourceEngineRGB", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var before = lines[index];
            lines.RemoveAt(index);
            report.Fixes.Add(new AppliedFix
            {
                RuleId = "rabbitfx-remove-stale-engine-buffer",
                RuleVersion = RuleVersion,
                File = Path.GetRelativePath(root, path),
                Line = index + 1,
                Before = before,
                After = string.Empty
            });
            changed = true;
        }

        if (changed)
        {
            WriteLines(path, lines);
        }
    }

    private static void ApplyLegacyModManagerGuardFix(string path, string root, ImportReport report)
    {
        var lines = ReadLines(path);
        var changed = false;

        for (var index = 0; index < lines.Count; index++)
        {
            var before = lines[index];
            if (IsComment(before)
                || !before.Contains(@"$\modmanageragl\", StringComparison.OrdinalIgnoreCase)
                    && !before.Contains(@"$\modmanager\", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var after = before;
            if (Regex.IsMatch(after, @"^\s*if\s+", RegexOptions.IgnoreCase))
            {
                after = Regex.Replace(after, @"^(?<indent>\s*)if\s+.*$", "${indent}if 1", RegexOptions.IgnoreCase);
            }
            else if (Regex.IsMatch(after, @"^\s*condition\s*=", RegexOptions.IgnoreCase))
            {
                var prefix = Regex.Match(after, @"^(?<prefix>\s*condition\s*=\s*)", RegexOptions.IgnoreCase).Groups["prefix"].Value;
                var expression = after[prefix.Length..];
                expression = Regex.Replace(
                    expression,
                    @"\s*(?:&&|\|\|)?\s*\$managed_slot_id\s*==\s*\$\\modmanager(?:agl)?\\[A-Za-z0-9_\\]+",
                    string.Empty,
                    RegexOptions.IgnoreCase);
                expression = Regex.Replace(expression, @"\s*(?:&&|\|\|)\s*$", string.Empty);
                after = prefix + (string.IsNullOrWhiteSpace(expression) ? "1" : expression.Trim());
            }
            else
            {
                continue;
            }

            if (string.Equals(before, after, StringComparison.Ordinal))
            {
                continue;
            }

            lines[index] = after;
            changed = true;
            report.Fixes.Add(new AppliedFix
            {
                RuleId = "remove-legacy-modmanager-guard",
                RuleVersion = RuleVersion,
                File = Path.GetRelativePath(root, path),
                Line = index + 1,
                Before = before,
                After = after
            });
        }

        if (changed)
        {
            WriteLines(path, lines);
        }
    }

    private static void ApplyUnusedMissingResourceFixes(
        IReadOnlyList<string> iniFiles,
        string root,
        ImportReport report)
    {
        var contexts = iniFiles.Select(path => ReadContext(path, root)).ToList();
        var changes = new Dictionary<string, List<(int Start, int End, ResourceBlock Resource)>>(StringComparer.OrdinalIgnoreCase);

        foreach (var context in contexts)
        {
            foreach (var resource in context.Resources)
            {
                if (resource.Filename is null)
                {
                    continue;
                }

                var resolved = Path.GetFullPath(Path.Combine(
                    Path.GetDirectoryName(context.Path)!,
                    resource.Filename.Replace('/', Path.DirectorySeparatorChar)));
                if (!FileSystemSafety.IsWithin(root, resolved)
                    || File.Exists(resolved)
                    || CountResourceUse(contexts, resource.Name) > 0)
                {
                    continue;
                }

                var list = changes.TryGetValue(context.Path, out var existing)
                    ? existing
                    : changes[context.Path] = [];
                list.Add((resource.StartLine, resource.EndLine, resource));
            }
        }

        foreach (var change in changes)
        {
            var lines = ReadLines(change.Key);
            foreach (var item in change.Value.OrderByDescending(item => item.Start))
            {
                var endExclusive = Math.Min(lines.Count, item.End + 1);
                var before = string.Join(" | ", lines.Skip(item.Start).Take(endExclusive - item.Start));
                lines.RemoveRange(item.Start, endExclusive - item.Start);
                report.Fixes.Add(new AppliedFix
                {
                    RuleId = "remove-unused-missing-resource",
                    RuleVersion = RuleVersion,
                    File = Path.GetRelativePath(root, change.Key),
                    Line = item.Start + 1,
                    Before = before,
                    After = string.Empty
                });
            }

            WriteLines(change.Key, lines);
        }
    }

    private static void ApplyInactiveMissingResourceBindingFixes(
        IReadOnlyList<string> iniFiles,
        string root,
        ImportReport report)
    {
        var contexts = iniFiles.Select(path => ReadContext(path, root)).ToList();
        var changes = new Dictionary<string, List<InactiveResourceChange>>(StringComparer.OrdinalIgnoreCase);

        foreach (var context in contexts)
        {
            foreach (var resource in context.Resources)
            {
                if (resource.Filename is null)
                {
                    continue;
                }

                var resolved = Path.GetFullPath(Path.Combine(
                    Path.GetDirectoryName(context.Path)!,
                    resource.Filename.Replace('/', Path.DirectorySeparatorChar)));
                if (!FileSystemSafety.IsWithin(root, resolved) || File.Exists(resolved))
                {
                    continue;
                }

                var uses = FindResourceUses(context, resource.Name);
                if (uses.Count == 0
                    || uses.Any(use => !use.IsReferenceBinding)
                    || uses.Any(use => !IsInactiveTextureOverride(context, use.SectionName)))
                {
                    continue;
                }

                var list = changes.TryGetValue(context.Path, out var existing)
                    ? existing
                    : changes[context.Path] = [];
                list.Add(new InactiveResourceChange(resource, uses.Select(use => use.Line).ToList()));
            }
        }

        foreach (var change in changes)
        {
            var lines = ReadLines(change.Key);
            var removals = new Dictionary<int, (bool Binding, string Before, string RuleId)>();
            var declarationFixes = new List<(int StartLine, string Before)>();
            foreach (var item in change.Value)
            {
                foreach (var line in item.BindingLines)
                {
                    if (line >= 0 && line < lines.Count)
                    {
                        removals[line] = (true, lines[line], "remove-inactive-missing-resource-binding");
                    }
                }

                var endExclusive = Math.Min(lines.Count, item.Resource.EndLine + 1);
                var before = string.Join(" | ", lines.Skip(item.Resource.StartLine).Take(endExclusive - item.Resource.StartLine));
                declarationFixes.Add((item.Resource.StartLine, before));
                for (var line = item.Resource.StartLine; line < endExclusive; line++)
                {
                    removals[line] = (false, string.Empty, string.Empty);
                }
            }

            foreach (var removal in removals.OrderByDescending(item => item.Key))
            {
                var line = removal.Key;
                if (line < 0 || line >= lines.Count)
                {
                    continue;
                }

                var metadata = removal.Value;
                lines.RemoveAt(line);
                if (metadata.Binding)
                {
                    report.Fixes.Add(new AppliedFix
                    {
                        RuleId = metadata.RuleId,
                        RuleVersion = RuleVersion,
                        File = Path.GetRelativePath(root, change.Key),
                        Line = line + 1,
                        Before = metadata.Before,
                        After = string.Empty
                    });
                }
            }

            foreach (var declaration in declarationFixes)
            {
                report.Fixes.Add(new AppliedFix
                {
                    RuleId = "remove-unused-missing-resource",
                    RuleVersion = RuleVersion,
                    File = Path.GetRelativePath(root, change.Key),
                    Line = declaration.StartLine + 1,
                    Before = declaration.Before,
                    After = string.Empty
                });
            }

            WriteLines(change.Key, lines);
        }
    }

    private static List<ResourceUse> FindResourceUses(IniContext context, string resourceName)
    {
        var uses = new List<ResourceUse>();
        var sectionName = string.Empty;
        for (var index = 0; index < context.Lines.Count; index++)
        {
            var section = SectionRegex.Match(context.Lines[index]);
            if (section.Success)
            {
                sectionName = section.Groups["name"].Value.Trim();
            }

            if (IsComment(context.Lines[index])
                || string.Equals(sectionName, resourceName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!Regex.IsMatch(
                    context.Lines[index],
                    $@"(?<![A-Za-z0-9_]){Regex.Escape(resourceName)}(?![A-Za-z0-9_])",
                    RegexOptions.IgnoreCase))
            {
                continue;
            }

            var reference = ResourceReferenceAssignmentRegex.Match(context.Lines[index]);
            uses.Add(new ResourceUse(
                index,
                sectionName,
                reference.Success
                && string.Equals(reference.Groups["name"].Value, resourceName, StringComparison.OrdinalIgnoreCase)));
        }

        return uses;
    }

    private static bool IsInactiveTextureOverride(IniContext context, string sectionName)
    {
        // This is intentionally narrow: it handles the legacy SeedHairB
        // vanilla-back section, whose missing texture bindings are the only
        // active-looking references and which contains no draw operation.
        if (!string.Equals(sectionName, "TextureOverrideSeedHairB", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var sectionStart = -1;
        var sectionEnd = context.Lines.Count;
        for (var index = 0; index < context.Lines.Count; index++)
        {
            var section = SectionRegex.Match(context.Lines[index]);
            if (!section.Success)
            {
                continue;
            }

            var name = section.Groups["name"].Value.Trim();
            if (sectionStart >= 0)
            {
                sectionEnd = index;
                break;
            }

            if (string.Equals(name, sectionName, StringComparison.OrdinalIgnoreCase))
            {
                sectionStart = index + 1;
            }
        }

        if (sectionStart < 0)
        {
            return false;
        }

        for (var index = sectionStart; index < sectionEnd; index++)
        {
            var trimmed = context.Lines[index].Trim();
            if (trimmed.Length == 0 || IsComment(trimmed))
            {
                continue;
            }

            if (trimmed.StartsWith("hash", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("match_first_index", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("ib", StringComparison.OrdinalIgnoreCase)
                || ResourceReferenceAssignmentRegex.IsMatch(trimmed)
                || string.Equals(trimmed, @"run = CommandList\ZZMI\SetTextures", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Any draw/dispatch/conditional or unknown command means the
            // section may actually use the texture and must stay blocked.
            return false;
        }

        return true;
    }

    private bool IsDependencyAvailable(string dependency)
    {
        if (!Directory.Exists(_paths.DependenciesRoot) && !Directory.Exists(_paths.ModsRoot))
        {
            return false;
        }

        var roots = new[] { _paths.DependenciesRoot, _paths.ModsRoot };
        foreach (var root in roots)
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
            {
                if (directory.Contains("DISABLED_", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (new DirectoryInfo(directory).Name.Contains(dependency, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static HashSet<string> CollectDeclaredCommandLists(IEnumerable<IniContext> contexts)
    {
        var declared = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var context in contexts)
        {
            var namespaceName = context.Lines
                .Select(line => NamespaceRegex.Match(line))
                .FirstOrDefault(match => match.Success)?
                .Groups["value"].Value.Trim();
            foreach (var line in context.Lines)
            {
                var section = SectionRegex.Match(line);
                if (!section.Success)
                {
                    continue;
                }

                var name = section.Groups["name"].Value.Trim();
                if (!name.StartsWith("CommandList", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                declared.Add(name);
                var suffix = name["CommandList".Length..].TrimStart('\\');
                if (!string.IsNullOrWhiteSpace(namespaceName) && !string.IsNullOrWhiteSpace(suffix))
                {
                    declared.Add($@"CommandList\{namespaceName}\{suffix}");
                }
            }
        }

        return declared;
    }

    private static void ValidateConditionalBalance(IniContext context, ImportReport report)
    {
        var openConditions = new Stack<int>();
        for (var index = 0; index < context.Lines.Count; index++)
        {
            var line = context.Lines[index];
            if (IsComment(line))
            {
                continue;
            }

            if (IfRegex.IsMatch(line))
            {
                openConditions.Push(index);
            }
            else if (EndIfRegex.IsMatch(line))
            {
                if (openConditions.Count > 0)
                {
                    openConditions.Pop();
                }
                else
                {
                    report.Issues.Add(new ValidationIssue
                    {
                        Severity = IssueSeverity.Error,
                        Code = "UNMATCHED_ENDIF",
                        Message = "INI 包含没有对应 if 的 endif。",
                        File = Path.GetRelativePath(context.Root, context.Path),
                        Line = index + 1,
                        Fixable = false
                    });
                }
            }
        }

        foreach (var index in openConditions.Order())
        {
            report.Issues.Add(new ValidationIssue
            {
                Severity = IssueSeverity.Error,
                Code = "UNTERMINATED_IF",
                Message = "INI 包含没有对应 endif 的 if。",
                File = Path.GetRelativePath(context.Root, context.Path),
                Line = index + 1,
                Fixable = false
            });
        }
    }

    private static IniContext ReadContext(string path, string root)
    {
        var lines = ReadLines(path);
        var resources = new List<ResourceBlock>();
        string? currentSection = null;
        var currentStart = -1;
        string? currentFilename = null;
        var currentFilenameLine = -1;

        void Flush(int endLine)
        {
            if (currentSection is not null && currentSection.StartsWith("Resource", StringComparison.OrdinalIgnoreCase))
            {
                resources.Add(new ResourceBlock(
                    currentSection,
                    currentStart,
                    Math.Max(currentStart, endLine),
                    currentFilename,
                    currentFilenameLine));
            }
        }

        for (var index = 0; index < lines.Count; index++)
        {
            var section = SectionRegex.Match(lines[index]);
            if (section.Success)
            {
                Flush(index - 1);
                currentSection = section.Groups["name"].Value.Trim();
                currentStart = index;
                currentFilename = null;
                currentFilenameLine = -1;
                continue;
            }

            if (currentSection is not null && currentSection.StartsWith("Resource", StringComparison.OrdinalIgnoreCase))
            {
                var filename = FilenameRegex.Match(lines[index]);
                if (filename.Success)
                {
                    currentFilename = filename.Groups["value"].Value.Trim().Trim('"');
                    currentFilenameLine = index;
                }
            }
        }

        Flush(lines.Count - 1);
        return new IniContext(path, root, lines, resources);
    }

    private static int CountResourceUse(IEnumerable<IniContext> contexts, string resourceName)
    {
        var count = 0;
        foreach (var context in contexts)
        {
            string? section = null;
            foreach (var line in context.Lines)
            {
                var sectionMatch = SectionRegex.Match(line);
                if (sectionMatch.Success)
                {
                    section = sectionMatch.Groups["name"].Value.Trim();
                }

                if (IsComment(line) || string.Equals(section, resourceName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (Regex.IsMatch(line, $@"(?<![A-Za-z0-9_]){Regex.Escape(resourceName)}(?![A-Za-z0-9_])",
                        RegexOptions.IgnoreCase))
                {
                    count++;
                }
            }
        }

        return count;
    }

    private static bool IsComment(string line)
    {
        var trimmed = line.TrimStart();
        return trimmed.StartsWith(';') || trimmed.StartsWith('#');
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

    private static void WriteLines(string path, IReadOnlyList<string> lines)
    {
        File.WriteAllLines(path, lines, new UTF8Encoding(false));
    }

    private sealed record IniContext(string Path, string Root, List<string> Lines, List<ResourceBlock> Resources);
    private sealed record ResourceBlock(string Name, int StartLine, int EndLine, string? Filename, int FilenameLine);
    private sealed record ResourceUse(int Line, string SectionName, bool IsReferenceBinding);
    private sealed record InactiveResourceChange(ResourceBlock Resource, IReadOnlyList<int> BindingLines);
}
