using Microsoft.Copilot;
using NewsletterGenerator.Services;

namespace NewsletterGenerator.Tests;

public class NewsletterServiceFallbackTests
{
    [Fact]
    public void BuildModelFallbackOrder_UsesDefaultOrder_WhenNoConfiguredFallbacks()
    {
        var result = NewsletterService.BuildModelFallbackOrder("claude-opus-4.6");

        Assert.Equal(["claude-opus-4.6", "gpt-5.3-codex", "gpt-4.1"], result);
    }

    [Fact]
    public void BuildModelFallbackOrder_UsesConfiguredOrder_WhenProvided()
    {
        var result = NewsletterService.BuildModelFallbackOrder("claude-opus-4.6", "gpt-4.1, gpt-5.3-codex, gpt-4.1");

        Assert.Equal(["claude-opus-4.6", "gpt-4.1", "gpt-5.3-codex"], result);
    }

    [Fact]
    public void IsModelFallbackEligible_ReturnsTrue_ForKnownTransientFailures()
    {
        Assert.True(NewsletterService.IsModelFallbackEligible(new TimeoutException("timed out")));
        Assert.True(NewsletterService.IsModelFallbackEligible(new HttpRequestException("503")));
        Assert.True(NewsletterService.IsModelFallbackEligible(new InvalidOperationException("rate limit exceeded")));
        Assert.True(NewsletterService.IsModelFallbackEligible(new InvalidOperationException("model unavailable")));
    }

    [Fact]
    public void IsModelFallbackEligible_ReturnsFalse_ForNonFallbackFailures()
    {
        Assert.False(NewsletterService.IsModelFallbackEligible(new InvalidOperationException("prompt validation failed")));
        Assert.False(NewsletterService.IsModelFallbackEligible(new ArgumentException("bad argument")));
    }

    [Theory]
    [InlineData("model unavailable", true)]
    [InlineData("rate limit exceeded", true)]
    [InlineData("prompt validation failed", false)]
    public void IsModelFallbackEligible_HandlesSdkRpcErrors(string message, bool expected)
    {
        Assert.Equal(expected, NewsletterService.IsModelFallbackEligible(
            new RemoteRpcException(message, -32000, null, null)));
    }
}
