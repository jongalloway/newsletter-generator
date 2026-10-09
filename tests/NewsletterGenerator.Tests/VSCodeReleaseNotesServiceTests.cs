using System.Net;
using NewsletterGenerator.Services;

namespace NewsletterGenerator.Tests;

public class VSCodeReleaseNotesServiceTests
{
    // ── ValidateFrontMatter ───────────────────────────────────────────────────

    [Theory]
    [InlineData("---\nProductEdition: Insiders\n---\n# Content", true)]
    [InlineData("---\nProductEdition: insiders\n---\n# Content", true)]
    [InlineData("---\nProductEdition: Stable\n---\n# Content", false)]
    [InlineData("# No front matter here", false)]
    [InlineData("---\nTitle: Some Title\n---\n# Content", false)]
    public void ValidateFrontMatter_ClassifiesCorrectly(string markdown, bool expected)
    {
        Assert.Equal(expected, VSCodeReleaseNotesService.ValidateFrontMatter(markdown));
    }

    // ── TruncateTitle ─────────────────────────────────────────────────────────

    [Fact]
    public void TruncateTitle_ShortTitle_ReturnsUnchanged()
    {
        var title = "Fix terminal rendering";
        Assert.Equal(title, VSCodeReleaseNotesService.TruncateTitle(title));
    }

    [Fact]
    public void TruncateTitle_TruncatesAtFirstPeriod()
    {
        var title = "Fix terminal rendering. This also improves performance for long-running tasks.";
        Assert.Equal("Fix terminal rendering", VSCodeReleaseNotesService.TruncateTitle(title));
    }

    [Fact]
    public void TruncateTitle_VeryLongTitle_TruncatesWithEllipsis()
    {
        var title = new string('A', 100);
        var result = VSCodeReleaseNotesService.TruncateTitle(title);

        Assert.Equal(80, result.Length);
        Assert.EndsWith("...", result);
    }

    // ── GetFirstThursdayOfMonth ───────────────────────────────────────────────

    [Theory]
    [InlineData(2026, 1, 1)]   // Jan 2026: 1st is Thursday
    [InlineData(2026, 2, 5)]   // Feb 2026: 5th is Thursday
    [InlineData(2026, 3, 5)]   // Mar 2026: 5th is Thursday
    public void GetFirstThursdayOfMonth_ReturnsCorrectDate(int year, int month, int expectedDay)
    {
        var result = VSCodeReleaseNotesService.GetFirstThursdayOfMonth(year, month);

        Assert.Equal(new DateOnly(year, month, expectedDay), result);
        Assert.Equal(DayOfWeek.Thursday, result.DayOfWeek);
    }

    // ── GetReleaseMonth ───────────────────────────────────────────────────────

    [Theory]
    [InlineData(2026, 2, 10, 2026, 2)]  // After first Thursday → same month
    [InlineData(2026, 2, 3, 2026, 1)]   // Before first Thursday → previous month
    [InlineData(2026, 2, 5, 2026, 2)]   // On first Thursday → same month
    public void GetReleaseMonth_ReturnsCorrectMonth(int year, int month, int day, int expectedYear, int expectedMonth)
    {
        var result = VSCodeReleaseNotesService.GetReleaseMonth(new DateOnly(year, month, day));

        Assert.Equal(new DateOnly(expectedYear, expectedMonth, 1), result);
    }

    // ── ExtractCategory ───────────────────────────────────────────────────────

    [Fact]
    public void ExtractCategory_WithDash_ExtractsAfterDash()
    {
        var result = VSCodeReleaseNotesService.ExtractCategory("## February 10 - Editor");
        Assert.Equal("Editor", result);
    }

    [Fact]
    public void ExtractCategory_NoDash_ReturnsGeneral()
    {
        var result = VSCodeReleaseNotesService.ExtractCategory("## February 10");
        Assert.Equal("General", result);
    }

    // ── GetProductEdition ────────────────────────────────────────────────────

    [Theory]
    [InlineData("---\nProductEdition: Insiders\n---\n# Content", "Insiders")]
    [InlineData("---\nProductEdition: insiders\n---\n# Content", "insiders")]
    [InlineData("---\nProductEdition: Stable\n---\n# Content", "Stable")]
    [InlineData("# No front matter here", null)]
    [InlineData("---\nTitle: Some Title\n---\n# Content", null)]
    public void GetProductEdition_ReturnsCorrectEdition(string markdown, string? expected)
    {
        Assert.Equal(expected, VSCodeReleaseNotesService.GetProductEdition(markdown));
    }

    // ── ParseStableHighlights ─────────────────────────────────────────────────

    [Fact]
    public void ParseStableHighlights_ExtractsBulletsFromWelcomeSection()
    {
        var markdown = """
            ---
            ProductEdition: Stable
            ---
            # Visual Studio Code 1.116

            ---

            Welcome to the 1.116 release of Visual Studio Code. Here are some highlights:

            * [Agent Debug Logs](#debug-previous-agent-sessions): view logs from previous agent sessions.
            * [Copilot CLI thinking effort](#configure-thinking-effort): configure model thinking effort.

            Happy Coding!

            ---

            ## Agent experience
            """;

        var highlights = VSCodeReleaseNotesService.ParseStableHighlights(markdown);

        Assert.Equal(2, highlights.Count);
        Assert.Equal("Agent Debug Logs: view logs from previous agent sessions.", highlights[0]);
        Assert.Equal("Copilot CLI thinking effort: configure model thinking effort.", highlights[1]);
    }

    [Fact]
    public void ParseStableHighlights_ReturnsEmptyForNoWelcomeSection()
    {
        var markdown = """
            ---
            ProductEdition: Stable
            ---
            # Visual Studio Code 1.116

            ## Agent experience
            """;

        var highlights = VSCodeReleaseNotesService.ParseStableHighlights(markdown);

        Assert.Empty(highlights);
    }

    [Fact]
    public void ParseStableHighlights_ExtractsBulletsFromReleaseHighlightsSection()
    {
        var markdown = """
            ---
            Order: 150
            TOCTitle: October 2026
            ProductEdition: Stable
            Date: 2026-10-07
            ---
            # Visual Studio Code 1.141

            <!-- RELEASE_HIGHLIGHTS_START -->
            ## Release highlights

            This release makes it easier to manage agent sessions.

            * [Clean up worktree storage](#reclaim-storage-from-inactive-worktrees): Reclaim disk space from inactive agent session worktrees.
            * [Cross-platform sandboxing](#sandboxing-on-windows): Run agents in a sandbox on Windows.
            * [Arrange sessions](#arrange-your-sessions): Drag sessions between windows.

            <!-- RELEASE_HIGHLIGHTS_END -->

            ## Agent sessions

            * [Not a highlight](#nope): should be ignored.
            """;

        var highlights = VSCodeReleaseNotesService.ParseStableHighlights(markdown);

        Assert.Equal(3, highlights.Count);
        Assert.Equal("Clean up worktree storage: Reclaim disk space from inactive agent session worktrees.", highlights[0]);
        Assert.Equal("Cross-platform sandboxing: Run agents in a sandbox on Windows.", highlights[1]);
        Assert.Equal("Arrange sessions: Drag sessions between windows.", highlights[2]);
    }

    [Fact]
    public void ParseStableHighlights_StopsAtNextHeadingWhenEndMarkerMissing()
    {
        var markdown = """
            ---
            ProductEdition: Stable
            ---
            # Visual Studio Code 1.141

            ## Release highlights

            * [First](#one): first highlight.

            ## Agent sessions

            * [Not a highlight](#nope): should be ignored.
            """;

        var highlights = VSCodeReleaseNotesService.ParseStableHighlights(markdown);

        Assert.Single(highlights);
        Assert.Equal("First: first highlight.", highlights[0]);
    }

    [Fact]
    public async Task GetReleaseNotesFetchResultForDateRangeAsync_UsesStableVersionPlusOneForInsiders()
    {
        using var httpClient = new HttpClient(new ReleaseNotesHttpMessageHandler());
        var service = new VSCodeReleaseNotesService(httpClient);

        var result = await service.GetReleaseNotesFetchResultForDateRangeAsync(
            new DateOnly(2026, 7, 21),
            new DateOnly(2026, 7, 23));

        Assert.NotNull(result.ReleaseNotes);
        Assert.EndsWith("v1_131.md", result.ReleaseNotes.VersionUrl);
        Assert.EndsWith("v1_130.md", result.StableVersionUrl);
        Assert.Single(result.StableHighlights);
        Assert.Single(result.ReleaseNotes.Features);
    }

    [Theory]
    [InlineData(1, 8, 6)]
    [InlineData(7, 7, 6)]
    [InlineData(1, 6, 0)]
    [InlineData(8, 15, 0)]
    public async Task GetReleaseNotesFetchResultForDateRangeAsync_IncludesModernStableHighlightsOnlyInWindow(
        int startDay, int endDay, int expectedHighlights)
    {
        const string stableMarkdown = """
            ---
            ProductEdition: Stable
            Date: 2026-10-07
            ---
            # Visual Studio Code 1.141

            <!-- RELEASE_HIGHLIGHTS_START -->
            ## Release highlights

            Highlights for this release.

            * [Clean up worktree storage](#worktrees): Remove inactive worktrees.
            * [Cross-platform sandboxing](#sandboxing): Restrict file and network access.
            * [Arrange sessions](#grid): View sessions side by side.
            * [Continue external sessions](#external): Resume local conversations.
            * [Block pasting](#paste): Paste rows across successive lines.
            * [Multiple GitHub Enterprise instances](#enterprise): Use multiple enterprise accounts.
            <!-- RELEASE_HIGHLIGHTS_END -->

            ## Agent loop
            ### Worktree cleanup
            Remove inactive worktrees on demand.

            * [Not a highlight](#other): Ignore this bullet.
            """;
        const string insidersMarkdown = """
            ---
            ProductEdition: Insiders
            ---
            ## October 8, 2026 - Editor
            * Merge highlights from multiple providers.
            """;
        using var httpClient = new HttpClient(new ReleaseNotesHttpMessageHandler(
            141, stableMarkdown, insidersMarkdown));
        var service = new VSCodeReleaseNotesService(httpClient);

        var result = await service.GetReleaseNotesFetchResultForDateRangeAsync(
            new DateOnly(2026, 10, startDay),
            new DateOnly(2026, 10, endDay));

        Assert.Equal(expectedHighlights, result.StableHighlights.Count);
        if (expectedHighlights > 0)
        {
            Assert.EndsWith("v1_141.md", result.StableVersionUrl);
            Assert.Equal("Clean up worktree storage: Remove inactive worktrees.", result.StableHighlights[0]);
            Assert.Equal("Multiple GitHub Enterprise instances: Use multiple enterprise accounts.", result.StableHighlights[5]);
            Assert.Single(result.StableFeatureCallouts);
        }
        else
        {
            Assert.Null(result.StableVersionUrl);
            Assert.Empty(result.StableFeatureCallouts);
        }

        if (startDay <= 8 && endDay >= 8)
        {
            Assert.NotNull(result.ReleaseNotes);
            Assert.EndsWith("v1_142.md", result.ReleaseNotes.VersionUrl);
            Assert.Single(result.ReleaseNotes.Features);
        }
        else
        {
            Assert.Null(result.ReleaseNotes);
        }
    }

    private sealed class ReleaseNotesHttpMessageHandler(
        int stableVersion = 130,
        string? stableMarkdown = null,
        string? insidersMarkdown = null) : HttpMessageHandler
    {
        private const string StableReleaseNotes = """
            ---
            ProductEdition: Stable
            Date: 2026-07-22
            ---
            Welcome to the 1.130 release of Visual Studio Code.

            * [Agent host](#agent-host): Run sessions in a dedicated process.

            Happy Coding!
            """;

        private const string InsidersReleaseNotes = """
            ---
            ProductEdition: Insiders
            ---
            ## July 22, 2026 - Agents
            * Add support for selecting a folder from a quick pick in the new session view.
            """;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();

            if (url == "https://code.visualstudio.com/updates")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    RequestMessage = new HttpRequestMessage(HttpMethod.Head, $"https://code.visualstudio.com/updates/v1_{stableVersion}")
                });
            }

            var content = url.EndsWith($"v1_{stableVersion}.md", StringComparison.Ordinal)
                ? stableMarkdown ?? StableReleaseNotes
                : url.EndsWith($"v1_{stableVersion + 1}.md", StringComparison.Ordinal)
                    ? insidersMarkdown ?? InsidersReleaseNotes
                    : null;

            if (content == null)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(content)
            });
        }
    }
}
