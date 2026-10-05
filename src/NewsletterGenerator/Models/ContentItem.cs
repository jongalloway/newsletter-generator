namespace NewsletterGenerator.Models;

internal enum ContentItemType
{
    BlogPost,
    ChangelogEntry,
    Video,
    Release
}

internal enum ContentCategory
{
    None,
    DotNet,
    AgentDevelopmentAndAzure,
    GitHubAndDevTools,
    OtherDeveloperUpdates
}

internal sealed record ContentSourceGroup(
    string Name,
    ContentCategory Category,
    IReadOnlyList<ReleaseEntry> Entries);

internal sealed record ContentItem(
    string Id,
    ContentItemType Type,
    ContentCategory Category,
    string SourceName,
    string Title,
    DateOnly PublishedAt,
    string Content,
    string Url);

internal sealed record CuratedContentItem(
    string ContentItemId,
    string Summary);

internal sealed record CuratedSection(
    string Summary,
    CuratedContentItem[] Items);
