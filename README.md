# 绝区零本地 Mod 管理器

当前版本：**0.2.2**。仅支持《绝区零》的 Windows x64 WPF Mod 管理器。程序、运行核心、Mod、配置、日志和备份统一保存在 `D:\ZZZMod`，不会修改 `D:\Mod\zzz_mods\XXMI-ZZZ` 中的原始文件。

### 游戏原始头脸预览

当 Mod 只替换身体而没有脸部/头发缓冲时，3D 预览可从游戏的 FrameAnalysis 转储中筛选对应角色的原始头脸，并按游戏版本缓存到 `Cache\BaseCharacters`。目前首个内置配置为 Remielle · Moonlight Whispers。

1. 在识别到该角色的 3D 预览窗口中点击“1. 准备安全采集”。程序会先备份 `d3dx.ini`，启用 3Dmigoto FrameAnalysis 上下文，再将 F8 分析限制为纹理、顶点缓冲和索引缓冲，不采集渲染目标或常量缓冲。
2. 完全退出并重新启动游戏。3Dmigoto 在设备创建时选择 FrameAnalysis 上下文，因此快捷键重载配置无效。让对应角色单独出现在画面中，按 `F8`，等待分析提示消失。
3. 回到预览窗口点击“2. 导入最新 F8 转储”。缓存只保存在管理器目录，不写入 Mod 或游戏文件。

## 主要功能

- 首页以角色分组展示 Mod 卡片；同一角色默认单选，RabbitFX 等通用依赖不参与角色单选。
- 识别 Mod 根目录中的 `preview.png`，提供限宽缩略图、点击放大、缩放与 Esc 关闭。
- 对包含 XXMI `Position.buf` / `Texcoord.buf` / `.ib` 的 Mod 提供离线 3D 静态预览；支持外观变量、损坏部件隔离、LRU 缓存、兼容诊断和 PNG 导出。
- 3D 预览可读取 INI 绑定的 DDS Diffuse、NormalMap、LightMap 与 MaterialMap，支持 BC1–BC7（含 BC6H）；内置可选 CPU Shader、法线光照和贴图轮廓增强。实验性增强默认关闭，打开预览仍优先保持原始 Diffuse 效果。
- 从用户选择的本地 ZIP、7Z、RAR 或文件夹更新已有 Mod；确认前显示逐文件新增、修改、删除差异，更新前自动创建完整版本备份，并可从历史记录回滚。
- ZIP、7Z、RAR 和文件夹导入，支持多层包装目录、多个候选 Mod、路径穿越防护、文件数与解压大小限制。
- 扫描 INI 引用、资源声明、哈希和 RabbitFX 依赖，只对安装副本应用可确定的兼容修复并保存 `import-report.json`。
- 运行中使用管理器内部绝对状态通道即时启用/禁用；首次接管、导入或目录结构变化时才按需发送一次管理器安全重载命令。
- 普通 F10 只用于隐藏 ZZMI 首次帮助提示，和管理器安全重载是两套独立通道；物理 F10 不会绕过管理器状态同步。静态顶点 Mod 会在管理器启动游戏前完成预加载；游戏运行时才出现尚未预载的静态 Mod 时，需先关闭游戏并由管理器重新启动完成准备。
- 同角色或哈希冲突切换会先物理隔离旧目录，再自动发送一次安全重载，无需重启游戏；无法安全门控或实时槽位已用尽时执行安全重载。
- 启用角色 Mod 时自动禁用同角色其他 Mod，并继续执行跨角色哈希冲突检测。
- 检测会绕过清单的活动源目录，使用 `DISABLED_UNMANAGED_` 前缀原地安全隔离，不删除文件。
- 固定 ZZMI 1.4.3 运行核心内嵌在单文件程序中；缺失或哈希损坏时可从设置页离线恢复。
- 设置页集中放置游戏路径、核心修复、Mod 导入、背景图和行为选项；日志页支持搜索、复制和打开目录。

## 快捷键与切换规则

- `Alt+W`：显示或隐藏管理器。
- 游戏内首次 ZZMI 帮助提示：按 `F10` 隐藏提示；该按键不会执行重载或改变 Mod 状态。
- `安全刷新 Mod`：由管理器刷新全部可重载 Mod；启动预加载的静态顶点 Mod 会保留在加载树中，手动刷新不会反转启用状态。
- 前 48 个可安全门控的 Mod 分配唯一内部通道：`F13-F24`、`0-9`、`A-Z`。这些按键由管理器自动模拟，用户无需寻找或按下 F20、F21 等物理按键。
- 管理器启动游戏前会预载所有静态 Mod；XXMI 对同一 `hash` 按最大的 `override_byte_width`（`override_byte_stride × override_vertex_count`；缺少显式 count 时使用 XXMI fallback）处理。普通切换发送绝对状态命令；同角色或同 hash 切换可能追加一次管理器安全刷新，但无需重启游戏。
- 唯一需要关闭并重新启动一次的正常情况，是游戏已在运行时才出现尚未预载的新静态 Mod，或规则升级后尚未完成启动前准备；下次由管理器启动后即可实时切换。无法安全门控或实时槽位已用尽时仅执行安全重载。
- 设置页可选择点击窗口关闭按钮时直接退出，或隐藏到后台并使用 `Alt+W` 恢复；隐藏模式下可从设置页点击“退出管理器”。

## 目录

- `D:\ZZZMod\Mods`：受管 Mod 库。
- `D:\ZZZMod\Runtime\ZZMI`：离线恢复后的 ZZMI 核心。
- `D:\ZZZMod\Logs`：统一日志。
- `D:\ZZZMod\Backups`：运行核心与配置备份；Mod 版本位于 `Backups\Mods\<Mod ID>`。
- `D:\ZZZMod\UI`：自定义背景图。
- `D:\ZZZMod\artifacts\win-x64`：Windows x64 单文件发布产物。

## 编译与测试

需要 .NET 10 SDK：

```powershell
dotnet restore .\ZZModManager.sln
dotnet test .\ZZModManager.sln -c Release
dotnet publish .\src\ZZZModManager\ZZZModManager.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o .\artifacts\win-x64
```

运行 `artifacts\win-x64\ZZZModManager.exe`。首次启动选择 `ZenlessZoneZero.exe`；若核心异常，在设置页点击“离线修复运行核心”。

## 安全边界

程序只处理本地文件、运行核心配置和用户发起的游戏启动；Mod 更新来源也仅限用户手动选择的本地归档或文件夹。程序不包含联网更新、账号操作、反作弊绕过或额外 DLL 注入。游戏更新可能使作者提供的哈希失效；管理器不会猜测新哈希或生成缺失模型。

## 许可证

应用源码使用 GPLv3。SharpCompress、BCnEncoder.NET、XXMI/ZZMI 和第三方 Mod 仍受各自上游许可证约束；RabbitFX 不作为程序内嵌资源重新分发。BC6H 解码使用 BCnEncoder.NET 2.3.0；BC7 解码部分基于 bc7enc / GARbro 的 MIT 实现，源文件中保留其版权与许可声明。
