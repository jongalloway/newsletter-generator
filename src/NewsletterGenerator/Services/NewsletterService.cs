using System.Text;
using GitHub.Copilot.SDK;
using NewsletterGenerator.Models;
using Spectre.Console;

namespace NewsletterGenerator.Services;

public class NewsletterService
{
    public async Task<string> GenerateReleaseSectionAsync(
        List<ReleaseEntry> cliReleases,
        List<ReleaseEntry> sdkReleases,
        List<ReleaseEntry> changelogEntries,
        List<ReleaseEntry> blogEntries,
        DateTimeOffset weekStart,
        DateTimeOffset weekEnd)
    {
        AnsiConsole.MarkupLine("[grey]Starting Copilot CLI...[/]");
        await using var client = new CopilotClient();
        await client.StartAsync();

        AnsiConsole.MarkupLine("[grey]Creating session...[/]");
        await using var session = await client.CreateSessionAsync(new SessionConfig
        {
            SystemMessage = new SystemMessageConfig
            {
                Mode = SystemMessageMode.Replace,
                Content = """
                    You are a technical newsletter editor for a GitHub Copilot developer community.
                    Your job is to curate and summarize release notes and blog posts into polished newsletter content.

                    Curation rules — apply strictly:
                    - INCLUDE: new features, capabilities, and developer-facing improvements.
                    - IGNORE: version bumps, dependency upgrades, internal refactors, test additions,
                      CI/CD changes, formatting fixes, and anything that doesn't affect how developers
                      use the product.
                    - COMBINE: group closely related changes from multiple releases into a single bullet.
                    - PRIORITIZE: pick the 4–6 most impactful developer-facing highlights per project.
                    - CROSS-REFERENCE: where a changelog entry or blog post provides useful additional
                      context for a release feature, weave it in — but do not duplicate content.

                    Write concise, well-organized Markdown.
                    Output ONLY the requested Markdown — no preamble, no commentary, no code fences.
                    """
            }
        });

        var prompt = BuildPrompt(cliReleases, sdkReleases, changelogEntries, blogEntries, weekStart, weekEnd);

        var response = new StringBuilder();
        var tcs = new TaskCompletionSource<string>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        session.On(evt =>
        {
            switch (evt)
            {
                case AssistantMessageEvent msg:
                    response.Clear();
                    response.Append(msg.Data.Content);
                    break;
                case SessionIdleEvent:
                    tcs.TrySetResult(response.ToString());
                    break;
                case SessionErrorEvent err:
                    tcs.TrySetException(new InvalidOperationException(err.Data.Message));
                    break;
            }
        });

        AnsiConsole.MarkupLine("[grey]Sending prompt to Copilot...[/]");
        await session.SendAsync(new MessageOptions { Prompt = prompt });
        return await tcs.Task;
    }

    private static string BuildPrompt(
        List<ReleaseEntry> cliReleases,
        List<ReleaseEntry> sdkReleases,
        List<ReleaseEntry> changelogEntries,
        List<ReleaseEntry> blogEntries,
        DateTimeOffset weekStart,
        DateTimeOffset weekEnd)
    {
        var sb = new StringBuilder();

        sb.AppendLine($"""
            Generate the "Project updates" section for a GitHub Copilot CLI & SDK weekly newsletter
            covering the week of {weekStart:MMMM d} to {weekEnd:MMMM d, yyyy}.

            You have four sources of information below:
            1. GitHub Copilot CLI release notes (from github/copilot-cli Atom feed)
            2. GitHub Copilot SDK release notes (from github/copilot-sdk Atom feed)
            3. GitHub Changelog entries labeled "copilot" (from github.blog/changelog)
            4. GitHub Blog posts tagged with Copilot or GitHub CLI (from github.blog)

            Prioritize release notes as the primary source. Use changelog and blog content to:
            - Add context or links for major features mentioned in release notes.
            - Surface significant announcements that aren't covered by release notes
              (e.g. new model availability, coding agent updates, VS Code Copilot releases).

            Do NOT include changelog/blog items that are unrelated to the CLI or SDK.
            Do NOT duplicate content — if a changelog entry and a release note cover the same feature, mention it once.
            Skip version bumps, dependency upgrades, internal refactors, test changes, and
            anything that doesn't change how a developer uses the product.
            Combine related changes from different releases into a single bullet where it reads better.
            Pick the 4–6 most impactful developer-facing highlights per project for the week.

            Output ONLY the Markdown below (no extra text). Follow this exact structure:

            ---
            ## Project updates

            ### GitHub Copilot CLI

            <SUMMARY: 4–6 curated themed bullets spanning ALL CLI releases and relevant changelog/blog items this week.
             Each bullet groups related changes under one theme.
             Format: - <emoji> **<Short Category Label>** — <concise description combining related changes>
             Example: - 🧠 **Models & context** — Adds Claude Opus 4.6 Fast (Preview) and 1M-token context support; expanded ACP model metadata.
             Example: - 🧩 **Plugin & skill flexibility** — Uppercase names, bundled LSP configs, auto-load from `.agents/skills`, and comma-separated tool lists.>

            ## Releases

            <one sub-section per version, curated developer-facing highlights only — skip noise>
            ### vX.X.X (YYYY-MM-DD)

            - bullet point

            Release notes: [Releases - github/copilot-cli](https://github.com/github/copilot-cli/releases)

            --

            ### GitHub Copilot SDK

            <SUMMARY: 4–6 curated themed bullets spanning ALL SDK releases and relevant changelog/blog items this week.
             Each bullet groups related changes under one theme.
             Format: - <emoji> **<Short Category Label>** — <concise description combining related changes>
             Example: - ♾️ **Infinite sessions** — Long-running conversations that automatically compact context so sessions never hit token limits.
             Example: - 🪝 **Hooks & user input** — Extensible lifecycle hooks and interactive user-input flows across all SDK languages.>

            ## Releases

            <one sub-section per version, curated developer-facing highlights only — skip noise>
            ### vX.X.X (YYYY-MM-DD)

            - bullet point

            Release notes: [Releases - github/copilot-sdk](https://github.com/github/copilot-sdk/releases)
            --

            Here is the raw source material for this week. Summarize from it — do not copy it verbatim:

            """);

        AppendReleases(sb, "GitHub Copilot CLI release notes", cliReleases);
        AppendReleases(sb, "GitHub Copilot SDK release notes", sdkReleases);
        AppendBlogEntries(sb, "GitHub Changelog (Copilot label)", changelogEntries);
        AppendBlogEntries(sb, "GitHub Blog (Copilot/CLI posts)", blogEntries);

        return sb.ToString();
    }

    private static void AppendReleases(StringBuilder sb, string sectionTitle, List<ReleaseEntry> releases)
    {
        sb.AppendLine($"## {sectionTitle}");
        sb.AppendLine();

        if (releases.Count == 0)
        {
            sb.AppendLine("_(No new releases this week.)_");
        }
        else
        {
            foreach (var r in releases)
            {
                sb.AppendLine($"### {r.Version} ({r.PublishedAt:yyyy-MM-dd})");
                sb.AppendLine();
                sb.AppendLine(r.PlainText);
                sb.AppendLine();
            }
        }

        sb.AppendLine();
    }

    private static void AppendBlogEntries(StringBuilder sb, string sectionTitle, List<ReleaseEntry> entries)
    {
        sb.AppendLine($"## {sectionTitle}");
        sb.AppendLine();

        if (entries.Count == 0)
        {
            sb.AppendLine("_(No entries this week.)_");
        }
        else
        {
            foreach (var e in entries)
            {
                sb.AppendLine($"### [{e.Version}]({e.Url}) ({e.PublishedAt:yyyy-MM-dd})");
                sb.AppendLine();
                if (!string.IsNullOrWhiteSpace(e.PlainText))
                    sb.AppendLine(e.PlainText);
                sb.AppendLine();
            }
        }

        sb.AppendLine();
    }
}
