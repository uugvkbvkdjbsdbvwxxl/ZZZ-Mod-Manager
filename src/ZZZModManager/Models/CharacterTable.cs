namespace ZZZModManager.Models;

/// <summary>
/// The externalized character roster and token lists consumed by
/// CharacterGroupDetector. The table lives in the library root as
/// characters.json so new characters can be added without rebuilding the
/// manager. A missing file is seeded from <see cref="CreateBuiltIn"/>; a corrupt
/// or rosterless file falls back to the built-in table instead of silently
/// degrading detection to "未识别".
/// </summary>
public sealed class CharacterTable
{
    public int SchemaVersion { get; set; } = 1;
    public string? FrameworkKey { get; set; } = "framework";
    public string? FrameworkDisplayName { get; set; } = "通用依赖 / 框架";
    public List<CharacterTableEntry>? Characters { get; set; }
    public List<string>? DependencyTokens { get; set; }
    public List<string>? NonCharacterTokens { get; set; }
    public List<string>? GenericChineseTokens { get; set; }
    public List<string>? GenericAsciiTokens { get; set; }

    public static CharacterTable CreateBuiltIn() => new()
    {
        SchemaVersion = 1,
        FrameworkKey = "framework",
        FrameworkDisplayName = "通用依赖 / 框架",
        Characters = BuiltInCharacters(),
        DependencyTokens = ["rabbitfx", "modframework", "3dmigoto", "通用依赖"],
        NonCharacterTokens =
        [
            "功能", "界面", "菜单", "快捷键", "法线", "修复", "插件", "依赖", "框架", "工具", "教程", "说明",
            "normalfix", "normalmap", "hotkey", "utility", "helper", "menu", "soundwave", "framework", "dependency",
            "ui", "3dmigoto", "misc", "scooter", "controller", "checkhash", "diffuse", "seed"
        ],
        GenericChineseTokens =
        [
            "皮肤", "武器", "模型", "模式", "文件夹", "放入", "修改", "修复", "法线", "时间", "角色", "外观", "配饰",
            "功能", "界面", "菜单", "快捷键", "插件", "依赖", "框架", "工具", "教程", "说明", "通用", "启动", "加载", "切换",
            "头发", "身体", "手", "腿", "脚", "脸", "背", "阴影", "材质", "顶点", "位置", "槽位", "检查", "资源", "标记"
        ],
        GenericAsciiTokens =
        [
            "disabled", "unmanaged", "mod", "mods", "skin", "outfit", "body", "face", "hair", "head",
            "weapon", "weapons", "fix", "normal", "map", "texture", "override", "resource", "commandlist",
            "rabbitfx", "uncensored", "updated", "misc", "soundwave", "demo", "toggle", "image", "draw",
            "main", "ini", "character", "model", "separate", "pack", "package", "school", "uniform",
            "fluffy", "nude", "half", "cyber", "mode", "time", "zzz", "zzmi", "xxmi", "ui", "utility", "helper",
            "hotkey", "menu", "feature", "guide", "framework", "dependency", "tool", "tools", "textureoverride",
            "checkhash", "diffuse", "scooter", "controller", "seed", "slot", "component", "position", "texcoord",
            "vertexlimitraise", "mark", "shadow"
        ]
    };

    private static List<CharacterTableEntry> BuiltInCharacters() =>
    [
        new("velina", "维琳娜 / Velina", ["velina", "velinablackfan", "维琳娜"]),
        new("alice", "爱丽丝 / Alice", ["alice", "alicetops", "爱丽丝"]),
        new("nicole", "妮可 / Nicole", ["nicole", "妮可"]),
        new("anby", "安比 / Anby", ["anby", "安比"]),
        new("billy", "比利 / Billy", ["billy", "比利"]),
        new("nekomata", "猫又 / Nekomata", ["nekomata", "猫又"]),
        new("corin", "可琳 / Corin", ["corin", "可琳"]),
        new("lycaon", "莱卡恩 / Lycaon", ["lycaon", "莱卡恩"]),
        new("soukaku", "苍角 / Soukaku", ["soukaku", "苍角"]),
        new("zhuyuan", "朱鸢 / Zhu Yuan", ["zhu yuan", "zhuyuan", "朱鸢"]),
        new("qingyi", "青衣 / Qingyi", ["qingyi", "青衣"]),
        new("jane", "简 / Jane Doe", ["jane doe", "janedoe", "简"]),
        new("seth", "赛斯 / Seth", ["seth", "赛斯", "席德", "席德流萤", "老席德"]),
        new("soldier11", "11号 / Soldier 11", ["soldier 11", "soldier11", "11号"]),
        new("rina", "丽娜 / Rina", ["rina", "丽娜"]),
        new("anton", "安东 / Anton", ["anton", "安东"]),
        new("grace", "格莉丝 / Grace", ["grace", "格莉丝"]),
        new("koleda", "珂蕾妲 / Koleda", ["koleda", "珂蕾妲"]),
        new("ben", "本 / Ben", ["ben", "本"]),
        new("caesar", "凯撒 / Caesar", ["caesar", "凯撒"]),
        new("burnice", "柏妮思 / Burnice", ["burnice", "柏妮思"]),
        new("lucy", "露西 / Lucy", ["lucy", "露西"]),
        new("piper", "派派 / Piper", ["piper", "派派"]),
        new("lighter", "莱特 / Lighter", ["lighter", "莱特"]),
        new("yanagi", "柳 / Yanagi", ["yanagi", "柳"]),
        new("miyabi", "星见雅 / Miyabi", ["miyabi", "星见雅"]),
        new("harumasa", "浅羽悠真 / Harumasa", ["harumasa", "浅羽悠真", "悠真"]),
        new("yixuan", "仪玄 / Yixuan", ["yixuan", "仪玄"]),
        new("yuzuha", "橘福福 / Yuzuha", ["yuzuha", "橘福福"]),
        new("evelyn", "伊芙琳 / Evelyn", ["evelyn", "伊芙琳"]),
        new("hugo", "雨果 / Hugo", ["hugo", "雨果"])
    ];
}

public sealed record CharacterTableEntry(string Key, string DisplayName, List<string> Aliases)
{
    public CharacterTableEntry() : this(string.Empty, string.Empty, [])
    {
    }
}
