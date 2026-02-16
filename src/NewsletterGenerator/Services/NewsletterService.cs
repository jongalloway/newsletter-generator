using System.Text;
using GitHub.Copilot.SDK;
using NewsletterGenerator.Models;
using Spectre.Console;

namespace NewsletterGenerator.Services;

public class NewsletterService
{
    public async Task<string> GenerateWelcomeSummaryAsync(
        string newsSection,
        string releaseSummaryBullets,
        DateTimeOffset weekStart,
        DateTimeOffset weekEnd)
    {
        if (string.IsNullOrEmpty(newsSection) && string.IsNullOrEmpty(releaseSummaryBullets))
            return "It's been another week of updates for GitHub Copilot CLI & SDK!";

        AnsiConsole.MarkupLine("[grey]Generating Welcome summary...[/]");
        await using var client = new CopilotClient();
        await client.StartAsync();

        await using var session = await client.CreateSessionAsync(new SessionConfig
        {
            SystemMessage = new SystemMessageConfig
            {
                Mode = SystemMessageMode.Replace,
                Content = """
                    You are a technical newsletter editor writing for an internal developer audience at Microsoft.
                    Your job is to create a concise, factual summary of the week's updates.

                    TONE GUIDELINES:
                    - Professional and informative, not marketing-y or promotional
                    - Measured enthusiasm — not every feature is "groundbreaking" or "game-changing"
                    - Factual rather than hyperbolic
                    - Reserve strong language for truly significant features
                    - This audience is skeptical of over-hyped marketing speak
                    - Write like you're informing colleagues, not selling a product

                    Keep it to 2-3 sentences. Focus on what actually shipped and what developers can use.
                    Output ONLY the paragraph text — no greeting, no markdown, no preamble.
                    """
            }
        });

        var prompt = $"""
            Write an opening paragraph for the GitHub Copilot CLI & SDK weekly newsletter
            covering {weekStart:MMMM d} to {weekEnd:MMMM d, yyyy}.

            This is an INTERNAL developer newsletter. Write in a factual, professional tone.
            Avoid marketing language like "groundbreaking", "game-changing", "revolutionize", etc.
            Be informative and measured — this goes out every week, so maintain credibility.

            Summarize the week's highlights in 2-3 factual sentences.

            Source material (already condensed highlights):

            {(!string.IsNullOrEmpty(newsSection) ? $"NEWS AND ANNOUNCEMENTS:\n{newsSection}\n\n" : "")}
            {(!string.IsNullOrEmpty(releaseSummaryBullets) ? $"RELEASE HIGHLIGHTS:\n{releaseSummaryBullets}\n\n" : "")}

            Example (note the measured tone - no hype):
            "This week brings several updates to GitHub Copilot CLI & SDK. The CLI now integrates with VS Code, GPT-5.3-Codex is available as a model option, and the SDK adds infinite session support for long-running conversations. Details below."

            Generate ONLY the paragraph text (no markdown, no greeting).
            """;

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

        await session.SendAsync(new MessageOptions { Prompt = prompt });
        return await tcs.Task;
    }

    public async Task<string> GenerateNewsAndAnnouncementsAsync(
        List<ReleaseEntry> changelogEntries,
        List<ReleaseEntry> blogEntries,
        DateTimeOffset weekStart,
        DateTimeOffset weekEnd)
    {
        if (changelogEntries.Count == 0 && blogEntries.Count == 0)
            return string.Empty;

        AnsiConsole.MarkupLine("[grey]Generating News and Announcements...[/]");
        await using var client = new CopilotClient();
        await client.StartAsync();

        await using var session = await client.CreateSessionAsync(new SessionConfig
        {
            SystemMessage = new SystemMessageConfig
            {
                Mode = SystemMessageMode.Replace,
                Content = """
                    You are a technical newsletter editor for an internal Microsoft developer community.
                    Your job is to curate and write the "News and Announcements" section from changelog
                    and blog entries.

                    CRITICAL FILTERING RULES:
                    - This newsletter is ONLY for GitHub Copilot CLI and GitHub Copilot SDK users
                    - EXCLUDE: General IDE features, VS Code extensions, JetBrains plugins, general Copilot 
                      features that don't involve CLI or SDK
                    - EXCLUDE: General coding agent updates unless they specifically mention CLI/SDK integration
                    - INCLUDE ONLY: Items that directly impact CLI or SDK users (new models available in CLI,
                      network configuration changes for CLI, SDK updates, CLI-specific features)
                    - If unsure whether something is relevant, lean toward excluding it
                    
                    Focus on:
                    - New model availability specifically in CLI/SDK
                    - CLI or SDK-specific feature launches
                    - Network, auth, or policy changes affecting CLI/SDK
                    - Educational content specifically about CLI/SDK (courses, tutorials)
                    - Breaking changes or important migration notices for CLI/SDK

                    TONE GUIDELINES:
                    - Professional and informative, not marketing-y
                    - Factual rather than promotional
                    - Avoid hyperbole like "groundbreaking", "revolutionary", "game-changing"
                    - This is an internal dev newsletter for skeptical engineers
                    - Save enthusiasm for truly significant updates
                    - Write like you're informing colleagues, not selling a product

                    Keep it concise but informative. If there's nothing relevant to CLI/SDK users, return an empty section.
                    Output ONLY Markdown — no preamble, no code fences.
                    """
            }
        });

        var prompt = BuildNewsPrompt(changelogEntries, blogEntries, weekStart, weekEnd);

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

        await session.SendAsync(new MessageOptions { Prompt = prompt });
        return await tcs.Task;
    }

    public async Task<string> GenerateReleaseSectionAsync(
        List<ReleaseEntry> cliReleases,
        List<ReleaseEntry> sdkReleases,
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
                    Your job is to aggressively curate and summarize release notes into polished newsletter content.

                    Curation rules — apply AGGRESSIVELY:
                    - CONDENSE RUTHLESSLY: Combine 5-10 related changes into a single thematic bullet.
                    - IGNORE: version bumps, dependency upgrades, internal refactors, test additions,
                      CI/CD changes, formatting fixes, bug fixes, and anything that doesn't add new
                      capabilities or significantly change developer workflows.
                    - COMBINE: Group keyboard shortcuts together, group MCP changes together, group
                      performance improvements together. DO NOT list them individually.
                    - HARD LIMIT: Maximum 6 bullets per release version, preferably 3-5. For major releases
                      with many changes, combine even more aggressively.
                    - THEMES OVER DETAILS: Focus on high-level themes (e.g., "Terminal UX improvements")
                      rather than individual features (e.g., listing every keyboard shortcut).
                    - CROSS-REFERENCE: where a changelog entry or blog post provides useful context,
                      weave it in — but do not duplicate content.

                    Write concise, well-organized Markdown.
                    Output ONLY the requested Markdown — no preamble, no commentary, no code fences.
                    """
            }
        });

        var prompt = BuildPrompt(cliReleases, sdkReleases, weekStart, weekEnd);

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
        DateTimeOffset weekStart,
        DateTimeOffset weekEnd)
    {
        var sb = new StringBuilder();

        sb.AppendLine($"""
            Generate the "Project updates" section for a GitHub Copilot CLI & SDK weekly newsletter
            covering the week of {weekStart:MMMM d} to {weekEnd:MMMM d, yyyy}.

            You have two sources of information below:
            1. GitHub Copilot CLI release notes (from github/copilot-cli Atom feed) - {cliReleases.Count} releases
            2. GitHub Copilot SDK release notes (from github/copilot-sdk Atom feed) - {sdkReleases.Count} releases

            Focus ONLY on the release notes. Do NOT include changelog or blog items in this section.
            
            CRITICAL SUMMARIZATION RULES:
            - Each release version should have NO MORE THAN 6 bullets (ideally 3-5)
            - Combine related items aggressively (e.g., all keyboard shortcuts → one bullet, all MCP changes → one bullet)
            - Skip bug fixes, internal improvements, and minor polish unless groundbreaking
            - Focus on new capabilities and major workflow changes only
            - If a release has 20+ changes, combine them into 3-5 thematic bullets

            Pick the 4–6 most impactful developer-facing highlights per project for the week.

            Output ONLY the Markdown below (no extra text). Follow this exact structure:

            ---
            ## Project updates

            ### GitHub Copilot CLI

            {(cliReleases.Count > 1 ? @"<SUMMARY: 4–6 curated themed bullets spanning ALL CLI releases and relevant changelog/blog items this week.
             Each bullet groups related changes under one theme.
             Format: - <emoji> **<Short Category Label>** — <concise description combining related changes>
             Example: - 🧠 **Models & context** — Adds Claude Opus 4.6 Fast (Preview) and 1M-token context support; expanded ACP model metadata.
             Example: - 🧩 **Plugin & skill flexibility** — Uppercase names, bundled LSP configs, auto-load from `.agents/skills`, and comma-separated tool lists.>

            " : "")}## Releases

            <one sub-section per version with MAXIMUM 6 bullets (ideally 3-5), highly condensed thematic summaries>
            ### vX.X.X (YYYY-MM-DD)

            - Combined thematic bullet covering multiple related changes
            - Another thematic bullet (e.g., "Terminal UX improvements" covering 10+ individual changes)

            Release notes: [Releases - github/copilot-cli](https://github.com/github/copilot-cli/releases)

            --

            ### GitHub Copilot SDK

            {(sdkReleases.Count > 1 ? @"<SUMMARY: 4–6 curated themed bullets spanning ALL SDK releases and relevant changelog/blog items this week.
             Each bullet groups related changes under one theme.
             Format: - <emoji> **<Short Category Label>** — <concise description combining related changes>
             Example: - ♾️ **Infinite sessions** — Long-running conversations that automatically compact context so sessions never hit token limits.
             Example: - 🪝 **Hooks & user input** — Extensible lifecycle hooks and interactive user-input flows across all SDK languages.>

            " : "")}## Releases

            <one sub-section per version with MAXIMUM 6 bullets (ideally 3-5), highly condensed thematic summaries>
            ### vX.X.X (YYYY-MM-DD)

            - Combined thematic bullet covering multiple related changes

            Release notes: [Releases - github/copilot-sdk](https://github.com/github/copilot-sdk/releases)
            --

            Here is the raw source material for this week. Summarize from it — do not copy it verbatim:

            """);

        AppendReleases(sb, "GitHub Copilot CLI release notes", cliReleases);
        AppendReleases(sb, "GitHub Copilot SDK release notes", sdkReleases);

        return sb.ToString();
    }

    private static string BuildNewsPrompt(
        List<ReleaseEntry> changelogEntries,
        List<ReleaseEntry> blogEntries,
        DateTimeOffset weekStart,
        DateTimeOffset weekEnd)
    {
        var sb = new StringBuilder();

        sb.AppendLine($"""
            Generate a "News and Announcements" section for the GitHub Copilot CLI & SDK newsletter
            covering the week of {weekStart:MMMM d} to {weekEnd:MMMM d, yyyy}.

            IMPORTANT: This newsletter is ONLY for GitHub Copilot CLI and SDK users.
            
            STRICT FILTERING CRITERIA:
            - ONLY include items that directly affect CLI or SDK users
            - EXCLUDE general GitHub Copilot features (IDE completions, chat in VS Code, etc.)
            - EXCLUDE general coding agent updates unless they mention CLI/SDK integration
            - EXCLUDE VS Code, JetBrains, or other IDE-specific features
            - INCLUDE model availability ONLY if it's mentioned in context of CLI/SDK
            - INCLUDE network/auth/policy changes that affect CLI/SDK
            - INCLUDE educational content specifically about CLI/SDK
            
            LINKING REQUIREMENT:
            - ALWAYS link your summaries to the source blog posts/changelog entries
            - Use markdown links: [descriptive text](URL)
            - Every announcement should link to its source
            - The URLs are provided in the source material below (look for the URLs in the headers)
            
            You have two sources:
            1. GitHub Changelog entries labeled "copilot" - {changelogEntries.Count} entries
            2. GitHub Blog posts tagged with Copilot or CLI - {blogEntries.Count} posts
            
            Most of these entries will NOT be relevant to CLI/SDK users. Filter aggressively.
            If nothing is relevant, return an empty string (no section header).
            
            Examples of RELEVANT items:
            - "GPT-5.3-Codex now available in GitHub Copilot CLI"
            - "Network configuration changes for Copilot coding agent (affects CLI)"
            - "New SDK release enables XYZ capability"
            
            Examples of IRRELEVANT items (DO NOT INCLUDE):
            - "Copilot chat improvements in VS Code"
            - "New inline suggestions in JetBrains IDEs"
            - "Copilot for Business now supports XYZ" (unless CLI/SDK specific)
            - General model announcements without CLI/SDK context
            
            Output format (if relevant content exists):
            ---
            ## News and Announcements

            <engaging paragraphs of CLI/SDK-relevant news with links to source URLs>

            Example: "We're excited to share that [Fast Mode for Claude Opus 4.6](https://github.blog/changelog/...) is beginning to roll out..."

            ---

            Here is the source material to filter (URLs are in the markdown headers):

            """);

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
