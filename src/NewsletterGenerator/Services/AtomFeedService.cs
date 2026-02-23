using System.Net;
using System.ServiceModel.Syndication;
using System.Text.RegularExpressions;
using System.Xml;
using Microsoft.Extensions.Logging;
using NewsletterGenerator.Models;

namespace NewsletterGenerator.Services;

public partial class AtomFeedService(ILogger<AtomFeedService> logger, HttpClient? httpClient = null)
{
    private readonly HttpClient _http = httpClient ?? new HttpClient();

    /// <summary>
    /// Fetches any Atom or RSS 2.0 feed and returns entries within the specified date range.
    /// Optionally filters by category keyword match (case-insensitive substring).
    /// For large blog post feeds, set <paramref name="preferShortSummary"/> = true to use the
    /// short description rather than the full article body.
    /// </summary>
    public async Task<List<ReleaseEntry>> FetchFeedAsync(
        string feedUrl,
        DateOnly startDate,
        DateOnly endDate,
        IEnumerable<string>? categoryKeywords = null,
        bool preferShortSummary = false,
        int maxContentChars = 0)
    {
        logger.LogInformation("Fetching feed: {Url} (range {Start} to {End})", feedUrl, startDate, endDate);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("NewsletterGenerator/1.0");
        var xml = await _http.GetStringAsync(feedUrl);
        logger.LogDebug("Feed response: {Length} chars", xml.Length);

        using var stringReader = new StringReader(xml);
        using var xmlReader = XmlReader.Create(stringReader);
        var feed = SyndicationFeed.Load(xmlReader);

        var keywords = categoryKeywords?
            .Select(k => k.ToLowerInvariant())
            .ToList();

        var entries = new List<ReleaseEntry>();
        int totalItems = 0;
        int skippedDate = 0;
        int skippedCategory = 0;

        foreach (var item in feed.Items)
        {
            totalItems++;
            // Pick the best available date: PublishDate (RSS) or LastUpdatedTime (Atom)
            // Convert to local DateOnly — releases happen during working hours, time doesn't matter
            var pubDateOffset = item.PublishDate != DateTimeOffset.MinValue
                ? item.PublishDate
                : item.LastUpdatedTime;

            var pubDate = DateOnly.FromDateTime(pubDateOffset.LocalDateTime);

            if (pubDate < startDate || pubDate > endDate)
            {
                logger.LogDebug("  Skipped (date {Date} outside {Start}-{End}): {Title}",
                    pubDate, startDate, endDate, item.Title?.Text?.Trim());
                skippedDate++;
                continue;
            }

            // Optional: filter by category label
            if (keywords is { Count: > 0 })
            {
                var cats = item.Categories
                               .Select(c => c.Name.ToLowerInvariant())
                               .ToList();
                if (!cats.Any(c => keywords.Any(k => c.Contains(k))))
                {
                    logger.LogDebug("  Skipped (category mismatch, cats=[{Categories}]): {Title}",
                        string.Join(", ", cats), item.Title?.Text?.Trim());
                    skippedCategory++;
                    continue;
                }
            }

            var rawHtml = GetContent(item, preferShortSummary);
            var plainText = FilterReleaseText(HtmlToText(rawHtml));

            if (maxContentChars > 0 && plainText.Length > maxContentChars)
                plainText = plainText[..maxContentChars].TrimEnd() + " …";

            var url = item.Links
                          .FirstOrDefault(l => l.RelationshipType == "alternate")
                          ?.Uri?.ToString()
                      ?? item.Links.FirstOrDefault()?.Uri?.ToString()
                      ?? "";

            entries.Add(new ReleaseEntry(
                Version: item.Title?.Text?.Trim() ?? "",
                PublishedAt: pubDate,
                PlainText: plainText,
                Url: url));
        }

        var result = entries
            .OrderByDescending(e => e.PublishedAt)
            .ToList();

        logger.LogInformation("Feed {Url}: {Total} items in feed, {Matched} matched, {SkippedDate} skipped (date), {SkippedCat} skipped (category)",
            feedUrl, totalItems, result.Count, skippedDate, skippedCategory);
        foreach (var entry in result)
            logger.LogDebug("  Entry: {Version} ({Date}, {TextLength} chars)", entry.Version, entry.PublishedAt, entry.PlainText.Length);

        return result;
    }

    // ── Content extraction ────────────────────────────────────────────────────

    private static string GetContent(SyndicationItem item, bool preferShortSummary)
    {
        if (preferShortSummary)
            return item.Summary?.Text ?? "";

        // Atom: <content> element
        if (item.Content is TextSyndicationContent textContent)
            return textContent.Text;

        // RSS: <content:encoded> extension
        var encoded = item.ElementExtensions
            .FirstOrDefault(e => e.OuterName == "encoded"
                              && e.OuterNamespace == "http://purl.org/rss/1.0/modules/content/");
        if (encoded is not null)
        {
            using var reader = encoded.GetReader();
            return reader.ReadElementContentAsString();
        }

        // Fallback: <description> / <summary>
        return item.Summary?.Text ?? "";
    }

    // ── HTML → plain text ─────────────────────────────────────────────────────

    [GeneratedRegex(@"<li[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex ReListItem();

    [GeneratedRegex(@"<br\s*/?>", RegexOptions.IgnoreCase)]
    private static partial Regex ReLineBreak();

    [GeneratedRegex(@"<h[1-6][^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex ReHeadOpen();

    [GeneratedRegex(@"</h[1-6]>", RegexOptions.IgnoreCase)]
    private static partial Regex ReHeadClose();

    [GeneratedRegex(@"<p[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex RePara();

    [GeneratedRegex(@"<[^>]+>", RegexOptions.IgnoreCase)]
    private static partial Regex ReTag();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex ReBlankLines();

    private static string HtmlToText(string html)
    {
        if (string.IsNullOrWhiteSpace(html)) return string.Empty;

        var text = ReListItem().Replace(html, "\n- ");
        text = ReLineBreak().Replace(text, "\n");
        text = ReHeadOpen().Replace(text, "\n### ");
        text = ReHeadClose().Replace(text, "\n");
        text = RePara().Replace(text, "\n");
        text = ReTag().Replace(text, "");
        text = WebUtility.HtmlDecode(text);
        text = ReBlankLines().Replace(text, "\n\n");

        return text.Trim();
    }

    // ── Low-value line filter ─────────────────────────────────────────────────

    [GeneratedRegex(
        @"(^\s*-?\s*" +
        @"(fix(\([^)]+\))?:\s*" +           // fix:, fix(go):, etc.
        @"|docs(\([^)]+\))?:\s*" +          // docs:, docs(python):, etc.
        @"|chore(\([^)]+\))?:\s*" +         // chore:, chore(deps):, etc.
        @"|style(\([^)]+\))?:\s*" +         // style:, style(lint):, etc.
        @"|test(\([^)]+\))?:\s*" +          // test:, test(unit):, etc.
        @"|refactor(\([^)]+\))?:\s*" +      // refactor:, refactor(api):, etc.
        @"|ci(\([^)]+\))?:\s*" +            // ci:, ci(actions):, etc.
        @"|build(\([^)]+\))?:\s*" +         // build:, build(deps):, etc.
        @"|perf(\([^)]+\))?:\s*" +          // perf:, perf(cache):, etc.
        @"|fix(es|ed)?\b" +                 // fix, fixed, fixes
        @"|improve(s|d|ment)?\b" +          // improve, improved, improves, improvement
        @"|update(s|d)?\s+(deps|dependencies|packages|changelog|readme|ci|tests|lock)" +
        @"|bump\b" +                        // bump
        @"|upgrade(s|d)?\b" +               // upgrade, upgraded, upgrades
        @"|refactor(s|ed)?\b" +             // refactor, refactored, refactors
        @"|clean(s|ed|up)?\b" +             // clean, cleaned, cleanup, cleans
        @"|revert(s|ed)?\b" +               // revert, reverted, reverts
        @"|minor\b" +                       // minor
        @"|misc\b" +                        // misc
        @"|tests?\b" +                      // test, tests
        @"|lint\b" +                        // lint
        @"|format(s|ted|ting)?\b" +         // format, formatted, formats, formatting
        @"|build\s+fix)" +                  // build fix
        @"|\sci[\s:])",                     // " ci " or "ci:" anywhere in line (case-insensitive)
        RegexOptions.IgnoreCase)]
    private static partial Regex ReLowValueLine();

    // ── GitHub attribution filter ─────────────────────────────────────────────
    // Removes "by @username in #123" from the end of lines

    [GeneratedRegex(@"\s+by\s+@\w+\s+in\s+#\d+\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex ReGitHubAttribution();

    private static string FilterReleaseText(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;

        // First remove GitHub attributions from each line
        var lines = text.Split('\n')
            .Select(line => ReGitHubAttribution().Replace(line, "").TrimEnd());

        // Then filter out low-value lines
        return string.Join('\n',
            lines.Where(line => !ReLowValueLine().IsMatch(line))
        ).Trim();
    }

    // ── Prerelease and language-prefix consolidation ───────────────────────────

    [GeneratedRegex(@"^(?<base>.+?)-(?:preview|alpha|beta|rc)(?:\.\d+)?$", RegexOptions.IgnoreCase)]
    private static partial Regex RePrereleaseVersion();

    [GeneratedRegex(@"^(?<lang>[a-zA-Z][a-zA-Z0-9._]*)/(?<version>.+)$")]
    private static partial Regex ReLangPrefixedVersion();

    /// <summary>
    /// Extracts just the version tag from an Atom feed title, stripping any
    /// trailing description (e.g., "go/v0.1.26-preview.0: Add E2E tests..." → "go/v0.1.26-preview.0").
    /// </summary>
    private static string ExtractVersionTag(string version)
    {
        var colonIdx = version.IndexOf(':');
        return colonIdx >= 0 ? version[..colonIdx].Trim() : version.Trim();
    }

    /// <summary>
    /// Consolidates language-prefixed versions (e.g., go/v0.1.25) and prereleases
    /// (e.g., go/v0.1.25-preview.0) into their matching unprefixed full releases.
    /// Language-specific content is annotated with the language name (e.g., "(Go)").
    /// Orphan prereleases with content are promoted to standalone entries.
    /// </summary>
    public static List<ReleaseEntry> ConsolidatePrereleases(List<ReleaseEntry> releases)
    {
        // Categorize all entries
        var fullReleases = new List<ReleaseEntry>();               // e.g., v0.1.25
        var prefixedFullReleases = new List<(ReleaseEntry Entry, string Lang, string Version)>(); // e.g., go/v0.1.25
        var prereleases = new List<(ReleaseEntry Entry, string? Lang, string BaseVersion)>();      // e.g., go/v0.1.25-preview.0

        foreach (var release in releases)
        {
            // Strip description suffix from Atom feed titles (e.g., "go/v0.1.26-preview.0: Add E2E tests..." → "go/v0.1.26-preview.0")
            var version = ExtractVersionTag(release.Version);
            string? lang = null;

            // Strip language prefix if present (e.g., "go/v0.1.25" → lang="go", version="v0.1.25")
            var langMatch = ReLangPrefixedVersion().Match(version);
            if (langMatch.Success)
            {
                lang = langMatch.Groups["lang"].Value;
                version = langMatch.Groups["version"].Value;
            }

            // Check if the (possibly stripped) version is a prerelease
            var preMatch = RePrereleaseVersion().Match(version);
            if (preMatch.Success)
            {
                prereleases.Add((release, lang, preMatch.Groups["base"].Value));
            }
            else if (lang != null)
            {
                prefixedFullReleases.Add((release, lang, version));
            }
            else
            {
                fullReleases.Add(release);
            }
        }

        // Merge prefixed full releases into unprefixed full releases (e.g., go/v0.1.25 → v0.1.25)
        foreach (var (entry, lang, version) in prefixedFullReleases)
        {
            if (string.IsNullOrWhiteSpace(entry.PlainText))
                continue; // Skip empty prefixed releases (common for Go module tags)

            var fullIndex = fullReleases.FindIndex(r =>
                string.Equals(r.Version, version, StringComparison.OrdinalIgnoreCase));

            if (fullIndex >= 0)
            {
                var full = fullReleases[fullIndex];
                var langLabel = FormatLangLabel(lang);
                var mergedText = $"{full.PlainText}\n\n{langLabel} changes:\n{entry.PlainText}";
                fullReleases[fullIndex] = full with { PlainText = mergedText };
            }
            // If no unprefixed match, keep as standalone (re-add to full releases)
            else
            {
                fullReleases.Add(entry);
            }
        }

        // Merge prereleases into the matching full release
        foreach (var (prerelease, lang, baseVersion) in prereleases)
        {
            if (string.IsNullOrWhiteSpace(prerelease.PlainText))
                continue;

            // Try matching against unprefixed full release first
            var fullIndex = fullReleases.FindIndex(r =>
                string.Equals(r.Version, baseVersion, StringComparison.OrdinalIgnoreCase));

            if (fullIndex >= 0)
            {
                var full = fullReleases[fullIndex];
                var langNote = lang != null ? $" ({FormatLangLabel(lang)})" : "";
                var mergedText = $"{full.PlainText}\n\nAdditional features from prerelease{langNote} ({prerelease.Version}):\n{prerelease.PlainText}";
                fullReleases[fullIndex] = full with { PlainText = mergedText };
            }
            // Orphan prerelease — no matching full release, drop it
        }

        return fullReleases;
    }

    private static string FormatLangLabel(string lang) =>
        lang.ToLowerInvariant() switch
        {
            "go" => "Go",
            "python" => "Python",
            "dotnet" or ".net" => ".NET",
            "csharp" or "cs" => "C#",
            "typescript" or "ts" => "TypeScript",
            "javascript" or "js" => "JavaScript",
            _ => lang
        };
}
