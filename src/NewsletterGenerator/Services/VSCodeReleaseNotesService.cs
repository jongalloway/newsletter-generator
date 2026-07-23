using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using NewsletterGenerator.Models;

namespace NewsletterGenerator.Services;

public partial class VSCodeReleaseNotesService
{
    private readonly HttpClient _http;

    private const string RawGitHubBaseUrl = "https://raw.githubusercontent.com/microsoft/vscode-docs/refs/heads/main/release-notes/";
    private const string StableUpdatesUrl = "https://code.visualstudio.com/updates";
    private const string RequiredProductEdition = "Insiders";
    private int? _resolvedStableVersionNumber;

    private const int MinBulletLength = 5;
    private const int MaxTitleLength = 80;
    private const int MaxSentenceEndIndex = 100;
    private const int TruncatedTitleLength = 77;

    public VSCodeReleaseNotesService(HttpClient? httpClient = null)
    {
        _http = httpClient ?? new HttpClient();
        if (!_http.DefaultRequestHeaders.UserAgent.Any())
        {
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("NewsletterGenerator/1.0");
        }
    }

    public async Task<VSCodeReleaseNotes?> GetReleaseNotesForDateRangeAsync(DateOnly startDate, DateOnly endDate)
    {
        var result = await GetReleaseNotesFetchResultForDateRangeAsync(startDate, endDate);
        return result.ReleaseNotes;
    }

    public async Task<VSCodeReleaseNotesFetchResult> GetReleaseNotesFetchResultForDateRangeAsync(DateOnly startDate, DateOnly endDate)
    {
        if (startDate > endDate)
            (startDate, endDate) = (endDate, startDate);

        var endUrls = await GetCandidateMarkdownUrlsAsync(endDate);
        var startUrls = await GetCandidateMarkdownUrlsAsync(startDate);
        var candidateUrls = endUrls
            .Concat(startUrls)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var allFeatures = new List<VSCodeFeature>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? versionUrl = null;
        var successfulUrls = 0;
        var matchedSections = 0;
        List<string> stableHighlights = [];
        List<StableFeatureCallout> stableFeatureCallouts = [];
        string? stableVersionUrl = null;

        foreach (var url in candidateUrls)
        {
            try
            {
                var markdown = await _http.GetStringAsync(url);
                var edition = GetProductEdition(markdown);

                if (string.Equals(edition, "Stable", StringComparison.OrdinalIgnoreCase))
                {
                    // Extract the welcome highlights and detailed feature callouts
                    // from the Stable release notes.
                    if (stableHighlights.Count == 0)
                    {
                        stableHighlights = ParseStableHighlights(markdown);
                        stableFeatureCallouts = ParseStableFeatureCallouts(markdown);
                        stableVersionUrl = url;
                    }
                    continue;
                }

                if (!string.Equals(edition, RequiredProductEdition, StringComparison.OrdinalIgnoreCase))
                    continue;

                successfulUrls++;

                var sections = ParseMarkdownSections(markdown, endDate.Year);
                var matchingSections = sections
                    .Where(s => s.Date >= startDate && s.Date <= endDate)
                    .ToList();
                matchedSections += matchingSections.Count;

                var features = matchingSections
                    .SelectMany(s => s.Features)
                    .ToList();

                if (features.Count == 0)
                    continue;

                versionUrl ??= url;

                foreach (var feature in features)
                {
                    var key = $"{feature.Title}|{feature.Description}";
                    if (seen.Add(key))
                        allFeatures.Add(feature);
                }
            }
            catch (Exception ex)
            {
                // Try next candidate
                Debug.WriteLine($"[VSCodeReleaseNotesService] Failed to fetch/parse {url}: {ex.Message}");
            }
        }

        if (allFeatures.Count == 0)
        {
            return new VSCodeReleaseNotesFetchResult(
                null,
                candidateUrls.Count,
                successfulUrls,
                matchedSections,
                0)
            {
                StableHighlights = stableHighlights,
                StableFeatureCallouts = stableFeatureCallouts,
                StableVersionUrl = stableVersionUrl
            };
        }

        return new VSCodeReleaseNotesFetchResult(
            new VSCodeReleaseNotes(
                Date: endDate,
                Features: allFeatures,
                VersionUrl: versionUrl ?? candidateUrls.First()),
            candidateUrls.Count,
            successfulUrls,
            matchedSections,
            allFeatures.Count)
        {
            StableHighlights = stableHighlights,
            StableFeatureCallouts = stableFeatureCallouts,
            StableVersionUrl = stableVersionUrl
        };
    }

    [GeneratedRegex(@"^##\s+(January|February|March|April|May|June|July|August|September|October|November|December)\s+(\d{1,2})(?:,\s*(\d{4}))?", RegexOptions.IgnoreCase)]
    private static partial Regex MarkdownDateHeadingPattern();

    [GeneratedRegex(@"^\*\s+\[([^\]]+)\]\([^)]*\):\s*(.*)", RegexOptions.IgnoreCase)]
    private static partial Regex StableHighlightBulletPattern();

    internal static List<string> ParseStableHighlights(string markdown)
    {
        var highlights = new List<string>();
        var lines = markdown.Split('\n');
        var inWelcomeSection = false;

        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd('\r');

            // Start capturing after "Welcome to the" line
            if (line.StartsWith("Welcome to the", StringComparison.OrdinalIgnoreCase))
            {
                inWelcomeSection = true;
                continue;
            }

            if (!inWelcomeSection)
                continue;

            // Stop at "Happy Coding", a horizontal rule, or a ## heading
            if (line.StartsWith("Happy Coding", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("---")
                || line.StartsWith("## "))
                break;

            // Match bullets like: * [Title](#anchor): description
            var match = StableHighlightBulletPattern().Match(line);
            if (match.Success)
            {
                var title = match.Groups[1].Value.Trim();
                var description = match.Groups[2].Value.Trim();
                highlights.Add(string.IsNullOrWhiteSpace(description)
                    ? title
                    : $"{title}: {description}");
            }
        }

        return highlights;
    }

    private const string ImageBaseUrl = "https://code.visualstudio.com/assets/updates/";

    [GeneratedRegex(@"!\[([^\]]*)\]\((images/[^\)]+)\)")]
    private static partial Regex MarkdownImagePattern();

    internal static List<StableFeatureCallout> ParseStableFeatureCallouts(string markdown, int maxCallouts = 5)
    {
        var callouts = new List<StableFeatureCallout>();
        var lines = markdown.Split('\n');
        string? currentTitle = null;
        var descriptionLines = new List<string>();
        string? firstImageUrl = null;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');

            // New ### feature heading — flush previous
            if (line.StartsWith("### "))
            {
                FlushCallout(callouts, currentTitle, descriptionLines, firstImageUrl, maxCallouts);
                currentTitle = line[4..].Trim();
                descriptionLines.Clear();
                firstImageUrl = null;
                continue;
            }

            // A ## category heading resets (don't capture category-level text)
            if (line.StartsWith("## "))
            {
                FlushCallout(callouts, currentTitle, descriptionLines, firstImageUrl, maxCallouts);
                currentTitle = null;
                descriptionLines.Clear();
                firstImageUrl = null;
                continue;
            }

            if (currentTitle == null)
                continue;

            // Check for image
            var imgMatch = MarkdownImagePattern().Match(line);
            if (imgMatch.Success && firstImageUrl == null)
            {
                var relativePath = imgMatch.Groups[2].Value;
                firstImageUrl = $"{ImageBaseUrl}{relativePath["images/".Length..]}";
                continue;
            }

            // Skip video tags, HTML comments, settings blocks, blank lines at start
            if (line.StartsWith("<video", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("<!--")
                || line.StartsWith("**Setting**", StringComparison.OrdinalIgnoreCase))
                continue;

            // Collect description lines (non-empty text)
            if (!string.IsNullOrWhiteSpace(line))
                descriptionLines.Add(line);
        }

        FlushCallout(callouts, currentTitle, descriptionLines, firstImageUrl, maxCallouts);
        return callouts;

        static void FlushCallout(
            List<StableFeatureCallout> callouts,
            string? title,
            List<string> descLines,
            string? imageUrl,
            int max)
        {
            if (title == null || callouts.Count >= max)
                return;

            // Take the first 1-3 non-empty paragraphs for a concise description
            var description = string.Join(" ", descLines.Take(3));
            if (string.IsNullOrWhiteSpace(description))
                return;

            callouts.Add(new StableFeatureCallout(title, description.Trim(), imageUrl));
        }
    }

    [GeneratedRegex(@"v1_(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex VersionPattern();

    [GeneratedRegex(@"(January|February|March|April|May|June|July|August|September|October|November|December)\s+(\d{1,2})(?:,\s*(\d{4}))?", RegexOptions.IgnoreCase)]
    private static partial Regex DatePattern();

    [GeneratedRegex(@"\[#?\d+\]\((https?://[^\)]+)\)")]
    private static partial Regex MarkdownLinkPattern();

    [GeneratedRegex(@"\[([^\]]*)\]\([^\)]*\)")]
    private static partial Regex MarkdownLinkStripPattern();

    [GeneratedRegex(@"\s*#\d+\s*$")]
    private static partial Regex TrailingIssueNumberPattern();

    internal static bool ValidateFrontMatter(string markdown) =>
        string.Equals(GetProductEdition(markdown), RequiredProductEdition, StringComparison.OrdinalIgnoreCase);

    internal static string? GetProductEdition(string markdown)
    {
        if (!markdown.StartsWith("---"))
            return null;

        var endIndex = markdown.IndexOf("---", 3, StringComparison.Ordinal);
        if (endIndex < 0)
            return null;

        var frontMatter = markdown[3..endIndex];

        foreach (var line in frontMatter.Split('\n'))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith("ProductEdition:", StringComparison.OrdinalIgnoreCase))
                continue;

            return trimmed["ProductEdition:".Length..].Trim();
        }

        return null;
    }

    private List<MarkdownDateSection> ParseMarkdownSections(string markdown, int defaultYear)
    {
        var sections = new List<MarkdownDateSection>();
        var lines = markdown.Split('\n');

        MarkdownDateSection? currentSection = null;
        var currentBulletLines = new List<string>();
        var currentCategory = "General";

        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd('\r');

            var dateMatch = MarkdownDateHeadingPattern().Match(line);
            if (dateMatch.Success)
            {
                FlushBullet(currentBulletLines, currentSection, currentCategory);

                var parsedDate = ParseDateFromMatch(dateMatch, defaultYear);
                if (parsedDate != null)
                {
                    currentSection = new MarkdownDateSection { Date = parsedDate.Value };
                    sections.Add(currentSection);
                    currentCategory = ExtractCategory(line);
                }

                continue;
            }

            if (currentSection == null)
                continue;

            if (line.StartsWith("* ") || line.StartsWith("- "))
            {
                FlushBullet(currentBulletLines, currentSection, currentCategory);
                currentBulletLines.Add(line[2..].TrimEnd());
                continue;
            }

            if (currentBulletLines.Count > 0 &&
                !string.IsNullOrWhiteSpace(line) &&
                !line.StartsWith('#'))
            {
                currentBulletLines.Add(line.TrimEnd());
                continue;
            }

            if (currentBulletLines.Count > 0)
                FlushBullet(currentBulletLines, currentSection, currentCategory);
        }

        FlushBullet(currentBulletLines, currentSection, currentCategory);

        return sections;
    }

    private void FlushBullet(List<string> bulletLines, MarkdownDateSection? section, string category)
    {
        if (bulletLines.Count == 0 || section == null)
            return;

        var rawText = string.Join(" ", bulletLines).Trim();
        bulletLines.Clear();

        if (rawText.Length < MinBulletLength)
            return;

        var linkMatch = MarkdownLinkPattern().Match(rawText);
        var link = linkMatch.Success ? linkMatch.Groups[1].Value : null;

        var cleanText = MarkdownLinkStripPattern().Replace(rawText, "$1").Trim();
        cleanText = TrailingIssueNumberPattern().Replace(cleanText, "").Trim();

        if (string.IsNullOrWhiteSpace(cleanText) || cleanText.Length < MinBulletLength)
            return;

        section.Features.Add(new VSCodeFeature(
            Title: TruncateTitle(cleanText),
            Description: cleanText,
            Category: category,
            Link: link));
    }

    private async Task<IReadOnlyList<string>> GetCandidateMarkdownUrlsAsync(DateOnly targetDate)
    {
        var currentStableVersion = await ResolveCurrentStableVersionAsync();

        if (currentStableVersion.HasValue)
        {
            // Stable releases are authoritative on the updates page. The next
            // release-note file is the current Insiders build.
            var urls = new List<string>
            {
                $"{RawGitHubBaseUrl}v1_{currentStableVersion.Value + 1}.md",
                $"{RawGitHubBaseUrl}v1_{currentStableVersion.Value}.md",
                $"{RawGitHubBaseUrl}v1_{currentStableVersion.Value - 1}.md"
            };

            return urls.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        return GetCandidateMarkdownUrlsByDate(targetDate);
    }

    private async Task<int?> ResolveCurrentStableVersionAsync()
    {
        if (_resolvedStableVersionNumber.HasValue)
            return _resolvedStableVersionNumber;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, StableUpdatesUrl);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

            var finalUrl = response.RequestMessage?.RequestUri?.ToString();
            if (string.IsNullOrEmpty(finalUrl))
                return null;

            var match = VersionPattern().Match(finalUrl);
            if (!match.Success)
                return null;

            _resolvedStableVersionNumber = int.Parse(match.Groups[1].Value);
            return _resolvedStableVersionNumber;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[VSCodeReleaseNotesService] Failed to resolve current stable version: {ex.Message}");
            return null;
        }
    }

    private static IReadOnlyList<string> GetCandidateMarkdownUrlsByDate(DateOnly targetDate)
    {
        var releaseMonth = GetReleaseMonth(targetDate);
        var nextMonth = releaseMonth.AddMonths(1);

        var urls = new List<string>
        {
            GetMarkdownUrlForMonth(releaseMonth),
            GetMarkdownUrlForMonth(nextMonth)
        };

        return urls.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string GetMarkdownUrlForMonth(DateOnly releaseMonth)
    {
        var referenceDate = new DateOnly(2026, 1, 1);
        const int referenceVersion = 109;

        var monthsDiff = ((releaseMonth.Year - referenceDate.Year) * 12) + releaseMonth.Month - referenceDate.Month;
        var version = referenceVersion + monthsDiff;

        return $"{RawGitHubBaseUrl}v1_{version}.md";
    }

    internal static DateOnly GetReleaseMonth(DateOnly targetDate)
    {
        var firstThursday = GetFirstThursdayOfMonth(targetDate.Year, targetDate.Month);
        if (targetDate < firstThursday)
        {
            var previousMonth = targetDate.AddMonths(-1);
            return new DateOnly(previousMonth.Year, previousMonth.Month, 1);
        }

        return new DateOnly(targetDate.Year, targetDate.Month, 1);
    }

    internal static DateOnly GetFirstThursdayOfMonth(int year, int month)
    {
        var firstDay = new DateOnly(year, month, 1);
        var offset = ((int)DayOfWeek.Thursday - (int)firstDay.DayOfWeek + 7) % 7;
        return firstDay.AddDays(offset);
    }

    private static DateOnly? ParseDateFromMatch(Match match, int defaultYear)
    {
        try
        {
            var month = match.Groups[1].Value;
            var day = int.Parse(match.Groups[2].Value);
            var year = match.Groups[3].Success ? int.Parse(match.Groups[3].Value) : defaultYear;
            var monthNumber = DateTime.ParseExact(month, "MMMM", CultureInfo.InvariantCulture).Month;
            return new DateOnly(year, monthNumber, day);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[VSCodeReleaseNotesService] Failed to parse date from match: {ex.Message}");
            return null;
        }
    }

    internal static string TruncateTitle(string text)
    {
        var firstPeriod = text.IndexOf('.');
        if (firstPeriod > 0 && firstPeriod < MaxSentenceEndIndex)
            return text[..firstPeriod];

        return text.Length > MaxTitleLength ? text[..TruncatedTitleLength] + "..." : text;
    }

    internal static string ExtractCategory(string headingText)
    {
        var dashIndex = headingText.IndexOf('-');
        if (dashIndex > 0 && dashIndex < headingText.Length - 2)
            return headingText[(dashIndex + 1)..].Trim();

        var dateMatch = DatePattern().Match(headingText);
        if (dateMatch.Success)
        {
            var startIndex = dateMatch.Index + dateMatch.Length;
            var remainder = headingText[startIndex..].Trim();
            if (!string.IsNullOrWhiteSpace(remainder))
                return remainder.TrimStart('-', ':', ' ');
        }

        return "General";
    }

    private sealed class MarkdownDateSection
    {
        public DateOnly Date { get; init; }
        public List<VSCodeFeature> Features { get; } = [];
    }
}

public sealed record VSCodeReleaseNotesFetchResult(
    VSCodeReleaseNotes? ReleaseNotes,
    int CandidateUrlCount,
    int SuccessfulUrlCount,
    int MatchedSectionCount,
    int UniqueFeatureCount)
{
    public List<string> StableHighlights { get; init; } = [];
    public List<StableFeatureCallout> StableFeatureCallouts { get; init; } = [];
    public string? StableVersionUrl { get; init; }
}