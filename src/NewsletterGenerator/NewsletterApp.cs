using System.Diagnostics;
using System.Text;
using GitHub.Copilot;
using Microsoft.Extensions.Logging;
using NewsletterGenerator.Models;
using NewsletterGenerator.Services;
using Spectre.Console;

namespace NewsletterGenerator;

internal static partial class NewsletterApp
{

    public static async Task<int> RunGenerateAsync(GenerateSettings settings)
    {
        var nonInteractive = settings.NonInteractive || Console.IsInputRedirected || Console.IsOutputRedirected;

        if (nonInteractive)
        {
            if (string.IsNullOrWhiteSpace(settings.Newsletter) || string.IsNullOrWhiteSpace(settings.Model) || !settings.DaysBack.HasValue)
            {
                AnsiConsole.MarkupLine("[red]Non-interactive mode requires `--newsletter`, `--model`, and `daysBack`.[/]");
                return 2;
            }
        }

        var repoRoot = FindRepoRoot(Directory.GetCurrentDirectory());
        var logPath = Path.Combine(repoRoot, "log", "newsletter-{Date}.log");

        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Debug);
            builder.AddFile(logPath, LogLevel.Debug);
        });

        var programLogger = loggerFactory.CreateLogger("Program");
        programLogger.LogInformation("Newsletter generator started");

        // Start Copilot connectivity checks early so they run during header rendering.
        var copilotStartupTask = StartCopilotStartupAsync();

        if (!Console.IsOutputRedirected)
            RenderHeader();

        var aggregateMetrics = new AggregateRunMetrics();
        bool runAgain;
        do
        {
            var metrics = new RunMetrics();
            var runStopwatch = Stopwatch.StartNew();

            var availableModels = await FinishCopilotStartupAsync(copilotStartupTask, metrics);

            var selectedNewsletter = ResolveNewsletterType(settings.Newsletter) ??
                (nonInteractive ? NewsletterType.CopilotCliSdk : PromptForNewsletterType());

            var selectedModel = await SelectModelAsync(settings.Model, availableModels, nonInteractive);

            var startupOptions = nonInteractive
                ? (settings.ClearCache, settings.ForceRefresh)
                : PromptForStartupOptions(settings.ClearCache, settings.ForceRefresh);

            var clearCache = settings.ClearCache || startupOptions.Item1;
            var forceRefresh = settings.ForceRefresh || startupOptions.Item2;
            var useCache = !forceRefresh;

            var today = DateOnly.FromDateTime(DateTime.Now);
            var defaultDaysBack = selectedNewsletter == NewsletterType.DevTechMVP ? 14 : 7;
            var selectedDaysBack = settings.DaysBack ?? (!nonInteractive
                ? AnsiConsole.Prompt(
                    new TextPrompt<int>("[yellow]How many days back?[/]")
                        .DefaultValue(defaultDaysBack)
                        .PromptStyle("green")
                        .Validate(days => days > 0
                            ? ValidationResult.Success()
                            : ValidationResult.Error("Days must be greater than 0.")))
                : defaultDaysBack);

            var weekEndDate = today;
            var weekStartDate = today.AddDays(-selectedDaysBack);
            var daySpan = weekEndDate.DayNumber - weekStartDate.DayNumber + 1;

            var cacheDir = Path.Combine(repoRoot, "src", "NewsletterGenerator", ".cache");

            if (clearCache)
            {
                if (Directory.Exists(cacheDir))
                {
                    Directory.Delete(cacheDir, recursive: true);
                    AnsiConsole.MarkupLine("[green]✓[/] Cache cleared");
                    metrics.Warnings.Add("Cache cleared before run.");
                }
                else
                {
                    AnsiConsole.MarkupLine("[dim]No cache to clear[/]");
                    metrics.Warnings.Add("Requested cache clear, but no cache directory existed.");
                }
            }

            var cache = new CacheService(loggerFactory.CreateLogger<CacheService>(), cacheDir, forceRefresh: forceRefresh);

            string? content;
            string title;

            try
            {
                if (selectedNewsletter == NewsletterType.VSCode)
                {
                    (content, title) = await GenerateVsCodeNewsletterAsync(
                        weekStartDate,
                        weekEndDate,
                        cache,
                        selectedModel,
                        loggerFactory,
                        metrics,
                        settings.Debug);
                }
                else if (selectedNewsletter == NewsletterType.DevTechMVP)
                {
                    (content, title) = await GenerateDevTechNewsletterAsync(
                        weekStartDate,
                        weekEndDate,
                        cache,
                        selectedModel,
                        loggerFactory,
                        metrics,
                        settings.Debug);
                }
                else
                {
                    (content, title) = await GenerateCopilotNewsletterAsync(
                        weekStartDate,
                        weekEndDate,
                        cache,
                        selectedModel,
                        loggerFactory,
                        metrics,
                        settings.Debug);
                }
            }
            catch (Exception ex)
            {
                RenderFriendlyException(ex, settings.Debug);
                return 1;
            }

            metrics.CacheHits = cache.CacheHits;
            metrics.CacheMisses = cache.CacheMisses;
            metrics.CacheSkips = cache.CacheSkips;
            metrics.CacheSections = cache.GetSectionMetrics();
            metrics.Newsletter = selectedNewsletter;
            metrics.Model = selectedModel;

            if (!string.IsNullOrWhiteSpace(content))
            {
                content = PrefixNewsletterName(content, title, weekStartDate, weekEndDate, selectedModel);
                content = NormalizeDashes(content);
                content = RemoveReleaseNotesHrules(content);
                content = StripMarkdownFence(content);

                if (!nonInteractive)
                {
                    RenderGeneratedPreview(selectedNewsletter, content);

                    var revisionPassCount = 0;
                    await using var newsletterService = new NewsletterService(
                        loggerFactory.CreateLogger<NewsletterService>(),
                        useInfiniteSessionsForRevisions: settings.InfiniteSessions);
                    while (true)
                    {
                        var revisionRequest = PromptForRevisionRequest(revisionPassCount > 0);
                        if (string.IsNullOrWhiteSpace(revisionRequest))
                            break;

                        var revisionStopwatch = Stopwatch.StartNew();
                        content = await newsletterService.ReviseNewsletterMarkdownAsync(
                            content,
                            revisionRequest,
                            GetNewsletterLabel(selectedNewsletter),
                            selectedModel);
                        metrics.CopilotUsage.AddRange(newsletterService.GetUsageMetricsSnapshot());
                        content = NormalizeDashes(content);
                        content = RemoveReleaseNotesHrules(content);
                        content = StripMarkdownFence(content);
                        revisionStopwatch.Stop();

                        metrics.StageSeconds["Apply revisions"] =
                            metrics.StageSeconds.GetValueOrDefault("Apply revisions") + revisionStopwatch.Elapsed.TotalSeconds;
                        metrics.RevisionApplied = true;
                        revisionPassCount++;

                        AnsiConsole.MarkupLine("[green]✓[/] Revisions applied");
                        RenderGeneratedPreview(selectedNewsletter, content);
                    }
                }

                var outputDir = Path.Combine(repoRoot, "output");
                Directory.CreateDirectory(outputDir);

                var filename = $"newsletter-{GetNewsletterSlug(selectedNewsletter)}-{weekEndDate:yyyy-MM-dd}.md";
                var outputPath = Path.Combine(outputDir, filename);

                metrics.OutputPath = outputPath;
                metrics.OverwroteOutput = File.Exists(outputPath);
                metrics.OutputCharacters = content.Length;
                metrics.OutputLines = content.Split('\n').Length;
                metrics.OutputSections = CountSections(content);

                if (metrics.OverwroteOutput)
                {
                    AnsiConsole.MarkupLine($"[yellow]⚠[/] Overwriting existing file [underline]{Markup.Escape(outputPath)}[/]");
                    File.SetAttributes(outputPath, FileAttributes.Normal);
                    metrics.Warnings.Add("Output file already existed and was overwritten.");
                }

                var writeStopwatch = Stopwatch.StartNew();
                await File.WriteAllTextAsync(outputPath, content, Encoding.UTF8);
                writeStopwatch.Stop();
                metrics.StageSeconds["Write output"] = writeStopwatch.Elapsed.TotalSeconds;

                AnsiConsole.MarkupLine($"[green]✓[/] Newsletter written to [underline]{Markup.Escape(outputPath)}[/]");

                // Copy to clipboard
                var clipboardService = new ClipboardService(loggerFactory.CreateLogger<ClipboardService>());
                var clipboardStopwatch = Stopwatch.StartNew();
                var clipboardSuccess = await clipboardService.TrySetClipboardTextAsync(content);
                clipboardStopwatch.Stop();
                metrics.StageSeconds["Copy to clipboard"] = clipboardStopwatch.Elapsed.TotalSeconds;

                if (clipboardSuccess)
                {
                    AnsiConsole.MarkupLine("[green]✓[/] Newsletter copied to clipboard");
                    metrics.ClipboardSucceeded = true;
                }
                else
                {
                    AnsiConsole.MarkupLine("[yellow]⚠[/] Could not copy newsletter to clipboard");
                    metrics.Warnings.Add("Failed to copy newsletter content to clipboard.");
                }
            }
            else
            {
                metrics.Warnings.Add("No newsletter output was generated for this run.");
            }

            runStopwatch.Stop();
            metrics.TotalWallSeconds = runStopwatch.Elapsed.TotalSeconds;
            aggregateMetrics.AddRun(metrics);

            runAgain = !nonInteractive && AnsiConsole.Confirm("Generate another newsletter?", false);
            if (runAgain)
            {
                AnsiConsole.WriteLine();
                AnsiConsole.Write(new Rule("[grey]Starting new run[/]").LeftJustified());
                AnsiConsole.WriteLine();
            }
        }
        while (runAgain);

        if (aggregateMetrics.TotalRuns > 0)
        {
            RenderAggregateDashboard(aggregateMetrics);
        }

        return 0;
    }

    // ── Progress helpers ────────────────────────────────────────────────────

    private static ProgressTask AddInactiveTask(ProgressContext ctx, string label)
    {
        return ctx.AddTask($"[grey]{label}[/]", maxValue: 100);
    }

    private static void SetTaskActive(ProgressTask task, string label)
    {
        task.Description = $"[cornflowerblue]{label}[/]";
    }

    private static void SetTaskInactive(ProgressTask task, string label)
    {
        task.Description = $"[grey]{label}[/]";
    }

    private static async Task<T> RunTrackedTaskAsync<T>(
        ProgressTask task,
        string label,
        Func<Task<T>> work,
        RunMetrics? metrics = null,
        string? stageKey = null)
    {
        SetTaskActive(task, label);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var result = await work();
            task.Increment(100);
            stopwatch.Stop();
            if (!string.IsNullOrWhiteSpace(stageKey))
                metrics?.StageSeconds.TryAdd(stageKey, stopwatch.Elapsed.TotalSeconds);
            return result;
        }
        finally
        {
            SetTaskInactive(task, label);
        }
    }

    // ── Newsletter type helpers ─────────────────────────────────────────────

    private static NewsletterType? ResolveNewsletterType(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return null;

        return input.Trim().ToLowerInvariant() switch
        {
            "copilot" => NewsletterType.CopilotCliSdk,
            "copilot-cli-sdk" => NewsletterType.CopilotCliSdk,
            "cli" => NewsletterType.CopilotCliSdk,
            "vscode" => NewsletterType.VSCode,
            "vs-code" => NewsletterType.VSCode,
            "devtech" => NewsletterType.DevTechMVP,
            "devtech-mvp" => NewsletterType.DevTechMVP,
            "mvp" => NewsletterType.DevTechMVP,
            _ => null
        };
    }

    private static NewsletterType PromptForNewsletterType()
    {
        var choice = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("[yellow]Which newsletter do you want to generate?[/]")
                .PageSize(6)
                .AddChoices([
                    "GitHub Copilot CLI/SDK/app",
                    "VS Code",
                    "DevTech MVP"
                ]));

        return choice switch
        {
            "VS Code" => NewsletterType.VSCode,
            "DevTech MVP" => NewsletterType.DevTechMVP,
            _ => NewsletterType.CopilotCliSdk
        };
    }

    private static (bool, bool) PromptForStartupOptions(bool clearCacheFromArg, bool forceRefreshFromArg)
    {
        if (clearCacheFromArg || forceRefreshFromArg)
            return (clearCacheFromArg, forceRefreshFromArg);

        var startupChoices = AnsiConsole.Prompt(
            new MultiSelectionPrompt<string>()
                .Title("[yellow]Startup options[/] (select any)")
                .NotRequired()
                .AddChoices([
                    "Clear cache before run",
                    "Force refresh (ignore cache reads this run)"
                ]));

        return (
            startupChoices.Contains("Clear cache before run"),
            startupChoices.Contains("Force refresh (ignore cache reads this run)")
        );
    }

    internal static string GetNewsletterLabel(NewsletterType type) => type switch
    {
        NewsletterType.VSCode => "VS Code",
        NewsletterType.DevTechMVP => "DevTech MVP",
        _ => "GitHub Copilot CLI/SDK/app"
    };

    private static string GetNewsletterSlug(NewsletterType type) => type switch
    {
        NewsletterType.VSCode => "vscode",
        NewsletterType.DevTechMVP => "devtech-mvp",
        _ => "copilot-cli-sdk"
    };

    // ── Content helpers ─────────────────────────────────────────────────────

    private static string PrefixNewsletterName(
        string content,
        string title,
        DateOnly weekStart,
        DateOnly weekEnd,
        string model)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {title}");
        sb.AppendLine();
        sb.AppendLine($"> Coverage: {weekStart:yyyy-MM-dd} to {weekEnd:yyyy-MM-dd}");
        sb.AppendLine($"> Model: {model}");
        sb.AppendLine();
        sb.AppendLine(content.TrimStart());
        return sb.ToString();
    }

    private static string PromptForRevisionRequest(bool isAdditionalRequest)
    {
        return AnsiConsole.Prompt(
            new TextPrompt<string>(isAdditionalRequest
                ? "[yellow]Additional revision request[/] [grey](press Enter to finish)[/]"
                : "[yellow]Revision request[/] [grey](press Enter to keep as-is)[/]")
                .PromptStyle("green")
                .AllowEmpty());
    }

    internal static string ExtractWelcomeSummary(string content)
    {
        var lines = content.Split('\n');
        var sb = new StringBuilder();
        bool inWelcome = false;

        foreach (var line in lines)
        {
            if (line.TrimStart().StartsWith("Welcome", StringComparison.OrdinalIgnoreCase))
            {
                inWelcome = true;
                continue;
            }

            if (inWelcome && line.Trim() == "--------")
                continue;

            if (inWelcome && (line.Trim() == "* * * * *" || line.TrimStart().StartsWith("---")))
                break;

            if (inWelcome && !string.IsNullOrWhiteSpace(line))
                sb.AppendLine(line);
        }

        return sb.ToString().Trim();
    }

    public static string FindRepoRoot(string startDir)
    {
        var dir = new DirectoryInfo(startDir);
        while (dir != null)
        {
            if (dir.EnumerateFiles("*.slnx").Any() || dir.EnumerateFiles("*.sln").Any())
                return dir.FullName;
            dir = dir.Parent;
        }

        return startDir;
    }

    private static int CountSections(string content)
    {
        var lines = content.Split('\n');

        var headingCount = lines
            .Count(line => line.StartsWith("## ", StringComparison.Ordinal) || line.StartsWith("### ", StringComparison.Ordinal));

        var hasWelcomeSection = false;
        foreach (var line in lines)
        {
            if (line.StartsWith("## ", StringComparison.Ordinal) || line.StartsWith("### ", StringComparison.Ordinal))
                break;

            if (!string.IsNullOrWhiteSpace(line))
            {
                hasWelcomeSection = true;
                break;
            }
        }

        return headingCount + (hasWelcomeSection ? 1 : 0);
    }

    private static string NormalizeDashes(string text)
        => text.Replace('\u2014', '-').Replace('\u2013', '-');

    private static string StripMarkdownFence(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.StartsWith("```markdown", StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed["```markdown".Length..].TrimStart('\n', '\r');
        else if (trimmed.StartsWith("```", StringComparison.Ordinal))
            trimmed = trimmed[3..].TrimStart('\n', '\r');
        if (trimmed.EndsWith("```", StringComparison.Ordinal))
            trimmed = trimmed[..^3].TrimEnd();
        return trimmed;
    }

    /// <summary>
    /// Removes stray "---" horizontal rules that appear on the line immediately
    /// after a "Release notes:" link, which downstream parsers misinterpret as
    /// a setext-style H2 heading.
    /// </summary>
    private static string RemoveReleaseNotesHrules(string text)
    {
        var lines = text.Split('\n');
        var result = new List<string>(lines.Length);

        for (var i = 0; i < lines.Length; i++)
        {
            // If this line is "---" and the previous non-empty line starts with "Release notes:",
            // skip this line.
            if (lines[i].Trim() == "---" && i > 0)
            {
                var prev = i - 1;
                while (prev >= 0 && string.IsNullOrWhiteSpace(lines[prev]))
                    prev--;

                if (prev >= 0 && lines[prev].TrimStart().StartsWith("Release notes:", StringComparison.Ordinal))
                    continue;
            }

            result.Add(lines[i]);
        }

        return string.Join('\n', result);
    }

    // ── Copilot startup & model selection ───────────────────────────────────

    private record CopilotStartupResult(
        string CliPath,
        string VersionStatus,
        double CliSeconds,
        bool IsAuthenticated,
        string AuthStatus,
        IList<ModelInfo>? Models,
        string SdkStatus,
        string PingStatus,
        double SdkSeconds,
        string? SdkError);

    /// <summary>
    /// Kicks off CLI discovery and SDK connection as background tasks.
    /// Call <see cref="FinishCopilotStartupAsync"/> to await and display results.
    /// </summary>
    private static Task<CopilotStartupResult> StartCopilotStartupAsync()
    {
        return Task.Run(async () =>
        {
            var cliTask = Task.Run(async () =>
            {
                var sw = Stopwatch.StartNew();
                var path = await TryFindCopilotCliOnPathAsync() ?? "copilot";
                var versionResult = await TryRunProcessAsync(path, "--version");
                string version;
                if (versionResult.success && versionResult.exitCode == 0)
                    version = string.IsNullOrWhiteSpace(versionResult.standardOutput) ? "Available" : versionResult.standardOutput;
                else
                    version = string.IsNullOrWhiteSpace(versionResult.standardError) ? "Unavailable" : versionResult.standardError;
                sw.Stop();
                return (path, version, sw.Elapsed.TotalSeconds);
            });

            var sdkTask = Task.Run(async () =>
            {
                var sw = Stopwatch.StartNew();
                await using var client = new CopilotClient();
                var sdkAuthStatus = await client.GetAuthStatusAsync();

                var authed = !string.IsNullOrEmpty(sdkAuthStatus.Login);
                var auth = string.IsNullOrWhiteSpace(sdkAuthStatus.StatusMessage)
                    ? (authed ? "Authenticated" : "Not authenticated")
                    : sdkAuthStatus.StatusMessage;

                await client.StartAsync();

                var modelsTask = client.ListModelsAsync();
                var pingTask = client.PingAsync().ContinueWith(t => t.IsCompletedSuccessfully ? "OK" : "Failed");

                await Task.WhenAll(modelsTask, pingTask);

                var m = await modelsTask;
                var ping = await pingTask;
                var status = m == null ? "Connected" : $"Connected ({m.Count} models available, ping: {ping})";
                sw.Stop();

                return (authed, auth, m, status, ping, sw.Elapsed.TotalSeconds);
            });

            string cliPath = "copilot", versionStatus = "Unknown";
            double cliSeconds = 0;
            bool isAuthenticated = false;
            string authStatus = "Unknown", sdkStatus = "Unknown", pingStatus = "Unknown";
            IList<ModelInfo>? models = null;
            double sdkSeconds = 0;
            string? sdkError = null;

            try { await Task.WhenAll(cliTask, sdkTask); } catch { /* handled below */ }

            if (cliTask.IsCompletedSuccessfully)
                (cliPath, versionStatus, cliSeconds) = cliTask.Result;

            if (sdkTask.IsCompletedSuccessfully)
                (isAuthenticated, authStatus, models, sdkStatus, pingStatus, sdkSeconds) = sdkTask.Result;
            else if (sdkTask.IsFaulted)
            {
                var ex = sdkTask.Exception?.InnerException ?? sdkTask.Exception;
                authStatus = ex?.Message ?? "Unknown error";
                sdkStatus = $"Not ready: {Truncate(ex?.Message ?? "Unknown error", 120)}";
                sdkError = ex?.Message ?? "Unknown error";
            }

            return new CopilotStartupResult(
                cliPath, versionStatus, cliSeconds,
                isAuthenticated, authStatus, models, sdkStatus, pingStatus, sdkSeconds,
                sdkError);
        });
    }

    /// <summary>
    /// Awaits the pre-started startup task, records metrics, and renders the status table.
    /// </summary>
    private static async Task<IList<ModelInfo>?> FinishCopilotStartupAsync(
        Task<CopilotStartupResult> startupTask,
        RunMetrics? metrics = null)
    {
        CopilotStartupResult result;

        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .SpinnerStyle(Style.Parse("cornflowerblue"))
            .StartAsync("Connecting to Copilot CLI...", async _ =>
        {
            result = await startupTask;
        });

        result = startupTask.Result;

        metrics?.StageSeconds.TryAdd("Startup: CLI discovery", result.CliSeconds);
        metrics?.StageSeconds.TryAdd("Startup: SDK ready", result.SdkSeconds);

        if (!string.Equals(result.PingStatus, "OK", StringComparison.OrdinalIgnoreCase) && result.SdkError == null)
            metrics?.Warnings.Add("Copilot SDK ping failed during startup checks.");

        if (result.SdkError != null)
            metrics?.Warnings.Add($"Copilot SDK startup failed: {result.SdkError}");

        var statusTable = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Grey)
            .AddColumn("[bold]Copilot startup status[/]")
            .AddColumn("[bold]Details[/]");

        statusTable.AddRow("CLI path", Markup.Escape(result.CliPath));
        statusTable.AddRow("CLI version", Markup.Escape(result.VersionStatus));
        statusTable.AddRow("Auth", Markup.Escape(result.IsAuthenticated
            ? $"Authenticated: {result.AuthStatus}"
            : $"Not authenticated: {result.AuthStatus}"));
        statusTable.AddRow("SDK", Markup.Escape(result.SdkStatus));

        AnsiConsole.Write(statusTable);
        AnsiConsole.WriteLine();

        return result.Models;
    }

    public static async Task<IList<ModelInfo>?> PrintCopilotStartupStatusAsync(RunMetrics? metrics = null)
    {
        var task = StartCopilotStartupAsync();
        return await FinishCopilotStartupAsync(task, metrics);
    }

    private static async Task<string?> TryFindCopilotCliOnPathAsync()
    {
        if (OperatingSystem.IsWindows())
        {
            var result = await TryRunProcessAsync("where", "copilot");
            if (result.success && result.exitCode == 0)
                return FirstNonEmptyLine(result.standardOutput);
        }
        else
        {
            var result = await TryRunProcessAsync("which", "copilot");
            if (result.success && result.exitCode == 0)
                return FirstNonEmptyLine(result.standardOutput);
        }

        return null;
    }

    private static string? FirstNonEmptyLine(string value)
    {
        return value
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .FirstOrDefault(line => !string.IsNullOrWhiteSpace(line));
    }

    private static async Task<(bool success, string standardOutput, string standardError, int exitCode)> TryRunProcessAsync(string fileName, string arguments)
    {
        try
        {
            var processStartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(processStartInfo);
            if (process == null)
                return (false, "", "", -1);

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            var standardOutput = (await stdoutTask).Trim();
            var standardError = (await stderrTask).Trim();
            var exitCode = process.ExitCode;

            return (true, standardOutput, standardError, exitCode);
        }
        catch
        {
            return (false, "", "", -1);
        }
    }

    private static string Truncate(string value, int max)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "(none)";

        return value.Length <= max ? value : value[..max] + "...";
    }

    private static async Task<string> SelectModelAsync(string? modelArg, IList<ModelInfo>? cachedModels, bool nonInteractive)
    {
        if (!string.IsNullOrWhiteSpace(modelArg))
            return modelArg.Trim();

        const string fallbackModel = "gpt-4.1";

        if (nonInteractive)
            return fallbackModel;

        try
        {
            var models = cachedModels?.ToList();

            if (models == null || models.Count == 0)
            {
                await AnsiConsole.Progress()
                    .AutoClear(true)
                    .HideCompleted(false)
                    .StartAsync(async ctx =>
                    {
                        const string taskLabel = "Querying available models";
                        var task = AddInactiveTask(ctx, taskLabel);
                        await using var client = new CopilotClient();
                        SetTaskActive(task, taskLabel);
                        task.Increment(40);
                        await client.StartAsync();
                        task.Increment(30);
                        models = (await client.ListModelsAsync()).ToList();
                        task.Increment(30);
                        SetTaskInactive(task, taskLabel);
                    });
            }

            if (models == null || models.Count == 0)
                return fallbackModel;

            var preferredIndex = models.FindIndex(m =>
                m.Id.Equals("gpt-5.3-codex", StringComparison.OrdinalIgnoreCase));

            if (preferredIndex < 0)
            {
                preferredIndex = models.FindIndex(m =>
                    m.Id.Equals("gpt-4.1", StringComparison.OrdinalIgnoreCase));
            }

            if (preferredIndex < 0)
                preferredIndex = 0;

            var orderedModels = models
                .Select((model, index) => new { Model = model, IsDefault = index == preferredIndex })
                .OrderByDescending(item => item.IsDefault)
                .ToList();

            var labels = orderedModels
                .Select(item => item.IsDefault ? $"{item.Model.Name} (recommended)" : item.Model.Name)
                .ToList();

            var selectedLabel = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title($"[yellow]Select a model[/] [dim]({models.Count} available)[/]")
                    .PageSize(12)
                    .MoreChoicesText("[grey](Move up and down to see all models)[/]")
                    .AddChoices(labels));

            var selectedIndex = labels.FindIndex(label => label == selectedLabel);
            return orderedModels[selectedIndex].Model.Id;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[yellow]⚠[/] Could not list models ([dim]{Markup.Escape(ex.Message)}[/]). Falling back to [white]{fallbackModel}[/].");
            return fallbackModel;
        }
    }
}
