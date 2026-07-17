namespace UProxy.Core.Tests;

/// <summary>
/// Static layout guards for WinForms sources. We can't render WinForms on Linux CI, so we
/// catch the DPI clipping patterns that bit Settings/Export/SecretScan (fixed-height
/// FlowLayoutPanels that refuse to wrap or grow with AutoSize buttons).
/// </summary>
public class UiLayoutGuardTests
{
    private static string UiDir
    {
        get
        {
            var candidates = new[]
            {
                Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "UProxy.UI")),
                "/workspace/src/UProxy.UI"
            };
            return candidates.First(Directory.Exists);
        }
    }

    public static TheoryData<string> FormFiles
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var file in Directory.EnumerateFiles(UiDir, "*Form.cs"))
                data.Add(file);
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(FormFiles))]
    public void Forms_DoNotUseFixedHeightFlowToolbars(string formPath)
    {
        var src = File.ReadAllText(formPath);
        // Collapse whitespace so multiline object-initializers still match.
        var flat = System.Text.RegularExpressions.Regex.Replace(src, @"\s+", " ");

        // Anti-pattern that clipped SecretScan/Settings/Export: Dock=Top FlowLayoutPanel with a
        // fixed Height and WrapContents=false (buttons get vertically clipped under DPI change).
        var matches = System.Text.RegularExpressions.Regex.Matches(
            flat,
            @"new FlowLayoutPanel\s*\{[^}]{0,400}?Height\s*=\s*\d+[^}]{0,400}?\}");

        foreach (System.Text.RegularExpressions.Match m in matches)
        {
            var block = m.Value;
            // AutoSize toolbars are fine even with a Height hint; fixed-only ones are not.
            if (block.Contains("AutoSize = true", StringComparison.Ordinal) ||
                block.Contains("AutoSize=true", StringComparison.Ordinal))
                continue;

            Assert.Fail(
                $"{Path.GetFileName(formPath)} has a fixed-height FlowLayoutPanel that will clip under DPI changes:\n{block}");
        }
    }

    [Theory]
    [MemberData(nameof(FormFiles))]
    public void Forms_EnableDpiAutoScale(string formPath)
    {
        var src = File.ReadAllText(formPath);
        // MainForm sets AutoScale in the ctor before BuildUi; dialogs set it in their ctor.
        Assert.Contains("AutoScaleMode", src, StringComparison.Ordinal);
        Assert.Contains("AutoScaleMode.Dpi", src, StringComparison.Ordinal);
    }

    [Fact]
    public void AllForms_AreCovered()
    {
        var names = Directory.EnumerateFiles(UiDir, "*Form.cs")
            .Select(Path.GetFileName)
            .OrderBy(x => x)
            .ToArray();
        Assert.Contains("MainForm.cs", names);
        Assert.Contains("SettingsForm.cs", names);
        Assert.Contains("ExportForm.cs", names);
        Assert.Contains("SecretScanForm.cs", names);
    }
}
