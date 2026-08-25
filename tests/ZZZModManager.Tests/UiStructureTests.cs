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
                     "LightboxOverlay", "LightboxImage", "LightboxZoomSlider",
                     "BackgroundImage", "BackgroundVeil", "ThemeComboBox",
                     "SidebarOpacitySlider", "PanelOpacitySlider", "BackgroundOpacitySlider",
                     "ModRootBox", "ModRootHintText"
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
    public void CardToggleIsNamedSoArrowKeyNavigationCanTargetIt()
    {
        var document = LoadFixture("MainWindow.xaml");
        var toggle = document.Descendants(PresentationNamespace + "Button")
            .Single(element => (string?)element.Attribute(XamlNamespace + "Name") == "CardToggleButton");

        Assert.Equal("ToggleMod_Click", (string?)toggle.Attribute("Click"));
        Assert.Equal("{Binding ToggleText}", (string?)toggle.Attribute("Content"));
        Assert.Equal("36", (string?)toggle.Attribute("MinHeight"));
        Assert.Contains("Enter", (string?)toggle.Attribute("ToolTip"));
    }

    [Fact]
    public void CharacterFilterAreaIsHeightCappedByClippingWithAnExpandAffordance()
    {
        var document = LoadFixture("MainWindow.xaml");
        var clip = document.Descendants(PresentationNamespace + "Border")
            .Single(element => (string?)element.Attribute(XamlNamespace + "Name") == "CharacterFilterClip");

        Assert.Equal("True", (string?)clip.Attribute("ClipToBounds"));
        Assert.Equal("82", (string?)clip.Attribute("MaxHeight"));
        Assert.Equal("CharacterFilterClip_GotKeyboardFocus", (string?)clip.Attribute("GotKeyboardFocus"));
        Assert.DoesNotContain(
            clip.DescendantsAndSelf(),
            element => element.Name.LocalName == "ScrollViewer");

        var filters = clip.Descendants(PresentationNamespace + "ItemsControl")
            .Single(element => (string?)element.Attribute(XamlNamespace + "Name") == "CharacterFiltersItemsControl");
        Assert.Equal("CharacterFilters_SizeChanged", (string?)filters.Attribute("SizeChanged"));

        var expand = document.Descendants(PresentationNamespace + "Button")
            .Single(element => (string?)element.Attribute(XamlNamespace + "Name") == "CharacterFilterExpandButton");
        Assert.Equal("Collapsed", (string?)expand.Attribute("Visibility"));
        Assert.Equal("ToggleCharacterFilterExpand_Click", (string?)expand.Attribute("Click"));
        Assert.Contains("GhostButton", (string?)expand.Attribute("Style"));
    }

    [Fact]
    public void SettingsSplitsSwitchCaveatsIntoImmediateAndRestartGroups()
    {
        var document = LoadFixture("MainWindow.xaml");
        var notes = document.Descendants(PresentationNamespace + "StackPanel")
            .Single(element => (string?)element.Attribute(XamlNamespace + "Name") == "SwitchBehaviorNotes");

        Assert.Equal("620", (string?)notes.Attribute("MaxWidth"));

        var lines = notes.Elements(PresentationNamespace + "TextBlock")
            .Select(block => (string?)block.Attribute("Text") ?? string.Empty)
            .ToList();

        Assert.Equal("立即生效", lines[0]);
        Assert.Contains("需要重启", lines[3]);
        Assert.All(lines, line => Assert.True(line.Length <= 70));
        Assert.All(
            notes.Elements(PresentationNamespace + "TextBlock").Skip(1).Where(block => block.Attribute("FontWeight") is null),
            block => Assert.Equal("Wrap", (string?)block.Attribute("TextWrapping")));
    }

    [Fact]
    public void CardSecondaryActionsLiveInOverflowMenuWithDeleteLast()
    {
        var document = LoadFixture("MainWindow.xaml");
        var menu = document.Descendants(PresentationNamespace + "ContextMenu").Single();
        var items = menu.Elements().ToList();

        var handlers = items
            .Where(item => item.Name.LocalName == "MenuItem")
            .Select(item => (string?)item.Attribute("Click"))
            .ToList();
        Assert.Equal(
            new[]
            {
                "ShowHotkeys_Click", "InspectMod_Click", "OpenModDirectory_Click",
                "ChangeGroup_Click", "DeleteMod_Click"
            },
            handlers);

        var last = items[^1];
        Assert.Equal("MenuItem", last.Name.LocalName);
        Assert.Equal("DeleteMod_Click", (string?)last.Attribute("Click"));
        Assert.Contains("DangerMenuItem", (string?)last.Attribute("Style"));
        Assert.Equal("Separator", items[^2].Name.LocalName);

        var overflow = menu.Parent?.Parent;
        Assert.NotNull(overflow);
        Assert.Equal("Button", overflow!.Name.LocalName);
        Assert.Equal("CardOverflow_Click", (string?)overflow.Attribute("Click"));

        Assert.DoesNotContain(document.Descendants(), element => element.Name.LocalName == "UniformGrid");
    }

    [Fact]
    public void FilterChipsSignalSelectionBeyondColourAlone()
    {
        var document = LoadFixture("MainWindow.xaml");
        var filterControl = document.Descendants(PresentationNamespace + "ItemsControl")
            .Single(element => (string?)element.Attribute(XamlNamespace + "Name") == "CharacterFiltersItemsControl");
        var chip = filterControl.Descendants(PresentationNamespace + "Button")
            .Single(button => (string?)button.Attribute("Click") == "CharacterFilter_Click");

        Assert.Equal("{Binding Foreground}", (string?)chip.Attribute("Foreground"));
        Assert.Equal("{Binding LabelWeight}", (string?)chip.Attribute("FontWeight"));
        Assert.Equal("34", (string?)chip.Attribute("MinHeight"));
    }

    [Fact]
    public void ToastIsPositionedByGridCellRatherThanSidebarMargin()
    {
        var document = LoadFixture("MainWindow.xaml");
        var toast = document.Descendants(PresentationNamespace + "Border")
            .Single(element => (string?)element.Attribute(XamlNamespace + "Name") == "ToastBorder");

        Assert.Equal("1", (string?)toast.Attribute("Grid.Column"));
        Assert.Equal("0", (string?)toast.Attribute("Grid.Row"));
        Assert.Equal("0,18,0,0", (string?)toast.Attribute("Margin"));
        Assert.Equal("Grid", toast.Parent?.Name.LocalName);
    }

    [Fact]
    public void MainWindowViewsCarryNoRawHexColours()
    {
        var document = LoadFixture("MainWindow.xaml");
        var offenders = document.Descendants()
            .SelectMany(element => element.Attributes())
            .Where(attribute => attribute.Name.LocalName is "Background" or "Foreground" or "BorderBrush" or "Fill")
            .Select(attribute => attribute.Value)
            .Where(value => value.StartsWith('#'))
            .ToList();

        Assert.Equal(new[] { "#E9000000" }, offenders);
    }

    [Fact]
    public void ThemeStylesPopupChromeSoOverflowMenusStayDark()
    {
        var document = LoadFixture("DarkTheme.xaml");
        var styles = document.Descendants(PresentationNamespace + "Style").ToList();

        foreach (var target in new[] { "ContextMenu", "MenuItem", "Separator" })
        {
            Assert.Contains(
                styles,
                style => ((string?)style.Attribute("TargetType"))?.Contains(target, StringComparison.Ordinal) == true
                         && style.Attribute(XamlNamespace + "Key") is null);
        }

        var danger = styles.Single(style => (string?)style.Attribute(XamlNamespace + "Key") == "DangerMenuItem");
        Assert.Contains(
            danger.Elements(PresentationNamespace + "Setter"),
            setter => (string?)setter.Attribute("Property") == "Foreground"
                      && (string?)setter.Attribute("Value") == "{DynamicResource ErrorBrush}");
    }

    [Fact]
    public void ThemeUsesOpaqueOpsConsoleTokensAndNoDecorativeGradient()
    {
        var document = LoadFixture("DarkTheme.xaml");
        var colors = document.Descendants(PresentationNamespace + "Color")
            .ToDictionary(
                element => (string?)element.Attribute(XamlNamespace + "Key") ?? string.Empty,
                element => element.Value,
                StringComparer.Ordinal);

        Assert.Equal("#FF0C0D12", colors["CanvasColor"]);
        Assert.Equal("#FF171922", colors["SurfaceColor"]);
        Assert.Equal("#FF8C7CFF", colors["AccentColor"]);
        Assert.Equal("#FF3FCF8E", colors["SuccessColor"]);
        Assert.NotEqual(colors["AccentColor"], colors["SuccessColor"]);
        foreach (var color in colors.Values)
        {
            Assert.StartsWith("#FF", color, StringComparison.Ordinal);
        }

        Assert.DoesNotContain(document.Descendants(), element => element.Name.LocalName == "LinearGradientBrush");
        Assert.Contains(
            document.Descendants(PresentationNamespace + "Style"),
            style => (string?)style.Attribute(XamlNamespace + "Key") == "PanelBorder");
    }

    [Fact]
    public void ThemeCentralisesShapeAndMetricTokens()
    {
        var document = LoadFixture("DarkTheme.xaml");
        var radii = document.Descendants(PresentationNamespace + "CornerRadius")
            .ToDictionary(
                element => (string?)element.Attribute(XamlNamespace + "Key") ?? string.Empty,
                element => element.Value,
                StringComparer.Ordinal);

        Assert.Equal("6", radii["ControlRadius"]);
        Assert.Equal("12", radii["PanelRadius"]);
        Assert.Contains("PillRadius", radii.Keys);
        Assert.DoesNotContain(
            document.Descendants(),
            element => (string?)element.Attribute("CornerRadius") == "8");
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
            setter => (string?)setter.Attribute("Value") == "{DynamicResource ErrorBrush}");
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

    [Fact]
    public void ModGridVirtualizesGroupsThroughAnInternalScrollViewer()
    {
        var document = LoadFixture("MainWindow.xaml");
        var grid = document.Descendants(PresentationNamespace + "ItemsControl")
            .Single(element => (string?)element.Attribute(XamlNamespace + "Name") == "ModGroupsItemsControl");

        Assert.Equal("True", (string?)grid.Attribute("VirtualizingPanel.IsVirtualizing"));
        Assert.Equal("Recycling", (string?)grid.Attribute("VirtualizingPanel.VirtualizationMode"));
        Assert.Equal("Pixel", (string?)grid.Attribute("VirtualizingPanel.ScrollUnit"));
        Assert.Equal("1,1", (string?)grid.Attribute("VirtualizingPanel.CacheLength"));
        Assert.Equal("Page", (string?)grid.Attribute("VirtualizingPanel.CacheLengthUnit"));

        Assert.DoesNotContain(
            grid.Ancestors(),
            element => element.Name.LocalName == "ScrollViewer");

        var template = grid.Elements(PresentationNamespace + "ItemsControl.Template")
            .Single()
            .Descendants(PresentationNamespace + "ControlTemplate")
            .Single();
        var scrollViewer = template.Descendants(PresentationNamespace + "ScrollViewer").Single();
        Assert.Equal("ModGridScrollViewer", (string?)scrollViewer.Attribute(XamlNamespace + "Name"));
        Assert.Equal("True", (string?)scrollViewer.Attribute("CanContentScroll"));
        Assert.NotEmpty(scrollViewer.Descendants(PresentationNamespace + "ItemsPresenter"));

        var itemsPanel = grid.Elements(PresentationNamespace + "ItemsControl.ItemsPanel").Single();
        Assert.NotEmpty(itemsPanel.Descendants(PresentationNamespace + "VirtualizingStackPanel"));
    }

    [Fact]
    public void ModRootIsShownReadOnlyNextToItsBrowseButtonWithARestartHint()
    {
        var document = LoadFixture("MainWindow.xaml");
        var box = document.Descendants(PresentationNamespace + "TextBox")
            .Single(element => (string?)element.Attribute(XamlNamespace + "Name") == "ModRootBox");

        Assert.Equal("True", (string?)box.Attribute("IsReadOnly"));

        var grid = box.Parent!;
        Assert.Equal("Grid", grid.Name.LocalName);
        var columns = grid.Elements(PresentationNamespace + "Grid.ColumnDefinitions")
            .Single()
            .Elements(PresentationNamespace + "ColumnDefinition")
            .Select(column => (string?)column.Attribute("Width"))
            .ToList();
        Assert.Equal(["*", "Auto"], columns);

        var browse = grid.Elements(PresentationNamespace + "Button").Single();
        Assert.Equal("BrowseModRoot_Click", (string?)browse.Attribute("Click"));
        Assert.Equal("1", (string?)browse.Attribute("Grid.Column"));

        var hint = document.Descendants(PresentationNamespace + "TextBlock")
            .Single(element => (string?)element.Attribute(XamlNamespace + "Name") == "ModRootHintText");
        Assert.Equal("Wrap", (string?)hint.Attribute("TextWrapping"));
        Assert.Same(grid.Parent, hint.Parent);
    }

    private static XDocument LoadFixture(string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", fileName);
        Assert.True(File.Exists(path), $"UI fixture was not copied: {path}");
        return XDocument.Load(path, LoadOptions.PreserveWhitespace);
    }
}
