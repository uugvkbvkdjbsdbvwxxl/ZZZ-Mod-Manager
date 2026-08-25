using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using ZZZModManager.Infrastructure;
using ZZZModManager.Models;

namespace ZZZModManager.Services;

/// <summary>
/// Resolves a Mod to a stable character group. Built-in groups are intentionally
/// separate from groups discovered in the current library and user-created groups.
/// The roster itself is data: <see cref="Configure"/> swaps in the table stored at
/// <c>characters.json</c> in the library root, and every consumer keeps using the
/// same static surface.
/// </summary>
public static class CharacterGroupDetector
{
    private sealed record CharacterDefinition(CharacterGroupInfo Group, string[] Aliases);

    private static readonly object TableGate = new();
    private static CharacterDefinition[] Characters;
    private static CharacterGroupInfo FrameworkGroup;
    private static string[] DependencyTokens;
    private static string[] NonCharacterTokens;
    private static string[] GenericChineseTokens;
    private static HashSet<string> GenericAsciiTokens;

    static CharacterGroupDetector()
    {
        var table = CharacterTable.CreateBuiltIn();
        Characters = BuildCharacters(table);
        FrameworkGroup = BuildFrameworkGroup(table);
        DependencyTokens = BuildTokens(table.DependencyTokens);
        NonCharacterTokens = BuildTokens(table.NonCharacterTokens);
        GenericChineseTokens = BuildTokens(table.GenericChineseTokens);
        GenericAsciiTokens = BuildAsciiTokens(table.GenericAsciiTokens);
    }

    private static readonly Regex ChineseLabelRegex = new(
        @"[\p{IsCJKUnifiedIdeographs}]{2,5}",
        RegexOptions.Compiled);
    private static readonly Regex TextureOverrideNameRegex = new(
        @"TextureOverride(?<name>[A-Za-z][A-Za-z0-9]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex AsciiWordRegex = new(
        @"[A-Za-z][A-Za-z0-9]{2,}",
        RegexOptions.Compiled);

    /// <summary>
    /// Loads the roster from the library root, seeding the file on first run.
    /// A missing, unreadable, corrupt or rosterless file leaves the built-in table
    /// in place so detection never degrades to "未识别" because of bad data.
    /// </summary>
    public static void Configure(AppPaths paths, JsonFileStore store)
    {
        var seeded = false;
        if (!File.Exists(paths.CharacterTableFile))
        {
            store.Save(paths.CharacterTableFile, CharacterTable.CreateBuiltIn());
            seeded = true;
        }

        if (seeded)
        {
            ApplyTable(CharacterTable.CreateBuiltIn());
            return;
        }

        var table = store.Load(paths.CharacterTableFile, CharacterTable.CreateBuiltIn);
        ApplyTable(table);
    }

    /// <summary>
    /// Restores the compiled-in roster. Exposed so tests can undo a
    /// <see cref="Configure"/> call and stay order independent.
    /// </summary>
    public static void ResetToBuiltIn() => ApplyTable(CharacterTable.CreateBuiltIn());

    internal static void ApplyTable(CharacterTable? table)
    {
        var fallback = CharacterTable.CreateBuiltIn();
        var source = table?.Characters is { Count: > 0 } ? table : fallback;
        lock (TableGate)
        {
            Characters = BuildCharacters(source!);
            FrameworkGroup = BuildFrameworkGroup(source!);
            DependencyTokens = BuildTokens(source!.DependencyTokens ?? fallback.DependencyTokens);
            NonCharacterTokens = BuildTokens(source.NonCharacterTokens ?? fallback.NonCharacterTokens);
            GenericChineseTokens = BuildTokens(source.GenericChineseTokens ?? fallback.GenericChineseTokens);
            GenericAsciiTokens = BuildAsciiTokens(source.GenericAsciiTokens ?? fallback.GenericAsciiTokens);
        }
    }

    private static CharacterDefinition[] BuildCharacters(CharacterTable table) =>
        (table.Characters ?? [])
        .Where(entry => !string.IsNullOrWhiteSpace(entry.Key) && !string.IsNullOrWhiteSpace(entry.DisplayName))
        .DistinctBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase)
        .Select(entry => new CharacterDefinition(
            new CharacterGroupInfo(entry.Key.Trim(), entry.DisplayName.Trim(), CharacterGroupKind.Character),
            (entry.Aliases ?? [])
                .Where(alias => !string.IsNullOrWhiteSpace(alias))
                .Select(alias => alias.Trim())
                .ToArray()))
        .ToArray();

    private static CharacterGroupInfo BuildFrameworkGroup(CharacterTable table) =>
        new(
            string.IsNullOrWhiteSpace(table.FrameworkKey) ? "framework" : table.FrameworkKey.Trim(),
            string.IsNullOrWhiteSpace(table.FrameworkDisplayName) ? "通用依赖 / 框架" : table.FrameworkDisplayName.Trim(),
            CharacterGroupKind.Framework);

    private static string[] BuildTokens(IEnumerable<string>? tokens) =>
        (tokens ?? [])
        .Where(token => !string.IsNullOrWhiteSpace(token))
        .Select(token => token.Trim())
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    private static HashSet<string> BuildAsciiTokens(IEnumerable<string>? tokens) =>
        new(BuildTokens(tokens), StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<CharacterGroupInfo> KnownGroups =>
        Characters.Select(item => item.Group).Append(FrameworkGroup).ToList();

    public static IReadOnlyList<CharacterGroupInfo> BuiltInCharacterGroups =>
        Characters.Select(item => item.Group).ToList();

    public static bool IsRoleGroup(CharacterGroupKind kind) =>
        kind is CharacterGroupKind.Character
            or CharacterGroupKind.Discovered
            or CharacterGroupKind.Custom;

    public static CharacterGroupInfo CreateCustomGroup(string displayName)
    {
        var name = displayName.Trim();
        var normalized = Normalize(name);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new ArgumentException("自定义分组名称不能为空。", nameof(displayName));
        }

        return new CharacterGroupInfo(
            StableKey("custom", normalized),
            name,
            CharacterGroupKind.Custom);
    }

    public static CharacterGroupInfo CreateDiscoveredGroup(string label)
    {
        var name = Normalize(label);
        return new CharacterGroupInfo(
            StableKey("discovered", name),
            $"自动发现 · {name}",
            CharacterGroupKind.Discovered);
    }

    public static CharacterGroupInfo DetectInfo(
        ModManifest manifest,
        string absolutePath,
        IReadOnlyList<CharacterGroupInfo>? additionalGroups = null)
    {
        var extras = additionalGroups ?? [];
        if (!string.IsNullOrWhiteSpace(manifest.CharacterGroupOverrideKey))
        {
            var overridden = FindGroup(manifest.CharacterGroupOverrideKey, extras);
            if (overridden is not null)
            {
                return overridden;
            }
        }

        var hints = CollectHints(manifest, absolutePath).ToList();
        var text = string.Join('\n', hints);
        if (DependencyTokens.Any(token => ContainsAlias(text, token)))
        {
            return FrameworkGroup;
        }

        foreach (var character in Characters)
        {
            if (character.Aliases.Any(alias => ContainsAlias(text, alias)))
            {
                return character.Group;
            }
        }

        if (NonCharacterTokens.Any(token => ContainsAlias(text, token)))
        {
            return CreateUnknownGroup(manifest);
        }

        foreach (var group in extras.Where(group => IsRoleGroup(group.Kind)))
        {
            if (GroupMatches(text, group))
            {
                return group;
            }
        }

        var discoveredLabel = DiscoverLabel(hints);
        if (!string.IsNullOrWhiteSpace(discoveredLabel))
        {
            return CreateDiscoveredGroup(discoveredLabel);
        }

        return CreateUnknownGroup(manifest);
    }

    private static CharacterGroupInfo CreateUnknownGroup(ModManifest manifest)
    {
        var label = Normalize(manifest.DisplayName);
        if (string.IsNullOrWhiteSpace(label))
        {
            label = "未知 Mod";
        }

        var identity = string.IsNullOrWhiteSpace(manifest.Id) ? manifest.DisplayName : manifest.Id;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
        return new CharacterGroupInfo($"unknown:{hash[..12]}", $"未识别 · {label}", CharacterGroupKind.Unknown);
    }

    public static string Detect(ModManifest manifest, string absolutePath) =>
        DetectInfo(manifest, absolutePath).DisplayName;

    public static CharacterGroupInfo? FindGroup(
        string key,
        IEnumerable<CharacterGroupInfo>? additionalGroups = null)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        return KnownGroups
            .Concat(additionalGroups ?? [])
            .FirstOrDefault(group => string.Equals(group.Key, key, StringComparison.OrdinalIgnoreCase));
    }

    public static CharacterGroupInfo? FindKnownGroup(string key) => FindGroup(key);

    private static IEnumerable<string> CollectHints(ModManifest manifest, string absolutePath)
    {
        var hints = new List<string>
        {
            manifest.DisplayName,
            Path.GetFileName(manifest.InstalledDirectory),
            Path.GetFileName(manifest.SourcePath),
            manifest.SourcePath
        };

        if (!Directory.Exists(absolutePath))
        {
            return hints;
        }

        try
        {
            foreach (var ini in Directory.EnumerateFiles(absolutePath, "*.ini", SearchOption.AllDirectories).Take(200))
            {
                hints.Add(Path.GetFileNameWithoutExtension(ini));
                foreach (var line in File.ReadLines(ini).Take(800))
                {
                    if (line.Contains("TextureOverride", StringComparison.OrdinalIgnoreCase)
                        || line.Contains("namespace", StringComparison.OrdinalIgnoreCase)
                        || line.Contains("character", StringComparison.OrdinalIgnoreCase))
                    {
                        hints.Add(line);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _ = ex;
        }

        return hints;
    }

    private static string? DiscoverLabel(IReadOnlyList<string> hints)
    {
        var chinese = new Dictionary<string, int>(StringComparer.Ordinal);
        var ascii = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var hint in hints)
        {
            foreach (Match match in ChineseLabelRegex.Matches(hint))
            {
                var candidate = match.Value;
                if (GenericChineseTokens.Any(candidate.Contains))
                {
                    continue;
                }

                chinese[candidate] = chinese.TryGetValue(candidate, out var score) ? score + 1 : 1;
            }

            foreach (Match match in TextureOverrideNameRegex.Matches(hint))
            {
                AddAsciiCandidate(ascii, match.Groups["name"].Value, 4);
            }

            foreach (Match match in AsciiWordRegex.Matches(hint))
            {
                AddAsciiCandidate(ascii, match.Value, 1);
            }
        }

        var chineseLabel = chinese
            .OrderByDescending(pair => pair.Value)
            .ThenByDescending(pair => pair.Key.Length)
            .Select(pair => pair.Key)
            .FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(chineseLabel))
        {
            return chineseLabel;
        }

        return ascii
            .OrderByDescending(pair => pair.Value)
            .ThenByDescending(pair => pair.Key.Length)
            .ThenBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => pair.Key)
            .FirstOrDefault();
    }

    private static void AddAsciiCandidate(Dictionary<string, int> scores, string raw, int weight)
    {
        var candidate = CleanAsciiCandidate(raw);
        if (candidate is null)
        {
            return;
        }

        scores[candidate] = scores.TryGetValue(candidate, out var score) ? score + weight : weight;
    }

    private static string? CleanAsciiCandidate(string raw)
    {
        var candidate = raw.Trim('_', '-');
        foreach (var suffix in new[] { "Body", "Face", "Hair", "Head", "Weapon", "Clothes", "Dress", "Skirt", "Shoes", "Hand", "Foot", "IB", "VB" })
        {
            if (candidate.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
                && candidate.Length - suffix.Length >= 3)
            {
                candidate = candidate[..^suffix.Length];
                break;
            }
        }

        if (candidate.Length < 3
            || GenericAsciiTokens.Contains(candidate)
            || candidate.All(char.IsDigit)
            || Regex.IsMatch(candidate, @"^(?:v?\d+(?:\.\d+)*|[a-f0-9]{6,})$", RegexOptions.IgnoreCase))
        {
            return null;
        }

        return candidate;
    }

    private static bool GroupMatches(string text, CharacterGroupInfo group)
    {
        var display = group.DisplayName
            .Replace("自动发现 · ", string.Empty, StringComparison.Ordinal)
            .Trim();
        var keyPart = group.Key.Contains(':', StringComparison.Ordinal)
            ? group.Key[(group.Key.IndexOf(':') + 1)..]
            : group.Key;
        return ContainsAlias(text, display) || ContainsAlias(text, keyPart);
    }

    private static bool ContainsAlias(string text, string alias)
    {
        if (string.IsNullOrWhiteSpace(alias))
        {
            return false;
        }

        if (alias.Any(ch => ch > 127))
        {
            if (alias.Length == 1)
            {
                var singleChinesePattern = $"(?<![\\p{{IsCJKUnifiedIdeographs}}]){Regex.Escape(alias)}(?![\\p{{IsCJKUnifiedIdeographs}}])";
                return Regex.IsMatch(text, singleChinesePattern, RegexOptions.CultureInvariant);
            }

            return text.Contains(alias, StringComparison.OrdinalIgnoreCase);
        }

        var pattern = $"(?<![A-Za-z0-9]){Regex.Escape(alias).Replace("\\ ", "[ _-]+")}(?![A-Za-z0-9])";
        return Regex.IsMatch(text, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static string StableKey(string prefix, string value)
    {
        var normalized = Normalize(value).ToLowerInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))
            .ToLowerInvariant();
        return $"{prefix}:{hash[..16]}";
    }

    private static string Normalize(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var ch in value.Trim())
        {
            if (char.IsLetterOrDigit(ch) || ch > 127)
            {
                builder.Append(ch);
            }
            else if (char.IsWhiteSpace(ch) || ch is '_' or '-' or '.')
            {
                builder.Append(' ');
            }
        }

        return string.Join(' ', builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }
}
