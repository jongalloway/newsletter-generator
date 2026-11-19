using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using NewsletterGenerator.Services;

namespace NewsletterGenerator.Tests;

public class FeedRevalidationTests : IDisposable
{
    private const string FeedUrl = "https://example.com/feed.xml";
    private const string GitHubFeedUrl = "https://github.com/example/project/releases.atom";
    private static readonly DateTimeOffset LastModified = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Start = new(2026, 10, 1);
    private static readonly DateOnly End = new(2026, 10, 8);

    private readonly string _cacheDirectory = Path.Combine(Path.GetTempPath(), $"newsletter-test-{Guid.NewGuid():N}");
    private readonly QueuedHandler _handler = new();
    private readonly HttpClient _client;

    public FeedRevalidationTests() => _client = new HttpClient(_handler);

    [Fact]
    public async Task Atom_ReusesFreshBodyAndRenewsItOn304()
    {
        var service = CreateService();
        _handler.Expect(FeedUrl, Response(body: Atom("old"), etag: "W/\"first\"", lastModified: LastModified));
        Assert.Equal("old", Assert.Single((await FetchAsync(service, FeedUrl)).Entries).Version);

        var filtered = await service.FetchFeedWithMetricsAsync(FeedUrl, Start, End, categoryKeywords: ["other"],
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Empty(filtered.Entries);
        Assert.Equal(1, filtered.SkippedCategoryItems);

        await ExpireAtomCacheAsync();
        _handler.Expect(FeedUrl, Response(HttpStatusCode.NotModified, etag: "\"renewed\""), etag: "W/\"first\"");
        Assert.Equal("old", Assert.Single((await FetchAsync(service, FeedUrl)).Entries).Version);
        Assert.Equal("old", Assert.Single((await FetchAsync(service, FeedUrl)).Entries).Version);

        await ExpireAtomCacheAsync();
        _handler.Expect(FeedUrl, Response(body: Atom("new"), etag: "\"second\""), etag: "\"renewed\"");
        Assert.Equal("new", Assert.Single((await FetchAsync(service, FeedUrl)).Entries).Version);
        await ExpireAtomCacheAsync();
        _handler.Expect(FeedUrl, Response(HttpStatusCode.NotModified), etag: "\"second\"");
        Assert.Equal("new", Assert.Single((await FetchAsync(service, FeedUrl)).Entries).Version);
        Assert.Empty(_handler.Pending);
    }

    [Fact]
    public async Task Atom_UsesLastModifiedWhenNoETagAndClearsValidatorsOnFullResponse()
    {
        var service = CreateService();
        _handler.Expect(FeedUrl, Response(body: Atom("old"), lastModified: LastModified));
        await FetchAsync(service, FeedUrl);
        await ExpireAtomCacheAsync();
        _handler.Expect(FeedUrl, Response(HttpStatusCode.NotModified), lastModified: LastModified);
        Assert.Equal("old", Assert.Single((await FetchAsync(service, FeedUrl)).Entries).Version);
        await ExpireAtomCacheAsync();

        _handler.Expect(FeedUrl, Response(body: Atom("new")), lastModified: LastModified);
        Assert.Equal("new", Assert.Single((await FetchAsync(service, FeedUrl)).Entries).Version);
        await ExpireAtomCacheAsync();
        _handler.Expect(FeedUrl, Response(body: Atom("newer")));
        Assert.Equal("newer", Assert.Single((await FetchAsync(service, FeedUrl)).Entries).Version);
        Assert.Empty(_handler.Pending);
    }

    [Fact]
    public async Task Atom_ReadsLegacyCacheAndDoesNotServeExpiredBodyOnFailure()
    {
        Directory.CreateDirectory(_cacheDirectory);
        var hash = CacheService.GetContentHash(FeedUrl)[..12];
        await File.WriteAllTextAsync(Path.Combine(_cacheDirectory, $"feed-example-com-feed.xml-{hash}.json"),
            JsonSerializer.Serialize(new { Content = Atom("legacy"), SourceHash = FeedUrl, Timestamp = DateTimeOffset.UtcNow }),
            TestContext.Current.CancellationToken);
        var service = CreateService();
        Assert.Equal("legacy", Assert.Single((await FetchAsync(service, FeedUrl)).Entries).Version);

        await ExpireAtomCacheAsync();
        _handler.Expect(FeedUrl, Response(HttpStatusCode.ServiceUnavailable));
        Assert.Empty((await FetchAsync(service, FeedUrl)).Entries);
        _handler.Expect(FeedUrl, Response(HttpStatusCode.NotModified));
        Assert.Empty((await FetchAsync(service, FeedUrl)).Entries);
        _handler.Expect(FeedUrl, Response(body: Atom("new")));
        Assert.Equal("new", Assert.Single((await FetchAsync(service, FeedUrl)).Entries).Version);
        Assert.Empty(_handler.Pending);
    }

    [Fact]
    public async Task Atom_DoesNotRenewAnEntryChangedSinceTheValidatorWasRead()
    {
        var cache = new CacheService(NullLogger<CacheService>.Instance, _cacheDirectory);
        await cache.SaveFeedCacheAsync(FeedUrl, Atom("old"), "\"old\"", null, FeedUrl);
        var (oldEntry, _) = await cache.ReadFeedCacheAsync(FeedUrl, TimeSpan.Zero);
        Assert.NotNull(oldEntry);

        await cache.SaveFeedCacheAsync(FeedUrl, Atom("new"), "\"new\"", null, FeedUrl);
        var current = await cache.RenewFeedCacheAsync(
            FeedUrl, oldEntry, "\"old-renewed\"", null, FeedUrl);

        Assert.NotNull(current);
        Assert.Equal(Atom("new"), current.Content);
        Assert.Equal("\"new\"", current.ETag);
    }

    [Fact]
    public async Task Atom_RetriesWithoutValidatorsWhen304RedirectTargetChanges()
    {
        var service = CreateService();
        const string oldTarget = "https://cdn.example.com/old-feed.xml";
        const string newTarget = "https://cdn.example.com/new-feed.xml";
        _handler.Expect(FeedUrl, Response(body: Atom("old"), etag: "\"old\""), effectiveUrl: oldTarget);
        await FetchAsync(service, FeedUrl);
        await ExpireAtomCacheAsync();

        _handler.Expect(FeedUrl, Response(HttpStatusCode.NotModified), etag: "\"old\"", effectiveUrl: newTarget);
        _handler.Expect(FeedUrl, Response(body: Atom("new"), etag: "\"new\""), effectiveUrl: newTarget);
        Assert.Equal("new", Assert.Single((await FetchAsync(service, FeedUrl)).Entries).Version);
        Assert.Empty(_handler.Pending);
    }

    [Fact]
    public async Task Atom_ForceRefreshBypassesFreshAndExpiredCacheReads()
    {
        var forced = CreateService(forceRefresh: true);
        _handler.Expect(FeedUrl, Response(body: Atom("old"), etag: "\"old\""));
        await FetchAsync(CreateService(), FeedUrl);

        _handler.Expect(FeedUrl, Response(body: Atom("fresh"), etag: "\"fresh\""));
        Assert.Equal("fresh", Assert.Single((await FetchAsync(forced, FeedUrl)).Entries).Version);

        await ExpireAtomCacheAsync();
        _handler.Expect(FeedUrl, Response(body: Atom("expired")));
        Assert.Equal("expired", Assert.Single((await FetchAsync(forced, FeedUrl)).Entries).Version);
        Assert.Equal("expired", Assert.Single((await FetchAsync(CreateService(), FeedUrl)).Entries).Version);
        Assert.Empty(_handler.Pending);
    }

    [Fact]
    public async Task GitHub_RejectsErrorStatusBeforeReadingBodyAndFallsBackToAtom()
    {
        var service = CreateService();
        ExpectPage(1, new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        {
            Content = new ThrowingContent()
        });
        _handler.Expect(GitHubFeedUrl, Response(body: Atom("fallback")));

        Assert.Equal("fallback", Assert.Single((await FetchAsync(service, GitHubFeedUrl)).Entries).Version);
        Assert.Empty(_handler.Pending);
    }

    [Fact]
    public async Task Atom_TimesOutWhileReadingSuccessfulResponseBody()
    {
        _client.Timeout = TimeSpan.FromMilliseconds(100);
        var service = CreateService();
        _handler.Expect(FeedUrl, Response(content: new BlockingContent()));

        var stopwatch = Stopwatch.StartNew();
        Assert.Empty((await FetchAsync(service, FeedUrl)).Entries);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2));
        Assert.Empty(_handler.Pending);
    }

    [Fact]
    public async Task GitHub_UsesSuccessfulResponseWhenCacheCannotBeWritten()
    {
        Directory.CreateDirectory(_cacheDirectory);
        var blockedCachePath = Path.Combine(_cacheDirectory, "not-a-directory");
        await File.WriteAllTextAsync(blockedCachePath, "", TestContext.Current.CancellationToken);
        var cache = new CacheService(NullLogger<CacheService>.Instance, blockedCachePath);
        var service = new AtomFeedService(NullLogger<AtomFeedService>.Instance, _client, cache);
        ExpectPage(1, Response(body: Release("new")));

        Assert.Equal("new", Assert.Single((await FetchAsync(service, GitHubFeedUrl)).Entries).Version);
        Assert.Equal("error", Assert.Single(cache.GetSectionMetrics()).SaveOutcome);
        Assert.Empty(_handler.Pending);
    }

    [Fact]
    public async Task GitHub_RevalidatesLaterPagesAndFollowsPageGrowthAndShrinkage()
    {
        var service = CreateService();
        var fullPage = $"[{string.Join(",", Enumerable.Repeat("{}", 100))}]";
        ExpectPage(1, Response(body: fullPage, etag: "\"one\""));
        ExpectPage(2, Response(body: "[]", etag: "\"empty\""));
        Assert.Empty((await FetchAsync(service, GitHubFeedUrl)).Entries);

        ExpectPage(1, Response(HttpStatusCode.NotModified), etag: "\"one\"");
        ExpectPage(2, Response(body: fullPage, lastModified: LastModified), etag: "\"empty\"");
        ExpectPage(3, Response(body: Release("new"), etag: "\"three\""));
        Assert.Equal("new", Assert.Single((await FetchAsync(service, GitHubFeedUrl)).Entries).Version);

        ExpectPage(1, Response(HttpStatusCode.NotModified), etag: "\"one\"");
        ExpectPage(2, Response(HttpStatusCode.NotModified), lastModified: LastModified);
        ExpectPage(3, Response(body: "[]"), etag: "\"three\"");
        Assert.Empty((await FetchAsync(service, GitHubFeedUrl)).Entries);

        ExpectPage(1, Response(HttpStatusCode.NotModified), etag: "\"one\"");
        ExpectPage(2, Response(body: Release("short"), etag: "\"short\""), lastModified: LastModified);
        Assert.Equal("short", Assert.Single((await FetchAsync(service, GitHubFeedUrl)).Entries).Version);

        ExpectPage(1, Response(body: fullPage, etag: "\"forced\""));
        ExpectPage(2, Response(body: Release("forced"), etag: "\"forced-two\""));
        Assert.Equal("forced", Assert.Single((await FetchAsync(CreateService(forceRefresh: true), GitHubFeedUrl)).Entries).Version);

        ExpectPage(1, Response(HttpStatusCode.NotModified), etag: "\"forced\"");
        ExpectPage(2, Response(HttpStatusCode.ServiceUnavailable), etag: "\"forced-two\"");
        _handler.Expect(GitHubFeedUrl, Response(body: Atom("fallback")));
        Assert.Equal("fallback", Assert.Single((await FetchAsync(service, GitHubFeedUrl)).Entries).Version);
        Assert.Empty(_handler.Pending);
    }

    private AtomFeedService CreateService(bool forceRefresh = false) => new(
        NullLogger<AtomFeedService>.Instance, _client,
        new CacheService(NullLogger<CacheService>.Instance, _cacheDirectory, forceRefresh));

    private static Task<FeedFetchResult> FetchAsync(AtomFeedService service, string url) =>
        service.FetchFeedWithMetricsAsync(url, Start, End, cancellationToken: TestContext.Current.CancellationToken);

    private void ExpectPage(int page, HttpResponseMessage response, string? etag = null, DateTimeOffset? lastModified = null) =>
        _handler.Expect($"https://api.github.com/repos/example/project/releases?per_page=100&page={page}", response, etag, lastModified);

    private async Task ExpireAtomCacheAsync()
    {
        var path = Assert.Single(Directory.GetFiles(_cacheDirectory, "*.json"));
        var entry = JsonNode.Parse(await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken))!;
        entry["Timestamp"] = DateTimeOffset.UtcNow.AddHours(-2);
        await File.WriteAllTextAsync(path, entry.ToJsonString(), TestContext.Current.CancellationToken);
    }

    private static string Atom(string title) => $"""
        <feed xmlns="http://www.w3.org/2005/Atom">
          <title>Example</title>
          <entry>
            <id>{title}</id><title>{title}</title><updated>2026-10-07T12:00:00Z</updated>
            <category term="news"/><content>Release notes</content>
          </entry>
        </feed>
        """;

    private static string Release(string tag) => $$"""
        [{"tag_name":"{{tag}}","html_url":"https://example.com/{{tag}}","published_at":"2026-10-07T12:00:00Z","body":"Release notes"}]
        """;

    private static HttpResponseMessage Response(HttpStatusCode status = HttpStatusCode.OK, string? body = null,
        string? etag = null, DateTimeOffset? lastModified = null, HttpContent? content = null)
    {
        var response = new HttpResponseMessage(status);
        if (body is not null)
            response.Content = new StringContent(body);
        else if (content is not null)
            response.Content = content;
        if (etag is not null)
            response.Headers.ETag = EntityTagHeaderValue.Parse(etag);
        if (lastModified is not null)
            response.Content.Headers.LastModified = lastModified;
        return response;
    }

    private sealed class BlockingContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context) =>
            Task.Delay(Timeout.InfiniteTimeSpan);

        protected override Task SerializeToStreamAsync(
            Stream stream, System.Net.TransportContext? context, CancellationToken cancellationToken) =>
            Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    private sealed class ThrowingContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context) =>
            throw new InvalidOperationException("The error response body should not be read.");

        protected override Task SerializeToStreamAsync(
            Stream stream, System.Net.TransportContext? context, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The error response body should not be read.");

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    public void Dispose()
    {
        _client.Dispose();
        if (Directory.Exists(_cacheDirectory))
            Directory.Delete(_cacheDirectory, recursive: true);
    }

    private sealed class QueuedHandler : HttpMessageHandler
    {
        public Queue<(string Url, HttpResponseMessage Response, string? ETag, DateTimeOffset? LastModified, string? EffectiveUrl)> Pending { get; } = new();

        public void Expect(string url, HttpResponseMessage response, string? etag = null,
            DateTimeOffset? lastModified = null, string? effectiveUrl = null) =>
            Pending.Enqueue((url, response, etag, lastModified, effectiveUrl));

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.True(Pending.TryDequeue(out var expected), $"Unexpected request to {request.RequestUri}");
            Assert.Equal(expected.Url, request.RequestUri?.AbsoluteUri);
            Assert.Equal(expected.ETag, request.Headers.IfNoneMatch.SingleOrDefault()?.ToString());
            Assert.Equal(expected.LastModified, request.Headers.IfModifiedSince);
            if (expected.EffectiveUrl is not null)
                request.RequestUri = new Uri(expected.EffectiveUrl);
            expected.Response.RequestMessage = request;
            return Task.FromResult(expected.Response);
        }
    }
}
