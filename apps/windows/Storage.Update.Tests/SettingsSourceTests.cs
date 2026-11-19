using System.Text.RegularExpressions;
using System.Xml.Linq;

static class SettingsSourceTests
{
    public static void Run()
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "BrowserSource");
        string xaml = File.ReadAllText(Path.Combine(directory, "Shell.xaml"));
        var shell = XDocument.Parse(xaml);
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var controls = shell.Descendants().Where(e => e.Attribute(x + "Name") is not null)
            .ToDictionary(e => e.Attribute(x + "Name")!.Value, e => e.Name.LocalName);
        foreach (string file in new[] { "MainWindow.cs", "WindowsUpdatesPanel.cs" })
        {
            string code = File.ReadAllText(Path.Combine(directory, file + ".txt"));
            var generics = Regex.Matches(code, "C<([A-Za-z]+)>\\(\"([^\"]+)\"\\)");
            var casts = Regex.Matches(code, "\\(([A-Za-z]+)\\)root.FindName\\(\"([^\"]+)\"\\)");
            foreach (Match match in generics.Concat(casts)) FeedTests.Require(controls.GetValueOrDefault(match.Groups[2].Value) == match.Groups[1].Value, "Real shell typed lookup: " + match.Value);
        }
        FeedTests.Require(!xaml.Contains("x:Class") && !xaml.Contains("x:Bind") && !xaml.Contains("Click="), "Runtime shell remains independent of generated XAML code");
        foreach (string name in new[] { "WhatsNew", "CommitDetails", "TechnicalDetails", "SecurityDetails" })
            FeedTests.Require(shell.Descendants().Single(e => e.Attribute(x + "Name")?.Value == name).Attribute("IsExpanded")?.Value == "False", "Native disclosure initially collapsed: " + name);
        var sectionStyle = shell.Descendants().Single(e => e.Attribute(x + "Key")?.Value == "SettingsSection");
        string? SectionToken(string property) => sectionStyle.Elements().Single(e => e.Attribute("Property")?.Value == property).Attribute("Value")?.Value;
        FeedTests.Require(SectionToken("FontSize") == "21" && SectionToken("FontWeight") == "SemiBold" && SectionToken("Foreground") == "#EFF3F5" && SectionToken("Margin") == "0,0,0,16", "Selected refined section headings: 21px semibold ink with 16px bottom gap");
        Console.WriteLine("PASS selected refined section heading tokens from parsed runtime XAML");
        XElement Named(string name) => shell.Descendants().Single(e => e.Attribute(x + "Name")?.Value == name);
        var columns = Named("WorkspaceColumns");
        var sidebar = columns.Elements().Single(e => e.Name.LocalName == "Border" && e.Attribute(x + "Name") is null);
        FeedTests.Require(columns.Elements().Single(e => e.Name.LocalName == "Grid.ColumnDefinitions").Elements().First().Attribute("Width")?.Value == "190", "Refined sidebar width is 190px");
        FeedTests.Require(sidebar.Attribute("Padding")?.Value == "12,22,12,16" && !sidebar.Descendants().Any(e => e.Attribute("Text")?.Value == "YOUR WORKSPACE"), "Refined sidebar starts directly with navigation at 12px horizontal / 22px top padding");
        FeedTests.Require(Named("WorkspaceContent").Attribute("Padding")?.Value == "28,22,28,26" && Named("Heading").Attribute("FontSize")?.Value == "28", "Refined main padding and 28px page title");
        var content = Named("SettingsContent");
        FeedTests.Require(content.Attribute("MaxWidth")?.Value == "720" && !content.Elements().Any(e => e.Name.LocalName == "TextBlock"), "Refined Settings begins with Updates, without the grouped-variant subtitle");
        FeedTests.Require(Named("SettingsPanel").Attribute("Margin")?.Value == "0,24,0,0", "Exactly one 24px page-title-to-Updates gap (workspace row spacing is zero in Settings)");
        foreach (string heading in new[] { "Updates", "Local service", "About Storage" })
            FeedTests.Require(content.Descendants().Any(e => e.Attribute("Text")?.Value == heading && e.Attribute("Style")?.Value == "{StaticResource SettingsSection}"), "Windows retains refined section: " + heading);
        Console.WriteLine("PASS selected refined sidebar, main/title geometry, subtitle-free Settings and all three Windows sections");
        FeedTests.Require(Named("OfferedVersion").Attribute("Foreground")?.Value == "{StaticResource Accent}" && shell.Descendants().Single(e => e.Attribute(x + "Key")?.Value == "Accent").Attribute("Color")?.Value == "#7CD2DD", "Offered version uses the refined cyan accent brush");
        Console.WriteLine("PASS selected refined offered-version accent from parsed runtime XAML");
        Console.WriteLine($"PASS actual runtime XAML parses; {controls.Count} unique control names and all MainWindow/update-panel typed lookups agree; disclosures collapsed");
    }
}
