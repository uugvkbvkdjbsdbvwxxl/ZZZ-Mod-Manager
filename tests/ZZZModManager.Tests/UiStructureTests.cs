using System.Xml.Linq;
using Xunit;

namespace ZZZModManager.Tests;

public sealed class UiStructureTests
{
    private static readonly XNamespace XamlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";
    private static readonly XNamespace PresentationNamespace = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    [Fact]
    public void CharacterFiltersUseWrappingChipsWithoutHorizontalScrollViewer()
    {
        var document = LoadFixture("MainWindow.xaml");
        var filterControl = document.Descendants(PresentationNamespace + "ItemsControl")
            .Single(element => (string?)element.Attribute(XamlNamespace + "Name") == "CharacterFiltersItemsControl");

        Assert.DoesNotContain(
            filterControl.AncestorsAndSelf(),
            element => element.Name.LocalName == "ScrollViewer");
        Assert.Contains(
            filterControl.Descendants(),
            element => element.Name.LocalName == "WrapPanel");
    }

    [Fact]
    public void MainWindowKeepsCoreAutomationNamesAndEmptyStateAction()
    {
        var document = LoadFixture("MainWindow.xaml");
        var names = document.Descendants()
            .Select(element => (string?)element.Attribute(XamlNamespace + "Name"))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToHashSet(StringComparer.Ordinal);

        foreach (var name in new[]
                 {
                     "HomePage", "SettingsPage", "LogsPage", "HeroTitle", "GameStatusDot",
                     "GameStatusText", "RuntimeStatusText", "ManualReloadButton", "PrimaryActionButton",
                     "CharacterFiltersItemsControl", "SearchBox", "StatusFilterCombo",
                     "ModGroupsItemsControl", "EmptyStateBorder", "EmptyStateText", "GamePathBox",
                     "ImportDropZone", "AutoHideCheckBox", "ReloadRequiredCheckBox",
                     "CloseBehaviorComboBox", "LogSearchBox", "LogListBox", "ToastBorder",
                     "LightboxOverlay", "LightboxImage", "LightboxZoomSlider"
                 })
        {
            Assert.Contains(name, names);
        }

        var emptyState = document.Descendants(PresentationNamespace + "Border")
            .Single(element => (string?)element.Attribute(XamlNamespace + "Name") == "EmptyStateBorder");
        Assert.Contains(
            emptyState.Descendants(PresentationNamespace + "Button"),
            button => (string?)button.Attribute("Click") == "ClearFilters_Click");
    }

    [Fact]
    public void ThemeUsesOpaqueNightShiftTokensAndNoDecorativeGradient()
    {
        var document = LoadFixture("DarkTheme.xaml");
        var colors = document.Descendants(PresentationNamespace + "Color")
            .ToDictionary(
                element => (string?)element.Attribute(XamlNamespace + "Key") ?? string.Empty,
                element => element.Value,
                StringComparer.Ordinal);

        Assert.Equal("#FF0B0F16", colors["CanvasColor"]);
        Assert.Equal("#FF171E29", colors["SurfaceColor"]);
        Assert.Equal("#FF58E6B2", colors["AccentColor"]);
        Assert.DoesNotContain(document.Descendants(), element => element.Name.LocalName == "LinearGradientBrush");
        Assert.Contains(
            document.Descendants(PresentationNamespace + "Style"),
            style => (string?)style.Attribute(XamlNamespace + "Key") == "PanelBorder");
    }

    [Fact]
    public void ThemeProvidesKeyboardAwareDarkComboBoxTemplate()
    {
        var document = LoadFixture("DarkTheme.xaml");
        var comboTemplate = document.Descendants(PresentationNamespace + "ControlTemplate")
            .Single(template => (string?)template.Attribute(XamlNamespace + "Key") == "ComboBoxTemplate");

        Assert.Contains(
            comboTemplate.Descendants(PresentationNamespace + "Popup"),
            popup => (string?)popup.Attribute(XamlNamespace + "Name") == "PART_Popup");
        Assert.NotEmpty(comboTemplate.Descendants(PresentationNamespace + "ItemsPresenter"));
        Assert.Contains(
            comboTemplate.Descendants(PresentationNamespace + "ToggleButton"),
            toggle => ((string?)toggle.Attribute("IsChecked"))?.Contains("IsDropDownOpen", StringComparison.Ordinal) == true);
        Assert.Contains(
            comboTemplate.Descendants(PresentationNamespace + "Trigger"),
            trigger => (string?)trigger.Attribute("Property") == "IsKeyboardFocusWithin");
    }

    [Fact]
    public void DangerButtonKeepsCoralSemanticsAcrossInteractionStates()
    {
        var document = LoadFixture("DarkTheme.xaml");
        var dangerTemplate = document.Descendants(PresentationNamespace + "ControlTemplate")
            .Single(template => (string?)template.Attribute(XamlNamespace + "Key") == "DangerButtonTemplate");
        var dangerStyle = document.Descendants(PresentationNamespace + "Style")
            .Single(style => (string?)style.Attribute(XamlNamespace + "Key") == "DangerButton");

        Assert.Contains(
            dangerTemplate.Descendants(PresentationNamespace + "Setter"),
            setter => (string?)setter.Attribute("Value") == "{StaticResource ErrorBrush}");
        Assert.Contains(
            dangerTemplate.Descendants(PresentationNamespace + "Trigger"),
            trigger => (string?)trigger.Attribute("Property") == "IsMouseOver");
        Assert.Contains(
            dangerTemplate.Descendants(PresentationNamespace + "Trigger"),
            trigger => (string?)trigger.Attribute("Property") == "IsPressed");
        Assert.Contains(
            dangerTemplate.Descendants(PresentationNamespace + "Trigger"),
            trigger => (string?)trigger.Attribute("Property") == "IsKeyboardFocusWithin");
        Assert.Contains(
            dangerStyle.Descendants(PresentationNamespace + "Setter"),
            setter => (string?)setter.Attribute("Property") == "Template"
                      && ((string?)setter.Attribute("Value"))?.Contains("DangerButtonTemplate", StringComparison.Ordinal) == true);
    }

    private static XDocument LoadFixture(string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", fileName);
        Assert.True(File.Exists(path), $"UI fixture was not copied: {path}");
        return XDocument.Load(path, LoadOptions.PreserveWhitespace);
    }
}
