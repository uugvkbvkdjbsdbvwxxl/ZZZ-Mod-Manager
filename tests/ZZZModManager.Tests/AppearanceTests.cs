using System.Windows;
using System.Windows.Media;
using ZZZModManager.Models;
using ZZZModManager.Themes;
using Xunit;

namespace ZZZModManager.Tests;

public sealed class AppearanceTests
{
    [Fact]
    public void ChromeOpacityKeepsAReadabilityFloorWhileBackgroundMayVanish()
    {
        Assert.Equal(AppearancePolicy.MinimumChromeOpacity, AppearancePolicy.ClampChromeOpacity(0.0));
        Assert.Equal(1.0, AppearancePolicy.ClampChromeOpacity(4.2));
        Assert.Equal(0.7, AppearancePolicy.ClampChromeOpacity(0.7));

        Assert.Equal(0.0, AppearancePolicy.ClampBackgroundOpacity(-1.0));
        Assert.Equal(1.0, AppearancePolicy.ClampBackgroundOpacity(1.5));
    }

    [Fact]
    public void VeilIsTheInverseOfBackgroundOpacitySoTheImageBecomesVisible()
    {
        // The shipped defect was a fixed 0.78 veil over a 0.18 image; the veil now
        // disappears exactly when the user asks for a full-strength background.
        Assert.Equal(0.0, AppearancePolicy.VeilOpacityFor(1.0));
        Assert.Equal(1.0, AppearancePolicy.VeilOpacityFor(0.0));
        Assert.Equal(0.55, AppearancePolicy.VeilOpacityFor(0.45), 6);
    }

    [Fact]
    public void AlphaConversionCoversTheFullByteRange()
    {
        Assert.Equal(byte.MinValue, AppearancePolicy.ToAlpha(0.0));
        Assert.Equal(byte.MaxValue, AppearancePolicy.ToAlpha(1.0));
        Assert.Equal((byte)128, AppearancePolicy.ToAlpha(0.5));
    }

    [Fact]
    public void ConfigDefaultsPreserveTheDarkOpaqueShellForExistingInstallations()
    {
        var config = new AppConfig();

        Assert.Equal(AppTheme.Dark, config.Theme);
        Assert.Equal(AppearancePolicy.DefaultSidebarOpacity, config.SidebarOpacity);
        Assert.Equal(AppearancePolicy.DefaultPanelOpacity, config.PanelOpacity);
        Assert.Equal(AppearancePolicy.DefaultBackgroundOpacity, config.BackgroundOpacity);
        Assert.True(config.SidebarOpacity >= AppearancePolicy.MinimumChromeOpacity);
    }

    [Fact]
    public void EveryMappedBrushAndColorTokenExistsInTheThemeDictionary()
    {
        var theme = LoadTheme();

        foreach (var (brushKey, colorKey) in AppearanceController.BrushColorTokens)
        {
            Assert.IsType<SolidColorBrush>(theme[brushKey]);
            Assert.IsType<Color>(theme[colorKey]);
        }

        foreach (var key in AppearanceController.SidebarBrushKeys.Concat(AppearanceController.PanelBrushKeys))
        {
            Assert.True(AppearanceController.BrushColorTokens.ContainsKey(key));
        }
    }

    [Fact]
    public void LightPaletteDeclaresTheSameOpaqueColorTokensAsTheDarkTheme()
    {
        var dark = LoadTheme();
        var light = LoadDictionary("/ZZZModManager;component/Themes/LightPalette.xaml");

        var darkColors = ColorKeys(dark);
        var lightColors = ColorKeys(light);
        Assert.Equal(darkColors, lightColors);

        foreach (var key in lightColors)
        {
            var color = (Color)light[key]!;
            Assert.Equal(byte.MaxValue, color.A);
            Assert.NotEqual((Color)dark[key]!, color);
        }
    }

    [Fact]
    public void ApplyingTheLightThemeRepaintsTheSameBrushInstances()
    {
        var theme = LoadTheme();
        var canvas = (SolidColorBrush)theme["CanvasBrush"]!;
        var sidebar = (SolidColorBrush)theme["SidebarBrush"]!;
        var surface = (SolidColorBrush)theme["SurfaceBrush"]!;
        var darkCanvas = canvas.Color;

        var controller = new AppearanceController(theme);
        controller.Apply(new AppConfig
        {
            Theme = AppTheme.Light,
            SidebarOpacity = 0.5,
            PanelOpacity = 0.6,
        });

        Assert.Equal(AppTheme.Light, controller.CurrentTheme);
        Assert.Same(canvas, theme["CanvasBrush"]);
        Assert.NotEqual(darkCanvas, canvas.Color);
        Assert.Equal(byte.MaxValue, canvas.Color.A);
        Assert.Equal(AppearancePolicy.ToAlpha(0.5), sidebar.Color.A);
        Assert.Equal(AppearancePolicy.ToAlpha(0.6), surface.Color.A);

        controller.Apply(new AppConfig());

        Assert.Equal(AppTheme.Dark, controller.CurrentTheme);
        Assert.Equal(darkCanvas, canvas.Color);
        Assert.Equal(AppearancePolicy.ToAlpha(AppearancePolicy.DefaultSidebarOpacity), sidebar.Color.A);
    }

    [Fact]
    public void OutOfRangeStoredOpacityCannotProduceAnUnreadableShell()
    {
        var theme = LoadTheme();
        var sidebar = (SolidColorBrush)theme["SidebarBrush"]!;
        var text = (SolidColorBrush)theme["TextBrush"]!;

        new AppearanceController(theme).Apply(new AppConfig { SidebarOpacity = 0.0, PanelOpacity = -3.0 });

        Assert.Equal(AppearancePolicy.ToAlpha(AppearancePolicy.MinimumChromeOpacity), sidebar.Color.A);
        Assert.Equal(byte.MaxValue, text.Color.A);
    }

    private static ResourceDictionary LoadTheme() =>
        LoadDictionary("/ZZZModManager;component/Themes/DarkTheme.xaml");

    private static ResourceDictionary LoadDictionary(string uri) =>
        new() { Source = new Uri(uri, UriKind.Relative) };

    private static SortedSet<string> ColorKeys(ResourceDictionary dictionary)
    {
        var keys = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var key in dictionary.Keys)
        {
            if (key is string name && dictionary[key] is Color)
            {
                keys.Add(name);
            }
        }

        return keys;
    }
}
