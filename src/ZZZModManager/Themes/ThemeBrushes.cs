using System.Windows;
using System.Windows.Media;

namespace ZZZModManager.Themes;

// View models and imperative status updates must never invent their own hex
// values: a second source of truth is how "enabled green" and "accent" drifted
// apart in the first place. Every brush is looked up from DarkTheme.xaml by
// token key, so the dictionary stays the only place a color is defined.
//
// Consumers assign these brushes straight to element properties and one-way
// bindings, so a theme switch cannot hand them a *different* instance later --
// the old assignment would keep painting the previous palette. That is how the
// light theme shipped with an invisible toast message and a dark "全部" chip:
// ThemeBrushes handed out the dictionary's own instance, the dictionary entry was
// then swapped (or was never the one AppearanceController repaints), and every
// imperative consumer stayed pinned to the dark palette while DynamicResource
// consumers re-resolved correctly. So ThemeBrushes now owns one brush per token
// for the lifetime of the process and Refresh repaints those in place.
public static class ThemeBrushes
{
    private const string ThemeUri = "/ZZZModManager;component/Themes/DarkTheme.xaml";
    private static readonly object Sync = new();
    private static readonly Dictionary<string, SolidColorBrush> Resolved = new(StringComparer.Ordinal);
    private static ResourceDictionary? _standalone;

    public static Brush Canvas => Get("CanvasBrush");
    public static Brush Surface => Get("SurfaceBrush");
    public static Brush SurfaceRaised => Get("SurfaceRaisedBrush");
    public static Brush SurfaceSunken => Get("SurfaceSunkenBrush");
    public static Brush Border => Get("BorderBrush");
    public static Brush BorderStrong => Get("StrongBorderBrush");
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

            // Own the instance instead of handing out the dictionary's: a consumer
            // that assigned it keeps the same object forever, so repainting it in
            // Refresh is the only thing a theme switch has to do. Deliberately not
            // frozen for exactly that reason.
            var brush = new SolidColorBrush(ColorFor(key));
            Resolved[key] = brush;
            return brush;
        }
    }

    // Called right after the application dictionary has been repainted, so brushes
    // already handed out follow the new palette instead of silently going stale.
    public static void Refresh()
    {
        lock (Sync)
        {
            _standalone = null;
            foreach (var (key, brush) in Resolved)
            {
                // Sealing a Style freezes any brush passed as a literal Setter value.
                // Skipping a frozen brush costs one stale pixel; throwing here would
                // take the whole theme switch down.
                if (!brush.IsFrozen)
                {
                    brush.Color = ColorFor(key);
                }
            }
        }
    }

    // Tests and headless hosts swap the application dictionary wholesale; dropping
    // the cache lets the next Get resolve against whatever is live now.
    public static void Invalidate()
    {
        lock (Sync)
        {
            Resolved.Clear();
            _standalone = null;
        }
    }

    private static Color ColorFor(string key) =>
        Lookup(key) is SolidColorBrush brush ? brush.Color : Colors.Transparent;

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
