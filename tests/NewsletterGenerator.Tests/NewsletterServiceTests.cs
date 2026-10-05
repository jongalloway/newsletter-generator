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
}
