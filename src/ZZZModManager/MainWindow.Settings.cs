using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using ZZZModManager.Infrastructure;
using ZZZModManager.Models;
using ZZZModManager.Services;

namespace ZZZModManager;

public partial class MainWindow
{
    private void BrowseGame_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择 ZenlessZoneZero.exe",
            Filter = "绝区零程序|ZenlessZoneZero.exe;ZenlessZoneZeroBeta.exe|所有文件|*.*",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        _config.GameExecutablePath = dialog.FileName;
        GamePathBox.Text = dialog.FileName;
        SaveConfig();
        Log($"已选择游戏：{dialog.FileName}");
        UpdateRuntimeAndGameStatus();
    }

    private void GamePathBox_LostFocus(object sender, RoutedEventArgs e)
    {
        _config.GameExecutablePath = GamePathBox.Text.Trim();
        SaveConfig();
        UpdateRuntimeAndGameStatus();
    }

    private void ImportRuntime_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "选择已授权的 XXMI/ZZMI 运行核心目录" };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            var manifest = _runtime.InstallFromFolder(dialog.FolderName);
            _config.RuntimePath = _paths.RuntimeRoot;
            SaveConfig();
            var message = $"运行核心已导入，校验了 {manifest.FileSha256.Count} 个关键文件。";
            Log(message);
            ShowToast(message);
            UpdateRuntimeAndGameStatus(forceRuntimeValidation: true);
        }
        catch (Exception ex)
        {
            ShowError("导入运行核心失败", ex);
        }
    }

    private void RepairRuntime_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _runtime.Repair();
            Log("已离线校验并修复运行核心与 Mods 递归路径配置。");
            ShowToast(_runtime.Validate().Message);
            UpdateRuntimeAndGameStatus(forceRuntimeValidation: true);
        }
        catch (Exception ex)
        {
            ShowError("修复核心配置失败", ex);
        }
    }

    private async void ImportMods_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择 GameBanana Mod 压缩包",
            Filter = "Mod 压缩包|*.zip;*.7z;*.rar|所有文件|*.*",
            Multiselect = true,
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) == true)
        {
            await ImportSourcesAsync(dialog.FileNames);
        }
    }

    private async void ImportFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "选择 GameBanana Mod 文件夹" };
        if (dialog.ShowDialog(this) == true)
        {
            await ImportSourcesAsync([dialog.FolderName]);
        }
    }

    private async void ImportDropZone_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop)
            && e.Data.GetData(DataFormats.FileDrop) is string[] paths)
        {
            await ImportSourcesAsync(paths);
        }
    }

    private void ImportDropZone_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async Task ImportSourcesAsync(IEnumerable<string> sources)
    {
        if (_busy)
        {
            return;
        }

        var sourceList = sources.Where(path => File.Exists(path) || Directory.Exists(path)).ToList();
        if (sourceList.Count == 0)
        {
            ShowToast("没有可导入的文件或文件夹。", true);
            return;
        }

        var installed = new List<string>();
        SetBusy(true, "正在分析并安装 Mod 副本…");
        try
        {
            foreach (var source in sourceList)
            {
                ImportSession? session = null;
                try
                {
                    Log($"正在分析：{source}");
                    session = await _importer.StageAsync(source);
                    var selected = SelectCandidates(session.Candidates);
                    if (selected is null)
                    {
                        Log("已取消候选 Mod 选择。");
                        continue;
                    }

                    foreach (var candidate in selected)
                    {
                        var report = await Task.Run(() => _validator.ValidateAndRepair(candidate));
                        candidate.Report = report;
                        Log(FormatReport(candidate, report));
                        if (report.Status == ImportStatus.Blocked)
                        {
                            _logger.Warning($"已阻止安装：{candidate.DisplayName}");
                            continue;
                        }

                        var manifest = _library.Install(candidate, report);
                        installed.Add(manifest.DisplayName);
                    }
                }
                catch (Exception ex)
                {
                    _logger.Error($"导入 {source} 失败：{ex.Message}");
                    ShowToast($"导入失败：{Path.GetFileName(source)} · {ex.Message}", true);
                }
                finally
                {
                    if (session is not null)
                    {
                        _importer.Cleanup(session);
                    }
                }
            }

            if (installed.Count > 0)
            {
                var preparation = _liveSwitch.PrepareAll(_library.GetAll());
                _library.SaveChanges();
                _stateCoordinator.MarkControlFilesChanged();
                var quarantined = _library.QuarantineActiveUnmanagedDirectories();
                if (quarantined.Count > 0)
                {
                    LogQuarantinedDirectories(quarantined);
                }

                Log($"已安装 {installed.Count} 个 Mod：{string.Join("、", installed)}");
                ShowToast(quarantined.Count == 0
                    ? $"导入完成：{installed.Count} 个 Mod。默认保持禁用，请在首页卡片中启用。"
                    : $"导入完成：{installed.Count} 个 Mod；另已禁用 {quarantined.Count} 个重复源目录。");
            }
        }
        finally
        {
            SetBusy(false);
            RefreshView();
        }
    }

    private List<ImportCandidate>? SelectCandidates(IReadOnlyList<ImportCandidate> candidates)
    {
        if (candidates.Count <= 1)
        {
            return candidates.ToList();
        }

        var window = new CandidateSelectionWindow(candidates) { Owner = this };
        return window.ShowDialog() == true ? window.SelectedCandidates : null;
    }

    private static string FormatReport(ImportCandidate candidate, ImportReport report)
    {
        var builder = new StringBuilder();
        builder.Append($"候选：{candidate.DisplayName}；状态：{report.Status}；哈希：{report.Hashes.Count}；修复：{report.Fixes.Count}");
        foreach (var issue in report.Issues)
        {
            builder.AppendLine().Append("  ").Append(issue);
        }

        return builder.ToString();
    }

    private void OpenMods_Click(object sender, RoutedEventArgs e) => OpenDirectory(_paths.ModsRoot);

    private void OpenLogs_Click(object sender, RoutedEventArgs e) => OpenDirectory(_paths.LogsRoot);

    private static void OpenDirectory(string path)
    {
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
    }

    private void SetBackground_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择启动器背景图",
            Filter = "图片文件|*.png;*.jpg;*.jpeg;*.bmp|所有文件|*.*",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            var source = new FileInfo(dialog.FileName);
            if (source.Length > 50L * 1024 * 1024)
            {
                throw new InvalidOperationException("背景图不能超过 50 MB。");
            }

            var extension = Path.GetExtension(source.Name).ToLowerInvariant();
            var target = Path.Combine(_paths.UiRoot, "background" + extension);
            File.Copy(source.FullName, target, true);
            _config.BackgroundImagePath = target;
            SaveConfig();
            LoadBackground();
            Log($"已设置背景图：{source.Name}");
            ShowToast("背景图已立即应用。");
        }
        catch (Exception ex)
        {
            ShowError("设置背景图失败", ex);
        }
    }

    private void ClearBackground_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var configured = _config.BackgroundImagePath;
            if (!string.IsNullOrWhiteSpace(configured)
                && FileSystemSafety.IsWithin(_paths.UiRoot, configured)
                && File.Exists(configured))
            {
                File.Delete(configured!);
            }

            _config.BackgroundImagePath = null;
            SaveConfig();
            LoadBackground();
            ShowToast("背景图已清除。");
        }
        catch (Exception ex)
        {
            ShowError("清除背景图失败", ex);
        }
    }

    private void LoadBackground()
    {
        var path = _config.BackgroundImagePath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            BackgroundImage.Source = null;
            BackgroundImage.Visibility = Visibility.Collapsed;
            BackgroundPathText.Text = "当前未设置背景图";
            return;
        }

        var image = PreviewImageLoader.Load(path, 2200);
        BackgroundImage.Source = image;
        BackgroundImage.Visibility = image is null ? Visibility.Collapsed : Visibility.Visible;
        BackgroundPathText.Text = image is null ? "背景图无法读取" : $"当前：{Path.GetFileName(path)}";
    }

    private void BehaviorOption_Changed(object sender, RoutedEventArgs e)
    {
        if (_config is null)
        {
            return;
        }

        _config.AutoHideAfterLiveSwitch = AutoHideCheckBox.IsChecked == true;
        _config.ReloadWhenRequired = ReloadRequiredCheckBox.IsChecked == true;
        _config.AutoReloadOnModChange = _config.ReloadWhenRequired;
        SaveConfig();
    }

    private void CloseBehavior_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_config is null
            || CloseBehaviorComboBox?.SelectedValue is not string value
            || !Enum.TryParse<WindowCloseBehavior>(value, ignoreCase: true, out var behavior))
        {
            return;
        }

        if (_config.CloseBehavior == behavior)
        {
            return;
        }

        _config.CloseBehavior = behavior;
        SaveConfig();
        ShowToast(behavior == WindowCloseBehavior.HideToBackground
            ? "关闭按钮将隐藏管理器；可用 Alt+W 恢复，或在此处点击“退出管理器”。"
            : "关闭按钮将直接退出管理器。");
    }

    private void ExitManager_Click(object sender, RoutedEventArgs e)
    {
        _allowWindowClose = true;
        Close();
    }

    private void LogSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_logger is not null)
        {
            RefreshLogs();
        }
    }

    private void RefreshLogs_Click(object sender, RoutedEventArgs e)
    {
        _logger.Reload();
        RefreshLogs();
    }

    private void CleanupLogs_Click(object sender, RoutedEventArgs e)
    {
        var result = _logger.Cleanup();
        _logger.Reload();
        RefreshLogs();
        if (!result.Succeeded)
        {
            ShowToast($"日志清理失败：{result.Error}", true);
            return;
        }

        ShowToast(result.RemovedEntries == 0
            ? $"日志已是最近 {AppLogger.MaximumEntries} 条，无需清理。"
            : $"已清理 {result.RemovedEntries} 条历史日志。当前保留最近 {AppLogger.MaximumEntries} 条。 ");
    }

    private void RefreshLogs()
    {
        var filter = LogSearchBox?.Text.Trim() ?? string.Empty;
        if (LogRetentionText is not null)
        {
            LogRetentionText.Text = $"自动保留最近 {AppLogger.MaximumEntries} 条；启动、刷新和日志达到上限时会自动清理。";
        }

        _visibleLogs.Clear();
        foreach (var entry in _logger.Entries.Where(entry =>
                     string.IsNullOrWhiteSpace(filter)
                     || entry.Message.Contains(filter, StringComparison.CurrentCultureIgnoreCase)))
        {
            _visibleLogs.Add(new LogRow(entry));
        }

        if (_visibleLogs.Count > 0)
        {
            LogListBox.ScrollIntoView(_visibleLogs[^1]);
        }
    }

    private void CopyLogs_Click(object sender, RoutedEventArgs e)
    {
        var text = string.Join(Environment.NewLine, _visibleLogs.Select(row => row.Entry.ToString()));
        if (!string.IsNullOrWhiteSpace(text))
        {
            Clipboard.SetText(text);
            ShowToast("当前筛选日志已复制。");
        }
    }
}

public sealed class LogRow
{
    public LogEntry Entry { get; }
    public string TimeText => Entry.Timestamp.ToString("HH:mm:ss");
    public string LevelText => Entry.Level switch { AppLogLevel.Error => "错误", AppLogLevel.Warning => "警告", _ => "信息" };
    public string Message => Entry.Message;
    public Brush LevelBrush => Entry.Level switch { AppLogLevel.Error => Brushes.LightSalmon, AppLogLevel.Warning => Brushes.Khaki, _ => Brushes.LightGreen };

    public LogRow(LogEntry entry) => Entry = entry;
}
