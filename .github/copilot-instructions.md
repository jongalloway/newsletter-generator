# Copilot Instructions

## Build & Run

```bash
# Build
dotnet build

# Run (from project dir)
cd src/NewsletterGenerator && dotnet run

# Run with options
dotnet run --clear-cache      # Force regenerate all sections
dotnet run 7                  # Custom: last 7 days
dotnet run --clear-cache 14   # Combine flags
```

There are no tests in this project. The CI workflow (`dotnet.yml`) runs `dotnet test` but it's a no-op.

## Architecture

This is a .NET 10 console app that generates a weekly markdown newsletter about GitHub Copilot CLI & SDK updates. It uses top-level statements in `Program.cs` as the entry point (no `Main` method).

### Pipeline

1. **Feed ingestion** — `AtomFeedService` fetches Atom/RSS feeds (CLI releases, SDK releases, GitHub Changelog, GitHub Blog), filters by date range, converts HTML to plain text, and strips low-value lines (dependency bumps, CI changes, attributions) via regex.
2. **AI summarization** — `NewsletterService` sends feed data to the GitHub Copilot SDK (`CopilotClient`/`CopilotSession`) with carefully tuned system prompts to generate three newsletter sections: News & Announcements, Project Updates, and a Welcome summary.
3. **Caching** — `CacheService` stores generated summaries in `.cache/` as JSON files, keyed by SHA256 hash of the source data. Sections are only regenerated when their source data changes.
4. **Output** — The assembled newsletter is written to `output/newsletter-YYYY-MM-DD.md`. Em-dashes are replaced with hyphens before writing.

### Key Dependencies

- **GitHub.Copilot.SDK** — AI summarization via `CopilotClient` → `CopilotSession` → event-driven response pattern (`AssistantMessageEvent`, `SessionIdleEvent`, `SessionErrorEvent`)
- **Spectre.Console** — Terminal UI (progress spinners, tables, figlet text)
- **System.ServiceModel.Syndication** — Atom/RSS feed parsing

### VS Code Release Notes (secondary feature)

`VSCodeReleaseNotesService` parses raw markdown from `microsoft/vscode-docs` on GitHub to extract VS Code Insiders release notes. It resolves the current version by following the `aka.ms/vscode/updates/insiders` redirect, then parses date-headed sections from the markdown files.

## Conventions

- **File-scoped namespaces** — All classes use `namespace X;` syntax.
- **Records for models** — Data types are positional records (e.g., `record ReleaseEntry(string Version, ...)`).
- **Source-generated regex** — Use `[GeneratedRegex]` with `partial` methods instead of `new Regex()`. See `AtomFeedService` for examples.
- **Nullable enabled** — The project has `<Nullable>enable</Nullable>`. Respect nullability annotations.
- **Constructor DI via primary constructors** — Services use primary constructor parameters (e.g., `class AtomFeedService(HttpClient? httpClient = null)`).
- **AI prompt tone** — System prompts enforce a professional, non-marketing tone. Never use hyperbolic language like "groundbreaking" or "game-changing" in prompts. The audience is skeptical internal engineers.
- **Collection expressions** — Uses C# 12 collection expressions (`List<T> items = [];`).
