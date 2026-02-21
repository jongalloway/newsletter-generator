namespace NewsletterGenerator.Models;

public record ReleaseEntry(
    string Version,
    DateOnly PublishedAt,
    string PlainText,
    string Url);
