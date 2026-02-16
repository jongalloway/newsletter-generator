using System.Globalization;
using System.Text.RegularExpressions;
using NewsletterGenerator.Models;

namespace NewsletterGenerator.Services;

public partial class VSCodeReleaseNotesService(HttpClient? httpClient = null)
{
    private readonly HttpClient _http = httpClient ?? new HttpClient();

    private const string RawGitHubBaseUrl = "https://raw.githubusercontent.com/microsoft/vscode-docs/refs/heads/main/release-notes/";
    private const string InsidersRedirectUrl = "https://aka.ms/vscode/updates/insiders";
    private const string RequiredProductEdition = "Insiders";
    private int? _resolvedVersionNumber;

    private const int MinBulletLength = 5;
    private const int MaxTitleLength = 80;
    private const int MaxSentenceEndIndex = 100;
    private const int TruncatedTitleLength = 77;

    public async Task<VSCodeReleaseNotes?> GetReleaseNotesForDateRangeAsync(DateTimeOffset startDate, DateTimeOffset endDate)
    {
        if (startDate > endDate)
            (startDate, endDate) = (endDate, startDate);

        _http.DefaultRequestHeaders.UserAgent.ParseAdd("NewsletterGenerator/1.0");

        var endUrls = await GetCandidateMarkdownUrlsAsync(endDate.Date);
        var startUrls = await GetCandidateMarkdownUrlsAsync(startDate.Date);
        var candidateUrls = endUrls
            .Concat(startUrls)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var allFeatures = new List<VSCodeFeature>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? versionUrl = null;

        foreach (var url in candidateUrls)
        {
            try
            {
                var markdown = await _http.GetStringAsync(url);
                if (!ValidateFrontMatter(markdown))
                    continue;

                var sections = ParseMarkdownSections(markdown, endDate.Year);
                var features = sections
                    .Where(s => s.Date.Date >= startDate.Date && s.Date.Date <= endDate.Date)
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
            catch
            {
                // Try next candidate
            }
        }

        if (allFeatures.Count == 0)
            return null;

        return new VSCodeReleaseNotes(
            Date: endDate.Date,
            Features: allFeatures,
            VersionUrl: versionUrl ?? candidateUrls.First());
    }

    [GeneratedRegex(@"^##\s+(January|February|March|April|May|June|July|August|September|October|November|December)\s+(\d{1,2})(?:,\s*(\d{4}))?", RegexOptions.IgnoreCase)]
    private static partial Regex MarkdownDateHeadingPattern();

    [GeneratedRegex(@"(January|February|March|April|May|June|July|August|September|October|November|December)\s+(\d{1,2})(?:,\s*(\d{4}))?", RegexOptions.IgnoreCase)]
    private static partial Regex DatePattern();

    [GeneratedRegex(@"v1_(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex VersionPattern();

    [GeneratedRegex(@"\[#?\d+\]\((https?://[^\)]+)\)")]
    private static partial Regex MarkdownLinkPattern();

    [GeneratedRegex(@"\[([^\]]*)\]\([^\)]*\)")]
    private static partial Regex MarkdownLinkStripPattern();

    private static bool ValidateFrontMatter(string markdown)
    {
        if (!markdown.StartsWith("---"))
            return false;

        var endIndex = markdown.IndexOf("---", 3, StringComparison.Ordinal);
        if (endIndex < 0)
            return false;

        var frontMatter = markdown[3..endIndex];

        foreach (var line in frontMatter.Split('\n'))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith("ProductEdition:", StringComparison.OrdinalIgnoreCase))
                continue;

            var value = trimmed["ProductEdition:".Length..].Trim();
            return string.Equals(value, RequiredProductEdition, StringComparison.OrdinalIgnoreCase);
        }

        return false;
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
        cleanText = Regex.Replace(cleanText, @"\s*#\d+\s*$", "").Trim();

        if (string.IsNullOrWhiteSpace(cleanText) || cleanText.Length < MinBulletLength)
            return;

        section.Features.Add(new VSCodeFeature(
            Title: TruncateTitle(cleanText),
            Description: cleanText,
            Category: category,
            Link: link));
    }

    private async Task<IReadOnlyList<string>> GetCandidateMarkdownUrlsAsync(DateTime targetDate)
    {
        var currentVersion = await ResolveCurrentVersionAsync();

        if (currentVersion.HasValue)
        {
            var urls = new List<string>
            {
                $"{RawGitHubBaseUrl}v1_{currentVersion.Value}.md",
                $"{RawGitHubBaseUrl}v1_{currentVersion.Value - 1}.md"
            };

            return urls.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        return GetCandidateMarkdownUrlsByDate(targetDate);
    }

    private async Task<int?> ResolveCurrentVersionAsync()
    {
        if (_resolvedVersionNumber.HasValue)
            return _resolvedVersionNumber;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, InsidersRedirectUrl);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

            var finalUrl = response.RequestMessage?.RequestUri?.ToString();
            if (string.IsNullOrEmpty(finalUrl))
                return null;

            var match = VersionPattern().Match(finalUrl);
            if (!match.Success)
                return null;

            _resolvedVersionNumber = int.Parse(match.Groups[1].Value);
            return _resolvedVersionNumber;
        }
        catch
        {
            return null;
        }
    }

    private static IReadOnlyList<string> GetCandidateMarkdownUrlsByDate(DateTime targetDate)
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

    private static string GetMarkdownUrlForMonth(DateTime releaseMonth)
    {
        var referenceDate = new DateTime(2026, 1, 1);
        const int referenceVersion = 109;

        var monthsDiff = ((releaseMonth.Year - referenceDate.Year) * 12) + releaseMonth.Month - referenceDate.Month;
        var version = referenceVersion + monthsDiff;

        return $"{RawGitHubBaseUrl}v1_{version}.md";
    }

    private static DateTime GetReleaseMonth(DateTime targetDate)
    {
        var firstThursday = GetFirstThursdayOfMonth(targetDate.Year, targetDate.Month);
        if (targetDate.Date < firstThursday.Date)
        {
            var previousMonth = targetDate.AddMonths(-1);
            return new DateTime(previousMonth.Year, previousMonth.Month, 1);
        }

        return new DateTime(targetDate.Year, targetDate.Month, 1);
    }

    private static DateTime GetFirstThursdayOfMonth(int year, int month)
    {
        var firstDay = new DateTime(year, month, 1);
        var offset = ((int)DayOfWeek.Thursday - (int)firstDay.DayOfWeek + 7) % 7;
        return firstDay.AddDays(offset);
    }

    private static DateTime? ParseDateFromMatch(Match match, int defaultYear)
    {
        try
        {
            var month = match.Groups[1].Value;
            var day = int.Parse(match.Groups[2].Value);
            var year = match.Groups[3].Success ? int.Parse(match.Groups[3].Value) : defaultYear;
            var monthNumber = DateTime.ParseExact(month, "MMMM", CultureInfo.InvariantCulture).Month;
            return new DateTime(year, monthNumber, day);
        }
        catch
        {
            return null;
        }
    }

    private static string TruncateTitle(string text)
    {
        var firstPeriod = text.IndexOf('.');
        if (firstPeriod > 0 && firstPeriod < MaxSentenceEndIndex)
            return text[..firstPeriod];

        return text.Length > MaxTitleLength ? text[..TruncatedTitleLength] + "..." : text;
    }

    private static string ExtractCategory(string headingText)
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
        public DateTime Date { get; init; }
        public List<VSCodeFeature> Features { get; } = [];
    }
}