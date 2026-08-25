using System.Windows;
using System.Windows.Controls;
using ZZZModManager.Models;
using ZZZModManager.Services;

namespace ZZZModManager;

public sealed class CandidateSelectionWindow : Window
{
    private readonly List<(ImportCandidate Candidate, CheckBox CheckBox)> _items = [];
    public List<ImportCandidate> SelectedCandidates { get; private set; } = [];

    public CandidateSelectionWindow(IReadOnlyList<ImportCandidate> candidates)
    {
        Title = "选择压缩包中的 Mod";
        Width = 760;
        Height = 560;
        MinWidth = 620;
        MinHeight = 420;
        ResizeMode = ResizeMode.CanResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = (System.Windows.Media.Brush)FindResource("DialogBackgroundBrush");
        Foreground = (System.Windows.Media.Brush)FindResource("DialogTextBrush");
        FontFamily = new System.Windows.Media.FontFamily("Segoe UI, Microsoft YaHei UI");
        FontSize = 13;

        var root = new DockPanel
        {
            Margin = new Thickness(20),
            Background = (System.Windows.Media.Brush)FindResource("DialogBackgroundBrush")
        };
        var title = new TextBlock
        {
            Text = "检测到多个独立 Mod",
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
            Foreground = (System.Windows.Media.Brush)FindResource("DialogTextBrush"),
            Margin = new Thickness(4, 0, 0, 5)
        };
        DockPanel.SetDock(title, Dock.Top);
        root.Children.Add(title);
        var hint = new TextBlock
        {
            Text = $"请选择要安装的候选项，它们不会被错误合并。共 {candidates.Count} 项，安装后可分别管理。",
            Foreground = (System.Windows.Media.Brush)FindResource("DialogMutedTextBrush"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(4, 0, 0, 14)
        };
        DockPanel.SetDock(hint, Dock.Top);
        root.Children.Add(hint);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0)
        };
        var cancel = new Button
        {
            Content = "取消",
            Width = 88,
            Foreground = (System.Windows.Media.Brush)FindResource("DialogTextBrush"),
            Background = (System.Windows.Media.Brush)FindResource("DialogRaisedBrush"),
            BorderBrush = (System.Windows.Media.Brush)FindResource("DialogBorderBrush")
        };
        cancel.Click += (_, _) => DialogResult = false;
        var install = new Button
        {
            Content = "安装所选",
            Width = 110,
            Style = (Style)FindResource("PrimaryButton"),
            Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(7, 23, 17))
        };
        install.Click += (_, _) =>
        {
            SelectedCandidates = _items
                .Where(item => item.CheckBox.IsChecked == true)
                .Select(item => item.Candidate)
                .ToList();
            DialogResult = true;
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(install);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);

        var list = new StackPanel
        {
            Margin = new Thickness(8, 2, 8, 2),
            Background = (System.Windows.Media.Brush)FindResource("DialogSurfaceBrush")
        };
        foreach (var candidate in candidates)
        {
            var check = new CheckBox
            {
                Content = new TextBlock
                {
                    Text = $"{candidate.DisplayName}    ({candidate.RelativeRoot})",
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = (System.Windows.Media.Brush)FindResource("DialogTextBrush")
                },
                IsChecked = true,
                Foreground = (System.Windows.Media.Brush)FindResource("DialogTextBrush"),
                Background = System.Windows.Media.Brushes.Transparent,
                BorderBrush = (System.Windows.Media.Brush)FindResource("DialogBorderBrush"),
                FontSize = 13,
                Padding = new Thickness(4, 7, 4, 7),
                Margin = new Thickness(5, 4, 5, 4)
            };
            _items.Add((candidate, check));
            list.Children.Add(check);
        }

        root.Children.Add(new Border
        {
            Background = (System.Windows.Media.Brush)FindResource("DialogSurfaceBrush"),
            BorderBrush = (System.Windows.Media.Brush)FindResource("DialogBorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(5),
            Child = new ScrollViewer
            {
                Content = list,
                Background = System.Windows.Media.Brushes.Transparent,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            }
        });
        Content = root;
    }
}

public sealed class TextViewerWindow : Window
{
    public TextViewerWindow(string title, string text)
    {
        Title = title;
        Width = 920;
        Height = 680;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Content = new TextBox
        {
            Text = text,
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            Margin = new Thickness(12),
            FontFamily = new System.Windows.Media.FontFamily("Cascadia Mono, Consolas, Microsoft YaHei UI")
        };
    }
}

public sealed class ModDiagnosticsWindow : Window
{
    public ModDiagnosticsWindow(string modName, ModDiagnosticsReport report)
    {
        Title = $"诊断 · {modName}";
        Width = 860;
        Height = 640;
        MinWidth = 640;
        MinHeight = 460;
        ResizeMode = ResizeMode.CanResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = (System.Windows.Media.Brush)FindResource("DialogBackgroundBrush");
        Foreground = (System.Windows.Media.Brush)FindResource("DialogTextBrush");
        FontFamily = new System.Windows.Media.FontFamily("Segoe UI, Microsoft YaHei UI");
        FontSize = 13;

        var root = new DockPanel
        {
            Margin = new Thickness(20),
            Background = (System.Windows.Media.Brush)FindResource("DialogBackgroundBrush")
        };

        var title = new TextBlock
        {
            Text = modName,
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
            Foreground = (System.Windows.Media.Brush)FindResource("DialogTextBrush"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(4, 0, 0, 5)
        };
        DockPanel.SetDock(title, Dock.Top);
        root.Children.Add(title);

        var headline = new TextBlock
        {
            Text = report.Headline,
            Foreground = SeverityBrush(report.Severity),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(4, 0, 0, 14)
        };
        DockPanel.SetDock(headline, Dock.Top);
        root.Children.Add(headline);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0)
        };
        var copy = new Button
        {
            Content = "复制全文",
            Width = 100,
            Foreground = (System.Windows.Media.Brush)FindResource("DialogTextBrush"),
            Background = (System.Windows.Media.Brush)FindResource("DialogRaisedBrush"),
            BorderBrush = (System.Windows.Media.Brush)FindResource("DialogBorderBrush")
        };
        copy.Click += (_, _) =>
        {
            // Clipboard access can fail while another process holds it; the dialog
            // stays usable instead of surfacing an exception on a read-only view.
            try
            {
                Clipboard.SetText(report.ToPlainText());
            }
            catch (System.Runtime.InteropServices.COMException)
            {
            }
        };
        var close = new Button
        {
            Content = "关闭",
            Width = 88,
            Margin = new Thickness(10, 0, 0, 0),
            Foreground = (System.Windows.Media.Brush)FindResource("DialogTextBrush"),
            Background = (System.Windows.Media.Brush)FindResource("DialogRaisedBrush"),
            BorderBrush = (System.Windows.Media.Brush)FindResource("DialogBorderBrush")
        };
        close.Click += (_, _) => Close();
        buttons.Children.Add(copy);
        buttons.Children.Add(close);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);

        var body = new StackPanel { Margin = new Thickness(10, 6, 10, 6) };
        foreach (var section in report.Sections)
        {
            body.Children.Add(new TextBlock
            {
                Text = section.Title,
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                Foreground = (System.Windows.Media.Brush)FindResource("DialogTextBrush"),
                Margin = new Thickness(0, 10, 0, 6)
            });

            if (section.Lines.Count == 0)
            {
                body.Children.Add(new TextBlock
                {
                    Text = string.IsNullOrEmpty(section.EmptyText) ? "没有内容。" : section.EmptyText,
                    Foreground = (System.Windows.Media.Brush)FindResource("DialogMutedTextBrush"),
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 0, 4)
                });
                continue;
            }

            foreach (var line in section.Lines)
            {
                var row = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
                var tag = SeverityTag(line.Severity);
                row.Children.Add(new TextBlock
                {
                    Text = string.IsNullOrEmpty(tag) ? line.Title : $"{tag} {line.Title}",
                    Foreground = SeverityBrush(line.Severity),
                    TextWrapping = TextWrapping.Wrap
                });
                if (!string.IsNullOrEmpty(line.Detail))
                {
                    row.Children.Add(new TextBlock
                    {
                        Text = line.Detail,
                        Foreground = (System.Windows.Media.Brush)FindResource("DialogMutedTextBrush"),
                        TextWrapping = TextWrapping.Wrap,
                        FontFamily = new System.Windows.Media.FontFamily("Cascadia Mono, Consolas, Microsoft YaHei UI"),
                        Margin = new Thickness(0, 2, 0, 0)
                    });
                }

                body.Children.Add(row);
            }
        }

        root.Children.Add(new Border
        {
            Background = (System.Windows.Media.Brush)FindResource("DialogSurfaceBrush"),
            BorderBrush = (System.Windows.Media.Brush)FindResource("DialogBorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(5),
            Child = new ScrollViewer
            {
                Content = body,
                Background = System.Windows.Media.Brushes.Transparent,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            }
        });
        Content = root;
    }

    // Severity is carried by the tag text as well, so the report stays readable
    // without relying on color alone.
    private static string SeverityTag(IssueSeverity severity) => severity switch
    {
        IssueSeverity.Error => "【错误】",
        IssueSeverity.Warning => "【警告】",
        _ => string.Empty
    };

    private System.Windows.Media.Brush SeverityBrush(IssueSeverity severity) => severity switch
    {
        IssueSeverity.Error => (System.Windows.Media.Brush)FindResource("ErrorBrush"),
        IssueSeverity.Warning => (System.Windows.Media.Brush)FindResource("WarningBrush"),
        _ => (System.Windows.Media.Brush)FindResource("DialogTextBrush")
    };
}

public sealed class GroupSelectionWindow : Window
{
    private readonly ComboBox _groups;
    private readonly System.Collections.ObjectModel.ObservableCollection<GroupChoice> _choices = [];
    private readonly List<CharacterGroupInfo> _createdGroups = [];
    private readonly TextBox _newGroupName;
    private readonly TextBlock _groupError;

    public string? SelectedGroupKey { get; private set; }
    public IReadOnlyList<CharacterGroupInfo> CreatedGroups => _createdGroups;

    public GroupSelectionWindow(string? currentKey)
        : this(currentKey, CharacterGroupDetector.KnownGroups)
    {
    }

    public GroupSelectionWindow(string? currentKey, IReadOnlyList<CharacterGroupInfo> availableGroups)
    {
        Title = "修改角色分组";
        Width = 520;
        Height = 360;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = (System.Windows.Media.Brush)FindResource("DialogBackgroundBrush");
        Foreground = (System.Windows.Media.Brush)FindResource("DialogTextBrush");
        FontFamily = new System.Windows.Media.FontFamily("Segoe UI, Microsoft YaHei UI");
        FontSize = 13;

        _choices.Add(new GroupChoice(null, "自动识别", CharacterGroupKind.Unknown));
        foreach (var group in availableGroups
                     .Where(group => CharacterGroupDetector.IsRoleGroup(group.Kind)
                                     || group.Kind == CharacterGroupKind.Framework)
                     .DistinctBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
                     .OrderBy(group => group.Kind == CharacterGroupKind.Framework ? 1 : 0)
                     .ThenBy(group => group.DisplayName, StringComparer.CurrentCultureIgnoreCase))
        {
            _choices.Add(new GroupChoice(group.Key, group.DisplayName, group.Kind));
        }

        var root = new Grid
        {
            Margin = new Thickness(20),
            Background = (System.Windows.Media.Brush)FindResource("DialogBackgroundBrush")
        };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.Children.Add(new TextBlock
        {
            Text = "选择稳定的角色分组",
            FontSize = 19,
            FontWeight = FontWeights.SemiBold
        });
        var hint = new TextBlock
        {
            Text = "同一角色默认只能启用一个 Mod；通用依赖不参与单选。自动发现的角色也会保存在这里。",
            Margin = new Thickness(0, 7, 0, 12),
            Foreground = (System.Windows.Media.Brush)FindResource("DialogMutedTextBrush"),
            TextWrapping = TextWrapping.Wrap
        };
        Grid.SetRow(hint, 1);
        root.Children.Add(hint);

        _groups = new ComboBox
        {
            ItemsSource = _choices,
            DisplayMemberPath = nameof(GroupChoice.DisplayName),
            SelectedItem = _choices.FirstOrDefault(choice => string.Equals(choice.Key, currentKey, StringComparison.OrdinalIgnoreCase)) ?? _choices[0],
            MinWidth = 420,
            VerticalAlignment = VerticalAlignment.Top
        };

        ApplyComboBoxTheme(_groups);
        Grid.SetRow(_groups, 2);
        root.Children.Add(_groups);

        var newGroupPanel = new Grid { Margin = new Thickness(0, 14, 0, 0) };
        newGroupPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        newGroupPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _newGroupName = new TextBox
        {
            Height = 36,
            Padding = new Thickness(10, 7, 10, 7),
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = (System.Windows.Media.Brush)FindResource("DialogRaisedBrush"),
            Foreground = (System.Windows.Media.Brush)FindResource("DialogTextBrush"),
            BorderBrush = (System.Windows.Media.Brush)FindResource("DialogBorderBrush"),
            ToolTip = "例如：Alice 夏日服装"
        };
        Grid.SetColumn(_newGroupName, 0);
        newGroupPanel.Children.Add(_newGroupName);

        var addGroup = new Button
        {
            Content = "添加分组",
            Width = 100,
            Height = 36,
            Margin = new Thickness(10, 0, 0, 0),
            Style = (Style)FindResource("PrimaryButton")
        };
        addGroup.Click += AddCustomGroup_Click;
        Grid.SetColumn(addGroup, 1);
        newGroupPanel.Children.Add(addGroup);
        Grid.SetRow(newGroupPanel, 3);
        root.Children.Add(newGroupPanel);

        _groupError = new TextBlock
        {
            Margin = new Thickness(2, 7, 0, 0),
            Foreground = System.Windows.Media.Brushes.Khaki,
            TextWrapping = TextWrapping.Wrap
        };
        Grid.SetRow(_groupError, 4);
        root.Children.Add(_groupError);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 12, 0, 0)
        };
        var cancel = new Button
        {
            Content = "取消",
            Width = 86,
            Foreground = (System.Windows.Media.Brush)FindResource("DialogTextBrush"),
            Background = (System.Windows.Media.Brush)FindResource("DialogRaisedBrush"),
            BorderBrush = (System.Windows.Media.Brush)FindResource("DialogBorderBrush")
        };
        cancel.Click += (_, _) => DialogResult = false;
        var save = new Button { Content = "保存", Width = 92, Style = (Style)FindResource("PrimaryButton") };
        save.Click += (_, _) =>
        {
            SelectedGroupKey = (_groups.SelectedItem as GroupChoice)?.Key;
            DialogResult = true;
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(save);
        Grid.SetRow(buttons, 5);
        root.Children.Add(buttons);
        Content = root;
    }

    private void AddCustomGroup_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var group = CharacterGroupDetector.CreateCustomGroup(_newGroupName.Text);
            var choice = _choices.FirstOrDefault(item =>
                string.Equals(item.Key, group.Key, StringComparison.OrdinalIgnoreCase));
            if (choice is null)
            {
                choice = new GroupChoice(group.Key, group.DisplayName, group.Kind);
                _choices.Add(choice);
                _createdGroups.Add(group);
            }

            _groups.SelectedItem = choice;
            _newGroupName.Clear();
            _groupError.Text = $"已添加：{group.DisplayName}";
            _groupError.Foreground = (System.Windows.Media.Brush)FindResource("AccentBrush");
        }
        catch (ArgumentException ex)
        {
            _groupError.Text = ex.Message;
            _groupError.Foreground = System.Windows.Media.Brushes.Khaki;
        }
    }

    private void ApplyComboBoxTheme(ComboBox combo)
    {
        // The status filter on the main page is light. This dialog owns both
        // the closed control and popup item styles so every option stays
        // readable on the dark surface.
        // Setter values must stay DynamicResource references: sealing a style
        // freezes any Freezable handed to it directly, and these brushes are the
        // live theme instances that AppearanceController repaints in place.
        var baseComboStyle = TryFindResource(typeof(ComboBox)) as Style;
        var comboStyle = baseComboStyle is null
            ? new Style(typeof(ComboBox))
            : new Style(typeof(ComboBox), baseComboStyle);
        comboStyle.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension("DialogTextBrush")));
        comboStyle.Setters.Add(new Setter(Control.BackgroundProperty, new DynamicResourceExtension("DialogRaisedBrush")));
        comboStyle.Setters.Add(new Setter(Control.BorderBrushProperty, new DynamicResourceExtension("DialogBorderBrush")));
        comboStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(10, 7, 10, 7)));
        comboStyle.Setters.Add(new Setter(Control.MinHeightProperty, 38.0));
        combo.Style = comboStyle;

        var baseItemStyle = TryFindResource(typeof(ComboBoxItem)) as Style;
        var itemStyle = baseItemStyle is null
            ? new Style(typeof(ComboBoxItem))
            : new Style(typeof(ComboBoxItem), baseItemStyle);
        itemStyle.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension("DialogTextBrush")));
        itemStyle.Setters.Add(new Setter(Control.BackgroundProperty, new DynamicResourceExtension("DialogSurfaceBrush")));
        itemStyle.Setters.Add(new Setter(Control.BorderBrushProperty, new DynamicResourceExtension("DialogBorderBrush")));
        itemStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(10, 8, 10, 8)));
        itemStyle.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        var hoverTrigger = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hoverTrigger.Setters.Add(new Setter(Control.BackgroundProperty, new DynamicResourceExtension("AccentStrongBrush")));
        hoverTrigger.Setters.Add(new Setter(
            Control.ForegroundProperty,
            new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(7, 23, 17))));
        itemStyle.Triggers.Add(hoverTrigger);
        combo.ItemContainerStyle = itemStyle;

        var comboTextStyle = new Style(typeof(TextBlock));
        comboTextStyle.Setters.Add(new Setter(TextBlock.ForegroundProperty, new DynamicResourceExtension("DialogTextBrush")));
        comboTextStyle.Setters.Add(new Setter(TextBlock.FontSizeProperty, 13.0));
        combo.Resources.Add(typeof(TextBlock), comboTextStyle);
        combo.Resources[SystemColors.WindowBrushKey] = FindResource("DialogSurfaceBrush");
        combo.Resources[SystemColors.ControlBrushKey] = FindResource("DialogSurfaceBrush");
        combo.Resources[SystemColors.WindowTextBrushKey] = FindResource("DialogTextBrush");
        combo.Resources[SystemColors.ControlTextBrushKey] = FindResource("DialogTextBrush");
        combo.Resources[SystemColors.HighlightBrushKey] = FindResource("AccentStrongBrush");
        combo.Resources[SystemColors.HighlightTextBrushKey] = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(7, 23, 17));
    }

    private sealed record GroupChoice(string? Key, string DisplayName, CharacterGroupKind Kind);
}
