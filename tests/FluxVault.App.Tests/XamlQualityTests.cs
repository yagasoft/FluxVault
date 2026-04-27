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
