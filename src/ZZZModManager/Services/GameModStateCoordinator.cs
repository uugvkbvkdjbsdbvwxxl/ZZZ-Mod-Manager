using ZZZModManager.Models;

namespace ZZZModManager.Services;

public interface IGameModStateCoordinator
{
    bool IsGameRunning { get; }
    int? CurrentGameProcessId { get; }
    void MarkControlFilesChanged();
    void PrepareForLaunch();
    ModStateChangeResult ApplyState(string modId, bool enabled, bool restoreManagerWindow, bool allowReload = true);
    ModStateChangeResult ApplyStates(IEnumerable<ModStateRequest> requests, bool restoreManagerWindow, bool allowReload = true);
    ModStateChangeResult ReloadAndSynchronize(bool restoreManagerWindow);
}

/// <summary>
/// Separates desired disk state from the state currently loaded by one game PID.
/// Normal changes use absolute manager keys. The manager-only reload chord is
/// reserved for first attach, include-tree changes and mods that cannot be gated.
/// </summary>
public sealed class GameModStateCoordinator : IGameModStateCoordinator
{
    private readonly IModLibrary _library;
    private readonly ILiveModSwitchService _liveSwitch;
    private readonly IGameModReloadService _gameInput;
    private readonly Func<string?> _gamePath;
    private readonly IAppLogger _logger;
    private int? _sessionPid;
    private bool _sessionSynchronized;
    private bool _controlFilesChanged;
    private bool _launchPrepared;

    public GameModStateCoordinator(
        IModLibrary library,
        ILiveModSwitchService liveSwitch,
        IGameModReloadService gameInput,
        Func<string?> gamePath,
        IAppLogger logger)
    {
        _library = library;
        _liveSwitch = liveSwitch;
        _gameInput = gameInput;
        _gamePath = gamePath;
        _logger = logger;
    }

    public bool IsGameRunning => RefreshSession() is not null;

    public int? CurrentGameProcessId => RefreshSession();

    public void MarkControlFilesChanged()
    {
        _controlFilesChanged = true;
        _sessionSynchronized = false;
    }

    public void PrepareForLaunch()
    {
        if (RefreshSession() is not null)
        {
            throw new InvalidOperationException("游戏正在运行，无法整理启动状态。");
        }

        // Normalize the physical include tree first, then promote static vertex
        // mods back into it with their desired state still disabled. ZZMI must
        // see their vertex capacity during process initialization; the manager
        // variable controls whether replacement actions run.
        _library.NormalizeDisabledDirectories();
        var preparation = _liveSwitch.PrepareForStartup(_library.GetAll());
        var preloadIds = _library.GetAll()
            .Where(manifest => manifest.LiveSwitchCapability == LiveSwitchCapability.Immediate
                               && _liveSwitch.RequiresStartupPreload(manifest))
            .Select(manifest => manifest.Id)
            .ToList();
        var preloaded = _library.PreloadForLiveSwitch(preloadIds);
        foreach (var manifest in _library.GetAll())
        {
            _liveSwitch.SetDefault(manifest, manifest.Enabled);
        }
        _library.SaveChanges();
        _controlFilesChanged |= preparation.ControlFilesChanged || preloaded;
        _launchPrepared = true;
    }

    public ModStateChangeResult ApplyState(
        string modId,
        bool enabled,
        bool restoreManagerWindow,
        bool allowReload = true) =>
        Apply(
            [new ModStateRequest(modId, enabled)],
            enabled ? "启用" : "禁用",
            restoreManagerWindow,
            allowReload);

    /// <summary>
    /// Applies a whole selection at once so a multi-select batch or a preset causes
    /// a single safe reload instead of one reload per mod.
    /// </summary>
    public ModStateChangeResult ApplyStates(
        IEnumerable<ModStateRequest> requests,
        bool restoreManagerWindow,
        bool allowReload = true) =>
        Apply(requests.ToList(), "批量切换", restoreManagerWindow, allowReload);

    private ModStateChangeResult Apply(
        IReadOnlyList<ModStateRequest> requests,
        string actionLabel,
        bool restoreManagerWindow,
        bool allowReload)
    {
        var pid = RefreshSession();
        var gameRunning = pid is not null;
        ModLibraryBatchResult batch;
        try
        {
            batch = _library.ApplyStateBatch(requests, keepLoaded: gameRunning);
            foreach (var manifest in batch.ChangedMods)
            {
                _liveSwitch.SetDefault(manifest, manifest.Enabled);
            }

            _library.SaveChanges();
            if (!gameRunning)
            {
                _library.NormalizeDisabledDirectories();
            }
        }
        catch (Exception ex)
        {
            _logger.Error($"切换 Mod 失败：{ex.Message}");
            return new ModStateChangeResult
            {
                Application = ModStateApplication.Failed,
                GameRunning = gameRunning,
                Message = ex.Message
            };
        }

        var automaticallyDisabled = batch.DisabledByCharacter
            .Concat(batch.DisabledByConflict)
            .DistinctBy(manifest => manifest.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var requiresConflictReload = batch.DisabledByCharacter.Count > 0
                                     || batch.DisabledByConflict.Count > 0;
        var suffix = automaticallyDisabled.Count == 0
            ? string.Empty
            : $"；同时禁用：{string.Join("、", automaticallyDisabled.Select(manifest => manifest.DisplayName))}";

        if (!gameRunning)
        {
            var message = $"已保存{actionLabel}状态，下次启动游戏时生效{suffix}。";
            _logger.Info(message);
            return new ModStateChangeResult
            {
                Application = ModStateApplication.Pending,
                DesiredStateSaved = true,
                GameRunning = false,
                Message = message,
                ChangedMods = batch.ChangedMods,
                AutomaticallyDisabled = automaticallyDisabled
            };
        }

        var restartOnly = batch.ChangedMods
            .Where(manifest => manifest.LiveSwitchCapability == LiveSwitchCapability.RequiresRestart)
            .ToList();
        if (restartOnly.Count > 0)
        {
            var pending = $"状态已保存；{string.Join("、", restartOnly.Select(manifest => manifest.DisplayName))} 使用静态顶点限制，必须重启游戏应用状态{suffix}。";
            _logger.Warning(pending);
            return Pending(batch, automaticallyDisabled, pending);
        }

        var hasLoadedRestartOnly = HasLoadedRestartOnlyMods();
        // ApplyStateBatch keeps an automatically disabled directory loaded while
        // the game is running so an ordinary live toggle can use an absolute
        // manager variable.  That is unsafe when the target also disables a
        // same-character or hash-conflicting Mod: both match selectors would
        // remain in ZZMI's include tree and the newly enabled variant could be
        // shadowed.  Force the existing safe reload path so it physically
        // isolates the old copy before ZZMI reparses the tree.
        var needsReload = requiresConflictReload
                          || (!_sessionSynchronized && !hasLoadedRestartOnly)
                          || _controlFilesChanged
                          || batch.IncludeTreeChanged
                          || batch.ChangedMods.Any(manifest => manifest.LiveSwitchCapability != LiveSwitchCapability.Immediate);
        if (needsReload)
        {
            if (!allowReload)
            {
                var pending = $"状态已保存，需要执行安全重载才能应用{suffix}。";
                _logger.Warning(pending);
                return Pending(batch, automaticallyDisabled, pending);
            }

            return ReloadForBatch(batch, automaticallyDisabled, restoreManagerWindow, suffix);
        }

        var immediate = SendAbsoluteStates(batch.ChangedMods, restoreManagerWindow);
        if (immediate.Succeeded)
        {
            var message = $"{actionLabel}命令已发送，状态已保存；管理器无法读取游戏渲染结果，未执行安全重载{suffix}。";
            _logger.Info(message);
            return new ModStateChangeResult
            {
                Application = ModStateApplication.Immediate,
                DesiredStateSaved = true,
                GameRunning = true,
                Message = message,
                ChangedMods = batch.ChangedMods,
                AutomaticallyDisabled = automaticallyDisabled
            };
        }

        _logger.Warning($"实时命令失败，尝试一次安全重载：{immediate.Message}");
        if (allowReload)
        {
            return ReloadForBatch(batch, automaticallyDisabled, restoreManagerWindow, suffix);
        }

        return Pending(batch, automaticallyDisabled, $"实时命令失败，状态已保存：{immediate.Message}");
    }

    public ModStateChangeResult ReloadAndSynchronize(bool restoreManagerWindow)
    {
        var pid = RefreshSession();
        if (pid is null)
        {
            return new ModStateChangeResult
            {
                Application = ModStateApplication.Pending,
                DesiredStateSaved = true,
                Message = "游戏未运行；当前状态会在下次启动时加载。"
            };
        }

        if (HasLoadedRestartOnlyMods())
        {
            const string restartMessage = "当前已加载的 Mod 使用静态顶点限制；底层重载会造成模型加载不完整，请重启游戏应用状态。";
            _logger.Warning(restartMessage);
            return new ModStateChangeResult
            {
                Application = ModStateApplication.Pending,
                DesiredStateSaved = true,
                GameRunning = true,
                Message = restartMessage
            };
        }

        try
        {
            // A full reload reparses the complete include tree. Remove every disabled mod
            // before that reparse so duplicate hashes cannot participate in
            // TextureOverride matching with a false runtime gate.
            _library.NormalizeDisabledDirectories();
            PreloadEligibleStaticMods();
            foreach (var manifest in _library.GetAll())
            {
                _liveSwitch.SetDefault(manifest, manifest.Enabled);
            }
        }
        catch (Exception ex)
        {
            _logger.Error($"写入实时默认状态失败：{ex.Message}");
            return new ModStateChangeResult
            {
                Application = ModStateApplication.Failed,
                DesiredStateSaved = true,
                GameRunning = true,
                Message = ex.Message
            };
        }

        var reload = SendReload(restoreManagerWindow: false);
        if (!reload.Succeeded)
        {
            _logger.Error(reload.Message);
            return new ModStateChangeResult
            {
                Application = ModStateApplication.Pending,
                DesiredStateSaved = true,
                GameRunning = true,
                Message = reload.Message
            };
        }

        _sessionSynchronized = true;
        _controlFilesChanged = false;
        var active = GetLoadedManifests();
        var absolute = SendAbsoluteStates(active, restoreManagerWindow);
        var message = absolute.Succeeded
            ? "安全重载命令已发送，全部绝对状态命令已发送，状态已保存；管理器无法读取游戏渲染结果。"
            : $"安全重载命令已发送，状态已保存；补充状态命令发送失败：{absolute.Message}";
        _logger.Info(message);
        return new ModStateChangeResult
        {
            Application = ModStateApplication.Reloaded,
            DesiredStateSaved = true,
            GameRunning = true,
            Message = message,
            ChangedMods = active
        };
    }

    private ModStateChangeResult ReloadForBatch(
        ModLibraryBatchResult batch,
        IReadOnlyList<ModManifest> automaticallyDisabled,
        bool restoreManagerWindow,
        string suffix)
    {
        if (HasLoadedRestartOnlyMods())
        {
            const string message = "状态已保存；当前已加载的 Mod 使用静态顶点限制，已阻止底层重载以避免模型不完整，请重启游戏。";
            _logger.Warning(message);
            return Pending(batch, automaticallyDisabled, message + suffix);
        }

        try
        {
            // A same-character switch can leave the old mod loaded for immediate
            // gating. Once a reload is required, physically isolate all disabled
            // copies first; otherwise identical match_* selectors remain active.
            _library.NormalizeDisabledDirectories();
            PreloadEligibleStaticMods();
        }
        catch (Exception ex)
        {
            var message = $"状态已保存，但整理禁用 Mod 目录失败：{ex.Message}";
            _logger.Error(message);
            return Pending(batch, automaticallyDisabled, message);
        }

        var reload = SendReload(restoreManagerWindow: false);
        if (!reload.Succeeded)
        {
            var message = $"状态已保存，但安全重载发送失败：{reload.Message}";
            _logger.Error(message);
            return Pending(batch, automaticallyDisabled, message);
        }

        _sessionSynchronized = true;
        _controlFilesChanged = false;
        var absolute = SendAbsoluteStates(GetLoadedManifests(), restoreManagerWindow);
        var resultMessage = absolute.Succeeded
            ? $"安全重载命令和状态恢复命令已发送，状态已保存；管理器无法读取游戏渲染结果{suffix}。"
            : $"安全重载命令已发送，状态已保存；补充状态命令发送失败：{absolute.Message}{suffix}。";
        _logger.Info(resultMessage);
        return new ModStateChangeResult
        {
            Application = ModStateApplication.Reloaded,
            DesiredStateSaved = true,
            GameRunning = true,
            Message = resultMessage,
            ChangedMods = batch.ChangedMods,
            AutomaticallyDisabled = automaticallyDisabled
        };
    }

    private ModReloadResult SendAbsoluteStates(IReadOnlyList<ModManifest> manifests, bool restoreManagerWindow)
    {
        var eligible = manifests
            .Where(manifest => manifest.LiveSwitchCapability == LiveSwitchCapability.Immediate
                               && manifest.LiveSwitchSlot is not null
                               && IsInIncludeTree(manifest))
            .ToList();
        if (eligible.Count == 0)
        {
            return new ModReloadResult { GameRunning = true, Succeeded = true, Message = "无需补充实时命令。" };
        }

        for (var index = 0; index < eligible.Count; index++)
        {
            var manifest = eligible[index];
            var restore = restoreManagerWindow && index == eligible.Count - 1;
            var result = _gameInput.SendKey(_gamePath(), _liveSwitch.GetStateChord(manifest, manifest.Enabled), restore);
            if (!result.Succeeded)
            {
                return result;
            }
        }

        return new ModReloadResult { GameRunning = true, Succeeded = true, Message = "绝对状态命令已发送。" };
    }

    private ModReloadResult SendReload(bool restoreManagerWindow) =>
        _gameInput.SendKey(_gamePath(), ManagerGameBindings.ReloadChord, restoreManagerWindow);

    private IReadOnlyList<ModManifest> GetLoadedManifests() =>
        _library.GetAll().Where(IsInIncludeTree).ToList();

    private bool PreloadEligibleStaticMods()
    {
        var ids = _library.GetAll()
            .Where(manifest => manifest.LiveSwitchCapability == LiveSwitchCapability.Immediate
                               && _liveSwitch.RequiresStartupPreload(manifest))
            .Select(manifest => manifest.Id)
            .ToList();
        return _library.PreloadForLiveSwitch(ids);
    }

    private bool HasLoadedRestartOnlyMods() =>
        GetLoadedManifests().Any(manifest => manifest.LiveSwitchCapability == LiveSwitchCapability.RequiresRestart);

    private static bool IsInIncludeTree(ModManifest manifest) =>
        !Path.GetFileName(manifest.InstalledDirectory).StartsWith("DISABLED_", StringComparison.OrdinalIgnoreCase);

    private int? RefreshSession()
    {
        var pid = _gameInput.GetGameProcessId(_gamePath());
        if (pid == _sessionPid)
        {
            return pid;
        }

        var previousPid = _sessionPid;
        _sessionPid = pid;
        if (pid is not null && previousPid is null && _launchPrepared)
        {
            _sessionSynchronized = true;
            _controlFilesChanged = false;
            _launchPrepared = false;
        }
        else
        {
            _sessionSynchronized = false;
        }

        return pid;
    }

    private static ModStateChangeResult Pending(
        ModLibraryBatchResult batch,
        IReadOnlyList<ModManifest> automaticallyDisabled,
        string message) =>
        new()
        {
            Application = ModStateApplication.Pending,
            DesiredStateSaved = true,
            GameRunning = true,
            Message = message,
            ChangedMods = batch.ChangedMods,
            AutomaticallyDisabled = automaticallyDisabled
        };
}
