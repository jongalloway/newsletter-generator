using Microsoft.Extensions.Logging;

namespace NewsletterGenerator.Services;

internal static partial class ServiceLogMessages
{
    [LoggerMessage(EventId = 1000, Level = LogLevel.Information, Message = "Fetching feed: {Url} (range {Start} to {End})")]
    internal static partial void FetchingFeed(ILogger logger, string url, DateOnly start, DateOnly end);

    [LoggerMessage(EventId = 1001, Level = LogLevel.Debug, Message = "Feed response: {Length} chars")]
    internal static partial void FeedResponseLength(ILogger logger, int length);

    [LoggerMessage(EventId = 1002, Level = LogLevel.Debug, Message = "Skipped (date {Date} outside {Start}-{End}): {Title}")]
    internal static partial void FeedItemSkippedDate(ILogger logger, DateOnly date, DateOnly start, DateOnly end, string? title);

    [LoggerMessage(EventId = 1003, Level = LogLevel.Debug, Message = "Skipped (category mismatch, cats=[{Categories}]): {Title}")]
    internal static partial void FeedItemSkippedCategory(ILogger logger, string categories, string? title);

    [LoggerMessage(EventId = 1004, Level = LogLevel.Information, Message = "Feed {Url}: {Total} items in feed, {Matched} matched, {SkippedDate} skipped (date), {SkippedCategory} skipped (category)")]
    internal static partial void FeedSummary(ILogger logger, string url, int total, int matched, int skippedDate, int skippedCategory);

    [LoggerMessage(EventId = 1005, Level = LogLevel.Debug, Message = "Entry: {Version} ({Date}, {TextLength} chars)")]
    internal static partial void FeedEntry(ILogger logger, string version, DateOnly date, int textLength);

    [LoggerMessage(EventId = 1100, Level = LogLevel.Debug, Message = "Cache skip (force refresh): {CacheKey}")]
    internal static partial void CacheSkipForceRefresh(ILogger logger, string cacheKey);

    [LoggerMessage(EventId = 1101, Level = LogLevel.Debug, Message = "Cache miss (no file): {CacheKey}")]
    internal static partial void CacheMissNoFile(ILogger logger, string cacheKey);

    [LoggerMessage(EventId = 1102, Level = LogLevel.Information, Message = "Cache hit: {CacheKey} (hash={Hash}, content={Length} chars)")]
    internal static partial void CacheHit(ILogger logger, string cacheKey, string hash, int length);

    [LoggerMessage(EventId = 1103, Level = LogLevel.Debug, Message = "Cache miss (hash mismatch): {CacheKey} expected={Expected} actual={Actual}")]
    internal static partial void CacheMissHashMismatch(ILogger logger, string cacheKey, string expected, string? actual);

    [LoggerMessage(EventId = 1104, Level = LogLevel.Warning, Message = "Cache read failed for {CacheKey}")]
    internal static partial void CacheReadFailed(ILogger logger, Exception exception, string cacheKey);

    [LoggerMessage(EventId = 1105, Level = LogLevel.Warning, Message = "Skipping cache save for {CacheKey}: content is empty")]
    internal static partial void CacheSaveSkippedEmpty(ILogger logger, string cacheKey);

    [LoggerMessage(EventId = 1106, Level = LogLevel.Information, Message = "Saving cache: {CacheKey} (hash={Hash}, content={Length} chars)")]
    internal static partial void CacheSaving(ILogger logger, string cacheKey, string hash, int length);
}
