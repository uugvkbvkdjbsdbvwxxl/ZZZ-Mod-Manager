# ZZZ Mod Manager · AI 协作指南

本文件面向在本仓库工作的 AI 编码代理与新加入的开发者，说明项目形态、必须遵守的边界和验证方式。产品行为说明见 [README.md](./README.md)，界面设计规范见 [.uicraft.md](./.uicraft.md)，两者优先级高于本文件中的概括描述。

## 项目概览

《绝区零》专用的本地 Mod 管理器，Windows x64 桌面应用。

- 技术栈：C# / .NET 10（`net10.0-windows`）+ WPF，`LangVersion=preview`，`Nullable=enable`，`ImplicitUsings=enable`。
- 唯一第三方依赖：`SharpCompress 0.50.0`（压缩包导入）。测试使用 `xunit 2.9.3`。
- 发布形态：单文件自包含 `win-x64` 可执行文件，ZZMI 1.4.3 运行核心以 `EmbeddedResource` 内嵌。
- **`TreatWarningsAsErrors=true` 在主项目与测试项目均已开启**，任何编译警告都会导致构建失败。

## 目录结构

```
src/ZZZModManager/
  Infrastructure/   路径、JSON 存储、文件系统安全、单实例协调
  Models/           领域模型、枚举、配置与清单（DomainModels.cs 集中定义）
  Services/         导入、校验、依赖解析、Mod 库、实时切换、运行核心与注入
  Themes/           DarkTheme.xaml（暗色主题与控件样式）
  MainWindow.*      主窗口按职责拆分为 xaml.cs / Cards.cs / Settings.cs
  Assets/           应用图标与内嵌 ZZMI 运行核心压缩包
tests/ZZZModManager.Tests/
  Fixtures/         导入测试用的 7z/rar 样本与链接进来的 XAML 副本
```

## 构建与测试

需要 .NET 10 SDK。先用 `dotnet --list-sdks` 确认：若输出为空（机器上只装了运行时），可在仓库内做局部安装，不污染系统环境（`.dotnet-sdk/` 与 `.dotnet-install.ps1` 已被 `.gitignore` 忽略）：

```powershell
Invoke-WebRequest -Uri "https://dot.net/v1/dotnet-install.ps1" -OutFile ".dotnet-install.ps1" -UseBasicParsing
& .\.dotnet-install.ps1 -Channel 10.0 -InstallDir "$PWD\.dotnet-sdk" -NoPath
$env:DOTNET_ROOT = "$PWD\.dotnet-sdk"; $env:PATH = "$PWD\.dotnet-sdk;$env:PATH"
```

若 `dotnet nuget list source` 显示"找不到任何源"，还原会以 `NU1100` 全量失败。此时在仓库根目录放一份 `NuGet.config` 显式声明 `nuget.org`，不要去改用户级 `%APPDATA%\NuGet\NuGet.Config`。

```powershell
dotnet restore .\ZZModManager.sln
dotnet build .\ZZModManager.sln -c Release --no-restore
dotnet test .\ZZModManager.sln -c Release --no-build
dotnet publish .\src\ZZZModManager\ZZZModManager.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o .\artifacts\win-x64
```

基线（.NET SDK 10.0.400）：构建 0 警告 0 错误，测试 103 个全部通过，发布产物为单文件 `ZZZModManager.exe`（约 141 MB，自包含）。改动后若数字低于此基线，属于回归。

仓库没有独立的 lint / typecheck 脚本，`dotnet build` 与 `dotnet test` 即为质量门；由于警告即错误，构建通过等同于静态检查通过。

## 代码约定

- 命名空间与目录一一对应：`ZZZModManager.Infrastructure` / `.Models` / `.Services`。
- 使用文件级命名空间声明（`namespace X;`），不使用大括号包裹形式。
- 服务默认 `public sealed class`，并配套 `public interface I<Name>`，通过构造函数注入依赖，不引入 DI 容器。
- 值对象优先使用 `sealed record` / `readonly record struct`；集合初始化使用 `[]` 目标类型化写法。
- 常用 `System.*` 命名空间已由 `GlobalUsings.cs` 全局引入，不要在文件顶部重复 `using System;` 等。
- 面向用户的异常消息与日志文本使用中文（与现有代码一致）；标识符、注释与提交信息使用英文。
- 注释只解释"为什么"，尤其是围绕 3DMigoto / ZZMI 行为的非显然约束。多处关键注释记录了规则版本演进（如 `LiveModSwitchService` 的 Rule v2→v10），修改相关逻辑时必须同步更新说明并递增 `RuleVersion`。
- 不要提交任何真实游戏路径、账号信息或用户本地数据；`.gitignore` 已排除 `Mods/`、`Logs/`、`config.json`、`library.json` 等运行期产物。

## 必须遵守的领域边界

这些约束来自游戏与 ZZMI 的实际行为，破坏它们会导致用户 Mod 状态错乱或数据丢失：

1. **只读原始文件**：程序在 `D:\ZZZMod` 下自建工作区，不得修改用户原始 Mod 目录（如 `D:\Mod\zzz_mods\XXMI-ZZZ`）。
2. **不静默删除用户文件**：受管 Mod 的启用/禁用通过 `DISABLED_` 目录前缀切换；库内未受管的活动目录用 `DISABLED_UNMANAGED_` 前缀原地隔离，禁止改为直接删除。只有用户显式发起的 `ModLibrary.Delete` 才会删除文件，且必须先经 `FileSystemSafety.IsWithin` 校验、经 `DISABLED_DELETING_` 中间目录，并在失败时回滚。
3. **绝对状态通道**：实时启用/禁用使用管理器自有的绝对状态命令（每个 Mod 一个全局 gate 变量），不得退化为"切换"语义，否则丢键会造成状态反转。
4. **F10 与安全重载是两套独立通道**：物理 F10 只用于隐藏 ZZMI 首次提示，不得用它同步 Mod 状态。安全重载使用 `ManagerGameBindings.ReloadChord`（`Ctrl+Shift+VK_F24`）。
5. **实时槽位上限 48**（`F13-F24`、`0-9`、`A-Z`），超出后必须降级为安全重载而非静默失败。
6. **静态顶点 Mod 需启动前预载**，同一 `hash` 的容量聚合行为遵循 XXMI 语义，不得由管理器擅自降级为 restart-only。
7. **路径与解压安全**：所有路径拼接必须经 `FileSystemSafety.IsWithin` 校验，解压受 `MaxExtractedBytes` / `MaxExtractedFiles` 限制，禁止绕过。
8. **不越界**：不新增联网更新、账号操作、反作弊绕过或额外 DLL 注入能力；不猜测游戏更新后的新哈希，也不生成缺失模型。

## UI 修改要求

改动 WPF 界面前先读 [.uicraft.md](./.uicraft.md)，其中的配色、间距、字号与状态表达规则是硬性约束。另外：

- 测试会断言 XAML 结构与自动化名称。`UiStructureTests` 检查 `MainWindow.xaml` 中的一批 `x:Name`（`HomePage`、`PrimaryActionButton`、`ModGroupsItemsControl`、`EmptyStateBorder` 等）必须存在，且角色筛选区使用 `WrapPanel` 而非横向 `ScrollViewer`；`ScrollBarStyleTests` 断言 `DarkTheme.xaml` 中 `ScrollBar` 隐式样式按方向给出 10px 尺寸。重命名或重构控件前先更新对应测试。
- 必须在 1280×820 与 1040×680 两个尺寸下都不出现裁剪、重叠或页面级横向滚动。
- 状态表达不能只靠颜色，需同时给出文字或形状标识。

## 测试约定

- 框架为 xUnit，测试类为 `public sealed class`，文件按主题划分（`CompatibilityTests`、`RealtimeSwitchTests`、`UiStructureTests`、`SingleInstanceTests`、`WindowCloseBehaviorTests`、`ScrollBarStyleTests`）。
- 涉及 WPF 对象的测试需在显式创建的 STA 线程内执行，并通过捕获异常再断言的方式回传失败（见 `ScrollBarStyleTests`）。
- 导入相关测试使用 `Fixtures/` 下的样本压缩包；新增样本需同时在 `.csproj` 中声明 `CopyToOutputDirectory="PreserveNewest"`。
- 修改实时切换、兼容修复或 INI 改写逻辑时，必须补充或更新 `RealtimeSwitchTests` / `CompatibilityTests` 中的断言。

## Git 与协作流程

- 分支：`main` 为稳定分支，`develop` 为日常开发分支。默认在 `develop` 上提交并推送，不直接改动 `main`。
- 提交信息使用英文、祈使句，主题行控制在 72 字符内，聚焦单一改动。
- 仅在用户明确要求时创建提交或推送；推送新分支使用 `git push -u`。
- 禁止在未获授权时执行破坏性操作：`git reset --hard`、`git push --force`、`git clean -f`、删除分支。
- 阶段性完成后再推送：一次推送应对应一个可自洽的改动集，并在推送前完成构建与测试（若环境缺少 SDK，需说明未验证的部分）。

## 许可证

应用源码为 GPLv3。SharpCompress、XXMI/ZZMI 与第三方 Mod 遵循各自上游许可证；RabbitFX 不作为内嵌资源重新分发。新增依赖前需确认许可证兼容并使用固定版本号。
