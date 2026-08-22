using System.Windows;
using System.Windows.Media;
using ZZZModManager.Models;

namespace ZZZModManager.Themes;

// Theme switching repaints the brush objects that DarkTheme.xaml already handed
// out instead of swapping resource dictionaries. Every brush in that dictionary
// binds its Color to a {StaticResource ...Color} token, and StaticResource only
// resolves once, so replacing the dictionary would leave every already-rendered
// element painted with the old instances. Mutating Color on the live brushes
// pushes a change notification through WPF and repaints the shell, the dialogs
// and the native popup chrome in one pass.
//
// This only works while the brushes stay thawed. WPF seals every Style and
// ControlTemplate the first time it is applied, and sealing freezes any Freezable
// handed to a Setter as a literal value - which is why every brush reference in
// XAML and in code-built Setters must be {DynamicResource ...} rather than
// {StaticResource ...}. Repaint keeps a clone-and-replace fallback so a stray
// literal reference degrades into a one-off repaint instead of crashing startup.
public sealed class AppearanceController
{
    private const string LightPaletteUri = "/ZZZModManager;component/Themes/LightPalette.xaml";

    // Brush key -> color token key, mirroring the declarations in DarkTheme.xaml.
    // The naming asymmetry on StrongBorderBrush/BorderStrongColor is intentional:
    // this table is the seam that reconciles it.
    public static readonly IReadOnlyDictionary<string, string> BrushColorTokens =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["CanvasBrush"] = "CanvasColor",
            ["SidebarBrush"] = "SidebarColor",
            ["SurfaceBrush"] = "SurfaceColor",
            ["SurfaceRaisedBrush"] = "SurfaceRaisedColor",
            ["SurfaceHoverBrush"] = "SurfaceHoverColor",
            ["SurfaceSunkenBrush"] = "SurfaceSunkenColor",
            ["BorderBrush"] = "BorderColor",
            ["StrongBorderBrush"] = "BorderStrongColor",
            ["TextBrush"] = "TextColor",
            ["SecondaryTextBrush"] = "SecondaryTextColor",
            ["MutedTextBrush"] = "MutedTextColor",
            ["AccentBrush"] = "AccentColor",
            ["AccentStrongBrush"] = "AccentStrongColor",
            ["AccentForegroundBrush"] = "AccentForegroundColor",
            ["AccentTintBrush"] = "AccentTintColor",
            ["SuccessBrush"] = "SuccessColor",
            ["SuccessForegroundBrush"] = "SuccessForegroundColor",
            ["WarningBrush"] = "WarningColor",
            ["WarningForegroundBrush"] = "WarningForegroundColor",
            ["ErrorBrush"] = "ErrorColor",
            ["ErrorSurfaceBrush"] = "ErrorSurfaceColor",
            ["ErrorBorderBrush"] = "ErrorBorderColor",
            ["ErrorHoverBrush"] = "ErrorHoverColor",
            ["ErrorPressedBrush"] = "ErrorPressedColor",
            ["InfoBrush"] = "InfoColor",
            ["DialogBackgroundBrush"] = "CanvasColor",
            ["DialogSurfaceBrush"] = "SurfaceColor",
            ["DialogRaisedBrush"] = "SurfaceRaisedColor",
            ["DialogBorderBrush"] = "BorderStrongColor",
            ["DialogTextBrush"] = "TextColor",
            ["DialogMutedTextBrush"] = "SecondaryTextColor",
        };

    // The sidebar is the "菜单栏" slider target.
    public static readonly IReadOnlyList<string> SidebarBrushKeys = ["SidebarBrush"];

    // Shell panels and cards are the "界面" slider target. Dialog surfaces stay
    // opaque because a dialog floats over its own window and translucency there
    // would only reveal the dialog's own canvas.
    public static readonly IReadOnlyList<string> PanelBrushKeys =
        ["SurfaceBrush", "SurfaceRaisedBrush", "SurfaceSunkenBrush"];

    private static readonly (object Key, string ColorKey)[] SystemBrushTokens =
    [
        (SystemColors.WindowBrushKey, "SurfaceRaisedColor"),
        (SystemColors.WindowTextBrushKey, "TextColor"),
        (SystemColors.ControlBrushKey, "SurfaceColor"),
        (SystemColors.ControlTextBrushKey, "TextColor"),
    ];

    private readonly ResourceDictionary _resources;
    private readonly Dictionary<string, Color> _darkColors = new(StringComparer.Ordinal);
    private Dictionary<string, Color>? _lightColors;

    public AppearanceController(ResourceDictionary resources)
    {
        _resources = resources ?? throw new ArgumentNullException(nameof(resources));
        CaptureDarkPalette();
    }

    public AppTheme CurrentTheme { get; private set; } = AppTheme.Dark;

    public void Apply(AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var palette = PaletteFor(config.Theme);
        var sidebarAlpha = AppearancePolicy.ToAlpha(
            AppearancePolicy.ClampChromeOpacity(config.SidebarOpacity));
        var panelAlpha = AppearancePolicy.ToAlpha(
            AppearancePolicy.ClampChromeOpacity(config.PanelOpacity));

        foreach (var (brushKey, colorKey) in BrushColorTokens)
        {
            if (palette.TryGetValue(colorKey, out var color))
            {
                Repaint(brushKey, WithAlpha(color, AlphaFor(brushKey, sidebarAlpha, panelAlpha)));
            }
        }

        foreach (var (key, colorKey) in SystemBrushTokens)
        {
            if (palette.TryGetValue(colorKey, out var color))
            {
                Repaint(key, color);
            }
        }

        CurrentTheme = config.Theme;
    }

    // A frozen brush cannot be repainted, so swap the dictionary entry for a thawed
    // clone. Elements bound through DynamicResource pick the clone up immediately;
    // any element still holding the frozen instance keeps its old colour, which is a
    // stale pixel rather than an unhandled exception on startup.
    private void Repaint(object brushKey, Color color)
    {
        if (_resources[brushKey] is not SolidColorBrush brush)
        {
            return;
        }

        if (brush.IsFrozen)
        {
            var thawed = brush.Clone();
            thawed.Color = color;
            _resources[brushKey] = thawed;
            return;
        }

        brush.Color = color;
    }

    private static byte AlphaFor(string brushKey, byte sidebarAlpha, byte panelAlpha)
    {
        if (SidebarBrushKeys.Contains(brushKey, StringComparer.Ordinal))
        {
            return sidebarAlpha;
        }

        return PanelBrushKeys.Contains(brushKey, StringComparer.Ordinal) ? panelAlpha : byte.MaxValue;
    }

    private static Color WithAlpha(Color color, byte alpha) =>
        Color.FromArgb(alpha, color.R, color.G, color.B);

    private Dictionary<string, Color> PaletteFor(AppTheme theme) =>
        theme == AppTheme.Light ? LoadLightPalette() : _darkColors;

    // The dark values live in the merged dictionary itself; the Color entries are
    // never mutated, so they remain a faithful snapshot to switch back to.
    private void CaptureDarkPalette()
    {
        foreach (var colorKey in BrushColorTokens.Values.Distinct(StringComparer.Ordinal))
        {
            if (_resources[colorKey] is Color color)
            {
                _darkColors[colorKey] = color;
            }
        }
    }

    private Dictionary<string, Color> LoadLightPalette()
    {
        if (_lightColors is not null)
        {
            return _lightColors;
        }

        var loaded = new Dictionary<string, Color>(StringComparer.Ordinal);
        try
        {
            var palette = new ResourceDictionary { Source = new Uri(LightPaletteUri, UriKind.Relative) };
            foreach (var entry in palette.Keys)
            {
                if (entry is string key && palette[entry] is Color color)
                {
                    loaded[key] = color;
                }
            }
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or UriFormatException)
        {
            // A missing palette must not take the window down: an unchanged dark
            // shell is a far better failure mode than a startup crash.
            loaded = new Dictionary<string, Color>(_darkColors, StringComparer.Ordinal);
        }

        _lightColors = loaded;
        return _lightColors;
    }
}
