using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ZZZModManager.Models;
using ZZZModManager.Themes;
using Xunit;

namespace ZZZModManager.Tests;

// v0.1.2 shipped an executable that died on startup with 0xE0434352 and no dialog:
// AppearanceController repaints the live theme brushes, but WPF seals every Style
// and ControlTemplate on first use and sealing freezes any Freezable handed to a
// Setter as a literal value. {StaticResource CanvasBrush} in the implicit Window
// style froze #FF0C0D12, and assigning Color then threw InvalidOperationException.
public sealed class ThemeFreezeRegressionTests
{
    [Fact]
    public void SealingAStyleFreezesABrushPassedAsALiteralSetterValue()
    {
        var brush = new SolidColorBrush(Colors.Red);

        var style = new Style(typeof(Control));
        style.Setters.Add(new Setter(Control.BackgroundProperty, brush));
        style.Seal();

        Assert.True(brush.IsFrozen);
        Assert.Throws<InvalidOperationException>(() => brush.Color = Colors.Blue);
    }

    [Fact]
    public void SealingLeavesBrushesReachedThroughDynamicResourceThawed()
    {
        var brush = new SolidColorBrush(Colors.Red);
        var resources = new ResourceDictionary { ["ProbeBrush"] = brush };

        var style = new Style(typeof(Control));
        style.Setters.Add(new Setter(Control.BackgroundProperty, new DynamicResourceExtension("ProbeBrush")));
        style.Seal();

        var template = new ControlTemplate(typeof(Control));
        var factory = new FrameworkElementFactory(typeof(Border));
        factory.SetResourceReference(Border.BackgroundProperty, "ProbeBrush");
        template.VisualTree = factory;
        template.Seal();

        Assert.False(brush.IsFrozen);
        Assert.Same(brush, resources["ProbeBrush"]);
        brush.Color = Colors.Blue;
    }

    [Theory]
    [InlineData("DarkTheme.xaml")]
    [InlineData("MainWindow.xaml")]
    public void ThemeMarkupNeverReferencesABrushThroughStaticResource(string fixtureName)
    {
        var markup = LoadFixtureText(fixtureName);

        // Colour, geometry and style keys may stay static; only brushes are mutated
        // at runtime, so only brush references have to survive sealing.
        var staticBrushReferences = System.Text.RegularExpressions.Regex
            .Matches(markup, @"\{StaticResource [A-Za-z0-9_]*Brush\}")
            .Select(match => match.Value)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(staticBrushReferences);
        Assert.Contains("{DynamicResource CanvasBrush}", markup, StringComparison.Ordinal);
    }

    [Fact]
    public void ApplyRepaintsThroughACloneWhenABrushIsAlreadyFrozen()
    {
        var theme = new ResourceDictionary
        {
            Source = new Uri("/ZZZModManager;component/Themes/DarkTheme.xaml", UriKind.Relative)
        };
        var live = (SolidColorBrush)theme["CanvasBrush"]!;
        var darkCanvas = live.Color;

        // Freeze a clone rather than the loaded instance: WPF may hand the same
        // brush to other consumers of this dictionary, and a frozen brush is exactly
        // the state this fallback has to survive.
        var frozen = live.Clone();
        frozen.Freeze();
        theme["CanvasBrush"] = frozen;

        var controller = new AppearanceController(theme);
        controller.Apply(new AppConfig { Theme = AppTheme.Light });

        var replacement = Assert.IsType<SolidColorBrush>(theme["CanvasBrush"]);
        Assert.NotSame(frozen, replacement);
        Assert.False(replacement.IsFrozen);
        Assert.NotEqual(darkCanvas, replacement.Color);
        Assert.Equal(darkCanvas, frozen.Color);
    }

    // The light theme shipped with an invisible toast message and a dark "全部" chip
    // because ThemeBrushes handed out the resource dictionary's own brush instance:
    // once Repaint replaced that entry with a thawed clone, every property that had
    // already been assigned kept painting the dark palette. Owning the instance is
    // what lets a theme switch reach assignments that were made long before it.
    [Fact]
    public void ThemeBrushesHandsOutItsOwnRepaintableInstance()
    {
        var theme = new ResourceDictionary
        {
            Source = new Uri("/ZZZModManager;component/Themes/DarkTheme.xaml", UriKind.Relative)
        };

        ThemeBrushes.Invalidate();
        try
        {
            var text = Assert.IsType<SolidColorBrush>(ThemeBrushes.Text);

            Assert.NotSame(theme["TextBrush"], text);
            Assert.False(text.IsFrozen);
            Assert.Equal(((SolidColorBrush)theme["TextBrush"]!).Color, text.Color);

            // A consumer that assigned this brush must never be handed a different
            // object later, before or after a palette switch.
            Assert.Same(text, ThemeBrushes.Text);
            ThemeBrushes.Refresh();
            Assert.Same(text, ThemeBrushes.Text);
        }
        finally
        {
            ThemeBrushes.Invalidate();
        }
    }

    [Fact]
    public void RefreshSurvivesACachedBrushThatStyleSealingFroze()
    {
        ThemeBrushes.Invalidate();
        try
        {
            var accent = (SolidColorBrush)ThemeBrushes.Accent;
            var style = new Style(typeof(Control));
            style.Setters.Add(new Setter(Control.BackgroundProperty, accent));
            style.Seal();

            Assert.True(accent.IsFrozen);
            ThemeBrushes.Refresh();
        }
        finally
        {
            ThemeBrushes.Invalidate();
        }
    }

    private static string LoadFixtureText(string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", fileName);
        Assert.True(File.Exists(path), $"UI fixture was not copied: {path}");
        return File.ReadAllText(path);
    }
}
