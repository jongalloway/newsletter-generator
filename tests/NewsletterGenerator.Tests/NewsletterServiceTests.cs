using NewsletterGenerator.Services;

namespace NewsletterGenerator.Tests;

public class NewsletterServiceTests
{
    [Theory]
    [InlineData("newsletter-title", "low")]
    [InlineData("welcome-summary", "medium")]
    [InlineData("news-announcements", "medium")]
    [InlineData("revision", "medium")]
    [InlineData("release-section-synthesis", "high")]
    [InlineData("vscode-newsletter", "high")]
    [InlineData("section-synthesis", "high")]
    public void ResolveReasoningEffort_ReturnsExpectedProfile(string operationProfile, string expectedReasoningEffort)
    {
        var actual = NewsletterService.ResolveReasoningEffort(operationProfile);

        Assert.Equal(expectedReasoningEffort, actual);
    }

    [Fact]
    public void ResolveReasoningEffort_UnknownOperation_UsesMediumDefault()
    {
        var actual = NewsletterService.ResolveReasoningEffort("unknown-operation");

        Assert.Equal("medium", actual);
    }

    [Theory]
    [InlineData(false, "temporarily unavailable", false)]
    [InlineData(true, "temporarily unavailable", true)]
    [InlineData(true, "InvalidArg: unsupported request", false)]
    [InlineData(true, "No GitHub OAuth token or Copilot HMAC key provided", false)]
    [InlineData(true, "authentication failed", false)]
    [InlineData(true, "unauthorized", false)]
    [InlineData(true, "", false)]
    public void ShouldRetrySessionError_ReturnsExpectedHandling(
        bool recoverable,
        string error,
        bool expected)
    {
        Assert.Equal(expected, NewsletterService.ShouldRetrySessionError(recoverable, error));
    }

    [Theory]
    [InlineData("Session error: No GitHub OAuth token or Copilot HMAC key provided", true)]
    [InlineData("Copilot HMAC key unavailable", true)]
    [InlineData("Request timed out", false)]
    [InlineData("InvalidArg: unsupported request", false)]
    public void IsCredentialSessionError_ReturnsExpectedResult(string message, bool expected)
    {
        Assert.Equal(
            expected,
            NewsletterService.IsCredentialSessionError(new InvalidOperationException(message)));
    }

    [Fact]
    public void IsCredentialSessionError_ExaminesInnerExceptions()
    {
        var exception = new Exception(
            "Outer failure",
            new InvalidOperationException("No GitHub OAuth token provided"));

        Assert.True(NewsletterService.IsCredentialSessionError(exception));
    }

    [Fact]
    public void NormalizeMarkdownListSpacing_SurroundsListWithoutSeparatingItems()
    {
        var markdown = string.Join(
            Environment.NewLine,
            "## Release",
            "",
            "Release summary.",
            "- First item",
            "- Second item",
            "Following content.");

        var normalized = NewsletterService.NormalizeMarkdownListSpacing(markdown);

        Assert.Equal(
            string.Join(
                Environment.NewLine,
                "## Release",
                "",
                "Release summary.",
                "",
                "- First item",
                "- Second item",
                "",
                "Following content."),
            normalized);
    }

    [Fact]
    public void NormalizeMarkdownListSpacing_PreservesAlreadySpacedLists()
    {
        const string markdown = """
            Intro.

            1. First item
            2. Second item

            Outro.
            """;

        Assert.Equal(markdown, NewsletterService.NormalizeMarkdownListSpacing(markdown));
    }

    [Fact]
    public void NormalizeMarkdownListSpacing_PreservesIndentedListContinuation()
    {
        const string markdown = """
            Intro.

            - Item
              continuation
              - Nested item

            Outro.
            """;

        Assert.Equal(markdown, NewsletterService.NormalizeMarkdownListSpacing(markdown));
    }
}
