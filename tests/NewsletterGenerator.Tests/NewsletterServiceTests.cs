using System.Text.Json;
using GitHub.Copilot;
using Microsoft.Extensions.Logging;
using NewsletterGenerator.Models;
using NewsletterGenerator.Services;

namespace NewsletterGenerator.Tests;

public class NewsletterServiceTests
{
    [Fact]
    public void PromptUsage_SumsCallsWithoutCarryingOverEarlierPrompts()
    {
        var firstPrompt = new NewsletterService.PromptUsage();
        firstPrompt.Add(new AssistantUsageData { Model = "model-a", InputTokens = 100, OutputTokens = 20 });
        firstPrompt.Add(new AssistantUsageData { Model = "model-b", InputTokens = 50, OutputTokens = 10 });
        Assert.Equal((150L, 30L, 180L), firstPrompt.GetTokenCounts());

        var nextPrompt = new NewsletterService.PromptUsage();
        nextPrompt.Add(new AssistantUsageData { Model = "model-b", InputTokens = 25, OutputTokens = 5 });
        Assert.Equal((25L, 5L, 30L), nextPrompt.GetTokenCounts());
    }

    [Fact]
    public void PromptUsage_DistinguishesMissingUsageFromZero()
    {
        var usage = new NewsletterService.PromptUsage();
        var missing = usage.GetTokenCounts();
        Assert.Null(missing.InputTokens);
        Assert.Null(missing.OutputTokens);
        Assert.Null(missing.TotalTokens);

        usage.Add(new AssistantUsageData { Model = "model-a", InputTokens = 0, OutputTokens = 0 });
        Assert.Equal((0L, 0L, 0L), usage.GetTokenCounts());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PromptUsage_DoesNotReportPartialCountsAsComplete(bool missingFirst)
    {
        var usage = new NewsletterService.PromptUsage();
        var complete = new AssistantUsageData { Model = "model-a", InputTokens = 100, OutputTokens = 20 };
        var incomplete = new AssistantUsageData { Model = "model-b", OutputTokens = 5 };
        usage.Add(missingFirst ? incomplete : complete);
        usage.Add(missingFirst ? complete : incomplete);

        var counts = usage.GetTokenCounts();
        Assert.Null(counts.InputTokens);
        Assert.Equal(25L, counts.OutputTokens);
        Assert.Null(counts.TotalTokens);
    }

    [Fact]
    public async Task LogSessionError_PreservesStructuredDiagnostics()
    {
        var logger = new RecordingLogger();
        await using var service = new NewsletterService(logger);
        service.LogSessionError(new SessionErrorData
        {
            Message = "Request rejected",
            ErrorType = "rate_limit",
            ErrorCode = "user_model_rate_limited",
            ProviderErrorType = "provider_limit",
            ProviderErrorCode = "capacity_exceeded",
            ProviderCallId = "request-123",
            OriginatingMessageId = "message-456"
        }, "feature bullets", "session-789");

        Assert.Equal(LogLevel.Error, logger.Level);
        Assert.Equal("rate_limit", logger.Fields["ErrorType"]);
        Assert.Equal("user_model_rate_limited", logger.Fields["ErrorCode"]);
        Assert.Equal("provider_limit", logger.Fields["ProviderErrorType"]);
        Assert.Equal("capacity_exceeded", logger.Fields["ProviderErrorCode"]);
        Assert.Equal("request-123", logger.Fields["ProviderCallId"]);
        Assert.Equal("message-456", logger.Fields["OriginatingMessageId"]);
        Assert.Equal("session-789", logger.Fields["SessionId"]);
        Assert.Equal("feature bullets", logger.Fields["Operation"]);
        Assert.Equal("Request rejected", logger.Fields["Message"]);
    }

    private sealed class RecordingLogger : ILogger<NewsletterService>
    {
        public LogLevel Level { get; private set; }
        public Dictionary<string, object?> Fields { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Level = logLevel;
            if (state is IEnumerable<KeyValuePair<string, object?>> fields)
            {
                foreach (var field in fields)
                    Fields[field.Key] = field.Value;
            }
        }
    }

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

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void IsTypedResponseParsingFailure_OnlyMatchesJsonExceptions(bool jsonFailure)
    {
        Exception exception = jsonFailure
            ? new JsonException("The structured response was invalid.")
            : new InvalidOperationException("The session reported an error.");

        Assert.Equal(jsonFailure, NewsletterService.IsTypedResponseParsingFailure(exception));
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

    [Fact]
    public void DetectMajorReleases_ExcludesModelAvailabilityAndVersionSupportAnnouncements()
    {
        var entries = new List<ReleaseEntry>
        {
            CreateRelease("Claude Opus 5.5 is now available in GitHub Copilot"),
            CreateRelease("Grok 4.7 is now available in GitHub Copilot"),
            CreateRelease("[Launched] Generally Available: Azure Functions support for PowerShell 7.6"),
            CreateRelease("[Launched] Generally Available: Azure Sphere OS version 26.09 is now available")
        };

        var releases = NewsletterService.DetectMajorReleases(entries);

        var release = Assert.Single(releases);
        Assert.Equal(
            "[Launched] Generally Available: Azure Sphere OS version 26.09 is now available",
            release.Version);
    }

    [Fact]
    public void DetectMajorReleases_IncludesDotNetReleaseCandidatesWithoutDottedVersions()
    {
        var release = CreateRelease("Announcing .NET 11 Release Candidate 1");

        var releases = NewsletterService.DetectMajorReleases([release]);

        Assert.Same(release, Assert.Single(releases));
    }

    [Fact]
    public void DetectMajorReleases_ExcludesPreOneComponentReleases()
    {
        var release = CreateRelease("Actions Runner Controller release 0.15.0");

        Assert.Empty(NewsletterService.DetectMajorReleases([release]));
    }

    [Fact]
    public void DetectMajorReleases_ExcludesUnsupportedProductsWithStableVersions()
    {
        var release = CreateRelease("Actions Runner Controller release 1.15.0");

        Assert.Empty(NewsletterService.DetectMajorReleases([release]));
    }

    [Theory]
    [InlineData("Announcing .NET 11 Release Candidate 1")]
    [InlineData("Aspire 13.6 released")]
    [InlineData("TypeScript 6.0 released")]
    [InlineData("PowerShell 7.6 released")]
    [InlineData("Azure Sphere OS version 26.09 is generally available")]
    [InlineData("Microsoft Agent Framework releasing version 1.0")]
    [InlineData("Announcing v2.0 of the official MCP C# SDK")]
    [InlineData("SkiaSharp 4.0 is here: announcing the first stable release")]
    [InlineData("Microsoft.Extensions.AI 10.0 released")]
    [InlineData("Microsoft.Extensions.VectorData 10.0 is generally available")]
    [InlineData("Windows App SDK 2.0 released")]
    [InlineData("Windows App Runtime 2.0 is now available")]
    [InlineData("WinUI 4.0 released")]
    [InlineData("WinApp CLI 2.0 released")]
    [InlineData("Semantic Kernel 2.0 released")]
    [InlineData("Microsoft Orleans 10.0 released")]
    [InlineData("NuGet 8.0 released")]
    [InlineData("Azure Developer CLI 2.0 released")]
    [InlineData("azd 2.0 released")]
    [InlineData("Azure Functions host 5.0 released")]
    [InlineData("Bicep 1.0 released")]
    [InlineData("Microsoft Foundry SDK 2.0 released")]
    [InlineData(".NET MAUI 12 Preview 1")]
    public void DetectMajorReleases_IncludesSupportedProductFamilies(string title)
    {
        var release = CreateRelease(title);

        Assert.Same(release, Assert.Single(NewsletterService.DetectMajorReleases([release])));
    }

    [Theory]
    [InlineData("Visual Studio 18.0 released")]
    [InlineData("VS Code 1.141 released")]
    [InlineData("Copilot SDK v1.0.0 released")]
    public void DetectMajorReleases_ExcludesProductsWithExistingNewsletterSections(string title)
    {
        Assert.Empty(NewsletterService.DetectMajorReleases([CreateRelease(title)]));
    }

    private static ReleaseEntry CreateRelease(string title) =>
        new(
            title,
            new DateOnly(2026, 10, 1),
            "Release details.",
            $"https://example.com/{Uri.EscapeDataString(title)}");
}
