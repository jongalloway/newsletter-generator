using NewsletterGenerator.Services;

namespace NewsletterGenerator.Tests;

public class AtomFeedServiceTests
{
    // ── HtmlToText ────────────────────────────────────────────────────────────

    [Fact]
    public void HtmlToText_ConvertsListItems()
    {
        var html = "<ul><li>First item</li><li>Second item</li></ul>";
        var result = AtomFeedService.HtmlToText(html);

        Assert.Contains("- First item", result);
        Assert.Contains("- Second item", result);
    }

    [Fact]
    public void HtmlToText_ConvertsHeadings()
    {
        var html = "<h2>Release Notes</h2><p>Some content</p>";
        var result = AtomFeedService.HtmlToText(html);

        Assert.Contains("### Release Notes", result);
        Assert.Contains("Some content", result);
    }

    [Fact]
    public void HtmlToText_StripsAllTags()
    {
        var html = "<div><span class=\"highlight\">Important</span> text</div>";
        var result = AtomFeedService.HtmlToText(html);

        Assert.Equal("Important text", result);
        Assert.DoesNotContain("<", result);
    }

    [Fact]
    public void HtmlToText_DecodesHtmlEntities()
    {
        var html = "AT&amp;T &lt;rocks&gt; &quot;yes&quot;";
        var result = AtomFeedService.HtmlToText(html);

        Assert.Contains("AT&T", result);
        Assert.Contains("<rocks>", result);
        Assert.Contains("\"yes\"", result);
    }

    [Fact]
    public void HtmlToText_CollapsesExcessiveBlankLines()
    {
        var html = "<p>First</p><p></p><p></p><p></p><p>Second</p>";
        var result = AtomFeedService.HtmlToText(html);

        Assert.DoesNotContain("\n\n\n", result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void HtmlToText_EmptyInput_ReturnsEmpty(string input)
    {
        Assert.Equal(string.Empty, AtomFeedService.HtmlToText(input));
    }

    [Fact]
    public void HtmlToText_ConvertsBrTags()
    {
        var html = "Line one<br/>Line two<br />Line three";
        var result = AtomFeedService.HtmlToText(html);

        Assert.Contains("Line one\nLine two\nLine three", result);
    }

    // ── FilterReleaseText ─────────────────────────────────────────────────────

    [Theory]
    [InlineData("- fix: correct typo in readme")]
    [InlineData("- chore(deps): bump lodash")]
    [InlineData("- docs: update API reference")]
    [InlineData("- ci: add GitHub Actions workflow")]
    [InlineData("- test: add unit tests for parser")]
    [InlineData("- refactor: clean up handler")]
    [InlineData("- build(deps): update dependency")]
    [InlineData("- style(lint): fix formatting")]
    public void FilterReleaseText_RemovesLowValueLines(string lowValueLine)
    {
        var text = $"feat: Add new API endpoint\n{lowValueLine}\nAdded streaming support";
        var result = AtomFeedService.FilterReleaseText(text);

        Assert.DoesNotContain(lowValueLine.TrimStart('-', ' '), result);
        Assert.Contains("Add new API endpoint", result);
        Assert.Contains("Added streaming support", result);
    }

    [Fact]
    public void FilterReleaseText_KeepsFeatureLines()
    {
        var text = "feat: Add streaming support\nNew MCP protocol handler\nAdd debug logging";
        var result = AtomFeedService.FilterReleaseText(text);

        Assert.Contains("Add streaming support", result);
        Assert.Contains("New MCP protocol handler", result);
    }

    [Fact]
    public void FilterReleaseText_StripsGitHubAttributions()
    {
        var text = "feat: Add streaming support by @octocat in #123";
        var result = AtomFeedService.FilterReleaseText(text);

        Assert.Contains("Add streaming support", result);
        Assert.DoesNotContain("@octocat", result);
        Assert.DoesNotContain("#123", result);
    }

    [Fact]
    public void FilterReleaseText_EmptyInput_ReturnsAsIs()
    {
        Assert.Equal("", AtomFeedService.FilterReleaseText(""));
        Assert.Equal("   ", AtomFeedService.FilterReleaseText("   "));
    }

    // ── ExtractVersionTag ─────────────────────────────────────────────────────

    [Theory]
    [InlineData("v0.1.25", "v0.1.25")]
    [InlineData("go/v0.1.26-preview.0: Add E2E tests", "go/v0.1.26-preview.0")]
    [InlineData("v0.1.25-preview.0: Fix MCP env vars", "v0.1.25-preview.0")]
    [InlineData("go/v0.1.25", "go/v0.1.25")]
    public void ExtractVersionTag_ParsesCorrectly(string input, string expected)
    {
        Assert.Equal(expected, AtomFeedService.ExtractVersionTag(input));
    }

    // ── FormatLangLabel ───────────────────────────────────────────────────────

    [Theory]
    [InlineData("go", "Go")]
    [InlineData("python", "Python")]
    [InlineData("dotnet", ".NET")]
    [InlineData("csharp", "C#")]
    [InlineData("typescript", "TypeScript")]
    [InlineData("javascript", "JavaScript")]
    [InlineData("rust", "rust")]
    public void FormatLangLabel_MapsCorrectly(string input, string expected)
    {
        Assert.Equal(expected, AtomFeedService.FormatLangLabel(input));
    }
}
