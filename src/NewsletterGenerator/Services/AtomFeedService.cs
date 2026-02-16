using System.Net;
using System.ServiceModel.Syndication;
using System.Text.RegularExpressions;
using System.Xml;
using NewsletterGenerator.Models;

namespace NewsletterGenerator.Services;

public partial class AtomFeedService(HttpClient? httpClient = null)
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
        DateTimeOffset startDate,
        DateTimeOffset endDate,
        IEnumerable<string>? categoryKeywords = null,
        bool preferShortSummary = false,
        int maxContentChars = 0)
    {
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("NewsletterGenerator/1.0");
        var xml = await _http.GetStringAsync(feedUrl);

        using var stringReader = new StringReader(xml);
        using var xmlReader = XmlReader.Create(stringReader);
        var feed = SyndicationFeed.Load(xmlReader);

        var keywords = categoryKeywords?
            .Select(k => k.ToLowerInvariant())
            .ToList();

        var entries = new List<ReleaseEntry>();

        foreach (var item in feed.Items)
        {
            // Pick the best available date: PublishDate (RSS) or LastUpdatedTime (Atom)
            var pubDate = item.PublishDate != DateTimeOffset.MinValue
                ? item.PublishDate
                : item.LastUpdatedTime;

            if (pubDate < startDate || pubDate > endDate)
                continue;

            // Optional: filter by category label
            if (keywords is { Count: > 0 })
            {
                var cats = item.Categories
                               .Select(c => c.Name.ToLowerInvariant())
                               .ToList();
                if (!cats.Any(c => keywords.Any(k => c.Contains(k))))
                    continue;
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
                Url: url
            ));
        }

        return entries
            .OrderByDescending(e => e.PublishedAt)
            .ToList();
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
}
