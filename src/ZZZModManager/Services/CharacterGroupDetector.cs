using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using ZZZModManager.Models;

namespace ZZZModManager.Services;

/// <summary>
/// Resolves a Mod to a stable character group. Built-in groups are intentionally
/// separate from groups discovered in the current library and user-created groups.
/// </summary>
public static class CharacterGroupDetector
{
    private sealed record CharacterDefinition(CharacterGroupInfo Group, string[] Aliases);

    private static readonly CharacterDefinition[] Characters =
    [
        Character("velina", "维琳娜 / Velina", "velina", "velinablackfan", "维琳娜"),
        Character("alice", "爱丽丝 / Alice", "alice", "alicetops", "爱丽丝"),
        Character("nicole", "妮可 / Nicole", "nicole", "妮可"),
        Character("anby", "安比 / Anby", "anby", "安比"),
        Character("billy", "比利 / Billy", "billy", "比利"),
        Character("nekomata", "猫又 / Nekomata", "nekomata", "猫又"),
        Character("corin", "可琳 / Corin", "corin", "可琳"),
        Character("lycaon", "莱卡恩 / Lycaon", "lycaon", "莱卡恩"),
        Character("soukaku", "苍角 / Soukaku", "soukaku", "苍角"),
        Character("zhuyuan", "朱鸢 / Zhu Yuan", "zhu yuan", "zhuyuan", "朱鸢"),
        Character("qingyi", "青衣 / Qingyi", "qingyi", "青衣"),
        Character("jane", "简 / Jane Doe", "jane doe", "janedoe", "简"),
        Character("seth", "赛斯 / Seth", "seth", "赛斯", "席德", "席德流萤", "老席德"),
        Character("soldier11", "11号 / Soldier 11", "soldier 11", "soldier11", "11号"),
        Character("rina", "丽娜 / Rina", "rina", "丽娜"),
        Character("anton", "安东 / Anton", "anton", "安东"),
        Character("grace", "格莉丝 / Grace", "grace", "格莉丝"),
        Character("koleda", "珂蕾妲 / Koleda", "koleda", "珂蕾妲"),
        Character("ben", "本 / Ben", "ben", "本"),
        Character("caesar", "凯撒 / Caesar", "caesar", "凯撒"),
        Character("burnice", "柏妮思 / Burnice", "burnice", "柏妮思"),
        Character("lucy", "露西 / Lucy", "lucy", "露西"),
        Character("piper", "派派 / Piper", "piper", "派派"),
        Character("lighter", "莱特 / Lighter", "lighter", "莱特"),
        Character("yanagi", "柳 / Yanagi", "yanagi", "柳"),
        Character("miyabi", "星见雅 / Miyabi", "miyabi", "星见雅"),
        Character("harumasa", "浅羽悠真 / Harumasa", "harumasa", "浅羽悠真", "悠真"),
        Character("yixuan", "仪玄 / Yixuan", "yixuan", "仪玄"),
        Character("yuzuha", "橘福福 / Yuzuha", "yuzuha", "橘福福"),
        Character("evelyn", "伊芙琳 / Evelyn", "evelyn", "伊芙琳"),
        Character("hugo", "雨果 / Hugo", "hugo", "雨果")
    ];

    private static readonly CharacterGroupInfo FrameworkGroup =
        new("framework", "通用依赖 / 框架", CharacterGroupKind.Framework);

    private static readonly string[] DependencyTokens =
    [
        "rabbitfx", "modframework", "3dmigoto", "通用依赖"
    ];
    private static readonly string[] NonCharacterTokens =
    [
        "功能", "界面", "菜单", "快捷键", "法线", "修复", "插件", "依赖", "框架", "工具", "教程", "说明",
        "normalfix", "normalmap", "hotkey", "utility", "helper", "menu", "soundwave", "framework", "dependency",
        "ui", "3dmigoto", "misc", "scooter", "controller", "checkhash", "diffuse", "seed"
    ];

    private static readonly Regex ChineseLabelRegex = new(
        @"[\p{IsCJKUnifiedIdeographs}]{2,5}",
        RegexOptions.Compiled);
    private static readonly Regex TextureOverrideNameRegex = new(
        @"TextureOverride(?<name>[A-Za-z][A-Za-z0-9]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex AsciiWordRegex = new(
        @"[A-Za-z][A-Za-z0-9]{2,}",
        RegexOptions.Compiled);
    private static readonly string[] GenericChineseTokens =
    [
        "皮肤", "武器", "模型", "模式", "文件夹", "放入", "修改", "修复", "法线", "时间", "角色", "外观", "配饰",
        "功能", "界面", "菜单", "快捷键", "插件", "依赖", "框架", "工具", "教程", "说明", "通用", "启动", "加载", "切换",
        "头发", "身体", "手", "腿", "脚", "脸", "背", "阴影", "材质", "顶点", "位置", "槽位", "检查", "资源", "标记"
    ];
    private static readonly HashSet<string> GenericAsciiTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "disabled", "unmanaged", "mod", "mods", "skin", "outfit", "body", "face", "hair", "head",
        "weapon", "weapons", "fix", "normal", "map", "texture", "override", "resource", "commandlist",
        "rabbitfx", "uncensored", "updated", "misc", "soundwave", "demo", "toggle", "image", "draw",
        "main", "ini", "character", "model", "separate", "pack", "package", "school", "uniform",
        "fluffy", "nude", "half", "cyber", "mode", "time", "zzz", "zzmi", "xxmi", "ui", "utility", "helper",
        "hotkey", "menu", "soundwave", "feature", "guide", "framework", "dependency", "tool", "tools", "textureoverride",
        "checkhash", "diffuse", "scooter", "controller", "seed", "slot", "component", "position", "texcoord", "vertexlimitraise", "mark", "shadow"
    };

    public static IReadOnlyList<CharacterGroupInfo> KnownGroups { get; } =
        Characters.Select(item => item.Group).Append(FrameworkGroup).ToList();

    public static IReadOnlyList<CharacterGroupInfo> BuiltInCharacterGroups { get; } =
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

    private static CharacterDefinition Character(string key, string displayName, params string[] aliases) =>
        new(new CharacterGroupInfo(key, displayName, CharacterGroupKind.Character), aliases);

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
