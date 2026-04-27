using System.IO;
using System.Xml.Linq;

namespace FluxVault.App.Tests;

public sealed class XamlQualityTests
{
    private static readonly XNamespace XamlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    [Fact]
    public void Activity_pane_refresh_button_uses_compact_header_column()
    {
        var document = LoadXaml("src", "FluxVault.App", "ActivityPaneWindow.xaml");

        var refreshButton = document
            .Descendants(XamlNamespace + "Button")
            .Single(element => (string?)element.Attribute("Content") == "Refresh");

        Assert.Equal("1", (string?)refreshButton.Attribute("Grid.Column"));
        Assert.Equal("Right", (string?)refreshButton.Attribute("HorizontalAlignment"));
        Assert.DoesNotContain(refreshButton.Ancestors(), ancestor => ancestor.Name == XamlNamespace + "DockPanel");
    }

    [Fact]
    public void Options_controls_have_helpful_tooltips()
    {
        var document = LoadXaml("src", "FluxVault.App", "OptionsWindow.xaml");
        var interactiveElements = document
            .Descendants()
            .Where(element => element.Name.Namespace == XamlNamespace
                              && element.Name.LocalName is "TextBox" or "ComboBox" or "CheckBox" or "Button")
            .ToArray();

        var missingTooltips = interactiveElements
            .Where(element => string.IsNullOrWhiteSpace((string?)element.Attribute("ToolTip")))
            .Select(Describe)
            .ToArray();

        Assert.Empty(missingTooltips);
    }

    [Fact]
    public void Options_labels_have_helpful_tooltips()
    {
        var document = LoadXaml("src", "FluxVault.App", "OptionsWindow.xaml");
        string[] optionLabels =
        [
            "Keep every version for",
            "Keep hourly versions for days",
            "Keep daily versions for days",
            "Minimum versions per file",
            "Watcher poll seconds",
            "Reconciliation minutes",
            "Debounce fast / balanced / quiet",
            "Max hot delay fast / balanced / quiet",
            "Minimum same-file interval seconds",
            "Maximum concurrent captures",
            "Profile / default / hot-file",
            "Level / minimum KB"
        ];

        foreach (var label in optionLabels)
        {
            var textBlock = document
                .Descendants(XamlNamespace + "TextBlock")
                .Single(element => (string?)element.Attribute("Text") == label);
            Assert.False(string.IsNullOrWhiteSpace((string?)textBlock.Attribute("ToolTip")), label);
        }
    }

    [Fact]
    public void Main_header_constrains_status_text_away_from_command_buttons()
    {
        var document = LoadXaml("src", "FluxVault.App", "MainWindow.xaml");

        Assert.Empty(document.Descendants(XamlNamespace + "DockPanel"));
        var serviceStatus = document
            .Descendants(XamlNamespace + "TextBlock")
            .Single(element => (string?)element.Attribute("Text") == "{Binding ServiceStatus}");
        var commandPanel = document
            .Descendants(XamlNamespace + "StackPanel")
            .Single(element => element
                .Descendants(XamlNamespace + "Button")
                .Any(button => (string?)button.Attribute("Content") == "Export diagnostics"));

        Assert.Equal("CharacterEllipsis", (string?)serviceStatus.Attribute("TextTrimming"));
        Assert.Equal("0", (string?)serviceStatus.Attribute("MinWidth"));
        Assert.NotNull(serviceStatus.Element(XamlNamespace + "TextBlock.ToolTip"));
        Assert.Equal("1", (string?)commandPanel.Attribute("Grid.Column"));
    }

    [Fact]
    public void Usn_health_uses_wrapping_tooltip_content()
    {
        var document = LoadXaml("src", "FluxVault.App", "MainWindow.xaml");
        var usnHealth = document
            .Descendants(XamlNamespace + "TextBlock")
            .Single(element => (string?)element.Attribute("Text") == "{Binding UsnHealth}");
        var tooltipText = usnHealth
            .Element(XamlNamespace + "TextBlock.ToolTip")
            ?.Element(XamlNamespace + "ToolTip")
            ?.Element(XamlNamespace + "TextBlock");

        Assert.NotNull(tooltipText);
        Assert.Equal("{Binding UsnHealthToolTip}", (string?)tooltipText.Attribute("Text"));
        Assert.Equal("Wrap", (string?)tooltipText.Attribute("TextWrapping"));
    }

    private static XDocument LoadXaml(params string[] relativePathParts)
    {
        var root = FindRepositoryRoot();
        return XDocument.Load(Path.Combine([root, .. relativePathParts]));
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "FluxVault.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Could not locate repository root.");
    }

    private static string Describe(XElement element)
    {
        return $"{element.Name.LocalName}:{(string?)element.Attribute("Content") ?? (string?)element.Attribute("Text") ?? (string?)element.Attribute("Name") ?? element.ToString(SaveOptions.DisableFormatting)}";
    }
}
