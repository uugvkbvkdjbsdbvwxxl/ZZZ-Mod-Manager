using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ZZZModManager.Infrastructure;
using ZZZModManager.Models;
using ZZZModManager.Services;
using ZZZModManager.Themes;

namespace ZZZModManager;

public partial class MainWindow
{
    private void RefreshView()
    {
        _library.GetAvailableCharacterGroups();
        var manifests = _library.GetAll().ToList();
        // 删除或改名后的 Mod 不应继续占着勾选位，否则批量操作会对着不存在的 Id 报错。
        _selectedModIds.IntersectWith(manifests.Select(manifest => manifest.Id));
        var missingByMod = _dependencyResolver.GetMissingDependencies(manifests);
        var overlapsByMod = _library.GetOverlapMap();
        var cards = manifests.Select(manifest =>
        {
            var path = _library.GetAbsolutePath(manifest);
            var missing = missingByMod.TryGetValue(manifest.Id, out var dependencies) ? dependencies : [];
            var overlaps = overlapsByMod.TryGetValue(manifest.Id, out var peers) ? peers : [];
            _runtimeStates.TryGetValue(manifest.Id, out var runtimeState);
            var character = _library.DetectCharacterGroup(manifest);
            return new ModCardViewModel(
                manifest,
                path,
                missing,
                overlaps,
                runtimeState,
                _liveSwitch,
                _modelPreviewLoader,
                character,
                _multiSelectMode,
                _selectedModIds.Contains(manifest.Id));
        }).ToList();

        RebuildCharacterFilters(cards);
        var search = SearchBox?.Text.Trim() ?? string.Empty;
        var status = (StatusFilterCombo?.SelectedItem as ComboBoxItem)?.Tag as string ?? "All";
        var filtered = cards.Where(card =>
            MatchesCharacterFilter(card)
            && card.MatchesSearch(search)
            && card.MatchesStatus(status)).ToList();

        _visibleGroups.Clear();
        _visibleModIds.Clear();
        _visibleModIds.AddRange(filtered.Select(card => card.Manifest.Id));
        foreach (var group in filtered
                     .GroupBy(card => card.Character.Key, StringComparer.OrdinalIgnoreCase)
                     .Select(group => new ModGroupViewModel(group.First().Character.DisplayName, group.ToList()))
                     .OrderBy(group => group.SortOrder)
                     .ThenBy(group => group.DisplayName, StringComparer.CurrentCultureIgnoreCase))
        {
            _visibleGroups.Add(group);
        }

        FilterResultText.Text = $"{filtered.Count} 个结果";
        EmptyStateText.Text = manifests.Count == 0
            ? "还没有导入 Mod；可以在设置页拖入压缩包或文件夹。"
            : "当前筛选没有结果，请清除搜索、角色或状态筛选。";
        EmptyStateBorder.Visibility = filtered.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateBatchBar();

        // 槽位池是"实时切换"能力的唯一硬上限，用满之后新 Mod 只能走安全重载；
        // 因此把占用量常驻状态栏，让用户在导入前就知道还剩多少余量。
        var occupancy = _liveSwitch.GetSlotOccupancy(manifests);
        StatusText.Text = $"Mod 库：{_paths.ModsRoot}    ·    {manifests.Count} 个 Mod    ·    {manifests.Count(mod => mod.Enabled)} 个已启用    ·    {occupancy.DisplayText}";
        StatusText.Foreground = occupancy.IsFull ? ThemeBrushes.Warning : ThemeBrushes.SecondaryText;
        StatusText.ToolTip = occupancy.IsFull
            ? $"实时槽位已全部占用；新增的 {occupancy.WaitingForSlot} 个 Mod 需要安全重载才能生效。"
            : $"还剩 {occupancy.FreeSlots} 个实时槽位；槽位由管理器内部分配，无需物理按键。";
    }

    private void RebuildCharacterFilters(IReadOnlyList<ModCardViewModel> cards)
    {
        var items = new List<(string Key, string Name, int Enabled, int Total)>
        {
            ("all", "全部", cards.Count(card => card.Manifest.Enabled), cards.Count)
        };
        items.AddRange(cards
            .Where(card => CharacterGroupDetector.IsRoleGroup(card.Character.Kind))
            .GroupBy(card => card.Character.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => (group.Key, group.First().Character.DisplayName, group.Count(card => card.Manifest.Enabled), group.Count()))
            .OrderBy(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase));

        var frameworks = cards.Where(card => card.Character.Kind == CharacterGroupKind.Framework).ToList();
        if (frameworks.Count > 0)
        {
            items.Add(("framework", "通用依赖", frameworks.Count(card => card.Manifest.Enabled), frameworks.Count));
        }

        var unknown = cards.Where(card => card.Character.Kind == CharacterGroupKind.Unknown).ToList();
        if (unknown.Count > 0)
        {
            items.Add(("unknown", "未识别", unknown.Count(card => card.Manifest.Enabled), unknown.Count));
        }

        if (!items.Any(item => string.Equals(item.Key, _selectedCharacterKey, StringComparison.OrdinalIgnoreCase)))
        {
            _selectedCharacterKey = "all";
        }

        _characterFilters.Clear();
        foreach (var item in items)
        {
            _characterFilters.Add(new CharacterFilterItem(
                item.Key,
                item.Name,
                item.Enabled,
                item.Total,
                string.Equals(item.Key, _selectedCharacterKey, StringComparison.OrdinalIgnoreCase)));
        }
    }

    private bool MatchesCharacterFilter(ModCardViewModel card) =>
        _selectedCharacterKey == "all"
        || (_selectedCharacterKey == "unknown" && card.Character.Kind == CharacterGroupKind.Unknown)
        || string.Equals(card.Character.Key, _selectedCharacterKey, StringComparison.OrdinalIgnoreCase);

    private void CharacterFilter_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is string key)
        {
            _selectedCharacterKey = key;
            RefreshView();
        }
    }

    // 角色数量会随库变化，这里用"内容比两行高就折叠"的方式限高：
    // 外层 Border 负责裁剪，内层 StackPanel 让 ItemsControl 仍按完整高度测量，
    // 因此 ActualHeight 反映的是真实内容高度，可直接用来判断是否需要展开按钮。
    private const double CollapsedCharacterFilterHeight = 82;

    private void CharacterFilters_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateCharacterFilterClip();
    }

    private void ToggleCharacterFilterExpand_Click(object sender, RoutedEventArgs e)
    {
        _characterFiltersExpanded = !_characterFiltersExpanded;
        UpdateCharacterFilterClip();
    }

    // 键盘 Tab 可能落到被裁掉的芯片上，那样焦点框看不见，所以焦点进入时自动展开。
    private void CharacterFilterClip_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (!_characterFiltersExpanded && CharacterFilterExpandButton.Visibility == Visibility.Visible)
        {
            _characterFiltersExpanded = true;
            UpdateCharacterFilterClip();
        }
    }

    private void UpdateCharacterFilterClip()
    {
        var overflows = CharacterFiltersItemsControl.ActualHeight > CollapsedCharacterFilterHeight + 1;
        if (!overflows)
        {
            _characterFiltersExpanded = false;
        }

        CharacterFilterExpandButton.Visibility = overflows ? Visibility.Visible : Visibility.Collapsed;
        CharacterFilterExpandButton.Content = _characterFiltersExpanded ? "收起角色筛选 ▴" : "展开全部角色 ▾";
        CharacterFilterClip.MaxHeight = _characterFiltersExpanded
            ? double.PositiveInfinity
            : CollapsedCharacterFilterHeight;
    }

    private void ClearFilters_Click(object sender, RoutedEventArgs e)
    {
        _selectedCharacterKey = "all";
        SearchBox.Clear();
        StatusFilterCombo.SelectedIndex = 0;
        RefreshView();
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_library is not null)
        {
            RefreshView();
        }
    }

    private void StatusFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_library is not null)
        {
            RefreshView();
        }
    }

    // 卡片操作现在同时来自主按钮和溢出菜单项，两者都把卡片放在 Tag 上，
    // 因此统一在这里取值，避免每个处理器各自判断控件类型。
    private static ModCardViewModel? CardOf(object sender) => sender switch
    {
        FrameworkElement element when element.Tag is ModCardViewModel card => card,
        _ => null
    };

    private const string CardToggleButtonName = "CardToggleButton";

    // 一行卡片占据的纵向距离：卡片高 360 加上 CardBorder 上下各 6 的外边距。
    private const double CardRowPitch = 372;
    private ScrollViewer? _modGridScrollViewer;

    // 卡片按 WrapPanel 排布，行宽随窗口变化，所以方向键不按索引加减，
    // 而是按几何位置找目标：先筛出朝向正确的候选，再取投影距离最近的一个。
    private bool MoveCardFocus(Key key) => MoveCardFocus(key, allowScrollRetry: true);

    private bool MoveCardFocus(Key key, bool allowScrollRetry)
    {
        var buttons = FindVisualChildren<Button>(ModGroupsItemsControl)
            .Where(button => button.Name == CardToggleButtonName && button.IsVisible)
            .ToList();
        if (buttons.Count == 0)
        {
            return false;
        }

        var current = buttons.FirstOrDefault(button => button.IsKeyboardFocusWithin);
        if (current is null)
        {
            var first = buttons[0];
            first.BringIntoView();
            return first.Focus();
        }

        var origin = CenterOf(current);
        Button? best = null;
        var bestScore = double.MaxValue;

        foreach (var candidate in buttons)
        {
            if (ReferenceEquals(candidate, current))
            {
                continue;
            }

            var center = CenterOf(candidate);
            var dx = center.X - origin.X;
            var dy = center.Y - origin.Y;

            var (along, across) = key switch
            {
                Key.Left => (-dx, Math.Abs(dy)),
                Key.Right => (dx, Math.Abs(dy)),
                Key.Up => (-dy, Math.Abs(dx)),
                _ => (dy, Math.Abs(dx))
            };

            if (along <= 1)
            {
                continue;
            }

            var score = along + (across * 3);
            if (score < bestScore)
            {
                bestScore = score;
                best = candidate;
            }
        }

        if (best is null)
        {
            // 虚拟化之后视野外的卡片没有可视容器，方向上"找不到目标"可能只是还没实例化。
            // 先按方向滚动一段并重新布局，把焦点接回同一张卡片后再算一次，只重试一次防止空转。
            if (!allowScrollRetry || !ScrollCardGrid(key))
            {
                return false;
            }

            var anchor = current.Tag;
            ModGroupsItemsControl.UpdateLayout();
            FindVisualChildren<Button>(ModGroupsItemsControl)
                .FirstOrDefault(button => button.Name == CardToggleButtonName
                    && button.IsVisible
                    && ReferenceEquals(button.Tag, anchor))
                ?.Focus();
            return MoveCardFocus(key, allowScrollRetry: false);
        }

        best.BringIntoView();
        return best.Focus();
    }

    private bool ScrollCardGrid(Key key)
    {
        _modGridScrollViewer ??= FindVisualChildren<ScrollViewer>(ModGroupsItemsControl).FirstOrDefault();
        if (_modGridScrollViewer is null)
        {
            return false;
        }

        var before = _modGridScrollViewer.VerticalOffset;
        var step = Math.Max(Math.Min(_modGridScrollViewer.ViewportHeight, CardRowPitch), 1);
        _modGridScrollViewer.ScrollToVerticalOffset(key is Key.Up or Key.Left ? before - step : before + step);
        _modGridScrollViewer.UpdateLayout();
        return Math.Abs(_modGridScrollViewer.VerticalOffset - before) > 0.5;
    }

    private Point CenterOf(FrameworkElement element)
    {
        var offset = element.TransformToAncestor(ModGroupsItemsControl).Transform(default);
        return new Point(offset.X + (element.ActualWidth / 2), offset.Y + (element.ActualHeight / 2));
    }

    private void CardOverflow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.ContextMenu is null)
        {
            return;
        }

        button.ContextMenu.PlacementTarget = button;
        button.ContextMenu.IsOpen = true;
    }

    private async void ToggleMod_Click(object sender, RoutedEventArgs e)
    {
        if (CardOf(sender) is not ModCardViewModel card || _busy)
        {
            return;
        }

        var enable = !card.Manifest.Enabled;
        if (enable && card.MissingDependencies.Count > 0)
        {
            ShowToast($"缺少依赖：{string.Join("、", card.MissingDependencies)}。请先在设置页导入依赖。", true);
            return;
        }

        SetBusy(true, enable ? $"正在启用 {card.DisplayName}…" : $"正在禁用 {card.DisplayName}…");
        var result = await Task.Run(() => _stateCoordinator.ApplyState(
            card.Manifest.Id,
            enable,
            restoreManagerWindow: !_config.AutoHideAfterLiveSwitch,
            allowReload: _config.ReloadWhenRequired));
        ApplyRuntimeResult(result);
        SetBusy(false);
        RefreshView();
        var error = result.Application == ModStateApplication.Failed
                    || (result.GameRunning && result.Application == ModStateApplication.Pending);
        ShowToast(result.Message, error);
        if (result.Succeeded && result.GameRunning && _config.AutoHideAfterLiveSwitch)
        {
            HideManagerWindow();
        }
    }

    private void ApplyRuntimeResult(ModStateChangeResult result)
    {
        foreach (var manifest in result.ChangedMods.Concat(result.AutomaticallyDisabled)
                     .DistinctBy(manifest => manifest.Id, StringComparer.OrdinalIgnoreCase))
        {
            _runtimeStates[manifest.Id] = new RuntimeCardState(result.Application, result.Message);
        }
    }

    private void Preview_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not ModCardViewModel card || card.PreviewPath is null)
        {
            ShowToast("该 Mod 里没有找到可用的预览图（preview.png/.jpg/.webp）。", true);
            return;
        }

        var image = PreviewImageLoader.Load(card.PreviewPath, 1800);
        if (image is null)
        {
            ShowToast("预览图已损坏或无法读取。", true);
            return;
        }

        LightboxImage.Source = image;
        LightboxZoomSlider.Value = 1;
        LightboxOverlay.Visibility = Visibility.Visible;
        LightboxOverlay.Focus();
    }

    private async void ModelPreview_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not ModCardViewModel card || _busy)
        {
            return;
        }

        SetBusy(true, $"正在生成 {card.DisplayName} 的 3D 预览…");
        try
        {
            var scene = await Task.Run(() => _modelPreviewLoader.Load(card.DirectoryPath));
            SetBusy(false);
            new ModelPreviewWindow(card.DisplayName, scene) { Owner = this }.ShowDialog();
        }
        catch (ModelPreviewException ex)
        {
            SetBusy(false);
            ShowToast(ex.Message, true);
        }
        catch (Exception ex)
        {
            SetBusy(false);
            ShowError("生成 3D 预览失败", ex);
        }
    }

    private void CloseLightbox_Click(object sender, RoutedEventArgs e) => CloseLightbox();

    private void LightboxBackdrop_Click(object sender, MouseButtonEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, LightboxOverlay))
        {
            CloseLightbox();
        }
    }

    private void CloseLightbox()
    {
        LightboxOverlay.Visibility = Visibility.Collapsed;
        LightboxImage.Source = null;
    }

    private void InspectMod_Click(object sender, RoutedEventArgs e)
    {
        if (CardOf(sender) is not ModCardViewModel card)
        {
            return;
        }

        try
        {
            var reportPath = Path.Combine(card.DirectoryPath, card.Manifest.ReportFile);
            // 报告缺失是老 Mod 的正常状态，交给汇总器写成一条说明而不是当成错误。
            var report = File.Exists(reportPath) ? _store.Load<ImportReport?>(reportPath, () => null) : null;
            var diagnostics = ModDiagnosticsSummarizer.Build(card.Manifest, report, _liveSwitch.Audit(card.Manifest));
            new ModDiagnosticsWindow(card.DisplayName, diagnostics) { Owner = this }.ShowDialog();
        }
        catch (Exception ex)
        {
            ShowError("读取诊断失败", ex);
        }
    }

    private void ShowHotkeys_Click(object sender, RoutedEventArgs e)
    {
        if (CardOf(sender) is not ModCardViewModel card)
        {
            return;
        }

        try
        {
            var hotkeys = ModHotkeyReader.Read(card.DirectoryPath);
            new TextViewerWindow($"游戏内快捷键 · {card.DisplayName}", FormatHotkeys(card, hotkeys)) { Owner = this }.ShowDialog();
        }
        catch (Exception ex)
        {
            ShowError("读取快捷键失败", ex);
        }
    }

    private void OpenModDirectory_Click(object sender, RoutedEventArgs e)
    {
        if (CardOf(sender) is ModCardViewModel card)
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{card.DirectoryPath}\"") { UseShellExecute = true });
        }
    }

    private void ChangeGroup_Click(object sender, RoutedEventArgs e)
    {
        if (CardOf(sender) is not ModCardViewModel card)
        {
            return;
        }

        var dialog = new GroupSelectionWindow(
            card.Manifest.CharacterGroupOverrideKey,
            _library.GetAvailableCharacterGroups())
        { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            foreach (var created in dialog.CreatedGroups)
            {
                _library.RegisterCustomCharacterGroup(created);
            }

            _library.SetCharacterGroupOverride(card.Manifest.Id, dialog.SelectedGroupKey);
            RefreshView();
            ShowToast("角色分组已更新；同角色单选会在下次启用时执行。");
        }
    }

    private void DeleteMod_Click(object sender, RoutedEventArgs e)
    {
        if (CardOf(sender) is not ModCardViewModel card)
        {
            return;
        }

        if (_stateCoordinator.IsGameRunning)
        {
            ShowToast("游戏运行时不能删除已加载资源，请先退出游戏。", true);
            return;
        }

        if (MessageBox.Show(this, $"删除“{card.DisplayName}”的安装副本？原始下载文件不会删除。",
                "确认删除", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            _library.Delete(card.Manifest.Id);
            _runtimeStates.Remove(card.Manifest.Id);
            Log($"已删除安装副本：{card.DisplayName}");
            RefreshView();
            ShowToast("安装副本已删除，无法从管理器恢复；原始下载文件未改动。");
        }
        catch (Exception ex)
        {
            ShowError("删除失败", ex);
        }
    }

    private string FormatHotkeys(ModCardViewModel card, IReadOnlyList<ModHotkey> hotkeys)
    {
        var builder = new StringBuilder();
        builder.AppendLine(card.DisplayName);
        builder.AppendLine("启用 / 禁用由管理器内部控制完成，无需按 F20、F21 等物理按键。");
        builder.AppendLine("以下仅列出 Mod 作者配置的饰品或菜单快捷键：");
        builder.AppendLine();
        if (hotkeys.Count == 0)
        {
            builder.AppendLine("未发现作者配置的饰品或菜单快捷键。");
        }
        else
        {
            foreach (var group in hotkeys.GroupBy(hotkey => hotkey.File, StringComparer.OrdinalIgnoreCase))
            {
                builder.AppendLine($"[{group.Key}]");
                foreach (var hotkey in group)
                {
                    builder.AppendLine($"- {hotkey.DisplayName}: {string.Join(" / ", hotkey.Keys)}");
                }

                builder.AppendLine();
            }
        }

        return builder.ToString().TrimEnd();
    }
}

public sealed class ModCardViewModel
{
    internal const int ThumbnailDecodeDimension = 360;

    public ModManifest Manifest { get; }
    public CharacterGroupInfo Character { get; }
    public string DirectoryPath { get; }
    public IReadOnlyList<string> MissingDependencies { get; }
    public IReadOnlyList<ModManifest> ConflictingMods { get; }
    public string DisplayName => Manifest.DisplayName;
    public string CharacterDisplayName => Character.DisplayName;
    public string? PreviewPath { get; }
    private readonly Lazy<bool> _modelPreviewAvailable;

    // 缩略图按需解码：卡片容器由虚拟化面板实例化时绑定才会读到这里，
    // 视野外的 Mod 因此不付出解码成本。不在视图模型里缓存位图，
    // 让 PreviewImageLoader 的 LRU 成为唯一的内存上限。
    public ImageSource? Thumbnail => PreviewPath is null
        ? null
        : PreviewImageLoader.Load(PreviewPath, ThumbnailDecodeDimension);

    public Visibility PreviewPlaceholderVisibility => Thumbnail is null ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ModelPreviewVisibility => _modelPreviewAvailable.Value ? Visibility.Visible : Visibility.Collapsed;
    public string PreviewPlaceholderText => _modelPreviewAvailable.Value ? "可生成 3D 模型预览" : "无预览图";
    public string ToggleText => Manifest.Enabled ? "禁用 Mod" : "启用 Mod";
    public string StatusText { get; }
    public Brush StatusBrush { get; }
    public Brush StatusForeground { get; }
    public string CapabilityText { get; }
    public Brush CapabilityBrush { get; }
    public string DependencyText => MissingDependencies.Count == 0 ? string.Empty : "缺少依赖：" + string.Join("、", MissingDependencies);
    public string ConflictText { get; }
    public Brush ConflictBrush { get; }
    public string RuntimeMessage { get; }

    // 多选状态由窗口保存后再投影进卡片：视图模型是不可变快照，
    // 每次 RefreshView 重建时按 Mod Id 重新读回勾选结果。
    public bool IsSelected { get; }
    public Visibility SelectionVisibility { get; }
    public Brush SelectionBorderBrush => IsSelected ? ThemeBrushes.Accent : ThemeBrushes.Border;

    internal ModCardViewModel(
        ModManifest manifest,
        string directoryPath,
        IReadOnlyList<string> missingDependencies,
        IReadOnlyList<ModManifest> conflictingMods,
        RuntimeCardState? runtimeState,
        ILiveModSwitchService liveSwitch,
        IModModelPreviewLoader modelPreviewLoader,
        CharacterGroupInfo character,
        bool multiSelectMode = false,
        bool isSelected = false)
    {
        IsSelected = isSelected;
        SelectionVisibility = multiSelectMode ? Visibility.Visible : Visibility.Collapsed;
        Manifest = manifest;
        DirectoryPath = directoryPath;
        MissingDependencies = missingDependencies;
        ConflictingMods = conflictingMods;
        Character = character;
        PreviewPath = ModPreviewLocator.Resolve(directoryPath, manifest.PreviewFile);
        _modelPreviewAvailable = new Lazy<bool>(() => modelPreviewLoader.CanLoad(directoryPath));
        RuntimeMessage = runtimeState?.Message ?? string.Empty;

        var pending = runtimeState?.Application is ModStateApplication.Pending or ModStateApplication.Failed;
        StatusText = missingDependencies.Count > 0
            ? "缺依赖"
            : pending ? "待应用" : manifest.Enabled ? "已启用" : "已禁用";
        StatusBrush = missingDependencies.Count > 0 || pending
            ? ThemeBrushes.Warning
            : manifest.Enabled ? ThemeBrushes.Success : ThemeBrushes.Border;
        StatusForeground = missingDependencies.Count > 0 || pending
            ? ThemeBrushes.WarningForeground
            : manifest.Enabled ? ThemeBrushes.SuccessForeground : ThemeBrushes.SecondaryText;
        CapabilityText = manifest.LiveSwitchCapability switch
        {
            LiveSwitchCapability.Immediate => $"实时切换 · {liveSwitch.GetDisplayBinding(manifest, true)}",
            LiveSwitchCapability.RequiresRestart => string.IsNullOrWhiteSpace(manifest.LiveSwitchBlockReason)
                ? "静态顶点限制 · 启动预加载后可实时切换"
                : manifest.LiveSwitchBlockReason,
            LiveSwitchCapability.SlotUnavailable => $"实时槽位已满（{LiveModSwitchService.MaximumSlots} / {LiveModSwitchService.MaximumSlots}）· 需要安全重载",
            LiveSwitchCapability.Unsupported => "门控审计未通过 · 需要安全重载",
            _ => "需要安全重载"
        };
        CapabilityBrush = manifest.LiveSwitchCapability == LiveSwitchCapability.Immediate
            ? ThemeBrushes.Success
            : ThemeBrushes.Warning;

        // 同时启用的重叠才会真的互相覆盖，未启用的只是提前预警，
        // 因此两种情况用不同措辞和不同语义色，避免把提示当成故障。
        var activeConflicts = conflictingMods
            .Where(peer => peer.Enabled && manifest.Enabled)
            .Select(peer => peer.DisplayName)
            .ToList();
        ConflictText = conflictingMods.Count == 0
            ? string.Empty
            : activeConflicts.Count > 0
                ? "正在冲突：" + string.Join("、", activeConflicts)
                : "文件重叠：" + string.Join("、", conflictingMods.Select(peer => peer.DisplayName));
        ConflictBrush = activeConflicts.Count > 0 ? ThemeBrushes.Error : ThemeBrushes.SecondaryText;
    }

    public bool MatchesSearch(string search) => string.IsNullOrWhiteSpace(search)
        || DisplayName.Contains(search, StringComparison.CurrentCultureIgnoreCase)
        || Character.DisplayName.Contains(search, StringComparison.CurrentCultureIgnoreCase)
        || Manifest.Dependencies.Any(item => item.Contains(search, StringComparison.CurrentCultureIgnoreCase));

    public bool MatchesStatus(string status) => status switch
    {
        "Enabled" => Manifest.Enabled,
        "Disabled" => !Manifest.Enabled,
        "Dependency" => MissingDependencies.Count > 0,
        "Pending" => Manifest.LiveSwitchCapability != LiveSwitchCapability.Immediate || StatusText == "待应用",
        _ => true
    };
}

public sealed record ModGroupViewModel(string DisplayName, IReadOnlyList<ModCardViewModel> Mods)
{
    public string CountText => $"{Mods.Count(card => card.Manifest.Enabled)} / {Mods.Count} 已启用";
    public int SortOrder => Mods[0].Character.Kind switch
    {
        CharacterGroupKind.Character or CharacterGroupKind.Discovered or CharacterGroupKind.Custom => 0,
        CharacterGroupKind.Framework => 1,
        _ => 2
    };
}

public sealed record CharacterFilterItem(string Key, string DisplayName, int Enabled, int Total, bool Selected)
{
    public string CountText => $"{Enabled}/{Total}";
    public Brush Background => Selected ? ThemeBrushes.AccentTint : Brushes.Transparent;
    public Brush BorderBrush => Selected ? ThemeBrushes.Accent : ThemeBrushes.Border;
    public Brush Foreground => Selected ? ThemeBrushes.Text : ThemeBrushes.SecondaryText;
    public FontWeight LabelWeight => Selected ? FontWeights.SemiBold : FontWeights.Normal;
}

public static class PreviewImageLoader
{
    private const int MaximumSourceDimension = 65_535;
    private const long MaximumSourcePixels = 200_000_000;

    // 网格虚拟化后同一路径会被反复请求，缓存条数上限抬高以覆盖一屏加上前后各一页的卡片；
    // 灯箱与设置页会放进 1800/2200 的大图，所以再加一道像素预算，
    // 避免"条数没超但内存已经很大"的情况。
    private const int MaximumCacheEntries = 128;
    private const long MaximumCachePixels = 24_000_000;
    private static readonly object CacheSync = new();
    private static readonly Dictionary<string, CacheEntry> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static long _accessSequence;
    private static long _cachedPixels;

    public static BitmapSource? Load(string path, int maximumDecodeDimension)
    {
        if (!File.Exists(path) || maximumDecodeDimension <= 0)
        {
            return null;
        }

        try
        {
            var fullPath = Path.GetFullPath(path);
            var file = new FileInfo(fullPath);
            var cacheKey = string.Join('\0', fullPath, file.Length, file.LastWriteTimeUtc.Ticks, maximumDecodeDimension);
            lock (CacheSync)
            {
                if (Cache.TryGetValue(cacheKey, out var cached))
                {
                    cached.LastAccess = ++_accessSequence;
                    return cached.Image;
                }
            }

            int pixelWidth;
            int pixelHeight;
            using (var input = File.Open(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                var decoder = BitmapDecoder.Create(
                    input,
                    BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile,
                    BitmapCacheOption.None);
                var frame = decoder.Frames[0];
                pixelWidth = frame.PixelWidth;
                pixelHeight = frame.PixelHeight;
            }

            if (pixelWidth <= 0 || pixelHeight <= 0
                || pixelWidth > MaximumSourceDimension || pixelHeight > MaximumSourceDimension
                || checked((long)pixelWidth * pixelHeight) > MaximumSourcePixels)
            {
                return null;
            }

            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            if (Math.Max(pixelWidth, pixelHeight) > maximumDecodeDimension)
            {
                if (pixelWidth >= pixelHeight)
                {
                    image.DecodePixelWidth = maximumDecodeDimension;
                }
                else
                {
                    image.DecodePixelHeight = maximumDecodeDimension;
                }
            }

            image.UriSource = new Uri(fullPath, UriKind.Absolute);
            image.EndInit();
            image.Freeze();
            CacheImage(cacheKey, fullPath, image);
            return image;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or FileFormatException)
        {
            return null;
        }
    }

    private static void CacheImage(string key, string fullPath, BitmapSource image)
    {
        lock (CacheSync)
        {
            var prefix = fullPath + '\0';
            foreach (var staleKey in Cache.Keys.Where(item => item.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList())
            {
                Evict(staleKey);
            }

            Cache[key] = new CacheEntry(image, ++_accessSequence);
            _cachedPixels += PixelCountOf(image);
            while (Cache.Count > MaximumCacheEntries || (_cachedPixels > MaximumCachePixels && Cache.Count > 1))
            {
                Evict(Cache.MinBy(item => item.Value.LastAccess).Key);
            }
        }
    }

    private static void Evict(string key)
    {
        if (Cache.Remove(key, out var removed))
        {
            _cachedPixels -= PixelCountOf(removed.Image);
        }
    }

    private static long PixelCountOf(BitmapSource image) => (long)image.PixelWidth * image.PixelHeight;

    private sealed class CacheEntry(BitmapSource image, long lastAccess)
    {
        public BitmapSource Image { get; } = image;
        public long LastAccess { get; set; } = lastAccess;
    }
}
