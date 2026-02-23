using NewsletterGenerator.Services;

namespace NewsletterGenerator.Tests;

public class CacheServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly CacheService _cache;

    public CacheServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"newsletter-test-{Guid.NewGuid():N}");
        var logger = new Microsoft.Extensions.Logging.Abstractions.NullLogger<CacheService>();
        _cache = new CacheService(logger, cacheDirectory: _tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    // ── GetContentHash ────────────────────────────────────────────────────────

    [Fact]
    public void GetContentHash_DeterministicForSameInput()
    {
        var hash1 = CacheService.GetContentHash("hello world");
        var hash2 = CacheService.GetContentHash("hello world");

        Assert.Equal(hash1, hash2);
    }

    [Fact]
    public void GetContentHash_DifferentForDifferentInput()
    {
        var hash1 = CacheService.GetContentHash("hello world");
        var hash2 = CacheService.GetContentHash("hello world!");

        Assert.NotEqual(hash1, hash2);
    }

    [Fact]
    public void GetContentHash_ReturnsSha256HexString()
    {
        var hash = CacheService.GetContentHash("test");

        // SHA256 produces 64 hex characters
        Assert.Equal(64, hash.Length);
        Assert.Matches("^[0-9A-F]+$", hash);
    }

    // ── TryGetCachedAsync / SaveCacheAsync round-trip ────────────────────────

    [Fact]
    public async Task SaveAndRetrieve_RoundTrips()
    {
        var content = "Generated newsletter content";
        var hash = CacheService.GetContentHash("source data");

        await _cache.SaveCacheAsync("test-key", content, hash);
        var cached = await _cache.TryGetCachedAsync("test-key", hash);

        Assert.Equal(content, cached);
    }

    [Fact]
    public async Task TryGetCached_ReturnNull_WhenNoCacheExists()
    {
        var result = await _cache.TryGetCachedAsync("nonexistent", "somehash");

        Assert.Null(result);
    }

    [Fact]
    public async Task TryGetCached_ReturnNull_WhenHashMismatch()
    {
        var hash1 = CacheService.GetContentHash("source-v1");
        var hash2 = CacheService.GetContentHash("source-v2");

        await _cache.SaveCacheAsync("test-key", "content", hash1);
        var result = await _cache.TryGetCachedAsync("test-key", hash2);

        Assert.Null(result);
    }

    [Fact]
    public async Task SaveCache_SkipsEmptyContent()
    {
        await _cache.SaveCacheAsync("empty-key", "", "somehash");
        var result = await _cache.TryGetCachedAsync("empty-key", "somehash");

        Assert.Null(result);
    }

    [Fact]
    public async Task SaveCache_OverwritesPreviousEntry()
    {
        var hash1 = CacheService.GetContentHash("source-v1");
        var hash2 = CacheService.GetContentHash("source-v2");

        await _cache.SaveCacheAsync("key", "old content", hash1);
        await _cache.SaveCacheAsync("key", "new content", hash2);

        var result = await _cache.TryGetCachedAsync("key", hash2);
        Assert.Equal("new content", result);
    }

    [Fact]
    public async Task ForceRefresh_SkipsCache()
    {
        var logger = new Microsoft.Extensions.Logging.Abstractions.NullLogger<CacheService>();
        var forceCache = new CacheService(logger, cacheDirectory: _tempDir, forceRefresh: true);

        var hash = CacheService.GetContentHash("source");
        await forceCache.SaveCacheAsync("key", "content", hash);
        var result = await forceCache.TryGetCachedAsync("key", hash);

        Assert.Null(result);
    }
}
