using NewsletterGenerator.Services;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net;
using System.Net.Http;
using System.Text;

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
    [InlineData("rust", "Rust")]
    public void FormatLangLabel_MapsCorrectly(string input, string expected)
    {
        Assert.Equal(expected, AtomFeedService.FormatLangLabel(input));
    }

    [Fact]
    public async Task FetchFeedWithMetricsAsync_UsesAtomContentWhenSummaryMissing()
    {
        const string atomFeed = """
            <?xml version="1.0" encoding="utf-8"?>
            <feed xmlns="http://www.w3.org/2005/Atom">
              <title>Visual Studio Code</title>
              <updated>2026-03-04T17:00:00.000Z</updated>
              <entry>
                <title>February 2026 (version 1.110)</title>
                <link href="https://code.visualstudio.com/updates/v1_110" />
                <updated>2026-03-04T17:00:00.000Z</updated>
                <id>https://code.visualstudio.com/updates/v1_110</id>
                <content type="html">&lt;p&gt;What's new in the Visual Studio Code February 2026 Release (1.110).&lt;/p&gt;</content>
              </entry>
            </feed>
            """;

        using var httpClient = new HttpClient(new StubHttpMessageHandler(atomFeed));
        var service = new AtomFeedService(NullLogger<AtomFeedService>.Instance, httpClient);

        var result = await service.FetchFeedWithMetricsAsync(
            "https://code.visualstudio.com/feed.xml",
            new DateOnly(2026, 2, 26),
            new DateOnly(2026, 3, 4),
            preferShortSummary: true,
            maxContentChars: 1000,
            cancellationToken: TestContext.Current.CancellationToken);

        var entry = Assert.Single(result.Entries);
        Assert.Equal(new DateOnly(2026, 3, 4), entry.PublishedAt);
        Assert.Contains("Visual Studio Code", entry.PlainText);
        Assert.Equal("https://code.visualstudio.com/updates/v1_110", entry.Url);
    }

    [Fact]
    public async Task FetchFeedWithMetricsAsync_IgnoresCommentsInsideAtomTitle()
    {
        const string atomFeed = """
            <?xml version="1.0" encoding="utf-8"?>
            <feed xmlns="http://www.w3.org/2005/Atom">
              <title>Visual Studio Code 1.113 <!-- %IF INSIDERS % (Insiders) %ENDIF % --></title>
              <updated>2026-03-19T17:00:00.000Z</updated>
              <entry>
                <title>March 19, 2026</title>
                <link href="https://code.visualstudio.com/updates/v1_113" />
                <updated>2026-03-19T17:00:00.000Z</updated>
                <id>https://code.visualstudio.com/updates/v1_113</id>
                <summary type="html">&lt;p&gt;Insiders release notes.&lt;/p&gt;</summary>
              </entry>
            </feed>
            """;

        using var httpClient = new HttpClient(new StubHttpMessageHandler(atomFeed));
        var service = new AtomFeedService(NullLogger<AtomFeedService>.Instance, httpClient);

        var result = await service.FetchFeedWithMetricsAsync(
            "https://code.visualstudio.com/feed.xml",
            new DateOnly(2026, 3, 18),
            new DateOnly(2026, 3, 20),
            preferShortSummary: true,
            maxContentChars: 1000,
            cancellationToken: TestContext.Current.CancellationToken);

        var entry = Assert.Single(result.Entries);
        Assert.Equal("March 19, 2026", entry.Version);
        Assert.Contains("Insiders release notes.", entry.PlainText);
        Assert.Equal("https://code.visualstudio.com/updates/v1_113", entry.Url);
    }

    [Fact]
    public async Task FetchFeedWithMetricsAsync_UsesGitHubReleaseApiForOlderReleases()
    {
        const string atomFeed = """
            <?xml version="1.0" encoding="utf-8"?>
            <feed xmlns="http://www.w3.org/2005/Atom">
              <title>Release notes from copilot-sdk</title>
              <updated>2026-10-08T22:27:19Z</updated>
              <entry>
                <id>tag:github.com,2008:Repository/1133883850/v1.0.18</id>
                <updated>2026-10-08T20:46:53Z</updated>
                <link rel="alternate" type="text/html" href="https://github.com/github/copilot-sdk/releases/tag/v1.0.18"/>
                <title>v1.0.18</title>
                <content type="html">&lt;p&gt;Internal dependency updates only.&lt;/p&gt;</content>
              </entry>
            </feed>
            """;

        const string githubApiResponse = """
            [
              {
                "html_url": "https://github.com/github/copilot-sdk/releases/tag/v1.0.18",
                "tag_name": "v1.0.18",
                "published_at": "2026-10-08T20:46:53Z",
                "body": "Internal dependency updates only."
              },
              {
                "html_url": "https://github.com/github/copilot-sdk/releases/tag/v1.0.17",
                "tag_name": "v1.0.17",
                "published_at": "2026-10-06T15:00:00Z",
                "body": "### Feature\nAdds binary session filesystem providers"
              }
            ]
            """;

        using var httpClient = new HttpClient(new GitHubReleaseStubHttpMessageHandler(atomFeed, githubApiResponse));
        var service = new AtomFeedService(NullLogger<AtomFeedService>.Instance, httpClient);

        var result = await service.FetchFeedWithMetricsAsync(
            "https://github.com/github/copilot-sdk/releases.atom",
            new DateOnly(2026, 10, 5),
            new DateOnly(2026, 10, 8),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains(result.Entries, entry => entry.Version == "v1.0.17");
        Assert.Contains(result.Entries, entry => entry.Version == "v1.0.18");
    }

    [Fact]
    public async Task FetchFeedWithMetricsAsync_IncludesRecentlyPublishedReleaseOnLaterPage()
    {
        const string atomFeed = """
            <?xml version="1.0" encoding="utf-8"?>
            <feed xmlns="http://www.w3.org/2005/Atom">
              <title>Release notes from copilot-sdk</title>
              <updated>2026-10-08T22:27:19Z</updated>
            </feed>
            """;

        // Older publications on page 1 must not hide a newly published draft on page 2.
        var releases = new List<string>();
        for (var i = 0; i < 100; i++)
        {
            var publishedAt = new DateTimeOffset(2026, 9, 30, 12, 0, 0, TimeSpan.Zero).AddDays(-i);
            releases.Add($$"""
                {
                  "html_url": "https://github.com/github/copilot-sdk/releases/tag/v1.0.{{100 - i}}",
                  "tag_name": "v1.0.{{100 - i}}",
                  "published_at": "{{publishedAt:yyyy-MM-ddTHH:mm:ssZ}}",
                  "body": "Release body"
                }
                """);
        }
        var githubApiPage1 = $"[{string.Join(",", releases)}]";

        const string githubApiPage2 = """
            [{
              "html_url": "https://github.com/github/copilot-sdk/releases/tag/v1.0.101",
              "tag_name": "v1.0.101",
              "created_at": "2026-01-01T12:00:00Z",
              "published_at": "2026-10-07T12:00:00Z",
              "body": "Newly published draft"
            }]
            """;
        using var handler = new PaginationTrackingHttpMessageHandler(atomFeed, githubApiPage1, githubApiPage2);
        using var httpClient = new HttpClient(handler);
        var service = new AtomFeedService(NullLogger<AtomFeedService>.Instance, httpClient);

        var result = await service.FetchFeedWithMetricsAsync(
            "https://github.com/github/copilot-sdk/releases.atom",
            new DateOnly(2026, 10, 1),
            new DateOnly(2026, 10, 8),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, handler.GitHubApiRequestCount);
        Assert.Equal("v1.0.101", Assert.Single(result.Entries).Version);
        Assert.Equal(101, result.TotalItems);
        Assert.Equal(100, result.SkippedDateItems);
    }

    [Fact]
    public async Task FetchFeedWithMetricsAsync_UsesApiWithoutDownloadingUnavailableAtomFeed()
    {
        const string apiResponse = """
            [{
              "html_url": "https://github.com/github/copilot-sdk/releases/tag/v1.0.17",
              "tag_name": "v1.0.17",
              "published_at": "2026-10-07T12:00:00Z",
              "body": "New feature"
            }]
            """;
        using var handler = new GitHubReleaseStubHttpMessageHandler("", apiResponse,
            atomStatus: HttpStatusCode.ServiceUnavailable);
        using var httpClient = new HttpClient(handler);
        var service = new AtomFeedService(NullLogger<AtomFeedService>.Instance, httpClient);

        var result = await service.FetchFeedWithMetricsAsync(
            "https://github.com/github/copilot-sdk/releases.atom",
            new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 8),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("v1.0.17", Assert.Single(result.Entries).Version);
        Assert.Equal(0, handler.AtomRequestCount);
    }

    [Fact]
    public async Task FetchFeedWithMetricsAsync_FallsBackToAtomWhenApiUnavailable()
    {
        const string atomFeed = """
            <feed xmlns="http://www.w3.org/2005/Atom">
              <title>SDK releases</title>
              <entry>
                <id>v1.0.17</id>
                <title>v1.0.17</title>
                <updated>2026-10-07T12:00:00Z</updated>
                <content type="html">&lt;p&gt;New feature&lt;/p&gt;</content>
              </entry>
            </feed>
            """;
        using var handler = new GitHubReleaseStubHttpMessageHandler(atomFeed, "",
            apiStatus: HttpStatusCode.ServiceUnavailable);
        using var httpClient = new HttpClient(handler);
        var service = new AtomFeedService(NullLogger<AtomFeedService>.Instance, httpClient);

        var result = await service.FetchFeedWithMetricsAsync(
            "https://github.com/github/copilot-sdk/releases.atom",
            new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 8),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("v1.0.17", Assert.Single(result.Entries).Version);
        Assert.Equal(1, handler.AtomRequestCount);
    }

    [Theory]
    [InlineData("2026-10-08T00:30:00Z")]
    [InlineData("2026-10-08T23:30:00Z")]
    public async Task FetchFeedWithMetricsAsync_UsesLocalPublicationDate(string timestamp)
    {
        var publishedAt = DateTimeOffset.Parse(timestamp, System.Globalization.CultureInfo.InvariantCulture);
        var expectedDate = DateOnly.FromDateTime(publishedAt.LocalDateTime);
        var apiResponse = $$"""
            [{
              "html_url": "https://github.com/github/copilot-sdk/releases/tag/v1.0.17",
              "tag_name": "v1.0.17",
              "published_at": "{{timestamp}}",
              "body": "New feature"
            }]
            """;
        using var httpClient = new HttpClient(new GitHubReleaseStubHttpMessageHandler("", apiResponse));
        var service = new AtomFeedService(NullLogger<AtomFeedService>.Instance, httpClient);

        var result = await service.FetchFeedWithMetricsAsync(
            "https://github.com/github/copilot-sdk/releases.atom", expectedDate, expectedDate,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(expectedDate, Assert.Single(result.Entries).PublishedAt);
    }

    private sealed class StubHttpMessageHandler(string content) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(content, Encoding.UTF8, "application/atom+xml")
            };

            return Task.FromResult(response);
        }
    }

    private sealed class GitHubReleaseStubHttpMessageHandler(
        string atomFeed, string githubApiResponse,
        HttpStatusCode apiStatus = HttpStatusCode.OK,
        HttpStatusCode atomStatus = HttpStatusCode.OK) : HttpMessageHandler
    {
        public int AtomRequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var content = request.RequestUri != null && request.RequestUri.Host == "api.github.com"
                ? githubApiResponse
                : atomFeed;

            var mediaType = request.RequestUri != null && request.RequestUri.Host == "api.github.com"
                ? "application/json"
                : "application/atom+xml";

            var isApi = request.RequestUri?.Host == "api.github.com";
            if (!isApi)
                AtomRequestCount++;

            var response = new HttpResponseMessage(isApi ? apiStatus : atomStatus)
            {
                Content = new StringContent(content, Encoding.UTF8, mediaType)
            };

            return Task.FromResult(response);
        }
    }

    private sealed class PaginationTrackingHttpMessageHandler(
        string atomFeed, string githubApiPage1, string githubApiPage2) : HttpMessageHandler
    {
        public int GitHubApiRequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var isGitHubApi = request.RequestUri != null && request.RequestUri.Host == "api.github.com";

            if (isGitHubApi)
            {
                GitHubApiRequestCount++;
                var content = request.RequestUri!.Query switch
                {
                    "?per_page=100&page=1" => githubApiPage1,
                    "?per_page=100&page=2" => githubApiPage2,
                    _ => "[]"
                };

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(content, Encoding.UTF8, "application/json")
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(atomFeed, Encoding.UTF8, "application/atom+xml")
            });
        }
    }
}
