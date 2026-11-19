using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace NewsletterGenerator.Services;

public class CacheService(ILogger<CacheService> logger, string? cacheDirectory = null, bool forceRefresh = false)
{
    private static readonly JsonSerializerOptions IndentedJsonOptions = new() { WriteIndented = true };

    private readonly string _cacheDir = cacheDirectory ?? Path.Combine(Directory.GetCurrentDirectory(), ".cache");
    private readonly bool _forceRefresh = forceRefresh;
    private readonly ConcurrentDictionary<string, CacheSectionMetric> _sectionMetrics = new(StringComparer.OrdinalIgnoreCase);

    private int _cacheHits;
    private int _cacheMisses;
    private int _cacheSkips;

    public int CacheHits => Volatile.Read(ref _cacheHits);
    public int CacheMisses => Volatile.Read(ref _cacheMisses);
    public int CacheSkips => Volatile.Read(ref _cacheSkips);

    public IReadOnlyList<CacheSectionMetric> GetSectionMetrics() =>
        _sectionMetrics.Values
            .OrderBy(metric => metric.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();

    // Ensure directory exists on first use
    private void EnsureCacheDirectory() => Directory.CreateDirectory(_cacheDir);

    /// <summary>
    /// Gets a hash of the content for cache key comparison
    /// </summary>
    public static string GetContentHash(string content)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexString(bytes);
    }

    /// <summary>
    /// Tries to get cached content if the source hash matches
    /// </summary>
    public async Task<string?> TryGetCachedAsync(string cacheKey, string sourceHash)
    {
        if (_forceRefresh)
        {
            Interlocked.Increment(ref _cacheSkips);
            RecordReadOutcome(cacheKey, "skip");
            ServiceLogMessages.CacheSkipForceRefresh(logger, cacheKey);
            return null;
        }

        EnsureCacheDirectory();
        var cacheFile = Path.Combine(_cacheDir, $"{cacheKey}.json");

        if (!File.Exists(cacheFile))
        {
            Interlocked.Increment(ref _cacheMisses);
            RecordReadOutcome(cacheKey, "miss");
            ServiceLogMessages.CacheMissNoFile(logger, cacheKey);
            return null;
        }

        try
        {
            var json = await File.ReadAllTextAsync(cacheFile);
            var cached = JsonSerializer.Deserialize<CachedItem>(json);

            if (cached?.SourceHash == sourceHash)
            {
                Interlocked.Increment(ref _cacheHits);
                RecordReadOutcome(cacheKey, "hit", cached.Content.Length);
                ServiceLogMessages.CacheHit(logger, cacheKey, sourceHash[..12], cached.Content.Length);
                return cached.Content;
            }

            Interlocked.Increment(ref _cacheMisses);
            RecordReadOutcome(cacheKey, "mismatch");
            ServiceLogMessages.CacheMissHashMismatch(logger, cacheKey, sourceHash[..12], cached?.SourceHash?[..12]);
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _cacheMisses);
            RecordReadOutcome(cacheKey, "error");
            ServiceLogMessages.CacheReadFailed(logger, ex, cacheKey);
        }

        return null;
    }

    /// <summary>
    /// Saves content to cache with its source hash
    /// </summary>
    public async Task SaveCacheAsync(string cacheKey, string content, string sourceHash)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            RecordSaveOutcome(cacheKey, "empty");
            ServiceLogMessages.CacheSaveSkippedEmpty(logger, cacheKey);
            return;
        }

        ServiceLogMessages.CacheSaving(logger, cacheKey, sourceHash[..12], content.Length);

        EnsureCacheDirectory();
        var cacheFile = Path.Combine(_cacheDir, $"{cacheKey}.json");

        var cached = new CachedItem
        {
            Content = content,
            SourceHash = sourceHash,
            Timestamp = DateTimeOffset.UtcNow
        };

        var json = JsonSerializer.Serialize(cached, IndentedJsonOptions);

        await File.WriteAllTextAsync(cacheFile, json);
        RecordSaveOutcome(cacheKey, "saved", content.Length);
    }

    private void RecordReadOutcome(string cacheKey, string readOutcome, int? contentLength = null)
    {
        _sectionMetrics.AddOrUpdate(
            cacheKey,
            key => new CacheSectionMetric(key, readOutcome, null, contentLength),
            (_, existing) => existing with
            {
                ReadOutcome = readOutcome,
                ContentLength = contentLength ?? existing.ContentLength
            });
    }

    private void RecordSaveOutcome(string cacheKey, string saveOutcome, int? contentLength = null)
    {
        _sectionMetrics.AddOrUpdate(
            cacheKey,
            key => new CacheSectionMetric(key, null, saveOutcome, contentLength),
            (_, existing) => existing with
            {
                SaveOutcome = saveOutcome,
                ContentLength = contentLength ?? existing.ContentLength
            });
    }

    private record CachedItem
    {
        public string Content { get; init; } = "";
        public string SourceHash { get; init; } = "";
        public DateTimeOffset Timestamp { get; init; }
    }

    /// <summary>
    /// Tries to get cached raw feed content if it was fetched within the TTL window.
    /// </summary>
    public async Task<string?> TryGetFeedCacheAsync(string feedUrl, TimeSpan maxAge)
    {
        var (entry, isFresh) = await ReadFeedCacheAsync(feedUrl, maxAge);
        return isFresh ? entry?.Content : null;
    }

    // A null max age requires revalidation on every use.
    internal async Task<(FeedCacheEntry? Entry, bool IsFresh)> ReadFeedCacheAsync(string feedUrl, TimeSpan? maxAge)
    {
        var cacheKey = FeedCacheKey(feedUrl);
        if (_forceRefresh)
        {
            Interlocked.Increment(ref _cacheSkips);
            RecordReadOutcome(cacheKey, "skip");
            ServiceLogMessages.CacheSkipForceRefresh(logger, cacheKey);
            return (null, false);
        }

        var cacheFile = Path.Combine(_cacheDir, $"{cacheKey}.json");

        if (!File.Exists(cacheFile))
        {
            Interlocked.Increment(ref _cacheMisses);
            RecordReadOutcome(cacheKey, "miss");
            ServiceLogMessages.CacheMissNoFile(logger, cacheKey);
            return (null, false);
        }

        try
        {
            var json = await File.ReadAllTextAsync(cacheFile);
            var cached = JsonSerializer.Deserialize<FeedCacheEntry>(json);

            if (string.IsNullOrWhiteSpace(cached?.Content))
            {
                Interlocked.Increment(ref _cacheMisses);
                RecordReadOutcome(cacheKey, "miss");
                return (null, false);
            }

            var age = DateTimeOffset.UtcNow - cached.Timestamp;
            if (maxAge is null || age > maxAge.Value)
            {
                Interlocked.Increment(ref _cacheMisses);
                RecordReadOutcome(cacheKey, maxAge is null ? "revalidate" : "expired");
                return (cached, false);
            }

            Interlocked.Increment(ref _cacheHits);
            RecordReadOutcome(cacheKey, "hit", cached.Content.Length);
            ServiceLogMessages.CacheHit(logger, cacheKey, $"age={age.TotalMinutes:F0}m", cached.Content.Length);
            return (cached, true);
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _cacheMisses);
            RecordReadOutcome(cacheKey, "error");
            ServiceLogMessages.CacheReadFailed(logger, ex, cacheKey);
        }

        return (null, false);
    }

    /// <summary>
    /// Saves raw feed content to cache with a timestamp for TTL-based expiry.
    /// </summary>
    public Task SaveFeedCacheAsync(string feedUrl, string content) =>
        SaveFeedCacheAsync(feedUrl, content, null, null);

    internal async Task SaveFeedCacheAsync(string feedUrl, string content, string? etag, DateTimeOffset? lastModified)
    {
        var cacheKey = FeedCacheKey(feedUrl);

        if (string.IsNullOrWhiteSpace(content))
        {
            RecordSaveOutcome(cacheKey, "empty");
            ServiceLogMessages.CacheSaveSkippedEmpty(logger, cacheKey);
            return;
        }

        logger.LogDebug("Saving feed cache: {CacheKey} ({Length} chars)", cacheKey, content.Length);

        var cached = new FeedCacheEntry(content, DateTimeOffset.UtcNow, etag, lastModified);
        var json = JsonSerializer.Serialize(cached, IndentedJsonOptions);

        try
        {
            EnsureCacheDirectory();
            var cacheFile = Path.Combine(_cacheDir, $"{cacheKey}.json");
            await File.WriteAllTextAsync(cacheFile, json);
            RecordSaveOutcome(cacheKey, "saved", content.Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            RecordSaveOutcome(cacheKey, "error");
            logger.LogWarning(ex, "Failed to save feed cache for {CacheKey}", cacheKey);
        }
    }

    private static string FeedCacheKey(string feedUrl)
    {
        // Produce a stable, filesystem-safe key from the URL
        var hash = GetContentHash(feedUrl);
        var host = new Uri(feedUrl).Host.Replace(".", "-");
        var path = new Uri(feedUrl).AbsolutePath.Trim('/').Replace('/', '-');
        if (path.Length > 40) path = path[..40];
        return $"feed-{host}-{path}-{hash[..12]}";
    }
}

internal sealed record FeedCacheEntry(string Content, DateTimeOffset Timestamp, string? ETag, DateTimeOffset? LastModified);

public sealed record CacheSectionMetric(
    string Key,
    string? ReadOutcome,
    string? SaveOutcome,
    int? ContentLength);
