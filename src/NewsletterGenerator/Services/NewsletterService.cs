using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using NewsletterGenerator;
using GitHub.Copilot;
using Microsoft.Extensions.Logging;
using NewsletterGenerator.Models;
using Spectre.Console;

namespace NewsletterGenerator.Services;

public partial class NewsletterService(
    ILogger<NewsletterService> logger,
    string? runContextKey = null,
    bool useInfiniteSessionsForRevisions = false) : IAsyncDisposable
{
    private const string CopilotClientName = "newsletter-generator";
    private const string ModelFallbacksEnvironmentVariable = "NEWSLETTER_MODEL_FALLBACKS";
    private const string NewsletterTitleOperation = "newsletter-title";
    private const string WelcomeSummaryOperation = "welcome-summary";
    private const string NewsAndAnnouncementsOperation = "news-announcements";
    private const string ReleaseSynthesisOperation = "release-section-synthesis";
    private const string RevisionOperation = "revision";
    private const string VsCodeNewsletterOperation = "vscode-newsletter";
    private const string SectionSynthesisOperation = "section-synthesis";
    private readonly object usageLock = new();
    private readonly List<CopilotUsageMetric> usageMetrics = [];
    private StartedSession? revisionSession;
    private string? revisionSessionModel;

    internal IReadOnlyList<CopilotUsageMetric> GetUsageMetricsSnapshot()
    {
        lock (usageLock)
        {
            return usageMetrics.ToList();
        }
    }

    private sealed class StartedSession(CopilotClient client, CopilotSession session) : IAsyncDisposable
    {
        public CopilotSession Session { get; } = session;

        public async ValueTask DisposeAsync()
        {
            await Session.DisposeAsync();
            await client.DisposeAsync();
        }
    }

    private SessionHooks CreateSessionHooks() => new()
    {
        OnErrorOccurred = (input, invocation) =>
        {
            logger.LogWarning("Session error in {Context}: {Error}", input.ErrorContext, input.Error);
            return Task.FromResult<ErrorOccurredHookOutput?>(new ErrorOccurredHookOutput
            {
                ErrorHandling = "retry"
            });
        },
        OnSessionStart = (input, invocation) =>
        {
            logger.LogDebug("Session started (source={Source})", input.Source);
            return Task.FromResult<SessionStartHookOutput?>(new SessionStartHookOutput());
        },
        OnSessionEnd = (input, invocation) =>
        {
            logger.LogDebug("Session ended (reason={Reason})", input.Reason);
            return Task.FromResult<SessionEndHookOutput?>(null);
        }
    };

    internal static string ResolveReasoningEffort(string operationProfile) => operationProfile switch
    {
        NewsletterTitleOperation => "low",
        WelcomeSummaryOperation => "medium",
        NewsAndAnnouncementsOperation => "medium",
        RevisionOperation => "medium",
        ReleaseSynthesisOperation => "high",
        VsCodeNewsletterOperation => "high",
        SectionSynthesisOperation => "high",
        _ => "medium"
    };

    private SessionConfig CreateSessionConfig(
        string? model,
        string systemMessageContent,
        string reasoningEffort,
        bool? enableInfiniteSessions = null) => new()
        {
            AvailableTools = [],
            ClientName = CopilotClientName,
            OnPermissionRequest = PermissionHandler.ApproveAll,
            Model = model,
            Streaming = true,
            ReasoningEffort = reasoningEffort,
            Hooks = CreateSessionHooks(),
            InfiniteSessions = enableInfiniteSessions.HasValue
            ? new InfiniteSessionConfig { Enabled = enableInfiniteSessions.Value }
            : null,
            SystemMessage = new SystemMessageConfig
            {
                Mode = SystemMessageMode.Replace,
                Content = systemMessageContent
            }
        };

    private ResumeSessionConfig CreateResumeSessionConfig(string? model, string systemMessageContent) => new()
    {
        AvailableTools = [],
        ClientName = CopilotClientName,
        OnPermissionRequest = PermissionHandler.ApproveAll,
        Model = model,
        Streaming = true,
        ReasoningEffort = null,
        Hooks = CreateSessionHooks(),
        SystemMessage = new SystemMessageConfig
        {
            Mode = SystemMessageMode.Replace,
            Content = systemMessageContent
        }
    };

    internal static string BuildSessionId(string runContext, string workflowStep, string? model)
    {
        var key = $"{runContext}|{workflowStep}|{model ?? "default"}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();
        return $"newsletter-generator-{hash[..24]}";
    }

    private async Task<StartedSession> CreateStartedSessionAsync(
        string? model,
        string operationProfile,
        string systemMessageContent,
        string? workflowStep = null,
        bool? enableInfiniteSessions = null)
    {
        var reasoningEffort = ResolveReasoningEffort(operationProfile);
        logger.LogInformation(
            "Creating Copilot session for {OperationProfile} (model={Model}, reasoning={ReasoningEffort})",
            operationProfile,
            model ?? "(default)",
            reasoningEffort);

        var client = new CopilotClient();
        await client.StartAsync();

        try
        {
            if (!string.IsNullOrWhiteSpace(runContextKey) && !string.IsNullOrWhiteSpace(workflowStep))
            {
                var filter = new SessionListFilter
                {
                    WorkingDirectory = Environment.CurrentDirectory
                };

                var sessions = await client.ListSessionsAsync(filter);
                var lastSessionId = await client.GetLastSessionIdAsync();
                var targetSessionId = BuildSessionId(runContextKey, workflowStep, model);
                var hasMatch = sessions.Any(session => string.Equals(session.SessionId, targetSessionId, StringComparison.Ordinal));

                logger.LogInformation(
                    "Session selection for {WorkflowStep}: context={RunContext}, model={Model}, target={TargetSessionId}, listed={ListedCount}, last={LastSessionId}",
                    workflowStep, runContextKey, model ?? "default", targetSessionId, sessions.Count, lastSessionId ?? "<none>");

                if (hasMatch)
                {
                    logger.LogInformation(
                        "Resuming prior session for {WorkflowStep} (sessionId={SessionId}, matchedLast={MatchedLast})",
                        workflowStep, targetSessionId, string.Equals(lastSessionId, targetSessionId, StringComparison.Ordinal));

                    var resumedSession = await client.ResumeSessionAsync(targetSessionId, CreateResumeSessionConfig(model, systemMessageContent));
                    return new StartedSession(client, resumedSession);
                }

                var newSessionConfig = CreateSessionConfig(model, systemMessageContent, reasoningEffort, enableInfiniteSessions);
                newSessionConfig.SessionId = targetSessionId;

                logger.LogInformation(
                    "Creating new session for {WorkflowStep} (sessionId={SessionId}, reason=No matching session found)",
                    workflowStep, targetSessionId);

                var createdSession = await client.CreateSessionAsync(newSessionConfig);
                return new StartedSession(client, createdSession);
            }

            logger.LogInformation(
                "Creating new session (resume disabled; runContext={RunContext}, workflowStep={WorkflowStep})",
                runContextKey ?? "<none>", workflowStep ?? "<none>");
            var session = await client.CreateSessionAsync(CreateSessionConfig(model, systemMessageContent, reasoningEffort, enableInfiniteSessions));
            return new StartedSession(client, session);
        }
        catch
        {
            await client.DisposeAsync();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (revisionSession is null)
            return;

        await revisionSession.DisposeAsync();
        revisionSession = null;
        revisionSessionModel = null;
    }

    internal static IReadOnlyList<string> BuildModelFallbackOrder(string? selectedModel, string? configuredFallbacks = null)
    {
        static IEnumerable<string> ParseConfiguredFallbacks(string? value) =>
            string.IsNullOrWhiteSpace(value)
                ? []
                : value.Split([',', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var fallbackModels = new List<string>();
        if (!string.IsNullOrWhiteSpace(selectedModel))
            fallbackModels.Add(selectedModel.Trim());

        var configured = ParseConfiguredFallbacks(configuredFallbacks).ToList();
        if (configured.Count > 0)
            fallbackModels.AddRange(configured);
        else
            fallbackModels.AddRange(["gpt-5.3-codex", "gpt-4.1"]);

        return fallbackModels
            .Where(static candidate => !string.IsNullOrWhiteSpace(candidate))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    internal static bool IsModelFallbackEligible(Exception exception)
    {
        if (exception is TimeoutException or HttpRequestException or TaskCanceledException)
            return true;

        if (exception is InvalidOperationException)
        {
            var message = exception.Message;
            if (string.IsNullOrWhiteSpace(message))
                return false;

            return message.Contains("rate limit", StringComparison.OrdinalIgnoreCase)
                || message.Contains("temporar", StringComparison.OrdinalIgnoreCase)
                || message.Contains("timeout", StringComparison.OrdinalIgnoreCase)
                || message.Contains("unavailable", StringComparison.OrdinalIgnoreCase)
                || message.Contains("overloaded", StringComparison.OrdinalIgnoreCase)
                || message.Contains("quota", StringComparison.OrdinalIgnoreCase)
                || message.Contains("model", StringComparison.OrdinalIgnoreCase)
                || message.Contains("429", StringComparison.OrdinalIgnoreCase)
                || message.Contains("502", StringComparison.OrdinalIgnoreCase)
                || message.Contains("503", StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    public async Task<string> GenerateWelcomeSummaryAsync(
        string newsSection,
        string releaseSummaryBullets,
        DateOnly weekStart,
        DateOnly weekEnd,
        CacheService cache,
        string? model = null)
    {
        if (string.IsNullOrEmpty(newsSection) && string.IsNullOrEmpty(releaseSummaryBullets))
        {
            logger.LogInformation("No news or release bullets provided; returning default welcome text");
            return "This week brings updates to GitHub Copilot CLI & SDK.";
        }

        // Check cache
        var sourceData = $"{newsSection}|||{releaseSummaryBullets}|||{model}";
        var sourceHash = CacheService.GetContentHash(sourceData);
        var cached = await cache.TryGetCachedAsync("welcome-summary", sourceHash);
        if (cached != null)
        {
            logger.LogInformation("Using cached Welcome summary (hash={Hash})", sourceHash[..12]);
            AnsiConsole.MarkupLine("[dim]Using cached Welcome summary[/]");
            return cached;
        }

        logger.LogInformation("Generating Welcome summary (model={Model})", model);
        AnsiConsole.MarkupLine("[grey]Generating Welcome summary...[/]");
        await using var copilot = await CreateStartedSessionAsync(
            model,
            WelcomeSummaryOperation,
            """
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
                    
                    OUTPUT REQUIREMENTS:
                    - Output ONLY the paragraph text — no greeting, no markdown, no preamble
                    - Do NOT include meta-commentary like "Here's a summary" or "Based on the material"
                    - Start directly with the content
                    """,
            "welcome-summary");

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

        var result = await SendPromptAsync(copilot.Session, prompt, "Welcome summary");
        logger.LogInformation("Welcome summary generated ({Length} chars)", result.Length);
        logger.LogDebug("Welcome summary content:\n{Content}", result);

        // Cache the result
        await cache.SaveCacheAsync("welcome-summary", result, sourceHash);

        return result;
    }

    public async Task<string> GenerateNewsletterTitleAsync(
        string welcomeSummary,
        string newsletterLabel,
        CacheService cache,
        string? model = null)
    {
        if (string.IsNullOrWhiteSpace(welcomeSummary))
            return $"{newsletterLabel} Weekly Newsletter";

        var sourceData = $"title|||{welcomeSummary}|||{model}";
        var sourceHash = CacheService.GetContentHash(sourceData);
        var cached = await cache.TryGetCachedAsync("newsletter-title", sourceHash);
        if (cached != null)
        {
            logger.LogInformation("Using cached newsletter title (hash={Hash})", sourceHash[..12]);
            AnsiConsole.MarkupLine("[dim]Using cached newsletter title[/]");
            return cached;
        }

        logger.LogInformation("Generating newsletter title (model={Model})", model);
        AnsiConsole.MarkupLine("[grey]Generating newsletter title...[/]");
        await using var copilot = await CreateStartedSessionAsync(
            model,
            NewsletterTitleOperation,
            """
                    You generate a short, descriptive title for a weekly developer newsletter.
                    The title should highlight 2-3 of the most important items from the week.
                    Keep it factual and specific - use actual feature/product names.
                    """,
            "newsletter-title");

        var prompt = $"""
            Generate a title for this week's {newsletterLabel} newsletter.

            Here is the welcome summary that describes this week's highlights:
            {welcomeSummary}

            FORMAT: "{newsletterLabel} - [Highlight 1], [Highlight 2], [Highlight 3], and more!"

            RULES:
            - Pick 2-3 of the most significant items from the summary
            - Use short, specific names (e.g., "Claude Sonnet 4.6 GA" not "new model availability")
            - Keep the total title under 100 characters if possible
            - Output ONLY the title text, no quotes, no markdown

            EXAMPLES:
            - {newsletterLabel} - New GPT-5.3-Codex, VS Code Integration, SDK Hooks, and more!
            - {newsletterLabel} - Claude Sonnet 4.6 GA, Cross-Session Memory, Infinite Sessions, and more!
            """;

        var result = await SendPromptAsync(copilot.Session, prompt, "Newsletter title");
        result = result.Trim().Trim('"').Trim();
        logger.LogInformation("Newsletter title generated: {Title}", result);

        await cache.SaveCacheAsync("newsletter-title", result, sourceHash);

        return result;
    }

    public async Task<string> GenerateNewsAndAnnouncementsAsync(
        List<ReleaseEntry> changelogEntries,
        List<ReleaseEntry> blogEntries,
        DateOnly weekStart,
        DateOnly weekEnd,
        CacheService cache,
        string? model = null)
    {
        logger.LogInformation("GenerateNewsAndAnnouncementsAsync called: changelog={ChangelogCount}, blog={BlogCount}", changelogEntries.Count, blogEntries.Count);
        if (changelogEntries.Count == 0 && blogEntries.Count == 0)
            return string.Empty;

        // Check cache
        var sourceData = System.Text.Json.JsonSerializer.Serialize(new { changelogEntries, blogEntries, model });
        var sourceHash = CacheService.GetContentHash(sourceData);
        var cached = await cache.TryGetCachedAsync("news-announcements", sourceHash);
        if (cached != null)
        {
            logger.LogInformation("Using cached News and Announcements (hash={Hash})", sourceHash[..12]);
            AnsiConsole.MarkupLine("[dim]Using cached News and Announcements[/]");
            return cached;
        }

        logger.LogInformation("Generating News and Announcements (model={Model})", model);
        AnsiConsole.MarkupLine("[grey]Generating News and Announcements...[/]");
        await using var copilot = await CreateStartedSessionAsync(
            model,
            NewsAndAnnouncementsOperation,
            """
                    You are a technical newsletter editor for an internal Microsoft developer community.
                    Your job is to curate and write the "News and Announcements" section from changelog
                    and blog entries.

                    CRITICAL FILTERING RULES:
                    - This newsletter is for GitHub Copilot CLI, SDK, and app users
                    - EXCLUDE: General IDE features, VS Code extensions, JetBrains plugins, general Copilot 
                      features that don't involve CLI, SDK, or the Copilot app
                    - EXCLUDE: General coding agent updates unless they specifically mention CLI/SDK/app integration
                    - INCLUDE ONLY: Items that directly impact CLI, SDK, or app users (new models available in CLI,
                      network configuration changes for CLI, SDK updates, CLI-specific features, app-specific features)
                    - If unsure whether something is relevant, lean toward excluding it
                    
                    Focus on:
                    - New model availability specifically in CLI/SDK/app
                    - CLI, SDK, or app-specific feature launches
                    - Network, auth, or policy changes affecting CLI/SDK/app
                    - Educational content specifically about CLI/SDK/app (courses, tutorials)
                    - Breaking changes or important migration notices for CLI/SDK/app

                    TONE GUIDELINES:
                    - Professional and informative, not marketing-y
                    - Factual rather than promotional
                    - Avoid hyperbole like "groundbreaking", "revolutionary", "game-changing"
                    - This is an internal dev newsletter for skeptical engineers
                    - Save enthusiasm for truly significant updates
                    - Write like you're informing colleagues, not selling a product

                    Keep it concise but informative. If there's nothing relevant to CLI/SDK/app users, return an empty section.
                    
                    OUTPUT REQUIREMENTS:
                    - Output ONLY the final newsletter Markdown content
                    - NO preamble, NO commentary, NO meta-statements
                    - Do NOT include phrases like "Based on my review", "Here's the content", etc.
                    - Start directly with the section header or content
                    - NO code fences, NO explanations
                    """,
            "news-announcements");

        var prompt = BuildNewsPrompt(changelogEntries, blogEntries, weekStart, weekEnd);
        var result = await SendPromptAsync(copilot.Session, prompt, "News and Announcements");
        logger.LogInformation("News section generated ({Length} chars)", result.Length);
        logger.LogDebug("News section content:\n{Content}", result);

        // Cache the result
        await cache.SaveCacheAsync("news-announcements", result, sourceHash);

        return result;
    }

    public async Task<string> GenerateProductReleaseAsync(
        string productName,
        string repoSlug,
        List<ReleaseEntry> releases,
        DateOnly weekStart,
        DateOnly weekEnd,
        CacheService cache,
        string? model = null)
    {
        logger.LogInformation("GenerateProductReleaseAsync called: product={Product}, releases={Count}", productName, releases.Count);
        if (releases.Count == 0)
        {
            logger.LogInformation("{Product}: no releases this week", productName);
            return $"## {productName} Updates\n\n_No new releases this week._\n";
        }

        // Check cache first
        var sourceData = System.Text.Json.JsonSerializer.Serialize(new { releases, model });
        var sourceHash = CacheService.GetContentHash(sourceData);
        var cacheKey = $"{productName.Replace(" ", "-").ToLower()}-releases";

        var cached = await cache.TryGetCachedAsync(cacheKey, sourceHash);
        if (cached != null)
        {
            logger.LogInformation("Using cached {Product} summary (hash={Hash})", productName, sourceHash[..12]);
            AnsiConsole.MarkupLine($"[dim]Using cached {productName} summary[/]");
            return cached;
        }

        logger.LogInformation("Generating {Product} summary (model={Model}, hash={Hash})", productName, model, sourceHash[..12]);
        AnsiConsole.MarkupLine($"[grey]Generating {productName} summary...[/]");

        var prompt = BuildProductPrompt(productName, repoSlug, releases, weekStart, weekEnd);
        logger.LogDebug("{Product} prompt ({Length} chars):\n{Prompt}", productName, prompt.Length, prompt);

        const int maxAttempts = 3;
        var result = string.Empty;
        var fallbackOrder = BuildModelFallbackOrder(model, Environment.GetEnvironmentVariable(ModelFallbacksEnvironmentVariable));
        var selectedModel = fallbackOrder.FirstOrDefault();

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            if (attempt > 1)
            {
                var delay = TimeSpan.FromSeconds(3 * attempt);
                logger.LogInformation("{Product}: retry {Attempt}/{Max} after {Delay}s delay",
                    productName, attempt, maxAttempts, delay.TotalSeconds);
                await Task.Delay(delay);
            }

            await using var copilot = await CreateStartedSessionAsync(
                selectedModel,
                ReleaseSynthesisOperation,
                """
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

                        Write concise, well-organized Markdown.
                        
                        OUTPUT REQUIREMENTS:
                        - Output ONLY the requested Markdown — no preamble, no commentary, no code fences
                        - Do NOT include meta-statements like "Here are the highlights" or "Based on my analysis"
                        - Start directly with the section header (## GitHub Copilot CLI Updates or ## GitHub Copilot SDK Updates)
                        """,
                $"{productName}-summary");

            for (var modelIndex = 0; modelIndex < fallbackOrder.Count; modelIndex++)
            {
                var activeModel = fallbackOrder[modelIndex];

                try
                {
                    result = await SendPromptAsync(copilot.Session, prompt, $"{productName} summary (attempt {attempt}, model {activeModel})");
                    logger.LogInformation("{Product} attempt {Attempt} with model {Model}: {Length} chars",
                        productName, attempt, activeModel, result.Length);

                    if (!string.IsNullOrWhiteSpace(result))
                        break;

                    logger.LogWarning("{Product}: empty response on attempt {Attempt}/{Max} with model {Model}",
                        productName, attempt, maxAttempts, activeModel);
                    break;
                }
                catch (Exception ex) when (IsModelFallbackEligible(ex))
                {
                    if (modelIndex >= fallbackOrder.Count - 1)
                    {
                        logger.LogWarning(ex,
                            "{Product}: model {Model} failed on attempt {Attempt}/{Max} and no further fallbacks remain; retrying with a new session on next attempt",
                            productName,
                            activeModel,
                            attempt,
                            maxAttempts);
                        break;
                    }

                    var nextModel = fallbackOrder[modelIndex + 1];
                    logger.LogWarning(ex,
                        "{Product}: model {Model} failed on attempt {Attempt}/{Max}. Falling back in-session to model {NextModel}.",
                        productName,
                        activeModel,
                        attempt,
                        maxAttempts,
                        nextModel);
                    try
                    {
                        await copilot.Session.SetModelAsync(nextModel);
                        logger.LogInformation("{Product}: switched session model from {Model} to {NextModel}",
                            productName, activeModel, nextModel);
                    }
                    catch (Exception setModelEx)
                    {
                        logger.LogWarning(setModelEx,
                            "{Product}: failed to switch session model from {Model} to {NextModel}; retrying with a new session on next attempt",
                            productName,
                            activeModel,
                            nextModel);
                        break;
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(result))
                break;
        }

        logger.LogInformation("{Product} summary generated ({Length} chars)", productName, result.Length);
        logger.LogDebug("{Product} summary content:\n{Content}", productName, result);

        // Cache the result
        await cache.SaveCacheAsync(cacheKey, result, sourceHash);

        return result;
    }

    public async Task<string> GenerateReleaseSectionAsync(
        List<ReleaseEntry> cliReleases,
        List<ReleaseEntry> sdkReleases,
        List<ReleaseEntry> appReleases,
        DateOnly weekStart,
        DateOnly weekEnd,
        CacheService cache,
        string? model = null)
    {
        logger.LogInformation("GenerateReleaseSectionAsync: CLI={CliCount} releases, SDK={SdkCount} releases, app={AppCount} releases",
            cliReleases.Count, sdkReleases.Count, appReleases.Count);

        // Generate CLI, SDK, and app summaries separately (with caching)
        var cliSummary = await GenerateProductReleaseAsync("GitHub Copilot CLI", "copilot-cli", cliReleases, weekStart, weekEnd, cache, model);
        logger.LogInformation("CLI summary result: {Length} chars, empty={IsEmpty}", cliSummary.Length, string.IsNullOrWhiteSpace(cliSummary));

        var sdkSummary = await GenerateProductReleaseAsync("GitHub Copilot SDK", "copilot-sdk", sdkReleases, weekStart, weekEnd, cache, model);
        logger.LogInformation("SDK summary result: {Length} chars, empty={IsEmpty}", sdkSummary.Length, string.IsNullOrWhiteSpace(sdkSummary));

        var appSummary = await GenerateProductReleaseAsync("GitHub Copilot app", "app", appReleases, weekStart, weekEnd, cache, model);
        logger.LogInformation("App summary result: {Length} chars, empty={IsEmpty}", appSummary.Length, string.IsNullOrWhiteSpace(appSummary));

        // Combine into final sections (top-level H2 headings, no wrapper)
        // Join with a blank line between sections. Trim trailing whitespace from each
        // section first so the separator is exactly one blank line regardless of whether
        // the model emitted a trailing newline (otherwise the next "## " heading can end
        // up glued directly to the previous section's last bullet).
        var combined = string.Join(
            "\n\n",
            new[] { cliSummary, sdkSummary, appSummary }
                .Select(section => section.TrimEnd())
                .Where(section => !string.IsNullOrWhiteSpace(section)));
        logger.LogInformation("Combined release section: {Length} chars", combined.Length);
        logger.LogDebug("Combined release section content:\n{Content}", combined);
        return combined;
    }

    public async Task<string> ReviseNewsletterMarkdownAsync(
        string markdown,
        string revisionRequest,
        string newsletterLabel,
        string? model = null)
    {
        logger.LogInformation("Revising newsletter markdown (model={Model})", model);
        AnsiConsole.MarkupLine("[grey]Applying revisions...[/]");

        const string revisionSystemPrompt =
            """
            You revise existing markdown newsletters for an internal developer audience.
            Keep the tone direct, factual, and concise.
            Preserve the existing markdown structure, headings, and links unless the request explicitly asks to change them.
            Return only the full revised markdown document.
            """;

        var prompt = $"""
            Apply the requested revisions to this {newsletterLabel} markdown newsletter.

            RULES:
            - Return the FULL revised markdown document.
            - Keep the existing headings, links, and overall structure unless the request says otherwise.
            - Keep the tone no-nonsense and developer-to-developer.
            - Do not add any explanation outside the markdown.

            Revision request:
            {revisionRequest}

            Current markdown:
            ```markdown
            {markdown}
            ```
            """;

        CopilotSession session;
        StartedSession? singleUseSession = null;

        if (useInfiniteSessionsForRevisions)
        {
            if (revisionSessionModel != model || revisionSession is null)
            {
                if (revisionSession is not null)
                {
                    await revisionSession.DisposeAsync();
                    revisionSession = null;
                    revisionSessionModel = null;
                }

                revisionSession = await CreateStartedSessionAsync(
                    model,
                    RevisionOperation,
                    revisionSystemPrompt,
                    enableInfiniteSessions: true);
                revisionSessionModel = model;
            }

            session = revisionSession.Session;
        }
        else
        {
            singleUseSession = await CreateStartedSessionAsync(
                model,
                RevisionOperation,
                revisionSystemPrompt,
                enableInfiniteSessions: false);
            session = singleUseSession.Session;
        }

        try
        {
            var result = await SendPromptAsync(session, prompt, "Revision");
            logger.LogInformation("Revised newsletter markdown generated ({Length} chars)", result.Length);
            return result;
        }
        finally
        {
            if (singleUseSession is not null)
                await singleUseSession.DisposeAsync();
        }
    }

    public async Task<string> GenerateVsCodeNewsletterAsync(
        VSCodeReleaseNotes releaseNotes,
        List<string> stableHighlights,
        List<StableFeatureCallout> stableFeatureCallouts,
        string? stableVersionUrl,
        List<ReleaseEntry> vscodeBlogEntries,
        List<ReleaseEntry> githubChangelogEntries,
        List<ReleaseEntry> githubBlogEntries,
        DateOnly weekStart,
        DateOnly weekEnd,
        CacheService cache,
        string? model = null)
    {
        var sourceData = System.Text.Json.JsonSerializer.Serialize(new
        {
            releaseNotes.Date,
            releaseNotes.VersionUrl,
            releaseNotes.WebsiteUrl,
            releaseNotes.Features,
            stableHighlights,
            stableFeatureCallouts,
            stableVersionUrl,
            vscodeBlogEntries,
            githubChangelogEntries,
            githubBlogEntries,
            model
        });

        var sourceHash = CacheService.GetContentHash(sourceData);
        var cached = await cache.TryGetCachedAsync("vscode-newsletter-v4", sourceHash);
        if (cached != null)
        {
            AnsiConsole.MarkupLine("[dim]Using cached VS Code newsletter[/]");
            return cached;
        }

        var stableVersion = stableVersionUrl != null
            ? ExtractVersionFromUrl(stableVersionUrl)
            : null;

        AnsiConsole.MarkupLine("[grey]Generating VS Code newsletter...[/]");
        await using var copilot = await CreateStartedSessionAsync(
            model,
            VsCodeNewsletterOperation,
            """
                    You are a technical newsletter editor writing for an internal Microsoft developer audience.
                    Your job is to summarize weekly VS Code updates in a concise, factual tone.
                    VS Code now ships weekly Stable releases with multiple Insiders updates per week.

                    TONE GUIDELINES:
                    - Professional and informative, not promotional
                    - No hype or hyperbole
                    - Focus on practical developer impact

                    OUTPUT REQUIREMENTS:
                    - Output ONLY final newsletter Markdown
                    - No preamble, no meta-commentary, no code fences
                    - Use this exact structure (omit sections that have no content):

                    Welcome
                    --------

                    <2-3 sentence factual intro paragraph covering the week's top items across ALL sections.
                     Mention specific version numbers, model names, or feature names.
                     Include the most notable news/announcements alongside release highlights.
                     This section is also used to generate the newsletter title, so lead with the biggest items.>

                    * * * * *

                    ---
                    ## News and Announcements

                    <Relevant announcements from the GitHub Changelog or GitHub Blog that affect VS Code users.
                     Use concise paragraphs with links to source URLs.
                     Only include items directly relevant to VS Code.
                     If nothing is relevant, omit this entire section.>

                    ---
                    ## This Week in VS Code Stable

                    <Summary of the stable release shipped this week.
                     Link the version number to its release notes page.
                     Use 5-10 concise bullets grouped by themes covering the key features.
                     Each bullet MUST start with a relevant emoji before the bolded title.
                     Format: - <emoji> **Title:** description
                     Example: - 🤖 **Copilot edits preview:** inline edit suggestions now stream in real-time...
                     Pick emojis that match the topic (e.g., 🤖 for AI, 🔧 for tools, 🖥️ for terminal, 🔒 for security)
                     If no stable release shipped this week, omit this entire section.>

                    ### 🔍 Feature Spotlight: <Feature Name>

                    <Pick one feature from the stable release that has the most developer impact.
                     Write a 2-3 sentence description of what it does and why it matters.
                     If a feature callout with an image URL is provided in the prompt data, include the image using standard markdown syntax:
                     ![description](image_url)
                     The image URL provided is an absolute URI — use it exactly as given. Do NOT convert it to a relative path.
                     If no callout data is provided or no stable release shipped, omit this subsection entirely.>

                    ---
                    ## VS Code Insiders Highlights (BUILD_NUMBER)

                    <Curated highlights from Insiders builds this week.
                     The heading MUST include the Insiders build number provided in the prompt (e.g., "## VS Code Insiders Highlights (1.116)").
                     Use 4-8 concise bullets grouped by themes.
                     Each bullet MUST start with a relevant emoji before the bolded title.
                     Format: - <emoji> **Title:** description
                     Example: - 🌐 **Native browser integration:** agents can now interact with page elements...
                     Pick emojis that match the topic (e.g., 🤖 for AI, 🔧 for tools, 🖥️ for terminal, 🔒 for security)
                     If no Insiders features are available, omit this entire section.>

                    Release notes: [VS Code Insiders](URL)
                    """,
            "vscode-newsletter");

        var featureLines = string.Join('\n', releaseNotes.Features.Select(f =>
            $"- [{f.Category}] {f.Description}{(string.IsNullOrWhiteSpace(f.Link) ? string.Empty : $" ({f.Link})")}"));

        var stableSection = stableHighlights.Count > 0
            ? $"""
            Stable release highlights (from the official release notes):
            Release notes URL: {(stableVersionUrl != null ? VSCodeReleaseNotes.GetWebsiteUrlFromRawUrl(stableVersionUrl) : "N/A")}
            Version: {stableVersion ?? "unknown"}
            {string.Join('\n', stableHighlights.Select(h => $"- {h}"))}
            """
            : "No stable release notes highlights available.";

        var calloutSection = stableFeatureCallouts.Count > 0
            ? $"""
            Stable feature callouts (pick ONE for the Feature Spotlight subsection):
            {string.Join("\n\n", stableFeatureCallouts.Select(c =>
                $"Feature: {c.Title}\nDescription: {c.Description}{(c.ImageUrl != null ? $"\nImage: {c.ImageUrl}" : "")}"))}
            """
            : "";

        var prompt = $"""
            Generate a weekly VS Code newsletter covering {weekStart:MMMM d} to {weekEnd:MMMM d, yyyy}.

            Source URL for Insiders release notes page: {releaseNotes.WebsiteUrl}
            Current Insiders build number: {ExtractVersionFromUrl(releaseNotes.VersionUrl)}

            VS Code now ships weekly Stable releases with multiple Insiders updates per week.
            Separate content into the appropriate sections:
            - "News and Announcements" for relevant changelog/blog items (e.g., new models, policy changes, ecosystem updates)
            - "This Week in VS Code Stable" for stable release highlights (use the stable release highlights below, supplemented by blog posts)
            - "VS Code Insiders Highlights ({ExtractVersionFromUrl(releaseNotes.VersionUrl)})" for Insiders-specific features (from the parsed release notes below) - the heading MUST include the build number

            Curate the most important developer-facing updates. Combine related items into thematic bullets.
            Omit any section that has no content.

            {stableSection}

            {calloutSection}

            Insiders release note features:
            {featureLines}

            Additional weekly sources (use for Stable and News sections):
            {BuildVsCodeAdditionalSources(vscodeBlogEntries, githubChangelogEntries, githubBlogEntries)}

            Generate ONLY the final Markdown newsletter content.
            """;

        var result = await SendPromptAsync(copilot.Session, prompt, "VS Code newsletter");
        await cache.SaveCacheAsync("vscode-newsletter-v4", result, sourceHash);
        return result;
    }

    private static string BuildVsCodeAdditionalSources(
        List<ReleaseEntry> vscodeBlogEntries,
        List<ReleaseEntry> githubChangelogEntries,
        List<ReleaseEntry> githubBlogEntries)
    {
        var sb = new StringBuilder();
        AppendBlogEntries(sb, "VS Code Blog (code.visualstudio.com/feed.xml)", vscodeBlogEntries);
        AppendBlogEntries(sb, "GitHub Changelog entries mentioning VS Code", githubChangelogEntries);
        AppendBlogEntries(sb, "GitHub Blog posts mentioning VS Code", githubBlogEntries);
        return sb.ToString();
    }

    [GeneratedRegex(@"v1_(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex InsidersVersionPattern();

    private static string ExtractVersionFromUrl(string versionUrl)
    {
        var match = InsidersVersionPattern().Match(versionUrl);
        return match.Success ? $"1.{match.Groups[1].Value}" : "Insiders";
    }

    // ── DevTech MVP multi-prompt section generation ────────────────────────

    [GeneratedRegex(@"\d+\.\d+")]
    private static partial Regex MajorVersionPattern();

    [GeneratedRegex(@"\b(announc|releas|ship|launch|generally.available|now.available|introducing)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ReleaseKeywordPattern();

    internal static List<ReleaseEntry> DetectMajorReleases(List<ReleaseEntry> blogEntries)
    {
        return blogEntries
            .Where(e => !string.IsNullOrWhiteSpace(e.Version)
                && MajorVersionPattern().IsMatch(e.Version)
                && ReleaseKeywordPattern().IsMatch(e.Version))
            .ToList();
    }

    private const string DevTechSectionSystem = """
        You are a technical newsletter section writer for Developer Technologies (DevTech) MVPs.
        Your audience is experienced developers who follow Microsoft developer tools closely.
        TONE: Direct, developer-to-developer. No marketing fluff or hyperbole. Factual and concise.
        Every bullet MUST include a markdown link to its source URL.
        No emoji in bullets.
        Keep bullet descriptions to one sentence, ideally under 25 words.
        OUTPUT: Only the requested Markdown section. No preamble, no commentary, no code fences.
        Start directly with the --- separator and ## heading.
        """;

    private async Task<string> GenerateCachedSectionAsync(
        string cacheKey,
        string sourceDataJson,
        string systemMessage,
        string prompt,
        CacheService cache,
        string? model,
        string displayLabel)
    {
        var sourceHash = CacheService.GetContentHash(sourceDataJson);
        var cached = await cache.TryGetCachedAsync(cacheKey, sourceHash);
        if (cached != null)
        {
            AnsiConsole.MarkupLine($"[dim]Using cached {Markup.Escape(displayLabel)}[/]");
            return cached;
        }

        AnsiConsole.MarkupLine($"[grey]Generating {Markup.Escape(displayLabel)}...[/]");
        await using var copilot = await CreateStartedSessionAsync(model, SectionSynthesisOperation, systemMessage, cacheKey);
        var result = await SendPromptAsync(copilot.Session, prompt, displayLabel);
        await cache.SaveCacheAsync(cacheKey, result, sourceHash);
        return result;
    }

    public async Task<string> GenerateDevTechCopilotSectionAsync(
        List<ReleaseEntry> cliReleases,
        List<ReleaseEntry> sdkReleases,
        List<ReleaseEntry> appReleases,
        List<ReleaseEntry> changelogEntries,
        DateOnly weekStart,
        DateOnly weekEnd,
        CacheService cache,
        string? model = null)
    {
        if (cliReleases.Count == 0 && sdkReleases.Count == 0 && appReleases.Count == 0 && changelogEntries.Count == 0)
            return string.Empty;

        var sb = new StringBuilder();
        sb.AppendLine($"""
            Generate the "Copilot CLI, SDK & app" section for a DevTech MVP newsletter covering {weekStart:MMMM d} to {weekEnd:MMMM d, yyyy}.

            Write one summary sentence followed by 3-5 bullets covering the most important changes.
            Each bullet: - **[Short label](url)** - description.

            SDK LANGUAGE NOTES:
            - The Copilot SDK ships for Go, .NET/C#, TypeScript, Python, and Rust.
            - Language-specific release tags (e.g., "rust/v1.0.0-beta.4") contain changes for that language.
              Give each language with notable changes its own bullet when space allows.
            - Do NOT describe language-specific tags as "first release" or "initial release" unless you
              have explicit evidence. Earlier betas may already include that language.

            Output exactly this format:

            ---
            ## Copilot CLI, SDK & app

            [summary sentence]

            - **[Label](url)** - description.

            Release notes: [cli](https://github.com/github/copilot-cli/releases) / [sdk](https://github.com/github/copilot-sdk/releases) / [app](https://github.com/github/app/releases)

            IMPORTANT: Always end the section with the "Release notes:" line shown above, exactly as written.

            Source material:

            """);
        AppendReleases(sb, "GitHub Copilot CLI releases", cliReleases);
        AppendReleases(sb, "GitHub Copilot SDK releases", sdkReleases);
        AppendReleases(sb, "GitHub Copilot app releases", appReleases);
        AppendBlogEntries(sb, "GitHub Copilot Changelog", changelogEntries);

        var sourceData = System.Text.Json.JsonSerializer.Serialize(new { cliReleases, sdkReleases, appReleases, changelogEntries, model });
        return await GenerateCachedSectionAsync("devtech-copilot", sourceData,
            DevTechSectionSystem, sb.ToString(), cache, model, "Copilot CLI, SDK & app section");
    }

    public async Task<string> GenerateDevTechVSCodeSectionAsync(
        VSCodeReleaseNotes? vscodeReleaseNotes,
        List<ReleaseEntry> vscodeBlogEntries,
        DateOnly weekStart,
        DateOnly weekEnd,
        CacheService cache,
        string? model = null)
    {
        var featureCount = vscodeReleaseNotes?.Features.Count ?? 0;
        if (featureCount == 0 && vscodeBlogEntries.Count == 0)
            return string.Empty;

        var sb = new StringBuilder();
        sb.AppendLine($"""
            Generate the "VS Code" section for a DevTech MVP newsletter covering {weekStart:MMMM d} to {weekEnd:MMMM d, yyyy}.

            Write one summary sentence, then include a sub-section for each available release (stable and/or Insiders) with 3-4 bullets each.
            Each bullet links to the release notes page or blog post.

            If both stable and Insiders releases are available, use this format:

            ---
            ## VS Code

            [summary sentence covering both releases]

            ### [VS Code X.Y](stable-release-url)

            [one sentence summary of the stable release]

            - **[Label](url)** - description.

            ### [VS Code Insiders X.Y](insiders-release-url)

            [one sentence summary of the Insiders release]

            - **[Label](url)** - description.
            ---

            If only one release is available, use this format:

            ---
            ## VS Code

            ### [VS Code X.Y](release-url)

            [one sentence summary of the release]

            - **[Label](url)** - description.
            ---

            Source material:

            """);

        if (vscodeReleaseNotes is { Features.Count: > 0 })
        {
            sb.AppendLine($"## VS Code Insiders Release Notes ({vscodeReleaseNotes.WebsiteUrl})");
            sb.AppendLine();
            foreach (var feature in vscodeReleaseNotes.Features)
                sb.AppendLine($"- [{feature.Category}] {feature.Description}{(string.IsNullOrWhiteSpace(feature.Link) ? "" : $" ({feature.Link})")}");
            sb.AppendLine();
        }
        AppendBlogEntries(sb, "VS Code Blog", vscodeBlogEntries);

        var sourceData = System.Text.Json.JsonSerializer.Serialize(new
        {
            vscodeFeatures = vscodeReleaseNotes?.Features,
            vscodeUrl = vscodeReleaseNotes?.WebsiteUrl,
            vscodeBlogEntries,
            model
        });
        return await GenerateCachedSectionAsync("devtech-vscode", sourceData,
            DevTechSectionSystem, sb.ToString(), cache, model, "VS Code section");
    }

    public async Task<string> GenerateDevTechVisualStudioSectionAsync(
        List<ReleaseEntry> vsBlogEntries,
        string vsReleaseNotesUrl,
        string vsInsidersReleaseNotesUrl,
        DateOnly weekStart,
        DateOnly weekEnd,
        CacheService cache,
        string? model = null)
    {
        if (vsBlogEntries.Count == 0)
            return string.Empty;

        var sb = new StringBuilder();
        sb.AppendLine($"""
            Generate the "Visual Studio" section for a DevTech MVP newsletter covering {weekStart:MMMM d} to {weekEnd:MMMM d, yyyy}.

            Write one summary sentence followed by 3-5 bullets from the Visual Studio blog.
            Include links to release notes pages when relevant.

            Reference links:
            - [Visual Studio 2026 Release Notes]({vsReleaseNotesUrl})
            - [Visual Studio 2026 Insiders Release Notes]({vsInsidersReleaseNotesUrl})

            Output exactly this format:

            ---
            ## Visual Studio

            [summary sentence]

            - **[Label](url)** - description.

            Source material:

            """);
        AppendBlogEntries(sb, "Visual Studio Blog", vsBlogEntries);

        var sourceData = System.Text.Json.JsonSerializer.Serialize(new { vsBlogEntries, model });
        return await GenerateCachedSectionAsync("devtech-visualstudio", sourceData,
            DevTechSectionSystem, sb.ToString(), cache, model, "Visual Studio section");
    }

    public async Task<string> GenerateDevTechMajorReleaseSectionAsync(
        ReleaseEntry entry,
        DateOnly weekStart,
        DateOnly weekEnd,
        CacheService cache,
        string? model = null)
    {
        var prompt = $"""
            Generate a dedicated major release section for a DevTech MVP newsletter covering {weekStart:MMMM d} to {weekEnd:MMMM d, yyyy}.

            This blog post announces a major product release. Create a section using the product name and version as the heading.

            Output exactly this format:

            ---
            ## [Product Name] [Version]

            One sentence summarizing what this release is and why it matters.
            - **[Feature 1]({entry.Url})** - description
            - **[Feature 2]({entry.Url})** - description
            - **[Feature 3]({entry.Url})** - description

            Extract 3-5 key features from the post. Be concise.

            Blog post title: {entry.Version}
            Blog post URL: {entry.Url}
            Content:
            {entry.PlainText}
            """;

        var sourceData = System.Text.Json.JsonSerializer.Serialize(new { entry, model });
        var cacheKey = $"devtech-major-{CacheService.GetContentHash(entry.Url)[..12]}";
        return await GenerateCachedSectionAsync(cacheKey, sourceData,
            DevTechSectionSystem, prompt, cache, model, $"major release: {entry.Version}");
    }

    public async Task<string> GenerateDevTechBlogsSectionAsync(
        List<ReleaseEntry> blogEntries,
        IReadOnlyList<string> excludeTitles,
        DateOnly weekStart,
        DateOnly weekEnd,
        CacheService cache,
        string? model = null)
    {
        var filtered = blogEntries
            .Where(e => !excludeTitles.Contains(e.Version, StringComparer.OrdinalIgnoreCase))
            .ToList();

        if (filtered.Count == 0)
            return string.Empty;

        var sb = new StringBuilder();
        sb.AppendLine($"""
            Generate the "Developer Blogs" section for a DevTech MVP newsletter covering {weekStart:MMMM d} to {weekEnd:MMMM d, yyyy}.

            Curate the most interesting 6-10 posts across .NET, Azure, Aspire, TypeScript, GitHub Blog, and developer.microsoft.com blogs.
            Group by topic area. Be selective - only include posts that would interest an MVP audience, but lean toward
            including a high-quality post rather than cutting it.
            Give extra weight to posts with broad audience appeal - for example, a post that spans multiple topics or
            products (such as .NET plus the GitHub Copilot app) is more valuable than a narrow single-topic post and
            should be favored when deciding what makes the cut.
            Each bullet: - **[Title](url)** - one SHORT sentence summary (under 20 words).
            Brevity is critical. State what changed or shipped, not background context.

            Output exactly this format:

            ---
            ## Developer Blogs

            [summary sentence]

            - **[Title](url)** - description.

            Source material:

            """);
        AppendBlogEntries(sb, "Developer Blogs", filtered);

        var sourceData = System.Text.Json.JsonSerializer.Serialize(new { filtered, model });
        return await GenerateCachedSectionAsync("devtech-blogs", sourceData,
            DevTechSectionSystem, sb.ToString(), cache, model, "Developer Blogs section");
    }

    public async Task<string> GenerateDevTechVideosSectionAsync(
        List<ReleaseEntry> youtubeDotNetEntries,
        List<ReleaseEntry> youtubeVSEntries,
        List<ReleaseEntry> youtubeVSCodeEntries,
        List<ReleaseEntry> youtubeGitHubEntries,
        List<ReleaseEntry> youtubeMicrosoftDevEntries,
        DateOnly weekStart,
        DateOnly weekEnd,
        CacheService cache,
        string? model = null)
    {
        var totalCount = youtubeDotNetEntries.Count + youtubeVSEntries.Count +
            youtubeVSCodeEntries.Count + youtubeGitHubEntries.Count + youtubeMicrosoftDevEntries.Count;
        if (totalCount == 0)
            return string.Empty;

        var sb = new StringBuilder();
        sb.AppendLine($"""
            Generate the "Developer Videos" section for a DevTech MVP newsletter covering {weekStart:MMMM d} to {weekEnd:MMMM d, yyyy}.

            Highlight 10 of the most interesting recent videos across the channels below.
            Focus on videos relevant to MVPs: developer tools, new features, AI + dev workflows, community content.
            Each video entry MUST use a 📺 emoji prefix (not a dash bullet).

            Output exactly this format:

            ---
            ## Developer Videos

            [summary sentence]

            📺 **[Video title](url)** - description.

            Source material:

            """);
        AppendBlogEntries(sb, "YouTube .NET Channel Videos", youtubeDotNetEntries);
        AppendBlogEntries(sb, "YouTube Visual Studio Videos", youtubeVSEntries);
        AppendBlogEntries(sb, "YouTube VS Code Videos", youtubeVSCodeEntries);
        AppendBlogEntries(sb, "YouTube GitHub Videos", youtubeGitHubEntries);
        AppendBlogEntries(sb, "YouTube Microsoft Developer Videos", youtubeMicrosoftDevEntries);

        var sourceData = System.Text.Json.JsonSerializer.Serialize(new
        {
            youtubeDotNetEntries,
            youtubeVSEntries,
            youtubeVSCodeEntries,
            youtubeGitHubEntries,
            youtubeMicrosoftDevEntries,
            model
        });
        return await GenerateCachedSectionAsync("devtech-videos", sourceData,
            DevTechSectionSystem, sb.ToString(), cache, model, "Developer Videos section");
    }

    public async Task<string> GenerateDevTechWelcomeAsync(
        IReadOnlyList<string> sectionOutputs,
        DateOnly weekStart,
        DateOnly weekEnd,
        CacheService cache,
        string? model = null)
    {
        var combinedSections = string.Join("\n\n", sectionOutputs.Where(s => !string.IsNullOrWhiteSpace(s)));
        if (string.IsNullOrWhiteSpace(combinedSections))
            return string.Empty;

        var prompt = $"""
            Generate the Welcome section for a DevTech MVP newsletter covering {weekStart:MMMM d} to {weekEnd:MMMM d, yyyy}.

            This goes to Developer Technologies MVPs.

            Write 1-2 short paragraphs summarizing the period's most important updates.
            An MVP should be able to read just this section and know what matters.
            Mention specific product names and version numbers.
            IMPORTANT: Link inline to the most important items using URLs from the sections below.

            After the paragraphs, add 4-8 emoji bullet highlights. Each bold label MUST be a markdown link.
            Example format:
            - 🚀 **[TypeScript 6.0 released](https://devblogs.microsoft.com/typescript/...)** - ships with isolated declarations
            - 🔧 **[Copilot CLI v1.2](https://github.com/github/copilot-cli/releases)** - adds streaming output

            After the bullets, add a short transition sentence like:
            "That's it! You're caught up now! Details below if you want to know more."

            Output exactly this format:

            Welcome
            --------

            **TLDR:**
            [paragraphs with inline links]

            [emoji bullets with linked bold labels]

            [transition sentence]

            * * * * *

            RULES:
            - Start with **TLDR:** before the summary paragraphs
            - Every emoji bullet MUST have its bold label wrapped in a markdown link: **[Label](url)**
            - Use actual URLs from the section content below
            - Emoji ONLY in the Welcome bullet highlights, not in paragraphs
            - End with a brief transition sentence before the separator

            Section content to summarize:

            {combinedSections}
            """;

        var sourceData = System.Text.Json.JsonSerializer.Serialize(new { combinedSections, model });
        return await GenerateCachedSectionAsync("devtech-welcome", sourceData,
            """
            You are a technical newsletter editor writing the opening summary for Developer Technologies MVPs.
            Your audience is experienced developers who follow Microsoft developer tools closely.
            TONE: Direct, developer-to-developer. No marketing fluff. Factual and concise.
            OUTPUT: Only the Welcome section Markdown. No preamble, no code fences.
            """,
            prompt, cache, model, "DevTech Welcome section");
    }

    private async Task<string> SendPromptAsync(CopilotSession session, string prompt, string operation)
    {
        logger.LogDebug("SendPromptAsync: sending prompt for {Operation} ({Length} chars)", operation, prompt.Length);
        var latestResponse = new StringBuilder();
        string? latestMessageId = null;
        var eventCount = 0;
        var streamedChars = 0;

        using var subscription = session.On<SessionEvent>(evt =>
        {
            switch (evt)
            {
                case AssistantMessageDeltaEvent delta:
                    var deltaContent = delta.Data.DeltaContent;
                    if (!string.IsNullOrEmpty(deltaContent))
                    {
                        streamedChars += deltaContent.Length;
                        logger.LogTrace("Streaming delta: +{Len} chars (total streamed={Total})",
                            deltaContent.Length, streamedChars);
                    }
                    break;
                case AssistantMessageEvent msg:
                    eventCount++;
                    var contentLen = msg.Data.Content?.Length ?? 0;
                    logger.LogDebug("AssistantMessageEvent #{Count}: {Length} chars", eventCount, contentLen);
                    latestMessageId = msg.Data.MessageId;
                    if (contentLen > 0)
                    {
                        latestResponse.Clear();
                        latestResponse.Append(msg.Data.Content);
                    }
                    else
                    {
                        logger.LogDebug("Ignoring empty AssistantMessageEvent #{Count}", eventCount);
                    }
                    break;
                case SessionIdleEvent:
                    logger.LogDebug("SessionIdleEvent received after {Count} message events, {StreamedChars} streamed chars",
                        eventCount, streamedChars);
                    break;
                case SessionErrorEvent err:
                    logger.LogError("Copilot session error: {Message}", err.Data.Message);
                    break;
            }
        });

        // The SDK default is 60 seconds, which isn't enough for high-reasoning operations.
        // Use the built-in timeout overload: SendAndWaitAsync(MessageOptions, TimeSpan?, CancellationToken).
        var finalMessage = await session.SendAndWaitAsync(new MessageOptions { Prompt = prompt }, TimeSpan.FromSeconds(180));
        var result = string.IsNullOrWhiteSpace(finalMessage?.Data.Content)
            ? latestResponse.ToString()
            : finalMessage.Data.Content;
        var messageId = finalMessage?.Data.MessageId ?? latestMessageId;
        logger.LogInformation("SendPromptAsync: received response ({Length} chars, empty={IsEmpty}, events={Events}, streamedChars={StreamedChars})",
            result.Length, string.IsNullOrWhiteSpace(result), eventCount, streamedChars);
        await TryCaptureUsageMetricsAsync(session, operation, prompt.Length, result.Length, messageId);
        if (string.IsNullOrWhiteSpace(result))
            logger.LogWarning("SendPromptAsync: AI returned empty response for prompt starting with: {PromptStart}",
                prompt.Length > 200 ? prompt[..200] : prompt);
        return result;
    }

    private async Task TryCaptureUsageMetricsAsync(
        CopilotSession session,
        string operation,
        int promptCharacters,
        int outputCharacters,
        string? messageId)
    {
        try
        {
            var usage = await session.Rpc.Usage.GetMetricsAsync();
            var inputTokens = ExtractTokenCount(usage, "InputTokens", "PromptTokens");
            var outputTokens = ExtractTokenCount(usage, "OutputTokens", "CompletionTokens");
            var totalTokens = ExtractTokenCount(usage, "TotalTokens", "Tokens");

            var metric = new CopilotUsageMetric(
                operation,
                session.SessionId,
                messageId,
                promptCharacters,
                outputCharacters,
                inputTokens,
                outputTokens,
                totalTokens,
                DateTimeOffset.UtcNow);

            lock (usageLock)
            {
                usageMetrics.Add(metric);
            }

            logger.LogInformation(
                "Copilot usage captured for {Operation}: in={InputTokens}, out={OutputTokens}, total={TotalTokens}",
                operation,
                inputTokens?.ToString() ?? "n/a",
                outputTokens?.ToString() ?? "n/a",
                totalTokens?.ToString() ?? "n/a");
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not capture usage metrics for {Operation}", operation);
        }
    }

    private static long? ExtractTokenCount(object? usage, params string[] candidatePropertyNames)
    {
        if (usage is null)
            return null;

        static bool IsCandidateName(string source, string target)
        {
            var normalizedSource = source.Replace("_", string.Empty, StringComparison.Ordinal);
            var normalizedTarget = target.Replace("_", string.Empty, StringComparison.Ordinal);
            return normalizedSource.Equals(normalizedTarget, StringComparison.OrdinalIgnoreCase);
        }

        var queue = new Queue<(object Value, int Depth)>();
        queue.Enqueue((usage, 0));

        while (queue.Count > 0)
        {
            var (value, depth) = queue.Dequeue();
            var properties = value.GetType().GetProperties();

            foreach (var property in properties)
            {
                object? propertyValue;
                try
                {
                    propertyValue = property.GetValue(value);
                }
                catch
                {
                    continue;
                }

                if (propertyValue is null)
                    continue;

                if (candidatePropertyNames.Any(name => IsCandidateName(property.Name, name))
                    && TryConvertToLong(propertyValue, out var tokenCount))
                {
                    return tokenCount;
                }

                if (depth >= 1)
                    continue;

                var propertyType = propertyValue.GetType();
                if (propertyType.IsPrimitive || propertyType == typeof(string) || propertyType.IsEnum)
                    continue;

                queue.Enqueue((propertyValue, depth + 1));
            }
        }

        return null;
    }

    private static bool TryConvertToLong(object value, out long converted)
    {
        switch (value)
        {
            case byte b:
                converted = b;
                return true;
            case short s:
                converted = s;
                return true;
            case int i:
                converted = i;
                return true;
            case long l:
                converted = l;
                return true;
            case float f:
                converted = (long)f;
                return true;
            case double d:
                converted = (long)d;
                return true;
            case decimal dec:
                converted = (long)dec;
                return true;
            case string str when long.TryParse(str, out var parsed):
                converted = parsed;
                return true;
            default:
                converted = 0;
                return false;
        }
    }

    private static string BuildProductPrompt(
        string productName,
        string repoSlug,
        List<ReleaseEntry> releases,
        DateOnly weekStart,
        DateOnly weekEnd)
    {
        var sb = new StringBuilder();

        sb.AppendLine($"""
            Generate the "{productName}" section for a weekly newsletter
            covering the week of {weekStart:MMMM d} to {weekEnd:MMMM d, yyyy}.

            You have release notes from {productName} - {releases.Count} releases

            Focus ONLY on the release notes. Do NOT include changelog or blog items in this section.
            
            CRITICAL SUMMARIZATION RULES:
            - Each release version should have NO MORE THAN 6 bullets (ideally 3-5)
            - Combine related items aggressively (e.g., all keyboard shortcuts → one bullet, all MCP changes → one bullet)
            - Skip bug fixes, internal improvements, and minor polish unless groundbreaking
            - Focus on new capabilities and major workflow changes only
            - If a release has 20+ changes, combine them into 3-5 thematic bullets

            SDK LANGUAGE COVERAGE (applies only to multi-language SDK releases):
            - The SDK ships for multiple languages (Go, .NET/C#, TypeScript, Python, Rust).
            - When the source material includes language-specific changelog sections (e.g., "Rust changes:"),
              ensure each language with notable additions gets at least one bullet or explicit mention.
            - Language-specific version tags (e.g., "rust-v0.1.0") represent the first standalone package
              publish for that language. Mention them, but do NOT call them "first public release" or
              "initial release" — earlier beta releases already included that language.
            - Do NOT collapse all language-specific changes into a single generic "across all SDKs" bullet
              when the changes differ meaningfully per language.

            Pick the 4–6 most impactful developer-facing highlights for the week.

            WEEKLY SUMMARY (required):
            - Immediately after the "## {productName} Updates" header, write a short plain-text summary
              of what changed for this product this week, BEFORE any per-version sub-sections.
            - Readers rely on this summary, so always include it. Scale it to the amount of activity:
              a single sentence is enough for one or two releases; use 2-3 sentences (or a few short
              lead bullets) when there are several releases or a big theme.
            - Frame the week at a high level (the main themes), do not restate every bullet below.
            - Use literal wording like "adds", "changes", "supports", "requires", and "fixes".
              Do NOT use marketing or hype language.

            Output ONLY the Markdown below (no extra text). Follow this exact structure:

            ## {productName} Updates

            <one or two sentences summarizing this week's {productName} changes at a high level>

            <one sub-section per version with MAXIMUM 6 bullets (ideally 3-5), highly condensed thematic summaries.
             Use **bold labels** to categorize each bullet. Do NOT use emojis.>
            ### vX.X.X (YYYY-MM-DD)

            - **Category label** - Combined thematic bullet covering multiple related changes
            - **Another label** - Another thematic bullet (e.g., "Terminal UX improvements" covering 10+ individual changes)

            Release notes: [Releases - github/{repoSlug}](https://github.com/github/{repoSlug}/releases)

            Here is the raw source material for this week. Summarize from it — do not copy it verbatim:

            """);

        AppendReleases(sb, $"{productName} release notes", releases);

        return sb.ToString();
    }

    private static string BuildNewsPrompt(
        List<ReleaseEntry> changelogEntries,
        List<ReleaseEntry> blogEntries,
        DateOnly weekStart,
        DateOnly weekEnd)
    {
        var sb = new StringBuilder();

        sb.AppendLine($"""
            Generate a "News and Announcements" section for the GitHub Copilot CLI, SDK & app newsletter
            covering the week of {weekStart:MMMM d} to {weekEnd:MMMM d, yyyy}.

            IMPORTANT: This newsletter is for GitHub Copilot CLI, SDK, and app users.
            
            STRICT FILTERING CRITERIA:
            - ONLY include items that directly affect CLI, SDK, or app users
            - EXCLUDE general GitHub Copilot features (IDE completions, chat in VS Code, etc.)
            - EXCLUDE general coding agent updates unless they mention CLI/SDK/app integration
            - EXCLUDE VS Code, JetBrains, or other IDE-specific features
            - INCLUDE model availability ONLY if it's mentioned in context of CLI/SDK/app
            - INCLUDE network/auth/policy changes that affect CLI/SDK/app
            - INCLUDE educational content specifically about CLI/SDK/app
            
            LINKING REQUIREMENT:
            - ALWAYS link your summaries to the source blog posts/changelog entries
            - Use markdown links: [descriptive text](URL)
            - Every announcement should link to its source
            - The URLs are provided in the source material below (look for the URLs in the headers)
            
            You have two sources:
            1. GitHub Changelog entries labeled "copilot" - {changelogEntries.Count} entries
            2. GitHub Blog posts tagged with Copilot or CLI - {blogEntries.Count} posts
            
            Most of these entries will NOT be relevant to CLI/SDK/app users. Filter aggressively.
            If nothing is relevant, return an empty string (no section header).
            
            CRITICAL OUTPUT REQUIREMENT:
            - Output ONLY the final newsletter Markdown
            - Do NOT include ANY meta-commentary or preamble
            - NEVER write phrases like "Based on my review", "Here's the content", "Here are the highlights", etc.
            - Start DIRECTLY with "---" or return empty string if nothing is relevant
            - You are generating the final newsletter content, not describing what you're doing
            
            Examples of RELEVANT items:
            - "GPT-5.3-Codex now available in GitHub Copilot CLI"
            - "Network configuration changes for Copilot coding agent (affects CLI)"
            - "New SDK release enables XYZ capability"
            - "New GitHub Copilot app feature for XYZ"
            
            Examples of IRRELEVANT items (DO NOT INCLUDE):
            - "Copilot chat improvements in VS Code"
            - "New inline suggestions in JetBrains IDEs"
            - "Copilot for Business now supports XYZ" (unless CLI/SDK/app specific)
            - General model announcements without CLI/SDK/app context
            
            Output format (if relevant content exists):
            ---
            ## News and Announcements

            <engaging paragraphs of CLI/SDK/app-relevant news with links to source URLs>

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
