using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace NewsletterGenerator.Services;

public class CacheService(ILogger<CacheService> logger, string? cacheDirectory = null, bool forceRefresh = false)
{
    private readonly string _cacheDir = cacheDirectory ?? Path.Combine(Directory.GetCurrentDirectory(), ".cache");
    private readonly bool _forceRefresh = forceRefresh;

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
            ServiceLogMessages.CacheSkipForceRefresh(logger, cacheKey);
            return null;
        }

        EnsureCacheDirectory();
        var cacheFile = Path.Combine(_cacheDir, $"{cacheKey}.json");

        if (!File.Exists(cacheFile))
        {
            ServiceLogMessages.CacheMissNoFile(logger, cacheKey);
            return null;
        }

        try
        {
            var json = await File.ReadAllTextAsync(cacheFile);
            var cached = JsonSerializer.Deserialize<CachedItem>(json);

            if (cached?.SourceHash == sourceHash)
            {
                ServiceLogMessages.CacheHit(logger, cacheKey, sourceHash[..12], cached.Content.Length);
                return cached.Content;
            }

            ServiceLogMessages.CacheMissHashMismatch(logger, cacheKey, sourceHash[..12], cached?.SourceHash?[..12]);
        }
        catch (Exception ex)
        {
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
    }

    private record CachedItem
    {
        public string Content { get; init; } = "";
        public string SourceHash { get; init; } = "";
        public DateTimeOffset Timestamp { get; init; }
    }
}
