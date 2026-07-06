using NewsletterGenerator.Models;
using NewsletterGenerator.Services;

namespace NewsletterGenerator.Tests;

public class ConsolidatePrereleasesTests
{
    private static readonly DateOnly Date = new(2026, 2, 17);

    private static ReleaseEntry Entry(string version, string text = "content") =>
        new(version, Date, text, $"https://github.com/releases/{version}");

    private static ReleaseEntry Entry(string version, DateOnly date, string text = "content") =>
        new(version, date, text, $"https://github.com/releases/{version}");

    [Fact]
    public void SimplePrerelease_MergesIntoFullRelease()
    {
        var releases = new List<ReleaseEntry>
        {
            Entry("v0.1.25", "Full release notes"),
            Entry("v0.1.25-preview.0", "Preview feature"),
        };

        var result = AtomFeedService.ConsolidatePrereleases(releases);

        Assert.Single(result);
        Assert.Equal("v0.1.25", result[0].Version);
        Assert.Contains("Full release notes", result[0].PlainText);
        Assert.Contains("Preview feature", result[0].PlainText);
    }

    [Fact]
    public void OrphanPrerelease_IsPromotedAsStandalone()
    {
        var releases = new List<ReleaseEntry>
        {
            Entry("v0.1.25", "Full release"),
            Entry("v0.1.26-preview.0", "Orphan preview"),
        };

        var result = AtomFeedService.ConsolidatePrereleases(releases);

        Assert.Equal(2, result.Count);
        Assert.Equal("v0.1.25", result[0].Version);
        Assert.Equal("v0.1.26-preview.0", result[1].Version);
        Assert.Contains("Orphan preview", result[1].PlainText);
    }

    [Fact]
    public void OrphanPrereleases_AreOrderedNewestFirst_NotAppendedLast()
    {
        // Reproduces the CLI ordering bug: 1.0.69-0/-1/-2 have no full 1.0.69 release,
        // so they are promoted as standalone entries. Previously all promoted orphans were
        // appended to the end of the list, dropping 1.0.69-0 below older full releases.
        // Input is in feed order (newest-first); consolidation must preserve it.
        var releases = new List<ReleaseEntry>
        {
            Entry("1.0.69-2", new DateOnly(2026, 7, 6), "Newest prerelease"),
            Entry("1.0.69-1", new DateOnly(2026, 7, 4), "Middle prerelease"),
            Entry("1.0.69-0", new DateOnly(2026, 7, 1), "Oldest prerelease"),
            Entry("1.0.68", new DateOnly(2026, 7, 1), "Full release 68"),
            Entry("1.0.67", new DateOnly(2026, 6, 30), "Full release 67"),
            Entry("1.0.66", new DateOnly(2026, 6, 30), "Full release 66"),
        };

        var result = AtomFeedService.ConsolidatePrereleases(releases);

        Assert.Equal(
            ["1.0.69-2", "1.0.69-1", "1.0.69-0", "1.0.68", "1.0.67", "1.0.66"],
            result.Select(r => r.Version));
    }

    [Fact]
    public void LangPrefixedPrerelease_MergesIntoUnprefixedRelease()
    {
        var releases = new List<ReleaseEntry>
        {
            Entry("v0.1.25", "Main release notes"),
            Entry("go/v0.1.25-preview.0", "Go preview fix"),
        };

        var result = AtomFeedService.ConsolidatePrereleases(releases);

        Assert.Single(result);
        Assert.Equal("v0.1.25", result[0].Version);
        Assert.Contains("Go preview fix", result[0].PlainText);
        Assert.Contains("(Go)", result[0].PlainText);
    }

    [Fact]
    public void LangPrefixedPrerelease_WithDescriptionSuffix_MergesCorrectly()
    {
        var releases = new List<ReleaseEntry>
        {
            Entry("v0.1.25", "Main release notes"),
            Entry("go/v0.1.25-preview.0: Fix MCP env vars", "Go env fix"),
        };

        var result = AtomFeedService.ConsolidatePrereleases(releases);

        Assert.Single(result);
        Assert.Contains("Go env fix", result[0].PlainText);
    }

    [Fact]
    public void LangPrefixedOrphanPrerelease_IsPromotedAsStandalone()
    {
        var releases = new List<ReleaseEntry>
        {
            Entry("v0.1.25", "Main release"),
            Entry("go/v0.1.26-preview.0: Add E2E tests", "E2E content"),
        };

        var result = AtomFeedService.ConsolidatePrereleases(releases);

        Assert.Equal(2, result.Count);
        Assert.Equal("v0.1.25", result[0].Version);
        Assert.Equal("go/v0.1.26-preview.0: Add E2E tests", result[1].Version);
        Assert.Contains("E2E content", result[1].PlainText);
    }

    [Fact]
    public void EmptyPrefixedFullRelease_IsSkipped()
    {
        var releases = new List<ReleaseEntry>
        {
            Entry("v0.1.25", "Main release"),
            Entry("go/v0.1.25", ""),
        };

        var result = AtomFeedService.ConsolidatePrereleases(releases);

        Assert.Single(result);
        Assert.Equal("Main release", result[0].PlainText);
    }

    [Fact]
    public void PrefixedFullRelease_WithContent_MergesIntoUnprefixed()
    {
        var releases = new List<ReleaseEntry>
        {
            Entry("v0.1.25", "Main release"),
            Entry("go/v0.1.25", "Go-specific changes"),
        };

        var result = AtomFeedService.ConsolidatePrereleases(releases);

        Assert.Single(result);
        Assert.Contains("Main release", result[0].PlainText);
        Assert.Contains("Go changes:", result[0].PlainText);
        Assert.Contains("Go-specific changes", result[0].PlainText);
    }

    [Fact]
    public void PrefixedFullRelease_WithoutUnprefixedMatch_KeptAsStandalone()
    {
        var releases = new List<ReleaseEntry>
        {
            Entry("go/v0.1.30", "Go-only release"),
        };

        var result = AtomFeedService.ConsolidatePrereleases(releases);

        Assert.Single(result);
        Assert.Equal("go/v0.1.30", result[0].Version);
    }

    [Fact]
    public void RealWorldSdkScenario_ConsolidatesCorrectly()
    {
        // Mirrors the actual SDK feed data from the logs
        var releases = new List<ReleaseEntry>
        {
            Entry("go/v0.1.26-preview.0: Add E2E scenario tests/examples for all SDK languages (#512)", "E2E content"),
            Entry("v0.1.25", "Main SDK v0.1.25 notes"),
            Entry("go/v0.1.25", ""),
            Entry("go/v0.1.25-preview.0: Fix MCP env vars: send envValueMode direct across all SDKs (#484)", "MCP env fix"),
            Entry("v0.1.24", "Main SDK v0.1.24 notes"),
            Entry("go/v0.1.24", ""),
        };

        var result = AtomFeedService.ConsolidatePrereleases(releases);

        // Should have 3 releases: the orphan go/v0.1.26-preview.0 (newest, promoted),
        // then v0.1.25 and v0.1.24. Feed order (newest-first) is preserved, so the promoted
        // orphan stays at the top rather than being appended after the older stable releases.
        Assert.Equal(3, result.Count);
        Assert.StartsWith("go/v0.1.26-preview.0", result[0].Version);
        Assert.Equal("v0.1.25", result[1].Version);
        Assert.Equal("v0.1.24", result[2].Version);

        // go/v0.1.26-preview.0 is promoted as standalone (orphan with content)
        Assert.Contains("E2E content", result[0].PlainText);

        // go/v0.1.25-preview.0 content should be merged into v0.1.25
        Assert.Contains("MCP env fix", result[1].PlainText);
    }

    [Fact]
    public void EmptyPrerelease_IsSkipped()
    {
        var releases = new List<ReleaseEntry>
        {
            Entry("v0.1.25", "Full release"),
            Entry("v0.1.25-preview.0", ""),
        };

        var result = AtomFeedService.ConsolidatePrereleases(releases);

        Assert.Single(result);
        Assert.Equal("Full release", result[0].PlainText);
    }

    [Fact]
    public void NoPreleases_ReturnsUnchanged()
    {
        var releases = new List<ReleaseEntry>
        {
            Entry("0.0.415", "Release 415"),
            Entry("0.0.414", "Release 414"),
        };

        var result = AtomFeedService.ConsolidatePrereleases(releases);

        Assert.Equal(2, result.Count);
        Assert.Equal("0.0.415", result[0].Version);
        Assert.Equal("0.0.414", result[1].Version);
    }

    [Fact]
    public void MultipleLangPrefixes_HandleCorrectly()
    {
        var releases = new List<ReleaseEntry>
        {
            Entry("v1.0.0", "Main release"),
            Entry("python/v1.0.0-preview.0", "Python preview"),
            Entry("dotnet/v1.0.0-preview.0", "Dotnet preview"),
        };

        var result = AtomFeedService.ConsolidatePrereleases(releases);

        Assert.Single(result);
        Assert.Contains("Python preview", result[0].PlainText);
        Assert.Contains(".NET", result[0].PlainText);
        Assert.Contains("Dotnet preview", result[0].PlainText);
    }

    [Fact]
    public void MultiplePrereleasesIntoSameFullRelease()
    {
        var releases = new List<ReleaseEntry>
        {
            Entry("v0.1.25", "Full release notes"),
            Entry("v0.1.25-preview.0", "First preview feature"),
            Entry("v0.1.25-preview.1", "Second preview feature"),
        };

        var result = AtomFeedService.ConsolidatePrereleases(releases);

        Assert.Single(result);
        Assert.Equal("v0.1.25", result[0].Version);
        Assert.Contains("First preview feature", result[0].PlainText);
        Assert.Contains("Second preview feature", result[0].PlainText);
    }

    [Fact]
    public void MixedPrefixedAndUnprefixedPrereleasesIntoSameRelease()
    {
        var releases = new List<ReleaseEntry>
        {
            Entry("v0.1.25", "Main release notes"),
            Entry("v0.1.25-preview.0", "Unprefixed preview"),
            Entry("go/v0.1.25-preview.0", "Go preview"),
        };

        var result = AtomFeedService.ConsolidatePrereleases(releases);

        Assert.Single(result);
        Assert.Contains("Unprefixed preview", result[0].PlainText);
        Assert.Contains("Go preview", result[0].PlainText);
        Assert.Contains("(Go)", result[0].PlainText);
    }

    [Fact]
    public void UnprefixedPrerelease_WithDescriptionSuffix_MergesCorrectly()
    {
        var releases = new List<ReleaseEntry>
        {
            Entry("v0.1.25", "Main release notes"),
            Entry("v0.1.25-preview.0: Fix something important", "Preview fix content"),
        };

        var result = AtomFeedService.ConsolidatePrereleases(releases);

        Assert.Single(result);
        Assert.Contains("Preview fix content", result[0].PlainText);
    }

    [Fact]
    public void AlphaBetaRcPrereleaseSuffixes_AreMerged()
    {
        var releases = new List<ReleaseEntry>
        {
            Entry("v2.0.0", "GA release"),
            Entry("v2.0.0-alpha.1", "Alpha feature"),
            Entry("v2.0.0-beta.2", "Beta feature"),
            Entry("v2.0.0-rc.1", "RC feature"),
        };

        var result = AtomFeedService.ConsolidatePrereleases(releases);

        Assert.Single(result);
        Assert.Equal("v2.0.0", result[0].Version);
        Assert.Contains("Alpha feature", result[0].PlainText);
        Assert.Contains("Beta feature", result[0].PlainText);
        Assert.Contains("RC feature", result[0].PlainText);
    }

    [Fact]
    public void NumericSuffixPrereleases_AreMerged()
    {
        var releases = new List<ReleaseEntry>
        {
            Entry("0.0.421", "Stable release"),
            Entry("0.0.421-0", "Preview build zero"),
            Entry("0.0.421-1", "Preview build one"),
        };

        var result = AtomFeedService.ConsolidatePrereleases(releases);

        Assert.Single(result);
        Assert.Equal("0.0.421", result[0].Version);
        Assert.Contains("Preview build zero", result[0].PlainText);
        Assert.Contains("Preview build one", result[0].PlainText);
    }

    [Fact]
    public void NumericSuffixOrphanPrerelease_IsPromoted()
    {
        var releases = new List<ReleaseEntry>
        {
            Entry("0.0.420", "Stable release"),
            Entry("0.0.421-0", "Orphan preview build"),
        };

        var result = AtomFeedService.ConsolidatePrereleases(releases);

        Assert.Equal(2, result.Count);
        Assert.Equal("0.0.420", result[0].Version);
        Assert.Equal("0.0.421-0", result[1].Version);
        Assert.Contains("Orphan preview build", result[1].PlainText);
    }

    [Fact]
    public void CaseInsensitiveVersionMatching()
    {
        var releases = new List<ReleaseEntry>
        {
            Entry("V0.1.25", "Main release"),
            Entry("v0.1.25-preview.0", "Preview feature"),
        };

        var result = AtomFeedService.ConsolidatePrereleases(releases);

        Assert.Single(result);
        Assert.Contains("Preview feature", result[0].PlainText);
    }

    [Fact]
    public void EmptyInputList_ReturnsEmpty()
    {
        var result = AtomFeedService.ConsolidatePrereleases([]);

        Assert.Empty(result);
    }

    [Fact]
    public void AllOrphanPrereleases_ArePromoted()
    {
        var releases = new List<ReleaseEntry>
        {
            Entry("v0.1.26-preview.0", "Orphan one"),
            Entry("go/v0.1.27-preview.0", "Orphan two"),
            Entry("python/v0.1.28-beta.1", "Orphan three"),
        };

        var result = AtomFeedService.ConsolidatePrereleases(releases);

        Assert.Equal(3, result.Count);
        Assert.Contains("Orphan one", result[0].PlainText);
        Assert.Contains("Orphan two", result[1].PlainText);
        Assert.Contains("Orphan three", result[2].PlainText);
    }

    [Fact]
    public void OrderPreservation_FullReleasesKeepInputOrder()
    {
        var releases = new List<ReleaseEntry>
        {
            Entry("v0.1.27", "Third release"),
            Entry("v0.1.25", "First release"),
            Entry("v0.1.26", "Second release"),
            Entry("v0.1.25-preview.0", "Preview for first"),
        };

        var result = AtomFeedService.ConsolidatePrereleases(releases);

        Assert.Equal(3, result.Count);
        Assert.Equal("v0.1.27", result[0].Version);
        Assert.Equal("v0.1.25", result[1].Version);
        Assert.Equal("v0.1.26", result[2].Version);
        Assert.Contains("Preview for first", result[1].PlainText);
    }

    [Fact]
    public void BetaOnlyReleases_WithRustPrefix_PromotesEachAsStandalone()
    {
        // Mirrors the current SDK feed: all releases are betas, Rust has its own tag
        var releases = new List<ReleaseEntry>
        {
            Entry("v1.0.0-beta.4", "Typed Go union interfaces and experimental schema annotations"),
            Entry("rust/v1.0.0-beta.4", "Rust SDK changelog for beta.4"),
            Entry("v1.0.0-beta.3", "Mode handler APIs and SDK tracing diagnostics"),
            Entry("v1.0.0-beta.2", "Remote session support"),
            Entry("rust-v0.1.0", "Initial Rust SDK release"),
        };

        var result = AtomFeedService.ConsolidatePrereleases(releases);

        // rust-v0.1.0 is a full release (no prerelease suffix, dash doesn't match lang prefix regex)
        // All betas (including rust/v1.0.0-beta.4) are promoted as separate standalone entries.
        // Feed order (newest-first) is preserved, so rust-v0.1.0 stays last (it is listed last).
        Assert.Equal(5, result.Count);
        Assert.Equal(
            ["v1.0.0-beta.4", "rust/v1.0.0-beta.4", "v1.0.0-beta.3", "v1.0.0-beta.2", "rust-v0.1.0"],
            result.Select(r => r.Version));

        var beta4 = result.First(r => r.Version == "v1.0.0-beta.4");
        Assert.Contains("Typed Go union interfaces", beta4.PlainText);
        Assert.DoesNotContain("Rust SDK changelog for beta.4", beta4.PlainText);

        var rustBeta4 = result.First(r => r.Version == "rust/v1.0.0-beta.4");
        Assert.Contains("Rust SDK changelog for beta.4", rustBeta4.PlainText);

        var beta3 = result.First(r => r.Version == "v1.0.0-beta.3");
        Assert.Contains("Mode handler APIs", beta3.PlainText);

        var beta2 = result.First(r => r.Version == "v1.0.0-beta.2");
        Assert.Contains("Remote session support", beta2.PlainText);
    }
}
