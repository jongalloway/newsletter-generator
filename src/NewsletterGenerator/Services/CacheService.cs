using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NewsletterGenerator.Services;

public class CacheService
{
    private readonly string _cacheDir;

    public CacheService(string? cacheDirectory = null)
    {
        _cacheDir = cacheDirectory ?? Path.Combine(Directory.GetCurrentDirectory(), ".cache");
        Directory.CreateDirectory(_cacheDir);
    }

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
        var cacheFile = Path.Combine(_cacheDir, $"{cacheKey}.json");

        if (!File.Exists(cacheFile))
            return null;

        try
        {
            var json = await File.ReadAllTextAsync(cacheFile);
            var cached = JsonSerializer.Deserialize<CachedItem>(json);

            if (cached?.SourceHash == sourceHash)
                return cached.Content;
        }
        catch
        {
            // Cache file corrupted or invalid, ignore
        }

        return null;
    }

    /// <summary>
    /// Saves content to cache with its source hash
    /// </summary>
    public async Task SaveCacheAsync(string cacheKey, string content, string sourceHash)
    {
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
