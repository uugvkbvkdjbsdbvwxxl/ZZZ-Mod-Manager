using System.Windows;
using System.Windows.Controls;
using ZZZModManager.Models;

namespace ZZZModManager;

public partial class MainWindow
{
    private void RefreshPresets()
    {
        // 预设列表每次都整体重建，所以要按 Id 把用户当前挑中的那条接回去，
        // 否则保存或删除之后下拉框会莫名跳回第一项。
        var selectedId = (PresetCombo.SelectedItem as ModPreset)?.Id;
        _presetItems.Clear();
        foreach (var preset in _presets.GetAll())
        {
            _presetItems.Add(preset);
        }

        PresetCombo.SelectedItem = _presetItems
            .FirstOrDefault(preset => string.Equals(preset.Id, selectedId, StringComparison.OrdinalIgnoreCase));
        UpdatePresetButtons();
    }

    private void UpdatePresetButtons()
    {
        var hasSelection = PresetCombo.SelectedItem is ModPreset;
        ApplyPresetButton.IsEnabled = hasSelection;
        DeletePresetButton.IsEnabled = hasSelection;
        PresetCombo.IsEnabled = _presetItems.Count > 0;
    }

    private void PresetCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // XAML 声明的事件在 InitializeComponent 期间就会触发，那时字段还没赋值完。
        if (_presets is not null)
        {
            UpdatePresetButtons();
        }
    }

    private async void ApplyPreset_Click(object sender, RoutedEventArgs e)
    {
        if (PresetCombo.SelectedItem is not ModPreset preset || _busy)
        {
            return;
        }

        // 预设是一份绝对状态：没被记录的 Mod 会被禁用，所以先确认一次。
        if (MessageBox.Show(this, $"应用预设“{preset.Name}”？未记录在预设里的 Mod 会被禁用。",
                "确认应用预设", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        var manifests = _library.GetAll().ToList();
        var missingByMod = _dependencyResolver.GetMissingDependencies(manifests);
        var skipped = new List<string>();
        var requests = _presets.BuildRequests(preset, manifests)
            .Select(request =>
            {
                if (!request.Enabled
                    || !missingByMod.TryGetValue(request.ModId, out var missing)
                    || missing.Count == 0)
                {
                    return request;
                }

                // 缺依赖的条目降级为禁用而不是让整批失败：预设多半是在别的机器上存的。
                skipped.Add(manifests.First(manifest =>
                    string.Equals(manifest.Id, request.ModId, StringComparison.OrdinalIgnoreCase)).DisplayName);
                return new ModStateRequest(request.ModId, false);
            })
            .ToList();

        await ApplyStateRequests(requests, $"正在应用预设 {preset.Name}…");
        if (skipped.Count > 0)
        {
            ShowToast($"以下 Mod 缺少依赖，已保持禁用：{string.Join("、", skipped)}", true);
        }
    }

    private void SavePreset_Click(object sender, RoutedEventArgs e) =>
        SavePresetFrom(
            _library.GetAll().Where(manifest => manifest.Enabled).Select(manifest => manifest.Id).ToList(),
            "当前已启用的 Mod");

    private void DeletePreset_Click(object sender, RoutedEventArgs e)
    {
        if (PresetCombo.SelectedItem is not ModPreset preset)
        {
            return;
        }

        if (MessageBox.Show(this, $"删除预设“{preset.Name}”？Mod 的当前启用状态不受影响。",
                "确认删除预设", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        if (_presets.Delete(preset.Id))
        {
            Log($"已删除预设：{preset.Name}");
            RefreshPresets();
            ShowToast($"预设“{preset.Name}”已删除。");
        }
    }

    private void SavePresetFrom(IReadOnlyList<string> enabledModIds, string scopeText)
    {
        var dialog = new PresetNameWindow(scopeText, enabledModIds.Count, (PresetCombo.SelectedItem as ModPreset)?.Name)
        {
            Owner = this
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            var preset = _presets.Save(dialog.PresetName, enabledModIds);
            RefreshPresets();
            PresetCombo.SelectedItem = _presetItems
                .FirstOrDefault(item => string.Equals(item.Id, preset.Id, StringComparison.OrdinalIgnoreCase));
            UpdatePresetButtons();
            Log($"已保存预设：{preset.Name}（记录 {enabledModIds.Count} 个启用项）");
            ShowToast($"预设“{preset.Name}”已保存，记录 {enabledModIds.Count} 个启用项。");
        }
        catch (Exception ex)
        {
            ShowError("保存预设失败", ex);
        }
    }

    private void UpdateBatchBar()
    {
        BatchActionBar.Visibility = _multiSelectMode ? Visibility.Visible : Visibility.Collapsed;
        BatchSelectionText.Text = $"已选 {_selectedModIds.Count} 个 Mod";
        var hasSelection = _selectedModIds.Count > 0;
        BatchEnableButton.IsEnabled = hasSelection;
        BatchDisableButton.IsEnabled = hasSelection;
        BatchSavePresetButton.IsEnabled = hasSelection;
        BatchClearButton.IsEnabled = hasSelection;
        BatchSelectVisibleButton.IsEnabled = _visibleModIds.Count > 0;
    }

    private void MultiSelectToggle_Click(object sender, RoutedEventArgs e)
    {
        _multiSelectMode = MultiSelectToggle.IsChecked == true;
        if (!_multiSelectMode)
        {
            // 退出多选时清空勾选：留着看不见的选中项，下次开启会造成误操作。
            _selectedModIds.Clear();
        }

        if (_library is not null)
        {
            RefreshView();
        }
    }

    private void CardSelect_Click(object sender, RoutedEventArgs e)
    {
        if (CardOf(sender) is not ModCardViewModel card)
        {
            return;
        }

        if (!_selectedModIds.Add(card.Manifest.Id))
        {
            _selectedModIds.Remove(card.Manifest.Id);
        }

        RefreshView();
    }

    private void BatchSelectVisible_Click(object sender, RoutedEventArgs e)
    {
        _selectedModIds.UnionWith(_visibleModIds);
        RefreshView();
    }

    private void BatchClearSelection_Click(object sender, RoutedEventArgs e)
    {
        _selectedModIds.Clear();
        RefreshView();
    }

    private async void BatchEnable_Click(object sender, RoutedEventArgs e) => await ApplySelectedState(true);

    private async void BatchDisable_Click(object sender, RoutedEventArgs e) => await ApplySelectedState(false);

    private void BatchSavePreset_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedModIds.Count == 0)
        {
            ShowToast("请先勾选要写入预设的 Mod。", true);
            return;
        }

        SavePresetFrom(_selectedModIds.ToList(), "当前勾选的 Mod");
    }

    private async Task ApplySelectedState(bool enable)
    {
        if (_busy || _selectedModIds.Count == 0)
        {
            return;
        }

        var manifests = _library.GetAll().ToList();
        var targets = manifests.Where(manifest => _selectedModIds.Contains(manifest.Id)).ToList();
        if (targets.Count == 0)
        {
            ShowToast("勾选的 Mod 已不存在，请重新选择。", true);
            RefreshView();
            return;
        }

        if (enable)
        {
            // 缺依赖的目标在下发之前就排除掉：整批是一次事务，
            // 事后回滚比一开始少启用一个更难向用户解释。
            var missingByMod = _dependencyResolver.GetMissingDependencies(manifests);
            var blocked = targets
                .Where(manifest => missingByMod.TryGetValue(manifest.Id, out var missing) && missing.Count > 0)
                .ToList();
            if (blocked.Count > 0)
            {
                ShowToast($"以下 Mod 缺少依赖，已跳过：{string.Join("、", blocked.Select(manifest => manifest.DisplayName))}", true);
                targets = targets.Except(blocked).ToList();
                if (targets.Count == 0)
                {
                    return;
                }
            }
        }

        var label = enable ? $"正在启用 {targets.Count} 个 Mod…" : $"正在禁用 {targets.Count} 个 Mod…";
        await ApplyStateRequests(targets.Select(manifest => new ModStateRequest(manifest.Id, enable)).ToList(), label);
    }

    // 预设与批量操作都只是"一次下发多条状态"，因此共用同一条繁忙 / 提示 / 刷新流程，
    // 和单卡片切换保持完全一致的反馈方式。
    private async Task ApplyStateRequests(IReadOnlyList<ModStateRequest> requests, string busyText)
    {
        if (requests.Count == 0)
        {
            return;
        }

        SetBusy(true, busyText);
        var result = await Task.Run(() => _stateCoordinator.ApplyStates(
            requests,
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
}
