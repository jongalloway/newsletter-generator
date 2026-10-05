using NewsletterGenerator.Models;
using NewsletterGenerator.Services;

namespace NewsletterGenerator.Tests;

public class CuratedSectionTests
{
    private static readonly ReleaseEntry FirstEntry = new(
        "Typed structured output",
        new DateOnly(2026, 9, 18),
        "Adds typed structured output to the SDK.",
        "https://github.blog/changelog/typed-output");

    private static readonly ReleaseEntry SecondEntry = new(
        "Copilot CLI update",
        new DateOnly(2026, 9, 19),
        "Adds a new CLI workflow.",
        "https://github.blog/copilot-cli-update");

    [Fact]
    public void CreateContentItems_AssignsNeutralSequentialIds()
    {
        List<ContentSourceGroup> sources =
        [
            new("GitHub Blog", ContentCategory.GitHubAndDevTools, [FirstEntry, SecondEntry])
        ];
        var items = NewsletterService.CreateContentItems(
            sources,
            new HashSet<string>());

        Assert.Collection(
            items,
            item =>
            {
                Assert.Equal("item-001", item.Id);
                Assert.Equal(ContentItemType.BlogPost, item.Type);
                Assert.Equal(ContentCategory.GitHubAndDevTools, item.Category);
                Assert.Equal("GitHub Blog", item.SourceName);
            },
            item =>
            {
                Assert.Equal("item-002", item.Id);
                Assert.Equal("GitHub Blog", item.SourceName);
            });
    }

    [Theory]
    [InlineData("https://devblogs.microsoft.com/dotnet/example", (int)ContentCategory.DotNet)]
    [InlineData("https://azure.microsoft.com/updates?id=123", (int)ContentCategory.AgentDevelopmentAndAzure)]
    [InlineData("https://devblogs.microsoft.com/agent-framework/example", (int)ContentCategory.AgentDevelopmentAndAzure)]
    [InlineData("https://github.blog/changelog/example", (int)ContentCategory.GitHubAndDevTools)]
    [InlineData("https://devblogs.microsoft.com/typescript/example", (int)ContentCategory.GitHubAndDevTools)]
    [InlineData("https://developer.microsoft.com/en-us/example", (int)ContentCategory.OtherDeveloperUpdates)]
    [InlineData("not-a-url", (int)ContentCategory.OtherDeveloperUpdates)]
    public void ResolveContentCategory_UsesUrlWhenFeedIsAggregate(
        string url,
        int expected)
    {
        Assert.Equal((ContentCategory)expected, NewsletterService.ResolveContentCategory(url));
    }

    [Fact]
    public void CreateContentItems_PreservesSpecificFeedCategoryAndDeduplicatesUrl()
    {
        var duplicateFromGeneralFeed = FirstEntry with { Version = "General feed copy" };
        List<ContentSourceGroup> sources =
        [
            new(".NET Blog", ContentCategory.DotNet, [FirstEntry]),
            new("Microsoft Developer Blog", ContentCategory.GitHubAndDevTools, [duplicateFromGeneralFeed, SecondEntry])
        ];

        var items = NewsletterService.CreateContentItems(sources, new HashSet<string>());

        Assert.Collection(
            items,
            item =>
            {
                Assert.Equal(FirstEntry.Version, item.Title);
                Assert.Equal(ContentCategory.DotNet, item.Category);
                Assert.Equal(".NET Blog", item.SourceName);
            },
            item => Assert.Equal(SecondEntry.Url, item.Url));
    }

    [Fact]
    public void CreateContentItems_DerivesCategoryForAggregateFeeds()
    {
        var azureEntry = FirstEntry with { Url = "https://azure.microsoft.com/updates?id=123" };
        var uncategorizedEntry = SecondEntry with { Url = "https://developer.microsoft.com/example" };
        List<ContentSourceGroup> sources =
        [
            new("Microsoft Developer Changelog", ContentCategory.None, [azureEntry, uncategorizedEntry])
        ];

        var items = NewsletterService.CreateContentItems(sources, new HashSet<string>());

        Assert.Collection(
            items,
            item => Assert.Equal(ContentCategory.AgentDevelopmentAndAzure, item.Category),
            item => Assert.Equal(ContentCategory.OtherDeveloperUpdates, item.Category));
    }

    [Fact]
    public void BuildCuratedContentPrompt_UsesIdsWithoutSendingUrls()
    {
        var items = CreateUncategorizedContentItems(FirstEntry);

        var prompt = NewsletterService.BuildCuratedContentPrompt("Select one item.", items);

        Assert.Contains("BlogPost | GitHub Blog", prompt);
        Assert.Contains("[item-001] 2026-09-18 | Typed structured output", prompt);
        Assert.Contains(FirstEntry.PlainText, prompt);
        Assert.DoesNotContain(FirstEntry.Url, prompt);
        Assert.DoesNotContain("Output exactly this format", prompt);
    }

    [Fact]
    public void RenderCuratedSection_UsesAuthoritativeUrlsAndDeterministicMarkdown()
    {
        var items = CreateUncategorizedContentItems(FirstEntry, SecondEntry);
        var section = new CuratedSection(
            "The SDK and CLI both shipped developer-facing updates.",
            [
                new CuratedContentItem("item-002", "Adds a new workflow"),
                new CuratedContentItem("item-001", "Adds schema-backed responses.")
            ]);

        var markdown = NewsletterService.RenderCuratedSection(
            "Developer Blogs",
            section,
            items,
            minimumItems: 2,
            maximumItems: 2,
            itemPrefix: "-");

        Assert.Equal(
            """
            ## Developer Blogs

            The SDK and CLI both shipped developer-facing updates.

            - **[Copilot CLI update](https://github.blog/copilot-cli-update)** - Adds a new workflow.
            - **[Typed structured output](https://github.blog/changelog/typed-output)** - Adds schema-backed responses.
            """,
            markdown);
    }

    [Fact]
    public void RenderCuratedSection_UsesCuratedDisplayTitleWithAuthoritativeUrl()
    {
        var sourceEntry = FirstEntry with
        {
            Version = "[Launched] Generally Available: Typed structured output"
        };
        var items = CreateUncategorizedContentItems(sourceEntry);
        var section = new CuratedSection(
            "The SDK shipped an update.",
            [
                new CuratedContentItem(
                    "item-001",
                    "Typed structured output is generally available",
                    "Adds schema-backed responses for SDK sessions")
            ]);

        var markdown = NewsletterService.RenderCuratedSection(
            "Developer Blogs",
            section,
            items,
            minimumItems: 1,
            maximumItems: 1,
            itemPrefix: "-");

        Assert.Contains(
            "**[Typed structured output is generally available](https://github.blog/changelog/typed-output)**",
            markdown);
        Assert.DoesNotContain("[Launched]", markdown);
    }

    [Fact]
    public void RenderCuratedSection_GroupsBlogPostsInDeterministicCategoryOrder()
    {
        List<ContentSourceGroup> sources =
        [
            new(".NET Blog", ContentCategory.DotNet, [FirstEntry]),
            new("GitHub Blog", ContentCategory.GitHubAndDevTools, [SecondEntry])
        ];
        var items = NewsletterService.CreateContentItems(sources, new HashSet<string>());
        var section = new CuratedSection(
            "Two areas shipped updates.",
            [
                new CuratedContentItem("item-002", "Adds a new workflow"),
                new CuratedContentItem("item-001", "Adds schema-backed responses")
            ]);

        var markdown = NewsletterService.RenderCuratedSection(
            "Developer Blogs",
            section,
            items,
            minimumItems: 2,
            maximumItems: 2,
            itemPrefix: "-");

        Assert.Equal(
            """
            ## Developer Blogs

            Two areas shipped updates.

            ### .NET

            - **[Typed structured output](https://github.blog/changelog/typed-output)** - Adds schema-backed responses.

            ### GitHub and DevTools

            - **[Copilot CLI update](https://github.blog/copilot-cli-update)** - Adds a new workflow.
            """,
            markdown);
    }

    [Theory]
    [InlineData(
        "[Launched] Generally Available: Vector search and vector indexes in Azure SQL",
        "Generally Available: Vector search and vector indexes in Azure SQL")]
    [InlineData(
        "Retirement: Support ends November 10, 2026-upgrade your apps",
        "Retirement: Support ends November 10, 2026 - upgrade your apps")]
    [InlineData(
        "Your Database Belongs in Source Control Too #databases #visualstudio",
        "Your Database Belongs in Source Control Too")]
    [InlineData(
        "🎖️ Captain's Training: Master Copilot Studio",
        "Captain's Training: Master Copilot Studio")]
    public void NormalizeDisplayTitle_RemovesFeedFormattingArtifacts(string title, string expected)
    {
        Assert.Equal(expected, NewsletterService.NormalizeDisplayTitle(title));
    }

    [Fact]
    public void RenderCuratedSection_TruncatesItemsAboveMaximum()
    {
        var thirdEntry = FirstEntry with
        {
            Version = "Third update",
            Url = "https://github.blog/third-update"
        };
        var items = CreateUncategorizedContentItems(FirstEntry, SecondEntry, thirdEntry);
        var section = new CuratedSection(
            "Summary.",
            [
                new CuratedContentItem("item-001", "First"),
                new CuratedContentItem("item-002", "Second"),
                new CuratedContentItem("item-003", "Third")
            ]);

        var markdown = NewsletterService.RenderCuratedSection(
            "Developer Blogs",
            section,
            items,
            minimumItems: 1,
            maximumItems: 2,
            itemPrefix: "-");

        Assert.Contains(FirstEntry.Url, markdown);
        Assert.Contains(SecondEntry.Url, markdown);
        Assert.DoesNotContain(thirdEntry.Url, markdown);
    }

    [Fact]
    public void RenderCuratedSection_KeepsVideoEntriesCompact()
    {
        var items = CreateUncategorizedContentItems([FirstEntry, SecondEntry], ContentItemType.Video);
        var section = new CuratedSection(
            "Summary.",
            [
                new CuratedContentItem("item-001", "First"),
                new CuratedContentItem("item-002", "Second")
            ]);

        var markdown = NewsletterService.RenderCuratedSection(
            "Developer Videos",
            section,
            items,
            minimumItems: 2,
            maximumItems: 2,
            itemPrefix: "📺");

        Assert.Contains(
            $")** - First.{Environment.NewLine}📺 **[Copilot CLI update]",
            markdown);
    }

    [Fact]
    public void RenderCuratedSection_RejectsItemsBelowMinimum()
    {
        var items = CreateUncategorizedContentItems(FirstEntry);
        var section = new CuratedSection("Summary.", []);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            NewsletterService.RenderCuratedSection(
                "Developer Blogs",
                section,
                items,
                minimumItems: 1,
                maximumItems: 2,
                itemPrefix: "-"));

        Assert.Contains("expected at least 1", exception.Message);
    }

    [Fact]
    public void RenderCuratedSection_RejectsUnknownContentItem()
    {
        var items = CreateUncategorizedContentItems(FirstEntry);
        var section = new CuratedSection(
            "Summary.",
            [new CuratedContentItem("item-999", "Description")]);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            NewsletterService.RenderCuratedSection(
                "Developer Blogs",
                section,
                items,
                minimumItems: 1,
                maximumItems: 1,
                itemPrefix: "-"));

        Assert.Contains("unknown content item item-999", exception.Message);
    }

    [Fact]
    public void RenderCuratedSection_RejectsDuplicateContentItems()
    {
        var items = CreateUncategorizedContentItems([FirstEntry], ContentItemType.Video);
        var section = new CuratedSection(
            "Summary.",
            [
                new CuratedContentItem("item-001", "First description"),
                new CuratedContentItem("item-001", "Duplicate description")
            ]);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            NewsletterService.RenderCuratedSection(
                "Developer Videos",
                section,
                items,
                minimumItems: 2,
                maximumItems: 2,
                itemPrefix: "📺"));

        Assert.Contains("item-001 more than once", exception.Message);
    }

    private static List<ContentItem> CreateUncategorizedContentItems(
        params ReleaseEntry[] entries) =>
        CreateUncategorizedContentItems(entries, ContentItemType.BlogPost);

    private static List<ContentItem> CreateUncategorizedContentItems(
        IReadOnlyList<ReleaseEntry> entries,
        ContentItemType type) =>
        entries.Select((entry, index) => new ContentItem(
            $"item-{index + 1:D3}",
            type,
            ContentCategory.None,
            type == ContentItemType.Video ? "YouTube GitHub" : "GitHub Blog",
            entry.Version,
            entry.PublishedAt,
            entry.PlainText,
            entry.Url)).ToList();
}
