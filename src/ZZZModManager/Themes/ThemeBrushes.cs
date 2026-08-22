using System.Windows;
using System.Windows.Media;

namespace ZZZModManager.Themes;

// View models and imperative status updates must never invent their own hex
// values: a second source of truth is how "enabled green" and "accent" drifted
// apart in the first place. Every brush is looked up from DarkTheme.xaml by
// token key, so the dictionary stays the only place a color is defined.
public static class ThemeBrushes
{
    private const string ThemeUri = "/ZZZModManager;component/Themes/DarkTheme.xaml";
    private static readonly object Sync = new();
    private static readonly Dictionary<string, Brush> Resolved = new(StringComparer.Ordinal);
    private static ResourceDictionary? _standalone;

    public static Brush Canvas => Get("CanvasBrush");
    public static Brush Surface => Get("SurfaceBrush");
    public static Brush SurfaceRaised => Get("SurfaceRaisedBrush");
    public static Brush SurfaceSunken => Get("SurfaceSunkenBrush");
    public static Brush Border => Get("BorderBrush");
    public static Brush BorderStrong => Get("BorderStrongBrush");
    public static Brush Text => Get("TextBrush");
    public static Brush SecondaryText => Get("SecondaryTextBrush");
    public static Brush MutedText => Get("MutedTextBrush");
    public static Brush Accent => Get("AccentBrush");
    public static Brush AccentTint => Get("AccentTintBrush");
    public static Brush AccentForeground => Get("AccentForegroundBrush");
    public static Brush Success => Get("SuccessBrush");
    public static Brush SuccessForeground => Get("SuccessForegroundBrush");
    public static Brush Warning => Get("WarningBrush");
    public static Brush WarningForeground => Get("WarningForegroundBrush");
    public static Brush Error => Get("ErrorBrush");
    public static Brush Info => Get("InfoBrush");

    public static Brush Get(string key)
    {
        lock (Sync)
        {
            if (Resolved.TryGetValue(key, out var cached))
            {
                return cached;
            }

            var brush = Lookup(key) ?? Brushes.Transparent;
            if (brush.CanFreeze && !brush.IsFrozen)
            {
                brush = (Brush)brush.GetCurrentValueAsFrozen();
            }

            Resolved[key] = brush;
            return brush;
        }
    }

    private static Brush? Lookup(string key)
    {
        if (Application.Current?.TryFindResource(key) is Brush applicationBrush)
        {
            return applicationBrush;
        }

        // Design-time and headless paths have no Application; load the same
        // dictionary directly rather than falling back to a literal color.
        try
        {
            _standalone ??= new ResourceDictionary { Source = new Uri(ThemeUri, UriKind.Relative) };
            return _standalone[key] as Brush;
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or UriFormatException)
        {
            return null;
        }
    }
}
