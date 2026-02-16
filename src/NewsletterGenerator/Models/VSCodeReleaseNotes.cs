using System.Text.RegularExpressions;

namespace NewsletterGenerator.Models;

public record VSCodeFeature(
    string Title,
    string Description,
    string Category,
    string? Link);

public record VSCodeReleaseNotes(
    DateTime Date,
    List<VSCodeFeature> Features,
    string VersionUrl)
{
    private const string WebsiteBaseUrl = "https://code.visualstudio.com/updates/";

    public string WebsiteUrl
    {
        get
        {
            var match = Regex.Match(VersionUrl, @"(v1_\d+)", RegexOptions.IgnoreCase);
            return match.Success
                ? $"{WebsiteBaseUrl}{match.Groups[1].Value}"
                : VersionUrl;
        }
    }
}