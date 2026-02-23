using NewsletterGenerator.Models;
using NewsletterGenerator.Services;

namespace NewsletterGenerator.Tests;

public class ConsolidatePrereleasesTests
{
    private static readonly DateOnly Date = new(2026, 2, 17);

    private static ReleaseEntry Entry(string version, string text = "content") =>
        new(version, Date, text, $"https://github.com/releases/{version}");

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
    public void OrphanPrerelease_IsDropped()
    {
        var releases = new List<ReleaseEntry>
        {
            Entry("v0.1.25", "Full release"),
            Entry("v0.1.26-preview.0", "Orphan preview"),
        };

        var result = AtomFeedService.ConsolidatePrereleases(releases);

        Assert.Single(result);
        Assert.Equal("v0.1.25", result[0].Version);
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
    public void LangPrefixedOrphanPrerelease_IsDropped()
    {
        var releases = new List<ReleaseEntry>
        {
            Entry("v0.1.25", "Main release"),
            Entry("go/v0.1.26-preview.0: Add E2E tests", "E2E content"),
        };

        var result = AtomFeedService.ConsolidatePrereleases(releases);

        Assert.Single(result);
        Assert.Equal("v0.1.25", result[0].Version);
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

        // Should have 2 full releases: v0.1.25 and v0.1.24
        Assert.Equal(2, result.Count);
        Assert.Equal("v0.1.25", result[0].Version);
        Assert.Equal("v0.1.24", result[1].Version);

        // go/v0.1.25-preview.0 content should be merged into v0.1.25
        Assert.Contains("MCP env fix", result[0].PlainText);

        // go/v0.1.26-preview.0 should be dropped (orphan)
        Assert.DoesNotContain("E2E content", result[0].PlainText);
        Assert.DoesNotContain("E2E content", result[1].PlainText);
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
    public void AllOrphanPrereleases_ReturnsEmpty()
    {
        var releases = new List<ReleaseEntry>
        {
            Entry("v0.1.26-preview.0", "Orphan one"),
            Entry("go/v0.1.27-preview.0", "Orphan two"),
            Entry("python/v0.1.28-beta.1", "Orphan three"),
        };

        var result = AtomFeedService.ConsolidatePrereleases(releases);

        Assert.Empty(result);
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
}
