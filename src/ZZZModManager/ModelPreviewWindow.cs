using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using ZZZModManager.Services;
using NumericsVector3 = System.Numerics.Vector3;

namespace ZZZModManager;

public sealed class ModelPreviewWindow : Window
{
    private readonly string _modName;
    private readonly string? _modDirectory;
    private readonly IModModelPreviewLoader? _loader;
    private readonly CharacterFaceCaptureService? _faceCapture;
    private readonly IModelPreviewShaderBackend _shaderBackend = new CpuModelPreviewShaderBackend();
    private readonly PerspectiveCamera _camera = new() { FieldOfView = 38 };
    private readonly Viewport3D _viewport = new();
    private readonly Model3DGroup _visibleMeshes = new();
    private readonly Dictionary<CheckBox, Model3D> _meshToggles = [];
    private readonly Dictionary<string, BitmapSource> _textureBitmaps = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ModelPreviewTexture?> _shadedTextures = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, double> _variantValues = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ComboBox> _variantSelectors = [];
    private readonly StackPanel _variantPanel = new();
    private readonly StackPanel _materialPanel = new();
    private readonly StackPanel _facePanel = new();
    private readonly StackPanel _partsPanel = new() { Margin = new Thickness(10, 8, 10, 8) };
    private readonly StackPanel _diagnosticItems = new() { Margin = new Thickness(4, 6, 4, 4) };
    private readonly Separator _variantSeparator = new() { Margin = new Thickness(2, 10, 2, 6) };
    private readonly Separator _materialSeparator = new() { Margin = new Thickness(2, 10, 2, 6) };
    private readonly Separator _faceSeparator = new() { Margin = new Thickness(2, 10, 2, 6) };
    private readonly TextBlock _faceStatus = new() { Margin = new Thickness(2, 0, 2, 8), TextWrapping = TextWrapping.Wrap };
    private readonly List<Button> _faceCaptureButtons = [];
    private readonly TextBlock _statistics = new()
    {
        Margin = new Thickness(0, 4, 0, 0),
        TextWrapping = TextWrapping.Wrap
    };
    private readonly TextBlock _footer = new()
    {
        Margin = new Thickness(4, 12, 4, 0),
        TextWrapping = TextWrapping.Wrap
    };
    private Border? _viewportSurface;
    private Point3D _initialTarget;
    private double _initialDistance;
    private Point3D _target;
    private double _distance;
    private double _yaw = 0.55;
    private double _pitch = 0.12;
    private Point _lastPointer;
    private PointerAction _pointerAction;
    private bool _rebuildingVariantControls;
    private bool _enhancedMaterials;
    private bool _normalMapping;
    private bool _outlineEnabled;
    private int _loadRevision;
    private ModelPreviewScene? _currentScene;

    public ModelPreviewWindow(string modName, ModelPreviewScene scene)
        : this(modName, null, null, scene, null)
    {
    }

    public ModelPreviewWindow(
        string modName,
        string? modDirectory,
        IModModelPreviewLoader? loader,
        ModelPreviewScene scene,
        CharacterFaceCaptureService? faceCapture = null)
    {
        _modName = modName;
        _modDirectory = modDirectory;
        _loader = loader;
        _faceCapture = faceCapture;
        Title = $"3D 预览 · {modName}";
        Width = 1120;
        Height = 760;
        MinWidth = 860;
        MinHeight = 560;
        ResizeMode = ResizeMode.CanResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = ResourceBrush("DialogBackgroundBrush");
        Foreground = ResourceBrush("DialogTextBrush");
        FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI");
        FontSize = 13;

        Content = BuildContent(modName);
        ApplyScene(MergeCapturedFace(scene), true);
        KeyDown += Window_KeyDown;
        Closed += (_, _) => _loadRevision++;
    }

    private UIElement BuildContent(string modName)
    {
        var root = new DockPanel
        {
            Margin = new Thickness(18),
            Background = ResourceBrush("DialogBackgroundBrush")
        };

        var header = new DockPanel { Margin = new Thickness(2, 0, 2, 14) };
        var headerButtons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        var reset = new Button
        {
            Content = "复位视角",
            MinWidth = 96,
            ToolTip = "恢复自动取景（R）"
        };
        reset.Click += (_, _) => ResetCamera();
        AutomationProperties.SetName(reset, "复位 3D 预览视角");
        var export = new Button
        {
            Content = "导出 PNG",
            MinWidth = 96,
            Margin = new Thickness(8, 0, 0, 0),
            ToolTip = "按当前视角导出 PNG 图片"
        };
        export.Click += ExportPreview_Click;
        AutomationProperties.SetName(export, "导出 3D 预览 PNG");
        var close = new Button
        {
            Content = "关闭",
            MinWidth = 82,
            Margin = new Thickness(8, 0, 0, 0)
        };
        close.Click += (_, _) => Close();
        headerButtons.Children.Add(reset);
        headerButtons.Children.Add(export);
        headerButtons.Children.Add(close);
        DockPanel.SetDock(headerButtons, Dock.Right);
        header.Children.Add(headerButtons);

        var heading = new StackPanel();
        heading.Children.Add(new TextBlock
        {
            Text = modName,
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
            Foreground = ResourceBrush("DialogTextBrush"),
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        _statistics.Foreground = ResourceBrush("DialogMutedTextBrush");
        heading.Children.Add(_statistics);
        header.Children.Add(heading);
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);

        DockPanel.SetDock(_footer, Dock.Bottom);
        root.Children.Add(_footer);

        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(284) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var sidebar = new StackPanel { Margin = new Thickness(6) };
        sidebar.Children.Add(_variantPanel);
        sidebar.Children.Add(_variantSeparator);
        ConfigureMaterialControls();
        sidebar.Children.Add(_materialPanel);
        sidebar.Children.Add(_materialSeparator);
        ConfigureFaceCaptureControls();
        sidebar.Children.Add(_facePanel);
        sidebar.Children.Add(_faceSeparator);
        sidebar.Children.Add(_partsPanel);
        var diagnostics = new Expander
        {
            Header = "兼容诊断",
            Margin = new Thickness(10, 8, 10, 8),
            IsExpanded = false,
            Content = _diagnosticItems
        };
        AutomationProperties.SetName(diagnostics, "3D 预览兼容诊断");
        sidebar.Children.Add(diagnostics);

        var partsBorder = new Border
        {
            Background = ResourceBrush("DialogSurfaceBrush"),
            BorderBrush = ResourceBrush("DialogBorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = ResourceCornerRadius("PanelRadius", 12),
            Padding = new Thickness(4),
            Child = new ScrollViewer
            {
                Content = sidebar,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            }
        };
        Grid.SetColumn(partsBorder, 0);
        body.Children.Add(partsBorder);

        ConfigureViewport();
        _viewportSurface = new Border
        {
            Background = ResourceBrush("SurfaceSunkenBrush"),
            BorderBrush = ResourceBrush("DialogBorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = ResourceCornerRadius("PanelRadius", 12),
            ClipToBounds = true,
            Child = _viewport
        };
        AutomationProperties.SetName(_viewportSurface, "3D 模型视口");
        Grid.SetColumn(_viewportSurface, 2);
        body.Children.Add(_viewportSurface);
        root.Children.Add(body);
        return root;
    }

    private void ConfigureViewport()
    {
        _viewport.Camera = _camera;
        _viewport.Focusable = true;
        _viewport.ClipToBounds = true;
        var scene = new Model3DGroup();
        scene.Children.Add(new AmbientLight(ColorFrom("SecondaryTextBrush")));
        scene.Children.Add(new DirectionalLight(ColorFrom("TextBrush"), new Vector3D(-0.45, -0.75, -1)));
        scene.Children.Add(new DirectionalLight(ColorFrom("InfoBrush"), new Vector3D(0.65, 0.15, 0.8)));
        scene.Children.Add(_visibleMeshes);
        _viewport.Children.Add(new ModelVisual3D { Content = scene });

        _viewport.MouseDown += Viewport_MouseDown;
        _viewport.MouseUp += EndPointerAction;
        _viewport.MouseMove += Viewport_MouseMove;
        _viewport.MouseWheel += Viewport_MouseWheel;
    }

    private void ApplyScene(ModelPreviewScene scene, bool resetCamera)
    {
        _currentScene = scene;
        _statistics.Text = $"{scene.Meshes.Count} 个网格 · {scene.Diagnostics.TextureCount} 张 DDS 贴图 · "
            + $"{scene.VertexCount:N0} 个顶点 · {scene.TriangleCount:N0} 个三角形";
        RebuildVariantControls(scene);
        RefreshMaterialControls(scene);
        RefreshFaceCaptureControls();
        RebuildMeshes(scene);
        RebuildDiagnostics(scene);
        ConfigureBounds(scene);
        if (resetCamera)
        {
            ResetCamera();
        }
    }

    private void ConfigureMaterialControls()
    {
        _materialPanel.Margin = new Thickness(10, 8, 10, 2);
        _materialPanel.Children.Add(CreateSectionHeading("材质与轮廓"));
        _materialPanel.Children.Add(new TextBlock
        {
            Text = "实验性 CPU Shader 只读合成贴图，不改写 Mod；默认保持原始 Diffuse 效果。",
            Foreground = ResourceBrush("DialogMutedTextBrush"),
            Margin = new Thickness(2, 0, 2, 7),
            TextWrapping = TextWrapping.Wrap
        });
        var material = new CheckBox
        {
            Content = "增强 LightMap / MaterialMap",
            IsChecked = false,
            Margin = new Thickness(2, 4, 2, 4),
            Foreground = ResourceBrush("DialogTextBrush")
        };
        material.Click += (_, _) =>
        {
            _enhancedMaterials = material.IsChecked == true;
            if (_currentScene is not null)
            {
                RebuildMeshes(_currentScene);
            }
        };
        AutomationProperties.SetName(material, "启用 LightMap MaterialMap 近似材质");
        material.Tag = "material-toggle";
        _materialPanel.Children.Add(material);

        var normal = new CheckBox
        {
            Content = "NormalMap 法线光照",
            IsChecked = false,
            Margin = new Thickness(2, 4, 2, 4),
            Foreground = ResourceBrush("DialogTextBrush")
        };
        normal.Click += (_, _) =>
        {
            _normalMapping = normal.IsChecked == true;
            if (_currentScene is not null)
            {
                RebuildMeshes(_currentScene);
            }
        };
        AutomationProperties.SetName(normal, "启用 NormalMap 自定义 Shader 光照");
        normal.Tag = "normal-toggle";
        _materialPanel.Children.Add(normal);

        var outline = new CheckBox
        {
            Content = "贴图轮廓增强（实验性）",
            IsChecked = false,
            Margin = new Thickness(2, 4, 2, 4),
            Foreground = ResourceBrush("DialogTextBrush")
        };
        outline.Click += (_, _) =>
        {
            _outlineEnabled = outline.IsChecked == true;
            if (_currentScene is not null)
            {
                RebuildMeshes(_currentScene);
            }
        };
        AutomationProperties.SetName(outline, "启用 3D 预览贴图轮廓增强");
        outline.Tag = "outline-toggle";
        _materialPanel.Children.Add(outline);
    }

    private void RefreshMaterialControls(ModelPreviewScene scene)
    {
        var hasMaterialMaps = scene.Meshes.Any(mesh => mesh.LightTexture is not null || mesh.MaterialTexture is not null);
        var materialToggle = _materialPanel.Children
            .OfType<CheckBox>()
            .First(checkBox => string.Equals(checkBox.Tag as string, "material-toggle", StringComparison.Ordinal));
        materialToggle.IsEnabled = hasMaterialMaps;
        materialToggle.ToolTip = hasMaterialMaps
            ? "在静态预览中合成 LightMap 与 MaterialMap"
            : "此 Mod 没有绑定 LightMap 或 MaterialMap";
        var normalToggle = _materialPanel.Children
            .OfType<CheckBox>()
            .First(checkBox => string.Equals(checkBox.Tag as string, "normal-toggle", StringComparison.Ordinal));
        var hasNormalMaps = scene.Meshes.Any(mesh => mesh.NormalTexture is not null);
        normalToggle.IsEnabled = hasNormalMaps;
        normalToggle.ToolTip = hasNormalMaps
            ? "使用 CPU Shader 计算 NormalMap 的近似法线光照"
            : "此 Mod 没有绑定 NormalMap";
        _materialPanel.Visibility = Visibility.Visible;
        _materialSeparator.Visibility = Visibility.Visible;
    }

    private void ConfigureFaceCaptureControls()
    {
        _facePanel.Margin = new Thickness(10, 8, 10, 2);
        _facePanel.Children.Add(CreateSectionHeading("游戏原始头脸"));
        _faceStatus.Foreground = ResourceBrush("DialogMutedTextBrush");
        _facePanel.Children.Add(_faceStatus);
        _facePanel.Children.Add(new TextBlock
        {
            Text = "先准备安全采集并完全重启游戏，再让对应角色单独出现在画面中按 F8。只缓存头脸缓冲与贴图；缓存不会写入 Mod 或游戏文件。",
            Foreground = ResourceBrush("DialogMutedTextBrush"),
            Margin = new Thickness(2, 0, 2, 8),
            TextWrapping = TextWrapping.Wrap
        });

        var prepare = new Button
        {
            Content = "1. 准备安全采集",
            MinHeight = 34,
            Margin = new Thickness(2, 2, 2, 5)
        };
        prepare.Click += PrepareSafeFaceCapture_Click;
        AutomationProperties.SetName(prepare, "准备安全的游戏头脸帧分析采集");
        _faceCaptureButtons.Add(prepare);
        _facePanel.Children.Add(prepare);

        var latest = new Button
        {
            Content = "2. 导入最新 F8 转储",
            MinHeight = 34,
            Margin = new Thickness(2, 0, 2, 5)
        };
        latest.Click += ImportLatestFaceCapture_Click;
        AutomationProperties.SetName(latest, "导入最新游戏头脸帧分析转储");
        _faceCaptureButtons.Add(latest);
        _facePanel.Children.Add(latest);

        var choose = new Button
        {
            Content = "选择转储目录",
            MinHeight = 34,
            Margin = new Thickness(2, 0, 2, 2)
        };
        choose.Click += SelectFaceCapture_Click;
        AutomationProperties.SetName(choose, "选择游戏头脸帧分析目录");
        _faceCaptureButtons.Add(choose);
        _facePanel.Children.Add(choose);
    }

    private void PrepareSafeFaceCapture_Click(object sender, RoutedEventArgs e)
    {
        if (_faceCapture is null || string.IsNullOrWhiteSpace(_modDirectory))
        {
            return;
        }

        try
        {
            var preparation = _faceCapture.PrepareSafeCapture(_modDirectory);
            _footer.Text = preparation.Changed
                ? "安全采集已准备；ZZMI 配置原件已备份。"
                : "安全采集已经处于就绪状态。";
            _footer.Foreground = ResourceBrush("SuccessBrush");
            MessageBox.Show(
                this,
                $"已为 {preparation.DisplayName} 准备安全采集。\n\n"
                + $"{preparation.ActivationInstruction}。快捷键重载无法启用 FrameAnalysis 上下文。重启后让该角色单独出现在画面中，按 F8，并等待左上角分析提示消失。\n\n"
                + "最后回到本窗口，点击“2. 导入最新 F8 转储”。",
                "安全采集已就绪",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or InvalidOperationException)
        {
            _footer.Text = "安全采集准备失败：" + ex.Message;
            _footer.Foreground = ResourceBrush("WarningBrush");
            MessageBox.Show(
                this,
                ex.Message,
                "无法准备游戏头脸采集",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void RefreshFaceCaptureControls()
    {
        if (_faceCapture is null || string.IsNullOrWhiteSpace(_modDirectory))
        {
            _facePanel.Visibility = Visibility.Collapsed;
            _faceSeparator.Visibility = Visibility.Collapsed;
            return;
        }

        CharacterFaceCacheStatus status;
        try
        {
            status = _faceCapture.GetStatus(_modDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            _facePanel.Visibility = Visibility.Visible;
            _faceSeparator.Visibility = Visibility.Visible;
            _faceStatus.Text = "头脸缓存状态读取失败：" + ex.Message;
            _faceStatus.Foreground = ResourceBrush("WarningBrush");
            SetFaceCaptureButtonsEnabled(false);
            return;
        }

        _facePanel.Visibility = status.IsRecognized ? Visibility.Visible : Visibility.Collapsed;
        _faceSeparator.Visibility = _facePanel.Visibility;
        if (!status.IsRecognized)
        {
            return;
        }

        _faceStatus.Foreground = status.HasCache
            ? ResourceBrush("SuccessBrush")
            : ResourceBrush("DialogMutedTextBrush");
        _faceStatus.Text = status.HasCache
            ? $"已合并 {status.DisplayName} · {status.GameVersion} · {status.MeshCount} 个头脸网格"
            : $"已识别 {status.DisplayName} · {status.GameVersion}；尚未采集原始头脸。";
        SetFaceCaptureButtonsEnabled(_loader is not null);
    }

    private ModelPreviewScene MergeCapturedFace(ModelPreviewScene scene)
    {
        if (_faceCapture is null || string.IsNullOrWhiteSpace(_modDirectory))
        {
            return scene;
        }

        try
        {
            return _faceCapture.MergeCached(_modDirectory, scene);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return scene with { Warnings = [.. scene.Warnings, "原始头脸缓存：" + ex.Message] };
        }
    }

    private async void ImportLatestFaceCapture_Click(object sender, RoutedEventArgs e)
    {
        if (_faceCapture is null)
        {
            return;
        }

        var directory = _faceCapture.FindLatestFrameAnalysis();
        if (directory is null)
        {
            MessageBox.Show(
                this,
                "没有找到 FrameAnalysis 转储。\n\n请先点击“1. 准备安全采集”并完全退出、重新启动游戏；快捷键重载无法启用 FrameAnalysis 上下文。让对应角色单独出现在画面中，按 F8 并等待转储完成，然后回到这里重试。",
                "采集游戏原始头脸",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        await ImportFaceCaptureAsync(directory);
    }

    private async void SelectFaceCapture_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "选择 FrameAnalysis-... 转储目录" };
        if (dialog.ShowDialog(this) == true)
        {
            await ImportFaceCaptureAsync(dialog.FolderName);
        }
    }

    private async Task ImportFaceCaptureAsync(string captureDirectory)
    {
        if (_faceCapture is null
            || _loader is null
            || string.IsNullOrWhiteSpace(_modDirectory))
        {
            return;
        }

        var revision = ++_loadRevision;
        SetFaceCaptureButtonsEnabled(false);
        SetVariantSelectorsEnabled(false);
        _footer.Text = "正在筛选、验证并缓存原始头脸…";
        _footer.Foreground = ResourceBrush("InfoBrush");
        var requestedValues = new Dictionary<string, double>(_variantValues, StringComparer.OrdinalIgnoreCase);
        try
        {
            var result = await Task.Run(() => _faceCapture.Import(_modDirectory, captureDirectory));
            var scene = await Task.Run(() => _loader.Load(_modDirectory, requestedValues));
            if (revision != _loadRevision)
            {
                return;
            }

            ApplyScene(MergeCapturedFace(scene), true);
            _footer.Text = $"已缓存并合并 {result.DisplayName}：{result.MeshCount} 个头脸网格。";
            _footer.Foreground = ResourceBrush("SuccessBrush");
            if (result.Warnings.Count > 0)
            {
                MessageBox.Show(
                    this,
                    string.Join(Environment.NewLine, result.Warnings.Take(8)),
                    "头脸采集完成，但有兼容提示",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or InvalidOperationException)
        {
            if (revision == _loadRevision)
            {
                _footer.Text = "头脸采集失败：" + ex.Message;
                _footer.Foreground = ResourceBrush("WarningBrush");
                MessageBox.Show(
                    this,
                    ex.Message,
                    "采集游戏原始头脸失败",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }
        finally
        {
            if (revision == _loadRevision)
            {
                SetFaceCaptureButtonsEnabled(true);
                SetVariantSelectorsEnabled(true);
                RefreshFaceCaptureControls();
            }
        }
    }

    private void SetFaceCaptureButtonsEnabled(bool enabled)
    {
        foreach (var button in _faceCaptureButtons)
        {
            button.IsEnabled = enabled;
        }
    }

    private void RebuildVariantControls(ModelPreviewScene scene)
    {
        _rebuildingVariantControls = true;
        _variantPanel.Children.Clear();
        _variantSelectors.Clear();
        _variantValues.Clear();
        _variantPanel.Visibility = scene.Variants.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        _variantSeparator.Visibility = _variantPanel.Visibility;
        if (scene.Variants.Count == 0)
        {
            _rebuildingVariantControls = false;
            return;
        }

        _variantPanel.Children.Add(CreateSectionHeading("外观变量"));
        _variantPanel.Children.Add(new TextBlock
        {
            Text = "仅改变静态预览，不写入 Mod 的 INI。",
            Foreground = ResourceBrush("DialogMutedTextBrush"),
            Margin = new Thickness(2, 0, 2, 8),
            TextWrapping = TextWrapping.Wrap
        });
        foreach (var variant in scene.Variants)
        {
            _variantValues[variant.Key] = variant.SelectedValue;
            var row = new StackPanel { Margin = new Thickness(2, 4, 2, 6) };
            row.Children.Add(new TextBlock
            {
                Text = variant.DisplayName,
                Foreground = ResourceBrush("DialogTextBrush"),
                TextTrimming = TextTrimming.CharacterEllipsis,
                ToolTip = variant.SourceFile
            });
            var choices = variant.Values
                .Select(value => new VariantChoice(value, FormatVariantValue(value)))
                .ToList();
            var selector = new ComboBox
            {
                MinHeight = 34,
                Margin = new Thickness(0, 4, 0, 0),
                ItemsSource = choices,
                DisplayMemberPath = nameof(VariantChoice.Label),
                Tag = variant.Key,
                IsEnabled = _loader is not null && !string.IsNullOrWhiteSpace(_modDirectory)
            };
            selector.SelectedItem = choices.First(choice => AreVariantValuesEqual(choice.Value, variant.SelectedValue));
            selector.SelectionChanged += VariantSelectionChanged;
            AutomationProperties.SetName(selector, $"选择外观变量 {variant.Variable.TrimStart('$')}");
            _variantSelectors.Add(selector);
            row.Children.Add(selector);
            _variantPanel.Children.Add(row);
        }

        _rebuildingVariantControls = false;
    }

    private void RebuildMeshes(ModelPreviewScene scene)
    {
        _visibleMeshes.Children.Clear();
        _meshToggles.Clear();
        _textureBitmaps.Clear();
        _shadedTextures.Clear();
        _partsPanel.Children.Clear();
        _partsPanel.Children.Add(CreateSectionHeading("模型部件"));

        var palette = new[] { "AccentBrush", "InfoBrush", "SecondaryTextBrush", "TextBrush" };
        for (var i = 0; i < scene.Meshes.Count; i++)
        {
            var meshData = scene.Meshes[i];
            var model = CreateModel(meshData, palette[i % palette.Length]);
            _visibleMeshes.Children.Add(model);
            var checkBox = new CheckBox
            {
                Content = $"{meshData.Name}  ·  {meshData.Indices.Length / 3:N0}"
                    + TextureLabel(meshData),
                IsChecked = true,
                Margin = new Thickness(2, 4, 2, 4),
                ToolTip = meshData.SourceFile,
                Foreground = ResourceBrush("DialogTextBrush")
            };
            AutomationProperties.SetName(checkBox, $"显示或隐藏 {meshData.Name}");
            checkBox.Checked += PartVisibilityChanged;
            checkBox.Unchecked += PartVisibilityChanged;
            _meshToggles[checkBox] = model;
            _partsPanel.Children.Add(checkBox);
        }
    }

    private void RebuildDiagnostics(ModelPreviewScene scene)
    {
        _diagnosticItems.Children.Clear();
        var diagnostics = scene.Diagnostics;
        _diagnosticItems.Children.Add(new TextBlock
        {
            Text = $"{(diagnostics.CacheHit ? "缓存命中" : "重新解析")} · {diagnostics.LoadDuration.TotalMilliseconds:N0} ms",
            FontWeight = FontWeights.SemiBold,
            Foreground = ResourceBrush("DialogTextBrush"),
            TextWrapping = TextWrapping.Wrap
        });
        _diagnosticItems.Children.Add(new TextBlock
        {
            Text = $"{diagnostics.SourceFileCount} 个源文件 · {diagnostics.TextureCount} 张贴图 · "
                + $"保留 {FormatBytes(diagnostics.RetainedTextureBytes)}",
            Margin = new Thickness(0, 4, 0, 0),
            Foreground = ResourceBrush("DialogMutedTextBrush"),
            TextWrapping = TextWrapping.Wrap
        });
        if (scene.Meshes.Any(mesh => mesh.NormalTexture is not null
                                     || mesh.LightTexture is not null
                                     || mesh.MaterialTexture is not null))
        {
            _diagnosticItems.Children.Add(new TextBlock
            {
                Text = _shaderBackend.DisplayName,
                Margin = new Thickness(0, 4, 0, 0),
                Foreground = ResourceBrush("InfoBrush"),
                TextWrapping = TextWrapping.Wrap
            });
        }
        if (diagnostics.DownsampledTextureCount > 0)
        {
            _diagnosticItems.Children.Add(new TextBlock
            {
                Text = $"{diagnostics.DownsampledTextureCount} 张大尺寸贴图已降采样到最长边 {ZzmiModelPreviewLoader.MaximumPreviewTextureDimension}px。",
                Margin = new Thickness(0, 6, 0, 0),
                Foreground = ResourceBrush("InfoBrush"),
                TextWrapping = TextWrapping.Wrap
            });
        }

        if (scene.Warnings.Count == 0)
        {
            _diagnosticItems.Children.Add(new TextBlock
            {
                Text = "未发现需要跳过的不兼容资源。",
                Margin = new Thickness(0, 8, 0, 0),
                Foreground = ResourceBrush("DialogMutedTextBrush"),
                TextWrapping = TextWrapping.Wrap
            });
        }
        else
        {
            foreach (var warning in scene.Warnings)
            {
                _diagnosticItems.Children.Add(new TextBlock
                {
                    Text = $"• {warning}",
                    Margin = new Thickness(0, 6, 0, 0),
                    Foreground = ResourceBrush("WarningBrush"),
                    TextWrapping = TextWrapping.Wrap
                });
            }
        }

        _footer.Text = scene.Warnings.Count == 0
            ? "左键旋转 · 滚轮缩放 · 中键平移 · 双击或按 R 复位"
            : $"已跳过 {scene.Warnings.Count} 项不兼容资源 · 展开“兼容诊断”查看详情";
        _footer.Foreground = scene.Warnings.Count == 0
            ? ResourceBrush("DialogMutedTextBrush")
            : ResourceBrush("WarningBrush");
    }

    private async void VariantSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_rebuildingVariantControls
            || sender is not ComboBox { Tag: string key, SelectedItem: VariantChoice choice }
            || _loader is null
            || string.IsNullOrWhiteSpace(_modDirectory))
        {
            return;
        }

        var previousValue = _variantValues[key];
        _variantValues[key] = choice.Value;
        var revision = ++_loadRevision;
        SetVariantSelectorsEnabled(false);
        _footer.Text = "正在切换静态预览变体…";
        _footer.Foreground = ResourceBrush("InfoBrush");
        var requestedValues = new Dictionary<string, double>(_variantValues, StringComparer.OrdinalIgnoreCase);
        try
        {
            var scene = await Task.Run(() => _loader.Load(_modDirectory, requestedValues));
            if (revision != _loadRevision)
            {
                return;
            }

            ApplyScene(MergeCapturedFace(scene), true);
        }
        catch (ModelPreviewException ex)
        {
            if (revision == _loadRevision)
            {
                RestoreVariantSelector(key, previousValue);
                _footer.Text = ex.Message;
                _footer.Foreground = ResourceBrush("WarningBrush");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            if (revision == _loadRevision)
            {
                RestoreVariantSelector(key, previousValue);
                _footer.Text = $"切换预览变体失败：{ex.Message}";
                _footer.Foreground = ResourceBrush("WarningBrush");
            }
        }
        catch (Exception ex)
        {
            if (revision == _loadRevision)
            {
                RestoreVariantSelector(key, previousValue);
                _footer.Text = $"切换预览变体失败：{ex.Message}";
                _footer.Foreground = ResourceBrush("WarningBrush");
            }
        }
        finally
        {
            if (revision == _loadRevision)
            {
                SetVariantSelectorsEnabled(true);
            }
        }
    }

    private void RestoreVariantSelector(string key, double value)
    {
        _variantValues[key] = value;
        var selector = _variantSelectors.FirstOrDefault(candidate => string.Equals(
            candidate.Tag as string,
            key,
            StringComparison.OrdinalIgnoreCase));
        if (selector?.ItemsSource is not IEnumerable<VariantChoice> choices)
        {
            return;
        }

        _rebuildingVariantControls = true;
        selector.SelectedItem = choices.First(choice => AreVariantValuesEqual(choice.Value, value));
        _rebuildingVariantControls = false;
    }

    private void SetVariantSelectorsEnabled(bool enabled)
    {
        foreach (var selector in _variantSelectors)
        {
            selector.IsEnabled = enabled && _loader is not null && !string.IsNullOrWhiteSpace(_modDirectory);
        }
    }

    private void ConfigureBounds(ModelPreviewScene scene)
    {
        var bounds = ToWpfBounds(scene.Minimum, scene.Maximum);
        _initialTarget = new Point3D(
            (bounds.Minimum.X + bounds.Maximum.X) / 2,
            (bounds.Minimum.Y + bounds.Maximum.Y) / 2,
            (bounds.Minimum.Z + bounds.Maximum.Z) / 2);
        var extent = bounds.Maximum - bounds.Minimum;
        var diagonal = Math.Max(extent.Length, 0.05);
        _initialDistance = diagonal / (2 * Math.Tan(_camera.FieldOfView * Math.PI / 360)) * 1.35;
    }

    private void ExportPreview_Click(object sender, RoutedEventArgs e)
    {
        if (_viewportSurface is null)
        {
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "导出 3D 预览 PNG",
            Filter = "PNG 图片 (*.png)|*.png",
            DefaultExt = ".png",
            AddExtension = true,
            FileName = $"{SanitizeFileName(_modName)}-3d-preview.png"
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            _viewportSurface.UpdateLayout();
            var width = Math.Max(1, (int)Math.Round(_viewportSurface.ActualWidth));
            var height = Math.Max(1, (int)Math.Round(_viewportSurface.ActualHeight));
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(_viewportSurface);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(dialog.FileName);
            encoder.Save(stream);
            _footer.Text = $"已导出 PNG：{dialog.FileName}";
            _footer.Foreground = ResourceBrush("InfoBrush");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            MessageBox.Show(
                this,
                $"导出 PNG 失败：{ex.Message}",
                "导出 3D 预览",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private TextBlock CreateSectionHeading(string text) => new()
    {
        Text = text,
        FontSize = 15,
        FontWeight = FontWeights.SemiBold,
        Foreground = ResourceBrush("DialogTextBrush"),
        Margin = new Thickness(2, 0, 2, 8)
    };

    private static string FormatVariantValue(double value) =>
        value.ToString("0.###", CultureInfo.InvariantCulture);

    private static bool AreVariantValuesEqual(double left, double right) =>
        Math.Abs(left - right) <= 0.000001 * Math.Max(1, Math.Max(Math.Abs(left), Math.Abs(right)));

    private static string FormatBytes(long bytes) => bytes >= 1024 * 1024
        ? $"{bytes / (1024d * 1024d):N1} MB"
        : $"{bytes / 1024d:N1} KB";

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var result = new string(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(result) ? "mod" : result;
    }

    private Model3D CreateModel(ModelPreviewMesh data, string brushKey)
    {
        var positions = new Point3DCollection(data.Positions.Length);
        var normals = new Vector3DCollection(data.Normals.Length);
        var textureCoordinates = new PointCollection(data.TextureCoordinates.Length);
        foreach (var position in data.Positions)
        {
            positions.Add(ToWpf(position));
        }

        foreach (var normal in data.Normals)
        {
            normals.Add(ToWpfVector(normal));
        }

        foreach (var textureCoordinate in data.TextureCoordinates)
        {
            textureCoordinates.Add(new Point(textureCoordinate.X, textureCoordinate.Y));
        }

        var geometry = new MeshGeometry3D
        {
            Positions = positions,
            Normals = normals,
            TextureCoordinates = textureCoordinates,
            TriangleIndices = new Int32Collection(data.Indices)
        };
        geometry.Freeze();

        var material = new DiffuseMaterial(CreateMaterialBrush(data, brushKey));
        material.Freeze();
        var model = new GeometryModel3D
        {
            Geometry = geometry,
            Material = material,
            BackMaterial = material
        };
        return model;
    }

    private Brush CreateMaterialBrush(ModelPreviewMesh mesh, string fallbackBrushKey)
    {
        var options = new ModelPreviewShaderOptions(
            UseLightMap: _enhancedMaterials,
            UseMaterialMap: _enhancedMaterials,
            UseNormalMap: _normalMapping,
            UseOutline: _outlineEnabled);
        var shaderKey = string.Join(
            "|",
            mesh.DiffuseTexture?.SourceFile,
            options.UseNormalMap ? mesh.NormalTexture?.SourceFile : null,
            options.UseLightMap ? mesh.LightTexture?.SourceFile : null,
            options.UseMaterialMap ? mesh.MaterialTexture?.SourceFile : null,
            options.UseNormalMap,
            options.UseLightMap,
            options.UseMaterialMap,
            options.UseOutline);
        if (!_shadedTextures.TryGetValue(shaderKey, out var texture))
        {
            texture = _shaderBackend.Render(mesh, options);
            _shadedTextures[shaderKey] = texture;
        }
        if (texture is null)
        {
            var fallback = new SolidColorBrush(ColorFrom(fallbackBrushKey));
            fallback.Freeze();
            return fallback;
        }

        if (!_textureBitmaps.TryGetValue(texture.SourceFile, out var bitmap))
        {
            bitmap = BitmapSource.Create(
                texture.Width,
                texture.Height,
                96,
                96,
                PixelFormats.Bgra32,
                null,
                texture.Bgra32Pixels,
                checked(texture.Width * 4));
            bitmap.Freeze();
            _textureBitmaps[texture.SourceFile] = bitmap;
        }

        var brush = new ImageBrush(bitmap)
        {
            Stretch = Stretch.Fill,
            TileMode = TileMode.None,
            ViewboxUnits = BrushMappingMode.RelativeToBoundingBox,
            ViewportUnits = BrushMappingMode.RelativeToBoundingBox
        };
        brush.Freeze();
        return brush;
    }

    private static string TextureLabel(ModelPreviewMesh mesh)
    {
        var labels = new List<string>();
        if (mesh.DiffuseTexture is { HasTransparency: true })
        {
            labels.Add("DDS/透明");
        }
        else if (mesh.DiffuseTexture is not null)
        {
            labels.Add("DDS");
        }

        if (mesh.LightTexture is not null)
        {
            labels.Add("Light");
        }

        if (mesh.NormalTexture is not null)
        {
            labels.Add("Normal");
        }

        if (mesh.MaterialTexture is not null)
        {
            labels.Add("Material");
        }

        return labels.Count == 0 ? string.Empty : " · " + string.Join("+", labels);
    }

    private void PartVisibilityChanged(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox checkBox || !_meshToggles.TryGetValue(checkBox, out var model))
        {
            return;
        }

        if (checkBox.IsChecked == true)
        {
            if (!_visibleMeshes.Children.Contains(model))
            {
                _visibleMeshes.Children.Add(model);
            }
        }
        else
        {
            _visibleMeshes.Children.Remove(model);
        }
    }

    private void StartPointerAction(MouseButtonEventArgs e, PointerAction action)
    {
        _pointerAction = action;
        _lastPointer = e.GetPosition(_viewport);
        _viewport.CaptureMouse();
        _viewport.Focus();
        e.Handled = true;
    }

    private void Viewport_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left && e.ClickCount == 2)
        {
            ResetCamera();
            e.Handled = true;
        }
        else if (e.ChangedButton == MouseButton.Left)
        {
            StartPointerAction(e, PointerAction.Rotate);
        }
        else if (e.ChangedButton == MouseButton.Middle)
        {
            StartPointerAction(e, PointerAction.Pan);
        }
    }

    private void EndPointerAction(object sender, MouseButtonEventArgs e)
    {
        _pointerAction = PointerAction.None;
        if (_viewport.IsMouseCaptured)
        {
            _viewport.ReleaseMouseCapture();
        }

        e.Handled = true;
    }

    private void Viewport_MouseMove(object sender, MouseEventArgs e)
    {
        if (_pointerAction == PointerAction.None)
        {
            return;
        }

        var current = e.GetPosition(_viewport);
        var delta = current - _lastPointer;
        _lastPointer = current;
        if (_pointerAction == PointerAction.Rotate)
        {
            _yaw -= delta.X * 0.009;
            _pitch = Math.Clamp(_pitch + (delta.Y * 0.009), -1.45, 1.45);
        }
        else
        {
            var look = _camera.LookDirection;
            look.Normalize();
            var right = Vector3D.CrossProduct(look, _camera.UpDirection);
            right.Normalize();
            var up = Vector3D.CrossProduct(right, look);
            up.Normalize();
            var scale = _distance / Math.Max(_viewport.ActualHeight, 1) * 1.7;
            _target += (right * (-delta.X * scale)) + (up * (delta.Y * scale));
        }

        UpdateCamera();
    }

    private void Viewport_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        _distance *= Math.Pow(0.88, e.Delta / 120.0);
        _distance = Math.Clamp(_distance, _initialDistance * 0.025, _initialDistance * 30);
        UpdateCamera();
        e.Handled = true;
    }

    private void ResetCamera()
    {
        _target = _initialTarget;
        _distance = _initialDistance;
        _yaw = 0.55;
        _pitch = 0.12;
        UpdateCamera();
    }

    private void UpdateCamera()
    {
        var horizontal = _distance * Math.Cos(_pitch);
        var offset = new Vector3D(
            horizontal * Math.Sin(_yaw),
            _distance * Math.Sin(_pitch),
            horizontal * Math.Cos(_yaw));
        _camera.Position = _target + offset;
        _camera.LookDirection = _target - _camera.Position;
        _camera.UpDirection = new Vector3D(0, 1, 0);
        _camera.NearPlaneDistance = Math.Max(_distance / 1000, 0.0001);
        _camera.FarPlaneDistance = Math.Max(_distance * 100, 100);
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
        else if (e.Key == Key.R)
        {
            ResetCamera();
            e.Handled = true;
        }
    }

    private Brush ResourceBrush(string key) => (Brush)FindResource(key);

    private Color ColorFrom(string brushKey) => ((SolidColorBrush)FindResource(brushKey)).Color;

    private CornerRadius ResourceCornerRadius(string key, double fallback) =>
        TryFindResource(key) is CornerRadius radius ? radius : new CornerRadius(fallback);

    private static Point3D ToWpf(NumericsVector3 value) => new(value.X, value.Z, -value.Y);

    private static Vector3D ToWpfVector(NumericsVector3 value) => new(value.X, value.Z, -value.Y);

    private static (Point3D Minimum, Point3D Maximum) ToWpfBounds(
        NumericsVector3 minimum,
        NumericsVector3 maximum) =>
        (new Point3D(minimum.X, minimum.Z, -maximum.Y), new Point3D(maximum.X, maximum.Z, -minimum.Y));

    private sealed record VariantChoice(double Value, string Label)
    {
        public override string ToString() => Label;
    }

    private enum PointerAction
    {
        None,
        Rotate,
        Pan
    }
}
