using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Media.Imaging;
using ZZZModManager.Services;
using NumericsVector3 = System.Numerics.Vector3;

namespace ZZZModManager;

public sealed class ModelPreviewWindow : Window
{
    private readonly PerspectiveCamera _camera = new() { FieldOfView = 38 };
    private readonly Viewport3D _viewport = new();
    private readonly Model3DGroup _visibleMeshes = new();
    private readonly Dictionary<CheckBox, GeometryModel3D> _meshToggles = [];
    private readonly Dictionary<string, BitmapSource> _textureBitmaps = new(StringComparer.OrdinalIgnoreCase);
    private readonly Point3D _initialTarget;
    private readonly double _initialDistance;
    private Point3D _target;
    private double _distance;
    private double _yaw = 0.55;
    private double _pitch = 0.12;
    private Point _lastPointer;
    private PointerAction _pointerAction;

    public ModelPreviewWindow(string modName, ModelPreviewScene scene)
    {
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

        var bounds = ToWpfBounds(scene.Minimum, scene.Maximum);
        _initialTarget = new Point3D(
            (bounds.Minimum.X + bounds.Maximum.X) / 2,
            (bounds.Minimum.Y + bounds.Maximum.Y) / 2,
            (bounds.Minimum.Z + bounds.Maximum.Z) / 2);
        var extent = bounds.Maximum - bounds.Minimum;
        var diagonal = Math.Max(extent.Length, 0.05);
        _initialDistance = diagonal / (2 * Math.Tan(_camera.FieldOfView * Math.PI / 360)) * 1.35;
        _target = _initialTarget;
        _distance = _initialDistance;

        Content = BuildContent(modName, scene);
        ResetCamera();
        KeyDown += Window_KeyDown;
    }

    private UIElement BuildContent(string modName, ModelPreviewScene scene)
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
        var close = new Button
        {
            Content = "关闭",
            MinWidth = 82,
            Margin = new Thickness(8, 0, 0, 0)
        };
        close.Click += (_, _) => Close();
        headerButtons.Children.Add(reset);
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
        heading.Children.Add(new TextBlock
        {
            Text = $"{scene.Meshes.Count} 个网格 · {scene.Meshes.Count(mesh => mesh.DiffuseTexture is not null)} 个 DDS 贴图 · "
                + $"{scene.VertexCount:N0} 个顶点 · {scene.TriangleCount:N0} 个三角形",
            Margin = new Thickness(0, 4, 0, 0),
            Foreground = ResourceBrush("DialogMutedTextBrush")
        });
        header.Children.Add(heading);
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);

        var footer = new TextBlock
        {
            Text = scene.Warnings.Count == 0
                ? "左键旋转 · 滚轮缩放 · 中键平移 · 双击或按 R 复位"
                : $"已跳过 {scene.Warnings.Count} 项不兼容资源 · 左键旋转 · 滚轮缩放 · 中键平移",
            Foreground = scene.Warnings.Count == 0
                ? ResourceBrush("DialogMutedTextBrush")
                : ResourceBrush("WarningBrush"),
            Margin = new Thickness(4, 12, 4, 0),
            TextWrapping = TextWrapping.Wrap
        };
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);

        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(224) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var partsPanel = new StackPanel { Margin = new Thickness(10, 8, 10, 8) };
        partsPanel.Children.Add(new TextBlock
        {
            Text = "模型部件",
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Foreground = ResourceBrush("DialogTextBrush"),
            Margin = new Thickness(2, 0, 2, 8)
        });

        var palette = new[] { "AccentBrush", "InfoBrush", "SecondaryTextBrush", "TextBrush" };
        for (var i = 0; i < scene.Meshes.Count; i++)
        {
            var meshData = scene.Meshes[i];
            var model = CreateModel(meshData, palette[i % palette.Length]);
            _visibleMeshes.Children.Add(model);
            var checkBox = new CheckBox
            {
                Content = $"{meshData.Name}  ·  {meshData.Indices.Length / 3:N0}"
                    + TextureLabel(meshData.DiffuseTexture),
                IsChecked = true,
                Margin = new Thickness(2, 4, 2, 4),
                ToolTip = meshData.SourceFile,
                Foreground = ResourceBrush("DialogTextBrush")
            };
            AutomationProperties.SetName(checkBox, $"显示或隐藏 {meshData.Name}");
            checkBox.Checked += PartVisibilityChanged;
            checkBox.Unchecked += PartVisibilityChanged;
            _meshToggles[checkBox] = model;
            partsPanel.Children.Add(checkBox);
        }

        var partsBorder = new Border
        {
            Background = ResourceBrush("DialogSurfaceBrush"),
            BorderBrush = ResourceBrush("DialogBorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = ResourceCornerRadius("PanelRadius", 12),
            Padding = new Thickness(4),
            Child = new ScrollViewer
            {
                Content = partsPanel,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            }
        };
        Grid.SetColumn(partsBorder, 0);
        body.Children.Add(partsBorder);

        ConfigureViewport();
        var viewportBorder = new Border
        {
            Background = ResourceBrush("SurfaceSunkenBrush"),
            BorderBrush = ResourceBrush("DialogBorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = ResourceCornerRadius("PanelRadius", 12),
            ClipToBounds = true,
            Child = _viewport
        };
        Grid.SetColumn(viewportBorder, 2);
        body.Children.Add(viewportBorder);
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

    private GeometryModel3D CreateModel(ModelPreviewMesh data, string brushKey)
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
        return new GeometryModel3D
        {
            Geometry = geometry,
            Material = material,
            BackMaterial = material
        };
    }

    private Brush CreateMaterialBrush(ModelPreviewMesh mesh, string fallbackBrushKey)
    {
        if (mesh.DiffuseTexture is not { } texture)
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

    private static string TextureLabel(ModelPreviewTexture? texture) => texture switch
    {
        { HasTransparency: true } => " · DDS/透明",
        not null => " · DDS",
        _ => string.Empty
    };

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

    private enum PointerAction
    {
        None,
        Rotate,
        Pan
    }
}
