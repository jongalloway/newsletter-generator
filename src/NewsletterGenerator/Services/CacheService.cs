using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace NewsletterGenerator.Services;

public class CacheService(ILogger<CacheService> logger, string? cacheDirectory = null, bool forceRefresh = false)
{
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

        var json = JsonSerializer.Serialize(cached, new JsonSerializerOptions
        {
            WriteIndented = true
        });

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
}

public sealed record CacheSectionMetric(
    string Key,
    string? ReadOutcome,
    string? SaveOutcome,
    int? ContentLength);
