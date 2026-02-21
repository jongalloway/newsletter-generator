using System.Text.RegularExpressions;

namespace NewsletterGenerator.Models;

public record VSCodeFeature(
    string Title,
    string Description,
    string Category,
    string? Link);

public partial record VSCodeReleaseNotes(
    DateOnly Date,
    List<VSCodeFeature> Features,
    string VersionUrl)
{
    private const string WebsiteBaseUrl = "https://code.visualstudio.com/updates/";

    [GeneratedRegex(@"(v1_\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex WebsiteUrlVersionPattern();

    public string WebsiteUrl
    {
        get
        {
            var match = WebsiteUrlVersionPattern().Match(VersionUrl);
            return match.Success
                ? $"{WebsiteBaseUrl}{match.Groups[1].Value}"
                : VersionUrl;
        }
    }
}