using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace NewsletterGenerator.Services;

public class CacheService(ILogger<CacheService> logger, string? cacheDirectory = null, bool forceRefresh = false)
{
    private readonly string _cacheDir = cacheDirectory ?? Path.Combine(Directory.GetCurrentDirectory(), ".cache");
    private readonly bool _forceRefresh = forceRefresh;

    public int CacheHits { get; private set; }
    public int CacheMisses { get; private set; }
    public int CacheSkips { get; private set; }

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
            CacheSkips++;
            logger.LogDebug("Cache skip (force refresh): {CacheKey}", cacheKey);
            return null;
        }

        EnsureCacheDirectory();
        var cacheFile = Path.Combine(_cacheDir, $"{cacheKey}.json");

        if (!File.Exists(cacheFile))
        {
            CacheMisses++;
            logger.LogDebug("Cache miss (no file): {CacheKey}", cacheKey);
            return null;
        }

        try
        {
            var json = await File.ReadAllTextAsync(cacheFile);
            var cached = JsonSerializer.Deserialize<CachedItem>(json);

            if (cached?.SourceHash == sourceHash)
            {
                CacheHits++;
                logger.LogInformation("Cache hit: {CacheKey} (hash={Hash}, content={Length} chars)", cacheKey, sourceHash[..12], cached.Content.Length);
                return cached.Content;
            }

            CacheMisses++;
            logger.LogDebug("Cache miss (hash mismatch): {CacheKey} expected={Expected} actual={Actual}", cacheKey, sourceHash[..12], cached?.SourceHash?[..12]);
        }
        catch (Exception ex)
        {
            CacheMisses++;
            logger.LogWarning(ex, "Cache read failed for {CacheKey}", cacheKey);
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
            logger.LogWarning("Skipping cache save for {CacheKey}: content is empty", cacheKey);
            return;
        }

        logger.LogInformation("Saving cache: {CacheKey} (hash={Hash}, content={Length} chars)", cacheKey, sourceHash[..12], content.Length);

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
