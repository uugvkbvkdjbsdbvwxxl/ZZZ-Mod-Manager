namespace ZZZModManager.Models;

public enum AppTheme
{
    Dark,
    Light
}

// Translucency is clamped instead of validated-and-rejected: a config file that
// was hand-edited to 0 must not produce an unreadable shell, and the sliders in
// the settings page share exactly these bounds so UI and storage cannot drift.
public static class AppearancePolicy
{
    public const double MinimumChromeOpacity = 0.45;
    public const double MaximumOpacity = 1.0;
    public const double MinimumBackgroundOpacity = 0.0;

    public const double DefaultSidebarOpacity = 0.92;
    public const double DefaultPanelOpacity = 0.92;
    public const double DefaultBackgroundOpacity = 0.45;

    // Sidebar and panels carry text and status colour, so they keep a floor that
    // guarantees contrast no matter how loud the background image is.
    public static double ClampChromeOpacity(double value) =>
        Clamp(value, MinimumChromeOpacity, MaximumOpacity);

    // The background image is purely decorative and may be dialled to zero.
    public static double ClampBackgroundOpacity(double value) =>
        Clamp(value, MinimumBackgroundOpacity, MaximumOpacity);

    // The veil rectangle above the image is driven by the inverse of the image
    // opacity, which is what makes a fully opaque canvas and a visible photo
    // coexist in the same layer stack.
    public static double VeilOpacityFor(double backgroundOpacity) =>
        MaximumOpacity - ClampBackgroundOpacity(backgroundOpacity);

    public static byte ToAlpha(double opacity)
    {
        var clamped = Clamp(opacity, 0.0, MaximumOpacity);
        return (byte)Math.Round(clamped * 255.0, MidpointRounding.AwayFromZero);
    }

    private static double Clamp(double value, double minimum, double maximum)
    {
        if (double.IsNaN(value))
        {
            return maximum;
        }

        return Math.Clamp(value, minimum, maximum);
    }
}
