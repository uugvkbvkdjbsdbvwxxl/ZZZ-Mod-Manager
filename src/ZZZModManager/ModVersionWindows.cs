using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using ZZZModManager.Models;

namespace ZZZModManager;

public sealed class ModUpdateReviewWindow : Window
{
    public ModUpdateReviewWindow(string modName, string candidateName, ModUpdatePreview preview)
    {
        Title = $"更新差异 · {modName}";
        Width = 820;
        Height = 620;
        MinWidth = 640;
        MinHeight = 460;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = ResourceBrush("DialogBackgroundBrush");
        Foreground = ResourceBrush("DialogTextBrush");
        FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI");
        FontSize = 13;

        var root = new DockPanel { Margin = new Thickness(22) };
        var heading = new TextBlock
        {
            Text = "确认安装副本更新",
            FontSize = 21,
            FontWeight = FontWeights.SemiBold,
            Foreground = ResourceBrush("DialogTextBrush")
        };
        DockPanel.SetDock(heading, Dock.Top);
        root.Children.Add(heading);

        var description = new TextBlock
        {
            Text = $"{modName}  ←  {candidateName}\n更新前会创建完整版本备份；此操作不会修改原始下载文件。",
            Margin = new Thickness(0, 5, 0, 14),
            Foreground = ResourceBrush("DialogMutedTextBrush"),
            TextWrapping = TextWrapping.Wrap
        };
        DockPanel.SetDock(description, Dock.Top);
        root.Children.Add(description);

        var summary = new UniformGrid
        {
            Columns = 4,
            Margin = new Thickness(0, 0, 0, 14)
        };
        summary.Children.Add(CreateMetric("新增", preview.AddedCount, "SuccessBrush"));
        summary.Children.Add(CreateMetric("修改", preview.ModifiedCount, "InfoBrush"));
        summary.Children.Add(CreateMetric("移除", preview.RemovedCount, "WarningBrush"));
        summary.Children.Add(CreateMetric("不变", preview.UnchangedCount, "DialogMutedTextBrush"));
        DockPanel.SetDock(summary, Dock.Top);
        root.Children.Add(summary);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0)
        };
        var cancel = new Button
        {
            Content = "取消",
            Width = 92,
            Margin = new Thickness(0, 0, 8, 0)
        };
        AutomationProperties.SetName(cancel, "取消 Mod 更新");
        cancel.Click += (_, _) => DialogResult = false;
        var confirm = new Button
        {
            Content = "备份并更新",
            Width = 126,
            Style = (Style)FindResource("PrimaryButton"),
            IsEnabled = preview.HasChanges
        };
        AutomationProperties.SetName(confirm, "备份并更新 Mod");
        confirm.Click += (_, _) => DialogResult = true;
        buttons.Children.Add(cancel);
        buttons.Children.Add(confirm);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);

        var changedFiles = preview.Files
            .Where(file => file.Kind != ModFileDifferenceKind.Unchanged)
            .Select(FormatDifference)
            .ToList();
        var list = new ListBox
        {
            ItemsSource = changedFiles.Count == 0 ? ["没有检测到内容变化。"] : changedFiles,
            FontFamily = new FontFamily("Cascadia Mono, Consolas, Microsoft YaHei UI"),
            Background = ResourceBrush("DialogSurfaceBrush"),
            Foreground = ResourceBrush("DialogTextBrush"),
            BorderBrush = ResourceBrush("DialogBorderBrush"),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(8),
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
        AutomationProperties.SetName(list, "Mod 更新文件差异");
        root.Children.Add(list);
        Content = root;
    }

    private Border CreateMetric(string label, int value, string brushKey) => new()
    {
        Margin = new Thickness(0, 0, 8, 0),
        Padding = new Thickness(12, 9, 12, 9),
        Background = ResourceBrush("DialogSurfaceBrush"),
        BorderBrush = ResourceBrush("DialogBorderBrush"),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(8),
        Child = new StackPanel
        {
            Children =
            {
                new TextBlock { Text = value.ToString(CultureInfo.InvariantCulture), FontSize = 19, FontWeight = FontWeights.SemiBold, Foreground = ResourceBrush(brushKey) },
                new TextBlock { Text = label, Margin = new Thickness(0, 2, 0, 0), Foreground = ResourceBrush("DialogMutedTextBrush") }
            }
        }
    };

    private static string FormatDifference(ModFileDifference file)
    {
        var marker = file.Kind switch
        {
            ModFileDifferenceKind.Added => "+",
            ModFileDifferenceKind.Modified => "~",
            ModFileDifferenceKind.Removed => "-",
            _ => " "
        };
        return $"{marker}  {file.RelativePath}    {FormatBytes(file.PreviousBytes)} → {FormatBytes(file.NewBytes)}";
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1024L * 1024L * 1024L => $"{bytes / (1024d * 1024d * 1024d):0.##} GB",
        >= 1024L * 1024L => $"{bytes / (1024d * 1024d):0.##} MB",
        >= 1024L => $"{bytes / 1024d:0.##} KB",
        _ => $"{bytes} B"
    };

    private Brush ResourceBrush(string key) => (Brush)FindResource(key);
}

public sealed class ModVersionHistoryWindow : Window
{
    private readonly ListBox _versions = new();
    private readonly Button _rollbackButton;

    public string? SelectedBackupId { get; private set; }

    public ModVersionHistoryWindow(string modName, IReadOnlyList<ModVersionBackup> backups)
    {
        Title = $"版本历史 · {modName}";
        Width = 760;
        Height = 560;
        MinWidth = 620;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = ResourceBrush("DialogBackgroundBrush");
        Foreground = ResourceBrush("DialogTextBrush");
        FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI");
        FontSize = 13;

        var root = new DockPanel { Margin = new Thickness(22) };
        var heading = new TextBlock
        {
            Text = "安装副本版本历史",
            FontSize = 21,
            FontWeight = FontWeights.SemiBold,
            Foreground = ResourceBrush("DialogTextBrush")
        };
        DockPanel.SetDock(heading, Dock.Top);
        root.Children.Add(heading);
        var description = new TextBlock
        {
            Text = "选择一个更新前版本进行回滚。回滚前仍会备份当前版本，并保持 Mod 当前的启用或禁用状态。",
            Margin = new Thickness(0, 5, 0, 14),
            Foreground = ResourceBrush("DialogMutedTextBrush"),
            TextWrapping = TextWrapping.Wrap
        };
        DockPanel.SetDock(description, Dock.Top);
        root.Children.Add(description);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0)
        };
        var close = new Button { Content = "关闭", Width = 92, Margin = new Thickness(0, 0, 8, 0) };
        close.Click += (_, _) => DialogResult = false;
        _rollbackButton = new Button
        {
            Content = "回滚到所选版本",
            Width = 146,
            Style = (Style)FindResource("PrimaryButton"),
            IsEnabled = false
        };
        AutomationProperties.SetName(_rollbackButton, "回滚到所选 Mod 版本");
        _rollbackButton.Click += (_, _) =>
        {
            SelectedBackupId = (_versions.SelectedItem as BackupChoice)?.Backup.BackupId;
            DialogResult = SelectedBackupId is not null;
        };
        buttons.Children.Add(close);
        buttons.Children.Add(_rollbackButton);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);

        _versions.ItemsSource = backups.Select(backup => new BackupChoice(backup)).ToList();
        _versions.SelectionChanged += (_, _) => _rollbackButton.IsEnabled = _versions.SelectedItem is BackupChoice;
        _versions.Background = ResourceBrush("DialogSurfaceBrush");
        _versions.Foreground = ResourceBrush("DialogTextBrush");
        _versions.BorderBrush = ResourceBrush("DialogBorderBrush");
        _versions.BorderThickness = new Thickness(1);
        _versions.Padding = new Thickness(8);
        AutomationProperties.SetName(_versions, "Mod 版本备份列表");
        if (backups.Count == 0)
        {
            _versions.ItemsSource = new[] { "还没有版本备份。首次更新前会自动创建。" };
        }

        root.Children.Add(_versions);
        Content = root;
    }

    private Brush ResourceBrush(string key) => (Brush)FindResource(key);

    private sealed record BackupChoice(ModVersionBackup Backup)
    {
        public override string ToString() =>
            $"{Backup.CreatedAt.LocalDateTime:yyyy-MM-dd HH:mm:ss}    {Backup.Reason}    "
            + $"{Backup.FileCount} 个文件 · {FormatBytes(Backup.TotalBytes)}";
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1024L * 1024L * 1024L => $"{bytes / (1024d * 1024d * 1024d):0.##} GB",
        >= 1024L * 1024L => $"{bytes / (1024d * 1024d):0.##} MB",
        >= 1024L => $"{bytes / 1024d:0.##} KB",
        _ => $"{bytes} B"
    };
}
