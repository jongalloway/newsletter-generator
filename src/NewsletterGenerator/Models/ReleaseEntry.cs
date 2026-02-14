namespace NewsletterGenerator.Models;

public record ReleaseEntry(
    string Version,
    DateTimeOffset PublishedAt,
    string PlainText,
    string Url);
