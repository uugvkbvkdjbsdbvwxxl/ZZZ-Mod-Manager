using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using ZZZModManager.Infrastructure;
using ZZZModManager.Models;
using ZZZModManager.Services;
using ZZZModManager.Themes;

namespace ZZZModManager;

public partial class MainWindow : Window
{
    private static readonly TimeSpan RuntimeValidationRefreshInterval = TimeSpan.FromSeconds(30);
    private const int ToggleManagerHotkeyId = 0x5A57;
    private const uint WmHotkey = 0x0312;
    private const uint ModAlt = 0x0001;
    private const uint ModNoRepeat = 0x4000;
    private const uint VirtualKeyW = 0x57;
    private const byte VirtualKeyMenu = 0x12;
    private const uint KeyEventKeyUp = 0x0002;
    private const int ShowNormal = 1;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;
    private static readonly IntPtr HwndTopmost = new(-1);
    private static readonly IntPtr HwndMessage = new(-3);
    private static readonly TimeSpan HotkeyDebounceInterval = TimeSpan.FromMilliseconds(350);
    private static readonly TimeSpan HotkeyLatchTimeout = TimeSpan.FromSeconds(1);

    private readonly AppPaths _paths = new();
    private readonly JsonFileStore _store = new();
    private readonly AppConfig _config;
    private readonly ModImporter _importer;
    private readonly ModValidator _validator;
    private readonly RuntimeManager _runtime;
    private readonly ModLibrary _library;
    private readonly ModPresetStore _presets;
    private readonly DependencyResolver _dependencyResolver;
    private readonly IGameModReloadService _gameInput;
    private readonly LiveModSwitchService _liveSwitch;
    private readonly GameModStateCoordinator _stateCoordinator;
    private readonly LaunchService _launcher;
    private readonly AppLogger _logger;
    private readonly ObservableCollection<ModGroupViewModel> _visibleGroups = [];
    private readonly ObservableCollection<CharacterFilterItem> _characterFilters = [];
    private readonly ObservableCollection<ModPreset> _presetItems = [];
    private readonly ObservableCollection<LogRow> _visibleLogs = [];
    private readonly Dictionary<string, RuntimeCardState> _runtimeStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(4) };
    private readonly DispatcherTimer _hotkeyReleaseTimer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private readonly DispatcherTimer _foregroundRecoveryTimer = new() { Interval = TimeSpan.FromMilliseconds(80) };
    private HwndSource? _hotkeySource;
    private IntPtr _hotkeyHandle;
    private IntPtr _windowHandle;
    private bool _hotkeyRegistered;
    private bool _hotkeyChordLatched;
    private bool _pendingHotkeyHide;
    private long _lastHotkeyToggleTimestamp;
    private bool _busy;
    private bool _managerOverlayVisible;
    private bool _keepManagerInForegroundOverGame;
    private bool _runtimeValidationInProgress;
    private bool _runtimeValidationQueued;
    private int _runtimeValidationGeneration;
    private RuntimeValidation? _cachedRuntimeValidation;
    private DateTimeOffset _lastRuntimeValidationUtc = DateTimeOffset.MinValue;
    private int? _cachedGameProcessId;
    private bool _gameProcessProbeInProgress;
    private bool _gameProcessProbeCompleted;
    private string _selectedCharacterKey = "all";
    private bool _characterFiltersExpanded;

    // 卡片视图模型每次 RefreshView 都会重建，所以多选状态只能挂在窗口上，
    // 并且以 Mod Id 为键，才能跨刷新、跨筛选保留用户已经勾选的目标。
    private bool _multiSelectMode;
    private readonly HashSet<string> _selectedModIds = new(StringComparer.OrdinalIgnoreCase);
    // "全选当前结果"只应覆盖筛选后仍然可见的卡片，所以每次刷新都记下这批 Id。
    private readonly List<string> _visibleModIds = [];
    private int? _lastObservedGameProcessId;
    private bool _splitPackageRepairDeferred;
    private bool _splitPackageRepairInProgress;
    private bool _splitPackageRepairCompleted;
    private bool _splitPackageRepairNoticeShown;
    private bool _allowWindowClose;
    private AppearanceController? _appearance;
    private bool _appearanceHydrated;

    public MainWindow()
    {
        InitializeComponent();
        _paths.Ensure();
        CharacterGroupDetector.Configure(_paths, _store);
        _config = _store.Load(_paths.ConfigFile, () => new AppConfig());
        _config.SchemaVersion = 3;
        _logger = new AppLogger(_paths);
        _importer = new ModImporter(_paths);
        _validator = new ModValidator(_paths);
        _runtime = new RuntimeManager(_paths, _store);
        _library = new ModLibrary(_paths, _store, new ConflictDetector());
        _presets = new ModPresetStore(_paths, _store);
        _dependencyResolver = new DependencyResolver(_paths);
        _gameInput = new GameModReloadService();
        IReadOnlyList<UnmanagedDirectoryChange> quarantined = [];
        if (!_gameInput.IsGameRunning(_config.GameExecutablePath))
        {
            _library.NormalizeDisabledDirectories();
            quarantined = _library.QuarantineActiveUnmanagedDirectories();
        }

        _liveSwitch = new LiveModSwitchService(_paths);
        var preparation = _liveSwitch.PrepareAll(_library.GetAll());
        _library.SaveChanges();
        _stateCoordinator = new GameModStateCoordinator(
            _library,
            _liveSwitch,
            _gameInput,
            () => _config.GameExecutablePath,
            _logger);
        if (preparation.ControlFilesChanged)
        {
            _stateCoordinator.MarkControlFilesChanged();
            _logger.Info($"实时控制已升级到规则 v{LiveModSwitchService.RuleVersion}：{preparation.ImmediateCount} 个可立即切换。");
        }

        if (quarantined.Count > 0)
        {
            _stateCoordinator.MarkControlFilesChanged();
            LogQuarantinedDirectories(quarantined);
        }

        _launcher = new LaunchService(_paths, _runtime, new GameSettingsManager());
        ModGroupsItemsControl.ItemsSource = _visibleGroups;
        CharacterFiltersItemsControl.ItemsSource = _characterFilters;
        PresetCombo.ItemsSource = _presetItems;
        RefreshPresets();
        LogListBox.ItemsSource = _visibleLogs;
        GamePathBox.Text = _config.GameExecutablePath ?? string.Empty;
        ModRootBox.Text = ModRootPointer.Resolve();
        UpdateModRootHint(ModRootBox.Text);
        AutoHideCheckBox.IsChecked = _config.AutoHideAfterLiveSwitch;
        ReloadRequiredCheckBox.IsChecked = _config.ReloadWhenRequired;
        CloseBehaviorComboBox.SelectedValue = _config.CloseBehavior.ToString();
        HydrateAppearance();
        LoadBackground();

        _statusTimer.Tick += (_, _) => UpdateRuntimeAndGameStatus();
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            ToastBorder.Visibility = Visibility.Collapsed;
        };
        _hotkeyReleaseTimer.Tick += (_, _) => ReleaseHotkeyLatchWhenKeysAreUp();
        _foregroundRecoveryTimer.Tick += (_, _) =>
        {
            _foregroundRecoveryTimer.Stop();
            if (_managerOverlayVisible
                && _keepManagerInForegroundOverGame
                && IsGameForeground())
            {
                EnsureManagerForeground();
            }
        };
        Deactivated += (_, _) => QueueForegroundRecoveryIfGameReclaimedFocus();
        Loaded += (_, _) =>
        {
            RefreshView();
            RefreshLogs();
            UpdateRuntimeAndGameStatus(forceRuntimeValidation: true);
            _ = RepairSplitPackagesIfSafeAsync();
            _statusTimer.Start();
        };
        SourceInitialized += (_, _) => RegisterToggleHotkey();
        Closing += MainWindow_Closing;
        Closed += (_, _) =>
        {
            _statusTimer.Stop();
            _toastTimer.Stop();
            _hotkeyReleaseTimer.Stop();
            _foregroundRecoveryTimer.Stop();
            UnregisterToggleHotkey();
        };
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (!WindowCloseBehaviorPolicy.ShouldHideOnClose(_config.CloseBehavior, _allowWindowClose))
        {
            return;
        }

        e.Cancel = true;
        HideManagerWindow();
        _logger.Info("已按设置隐藏管理器到后台，可用 Alt+W 恢复；需要退出时请使用设置页的“退出管理器”。");
    }

    private void Navigation_Click(object sender, RoutedEventArgs e)
    {
        if (HomePage is null || sender is not RadioButton { Tag: string page })
        {
            return;
        }

        HomePage.Visibility = page == "Home" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = page == "Settings" ? Visibility.Visible : Visibility.Collapsed;
        LogsPage.Visibility = page == "Logs" ? Visibility.Visible : Visibility.Collapsed;
        if (page == "Logs")
        {
            RefreshLogs();
        }
    }

    private void NavigateTo(string page)
    {
        var button = FindVisualChildren<RadioButton>(this)
            .FirstOrDefault(item => string.Equals(item.Tag as string, page, StringComparison.Ordinal));
        if (button is not null)
        {
            button.IsChecked = true;
        }
    }

    private async void PrimaryAction_Click(object sender, RoutedEventArgs e)
    {
        if (_stateCoordinator.IsGameRunning)
        {
            var focus = _gameInput.ActivateGame(_config.GameExecutablePath);
            ShowToast(focus.Message, !focus.Succeeded);
            if (focus.Succeeded)
            {
                HideManagerWindow();
            }

            return;
        }

        _config.GameExecutablePath = GamePathBox.Text.Trim();
        SaveConfig();
        var validation = _runtime.Validate();
        if (!validation.IsValid)
        {
            NavigateTo("Settings");
            ShowToast(validation.Message, true);
            return;
        }

        SetBusy(true, "正在启动并等待 ZZMI 注入…");
        try
        {
            _stateCoordinator.PrepareForLaunch();
            var message = await _launcher.LaunchAsync(_config);
            Log(message);
            ShowToast(message);
        }
        catch (Exception ex)
        {
            ShowError("启动失败", ex);
        }
        finally
        {
            SetBusy(false);
            UpdateRuntimeAndGameStatus();
        }
    }

    private async void ManualReload_Click(object sender, RoutedEventArgs e)
    {
        SetBusy(true, "正在执行安全重载并恢复已保存状态…");
        var result = await Task.Run(() => _stateCoordinator.ReloadAndSynchronize(
            restoreManagerWindow: !_config.AutoHideAfterLiveSwitch));
        ApplyRuntimeResult(result);
        SetBusy(false);
        ShowToast(result.Message, result.Application is ModStateApplication.Failed or ModStateApplication.Pending);
        RefreshView();
        if (result.Succeeded && _config.AutoHideAfterLiveSwitch)
        {
            HideManagerWindow();
        }
    }

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        var quarantined = _library.QuarantineActiveUnmanagedDirectories();
        if (quarantined.Count > 0)
        {
            _stateCoordinator.MarkControlFilesChanged();
            LogQuarantinedDirectories(quarantined);
        }

        RefreshView();
        UpdateRuntimeAndGameStatus(forceRuntimeValidation: true);
        ShowToast(quarantined.Count == 0
            ? "Mod 库已刷新。"
            : $"已安全禁用 {quarantined.Count} 个未受管源目录；游戏运行中请执行一次安全刷新。");
    }

    private void LogQuarantinedDirectories(IReadOnlyList<UnmanagedDirectoryChange> changes)
    {
        foreach (var change in changes)
        {
            _logger.Warning($"检测到会绕过清单加载的目录，已安全禁用：{change.OriginalDirectory} → {change.QuarantinedDirectory}");
        }
    }

    private void UpdateRuntimeAndGameStatus(bool forceRuntimeValidation = false)
    {
        if (_busy)
        {
            return;
        }

        // Process enumeration and MainModule.FileName access can block while a
        // game is starting or exiting. The status timer only consumes the last
        // background result so it never competes with wheel input on the UI
        // dispatcher.
        var gameProcessId = _cachedGameProcessId;
        var gameRunning = _gameProcessProbeCompleted && gameProcessId is not null;
        if (gameProcessId is not null && gameProcessId != _lastObservedGameProcessId)
        {
            _runtimeStates.Clear();
            RefreshView();
        }

        _lastObservedGameProcessId = gameProcessId;
        var runtime = _cachedRuntimeValidation;
        RuntimeStatusText.Text = runtime?.Message ?? "正在后台校验运行核心…";
        SettingsRuntimeStatusText.Text = RuntimeStatusText.Text;
        RuntimeStatusText.Foreground = runtime is null
            ? ThemeBrushes.MutedText
            : runtime.IsValid ? ThemeBrushes.Success : ThemeBrushes.Error;
        SettingsRuntimeStatusText.Foreground = RuntimeStatusText.Foreground;
        GameStatusText.Text = gameRunning ? "游戏运行中" : "游戏未运行";
        GameStatusText.Foreground = gameRunning ? ThemeBrushes.Success : ThemeBrushes.MutedText;
        GameStatusDot.Fill = gameRunning ? ThemeBrushes.Success : ThemeBrushes.MutedText;
        HeroTitle.Text = gameRunning
            ? "绝区零正在运行"
            : runtime is null ? "正在校验运行核心" : runtime.IsValid ? "准备启动绝区零" : "需要修复运行核心";
        PrimaryActionButton.Content = gameRunning ? "切回游戏" : "启动绝区零";
        PrimaryActionButton.IsEnabled = gameRunning || runtime?.IsValid == true;
        ManualReloadButton.IsEnabled = gameRunning;

        if (forceRuntimeValidation)
        {
            _runtimeValidationGeneration++;
            _lastRuntimeValidationUtc = DateTimeOffset.MinValue;
        }

        var validationExpired = DateTimeOffset.UtcNow - _lastRuntimeValidationUtc >= RuntimeValidationRefreshInterval;
        if (_cachedRuntimeValidation is null || validationExpired)
        {
            QueueRuntimeValidation(forceRuntimeValidation);
        }

        QueueGameProcessProbe();

        if (_splitPackageRepairDeferred && _gameProcessProbeCompleted && !gameRunning)
        {
            _ = RepairSplitPackagesIfSafeAsync();
        }
    }

    private async Task RepairSplitPackagesIfSafeAsync()
    {
        if (_splitPackageRepairInProgress || _splitPackageRepairCompleted)
        {
            return;
        }

        var packages = _library.FindSplitPackages()
            .Where(package => package.Mods.Any(mod => mod.Enabled))
            .ToList();
        if (packages.Count == 0)
        {
            _splitPackageRepairCompleted = true;
            return;
        }

        if (!_gameProcessProbeCompleted || _stateCoordinator.IsGameRunning)
        {
            _splitPackageRepairDeferred = true;
            if (!_splitPackageRepairNoticeShown)
            {
                _splitPackageRepairNoticeShown = true;
                _logger.Warning($"检测到 {packages.Count} 个同源拆分 Mod；游戏运行中，退出游戏后将自动合并并恢复完整目录。");
            }

            return;
        }

        _splitPackageRepairInProgress = true;
        _splitPackageRepairDeferred = false;
        SetBusy(true, "正在修复同源拆分 Mod…");
        var repaired = 0;
        try
        {
            foreach (var package in packages)
            {
                if (!File.Exists(package.SourcePath) && !Directory.Exists(package.SourcePath))
                {
                    _logger.Warning($"拆分 Mod 的原始来源不存在，暂时跳过：{package.SourcePath}");
                    continue;
                }

                ImportSession? session = null;
                try
                {
                    session = await _importer.StageAsync(package.SourcePath);
                    if (session.Candidates.Count != 1)
                    {
                        _logger.Warning($"拆分 Mod 来源仍识别为 {session.Candidates.Count} 个候选，暂不自动合并：{package.SourcePath}");
                        continue;
                    }

                    var candidate = session.Candidates[0];
                    var report = await Task.Run(() => _validator.ValidateAndRepair(candidate));
                    if (report.Status == ImportStatus.Blocked)
                    {
                        foreach (var issue in report.Issues.Where(issue => issue.Severity == IssueSeverity.Error))
                        {
                            _logger.Warning($"拆分 Mod 阻止项：{issue.Code} · {issue.File}:{issue.Line} · {issue.Message}");
                        }

                        _logger.Warning($"拆分 Mod 合并验证被阻止：{candidate.DisplayName}");
                        continue;
                    }

                    var wasEnabled = package.Mods.Any(mod => mod.Enabled);
                    var merged = _library.Install(candidate, report);
                    foreach (var old in package.Mods.ToList())
                    {
                        _library.SetEnabled(old.Id, false, keepLoaded: false);
                        _library.Delete(old.Id);
                    }

                    if (wasEnabled)
                    {
                        _library.ApplyStateBatch(merged.Id, true, keepLoaded: false);
                    }

                    repaired++;
                    _logger.Info($"已合并拆分 Mod：{string.Join("、", package.Mods.Select(mod => mod.DisplayName))} → {merged.DisplayName}");
                }
                catch (Exception ex)
                {
                    _logger.Error($"合并拆分 Mod 失败：{package.SourcePath} · {ex.Message}");
                }
                finally
                {
                    if (session is not null)
                    {
                        _importer.Cleanup(session);
                    }
                }
            }

            if (repaired > 0)
            {
                _liveSwitch.PrepareAll(_library.GetAll());
                _library.SaveChanges();
                _stateCoordinator.MarkControlFilesChanged();
                RefreshView();
                ShowToast($"已合并 {repaired} 个拆分 Mod；完整目录已恢复。", false);
            }

            _splitPackageRepairCompleted = true;
        }
        finally
        {
            _splitPackageRepairInProgress = false;
            SetBusy(false);
            RefreshView();
        }
    }

    private void QueueGameProcessProbe()
    {
        if (_gameProcessProbeInProgress)
        {
            return;
        }

        _gameProcessProbeInProgress = true;
        _ = ProbeGameProcessInBackgroundAsync(_config.GameExecutablePath);
    }

    private async Task ProbeGameProcessInBackgroundAsync(string? gamePath)
    {
        int? processId;
        try
        {
            processId = await Task.Run(() => _gameInput.GetGameProcessId(gamePath));
        }
        catch (Exception ex)
        {
            processId = null;
            _logger.Warning($"游戏状态探测失败：{ex.Message}");
        }

        _gameProcessProbeInProgress = false;
        if (!string.Equals(gamePath, _config.GameExecutablePath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var changed = !_gameProcessProbeCompleted || processId != _cachedGameProcessId;
        _cachedGameProcessId = processId;
        _gameProcessProbeCompleted = true;
        if (changed && IsLoaded && !_busy)
        {
            UpdateRuntimeAndGameStatus();
        }
    }

    private void QueueRuntimeValidation(bool force)
    {
        if (_runtimeValidationInProgress)
        {
            _runtimeValidationQueued |= force;
            return;
        }

        _runtimeValidationInProgress = true;
        var generation = _runtimeValidationGeneration;
        _ = ValidateRuntimeInBackgroundAsync(generation);
    }

    private async Task ValidateRuntimeInBackgroundAsync(int generation)
    {
        RuntimeValidation validation;
        try
        {
            validation = await Task.Run(_runtime.Validate);
        }
        catch (Exception ex)
        {
            validation = new RuntimeValidation
            {
                IsValid = false,
                RuntimePath = _paths.RuntimeRoot,
                Message = $"运行核心校验失败：{ex.Message}"
            };
            _logger.Warning(validation.Message);
        }

        var resultIsCurrent = generation == _runtimeValidationGeneration;
        if (resultIsCurrent)
        {
            _cachedRuntimeValidation = validation;
            _lastRuntimeValidationUtc = DateTimeOffset.UtcNow;
        }

        _runtimeValidationInProgress = false;
        var rerun = _runtimeValidationQueued || !resultIsCurrent;
        _runtimeValidationQueued = false;
        if (rerun)
        {
            QueueRuntimeValidation(force: false);
            return;
        }

        if (IsLoaded && !_busy)
        {
            UpdateRuntimeAndGameStatus();
        }
    }

    private void SetBusy(bool busy, string? message = null)
    {
        _busy = busy;
        BusyText.Text = busy ? message ?? "处理中…" : string.Empty;
        PrimaryActionButton.IsEnabled = !busy;
        ManualReloadButton.IsEnabled = !busy && _stateCoordinator.IsGameRunning;
    }

    private void ShowToast(string message, bool isError = false)
    {
        ToastText.Text = message;
        ToastText.Foreground = isError ? ThemeBrushes.Error : ThemeBrushes.Text;
        ToastBorder.BorderBrush = isError ? ThemeBrushes.Error : ThemeBrushes.Accent;
        ToastBorder.Visibility = Visibility.Visible;
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    private void Log(string message) => _logger.Info(message);

    private void ShowError(string title, Exception ex)
    {
        _logger.Error($"{title}：{ex.Message}");
        ShowToast($"{title}：{ex.Message}", true);
    }

    private void SaveConfig() => _store.Save(_paths.ConfigFile, _config);

    private void RegisterToggleHotkey()
    {
        if (_hotkeyRegistered)
        {
            return;
        }

        _windowHandle = new WindowInteropHelper(this).Handle;
        var parameters = new HwndSourceParameters("ZZZModManager.HotkeySink")
        {
            Width = 0,
            Height = 0,
            WindowStyle = 0,
            ParentWindow = HwndMessage
        };
        _hotkeySource = new HwndSource(parameters);
        _hotkeyHandle = _hotkeySource.Handle;
        _hotkeySource.AddHook(WindowMessageHook);
        _hotkeyRegistered = RegisterHotKey(
            _hotkeyHandle,
            ToggleManagerHotkeyId,
            ModAlt | ModNoRepeat,
            VirtualKeyW);
        Log(_hotkeyRegistered
            ? "已注册 Alt+W：显示/隐藏 Mod 管理器。"
            : $"注册 Alt+W 失败，错误码 {Marshal.GetLastWin32Error()}。");
    }

    private void UnregisterToggleHotkey()
    {
        if (_hotkeyHandle != IntPtr.Zero && _hotkeyRegistered)
        {
            UnregisterHotKey(_hotkeyHandle, ToggleManagerHotkeyId);
        }

        _hotkeySource?.RemoveHook(WindowMessageHook);
        _hotkeySource?.Dispose();
        _hotkeySource = null;
        _hotkeyHandle = IntPtr.Zero;
        _hotkeyRegistered = false;
    }

    private IntPtr WindowMessageHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == WmHotkey && wParam.ToInt64() == ToggleManagerHotkeyId)
        {
            handled = true;
            var now = Stopwatch.GetTimestamp();
            if (_hotkeyChordLatched
                || (_lastHotkeyToggleTimestamp != 0
                    && Stopwatch.GetElapsedTime(_lastHotkeyToggleTimestamp, now) < HotkeyDebounceInterval))
            {
                return IntPtr.Zero;
            }

            _hotkeyChordLatched = true;
            _lastHotkeyToggleTimestamp = now;
            _hotkeyReleaseTimer.Start();
            ReleaseHotkeyChordState();
            ToggleManagerWindow();
        }

        return IntPtr.Zero;
    }

    private void ToggleManagerWindow()
    {
        if (_managerOverlayVisible
            && Visibility == Visibility.Visible
            && WindowState != WindowState.Minimized)
        {
            // WM_HOTKEY arrives on key-down. Hiding here can destroy the input
            // target before W/Alt key-up is delivered, leaving MOD_NOREPEAT
            // latched and raw game input active. Hide from the release timer.
            _pendingHotkeyHide = true;
            return;
        }

        _pendingHotkeyHide = false;
        ActivateFromSecondaryInstance();
    }

    internal void ActivateFromSecondaryInstance()
    {
        _managerOverlayVisible = true;
        _keepManagerInForegroundOverGame = _stateCoordinator.IsGameRunning;
        Show();
        WindowState = WindowState.Normal;
        if (_windowHandle == IntPtr.Zero)
        {
            _windowHandle = new WindowInteropHelper(this).Handle;
        }

        ShowWindow(_windowHandle, ShowNormal);
        // Keep the manager above a borderless/fullscreen game while it is open.
        // Toggling Topmost back to false here lets the game reclaim the z-order
        // as soon as the mouse moves, which looks like an unexpected hide and
        // also sends raw mouse input back to the game.
        Topmost = true;
        EnsureManagerForeground();
        if (_keepManagerInForegroundOverGame)
        {
            _foregroundRecoveryTimer.Stop();
            _foregroundRecoveryTimer.Start();
        }

        RefreshView();
    }

    private void HideManagerWindow()
    {
        _pendingHotkeyHide = false;
        _managerOverlayVisible = false;
        _keepManagerInForegroundOverGame = false;
        _foregroundRecoveryTimer.Stop();
        Topmost = false;
        Hide();
    }

    private void ReleaseHotkeyLatchWhenKeysAreUp()
    {
        var latchExpired = _lastHotkeyToggleTimestamp != 0
                           && Stopwatch.GetElapsedTime(_lastHotkeyToggleTimestamp) >= HotkeyLatchTimeout;
        if (IsVirtualKeyDown(VirtualKeyW) && !latchExpired)
        {
            return;
        }

        _hotkeyChordLatched = false;
        _hotkeyReleaseTimer.Stop();
        if (_pendingHotkeyHide)
        {
            HideManagerWindow();
        }
    }

    private void QueueForegroundRecoveryIfGameReclaimedFocus()
    {
        if (!_managerOverlayVisible
            || !_keepManagerInForegroundOverGame
            || !IsGameForeground())
        {
            return;
        }

        _foregroundRecoveryTimer.Stop();
        _foregroundRecoveryTimer.Start();
    }

    private bool IsGameForeground()
    {
        // This method runs from Window.Deactivated, which is on the input
        // dispatcher. Never enumerate processes here; the status probe keeps
        // the PID cache current in the background.
        var gameProcessId = _cachedGameProcessId;
        var foregroundWindow = GetForegroundWindow();
        if (!_gameProcessProbeCompleted || gameProcessId is null || foregroundWindow == IntPtr.Zero)
        {
            return false;
        }

        GetWindowThreadProcessId(foregroundWindow, out var foregroundProcessId);
        return foregroundProcessId == (uint)gameProcessId.Value;
    }

    private bool EnsureManagerForeground()
    {
        if (!_managerOverlayVisible || _windowHandle == IntPtr.Zero)
        {
            return false;
        }

        ShowWindow(_windowHandle, ShowNormal);
        SetWindowPos(
            _windowHandle,
            HwndTopmost,
            0,
            0,
            0,
            0,
            SwpNoMove | SwpNoSize | SwpNoActivate | SwpShowWindow);
        Activate();
        BringWindowToTop(_windowHandle);
        SetForegroundWindow(_windowHandle);
        Focus();

        if (GetForegroundWindow() == _windowHandle)
        {
            return true;
        }

        var foregroundWindow = GetForegroundWindow();
        var foregroundThread = foregroundWindow == IntPtr.Zero
            ? 0
            : GetWindowThreadProcessId(foregroundWindow, out _);
        var managerThread = GetWindowThreadProcessId(_windowHandle, out _);
        var attached = foregroundThread != 0
                       && managerThread != 0
                       && foregroundThread != managerThread
                       && AttachThreadInput(managerThread, foregroundThread, true);
        try
        {
            BringWindowToTop(_windowHandle);
            SetForegroundWindow(_windowHandle);
            SetFocus(_windowHandle);
        }
        finally
        {
            if (attached)
            {
                AttachThreadInput(managerThread, foregroundThread, false);
            }
        }

        return GetForegroundWindow() == _windowHandle;
    }

    private static bool IsVirtualKeyDown(uint virtualKey) =>
        (GetAsyncKeyState((int)virtualKey) & 0x8000) != 0;

    private static void ReleaseHotkeyChordState()
    {
        // WM_HOTKEY is delivered on key-down. If the target window changes or
        // is hidden before the physical key-up, games using raw input can keep
        // observing Alt/W as pressed. Explicit key-up events prevent stuck
        // movement and also re-arm MOD_NOREPEAT for the next Alt+W press.
        keybd_event((byte)VirtualKeyW, 0, KeyEventKeyUp, UIntPtr.Zero);
        keybd_event(VirtualKeyMenu, 0, KeyEventKeyUp, UIntPtr.Zero);
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && LightboxOverlay.Visibility == Visibility.Visible)
        {
            CloseLightbox();
            e.Handled = true;
            return;
        }

        if (LightboxOverlay.Visibility == Visibility.Visible
            || HomePage.Visibility != Visibility.Visible
            || Keyboard.Modifiers != ModifierKeys.None)
        {
            return;
        }

        // 搜索框和下拉框自己要用方向键与回车，所以只有焦点已经落在卡片区时才接管。
        if (Keyboard.FocusedElement is DependencyObject focused
            && !ModGroupsItemsControl.IsKeyboardFocusWithin
            && focused is not MainWindow)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Left or Key.Right or Key.Up or Key.Down:
                e.Handled = MoveCardFocus(e.Key);
                break;
            case Key.Enter when ModGroupsItemsControl.IsKeyboardFocusWithin:
                if (Keyboard.FocusedElement is Button toggle && toggle.Name == "CardToggleButton")
                {
                    ToggleMod_Click(toggle, new RoutedEventArgs());
                    e.Handled = true;
                }

                break;
        }
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var descendant in FindVisualChildren<T>(child))
            {
                yield return descendant;
            }
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr SetFocus(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte virtualKey, byte scanCode, uint flags, UIntPtr extraInfo);
}

internal sealed record RuntimeCardState(ModStateApplication Application, string Message);
