using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using GitHub.Copilot.SDK;
using Microsoft.Extensions.Logging;
using NewsletterGenerator.Models;
using NewsletterGenerator.Services;
using Spectre.Console;
using Spectre.Console.Cli;

var app = new CommandApp();
app.Configure(config =>
{
    config.SetApplicationName("newsletter-generator");

    config.AddCommand<GenerateCommand>("generate")
        .WithDescription("Generate a newsletter (default command).")
        .WithExample(["generate", "--newsletter", "copilot", "--model", "gpt-5.3-codex", "7"]);

    config.AddCommand<ListModelsCommand>("list-models")
        .WithDescription("List models exposed by the GitHub Copilot SDK.");

    config.AddCommand<ClearCacheCommand>("clear-cache")
        .WithDescription("Clear the local .cache directory.");

    config.AddCommand<DoctorCommand>("doctor")
        .WithDescription("Run environment checks for Copilot CLI/SDK readiness.");

});

app.SetDefaultCommand<GenerateCommand>();

return await app.RunAsync(args);

internal sealed class GenerateSettings : CommandSettings
{
    [Description("Newsletter type: copilot or vscode")]
    [CommandOption("--newsletter|-n <TYPE>")]
    public string? Newsletter { get; init; }

    [Description("Model ID to use (for example: gpt-5.3-codex)")]
    [CommandOption("--model|-m <MODEL>")]
    public string? Model { get; init; }

    [Description("Clear cache before generation")]
    [CommandOption("--clear-cache|-c")]
    [DefaultValue(false)]
    public bool ClearCache { get; init; }

    [Description("Force refresh (ignore cache reads this run)")]
    [CommandOption("--force-refresh|-f")]
    [DefaultValue(false)]
    public bool ForceRefresh { get; init; }

    [Description("Run without interactive prompts")]
    [CommandOption("--non-interactive")]
    [DefaultValue(false)]
    public bool NonInteractive { get; init; }

    [Description("Skip confirmation prompt before generation")]
    [CommandOption("--yes|-y")]
    [DefaultValue(false)]
    public bool Yes { get; init; }

    [Description("Show full exception details")]
    [CommandOption("--debug")]
    [DefaultValue(false)]
    public bool Debug { get; init; }

    [Description("Days back from today (for example: 7)")]
    [CommandArgument(0, "[daysBack]")]
    public int? DaysBack { get; init; }

    public override ValidationResult Validate()
    {
        if (DaysBack is <= 0)
            return ValidationResult.Error("daysBack must be greater than 0.");

        if (NonInteractive)
        {
            if (string.IsNullOrWhiteSpace(Newsletter))
                return ValidationResult.Error("--non-interactive requires --newsletter.");

            if (string.IsNullOrWhiteSpace(Model))
                return ValidationResult.Error("--non-interactive requires --model.");

            if (!DaysBack.HasValue)
                return ValidationResult.Error("--non-interactive requires daysBack argument.");
        }

        return ValidationResult.Success();
    }
}

internal sealed class GenerateCommand : AsyncCommand<GenerateSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, GenerateSettings settings, CancellationToken cancellationToken)
    {
        return await NewsletterApp.RunGenerateAsync(settings);
    }
}

internal sealed class ListModelsCommand : AsyncCommand<CommandSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, CommandSettings settings, CancellationToken cancellationToken)
    {
        var models = await NewsletterApp.PrintCopilotStartupStatusAsync();
        if (models == null || models.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No models returned by SDK.[/]");
            return 1;
        }

        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Grey)
            .AddColumn("[bold]Model[/]")
            .AddColumn("[bold]ID[/]");

        foreach (var model in models.OrderBy(m => m.Name))
            table.AddRow(Markup.Escape(model.Name), Markup.Escape(model.Id));

        AnsiConsole.Write(table);
        return 0;
    }
}

internal sealed class ClearCacheCommand : Command<CommandSettings>
{
    public override int Execute(CommandContext context, CommandSettings settings, CancellationToken cancellationToken)
    {
        var repoRoot = NewsletterApp.FindRepoRoot(Directory.GetCurrentDirectory());
        var cacheDir = Path.Combine(repoRoot, "src", "NewsletterGenerator", ".cache");

        if (!Directory.Exists(cacheDir))
        {
            AnsiConsole.MarkupLine("[dim]No cache directory found.[/]");
            return 0;
        }

        Directory.Delete(cacheDir, recursive: true);
        AnsiConsole.MarkupLine($"[green]✓[/] Cleared cache at [underline]{Markup.Escape(cacheDir)}[/]");
        return 0;
    }
}

internal sealed class DoctorCommand : AsyncCommand<CommandSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, CommandSettings settings, CancellationToken cancellationToken)
    {
        var models = await NewsletterApp.PrintCopilotStartupStatusAsync();
        var healthy = models != null && models.Count > 0;

        if (healthy)
            AnsiConsole.MarkupLine("[green]Environment checks passed.[/]");
        else
            AnsiConsole.MarkupLine("[yellow]Environment checks completed with warnings. Run `copilot auth status` if needed.[/]");

        return healthy ? 0 : 1;
    }
}

internal static class NewsletterApp
{
    private const string CliAtomUrl = "https://github.com/github/copilot-cli/releases.atom";
    private const string SdkAtomUrl = "https://github.com/github/copilot-sdk/releases.atom";
    private const string ChangelogCopilotUrl = "https://github.blog/changelog/label/copilot/feed/";
    private const string BlogUrl = "https://github.blog/feed/";
    private const string VSCodeBlogUrl = "https://code.visualstudio.com/feed.xml";

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

        if (!Console.IsOutputRedirected)
            RenderHeader();

        bool runAgain;
        do
        {
            var metrics = new RunMetrics();
            var runStopwatch = Stopwatch.StartNew();

            var availableModels = await PrintCopilotStartupStatusAsync(metrics);

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
            var selectedDaysBack = settings.DaysBack ?? (!nonInteractive
                ? AnsiConsole.Prompt(
                    new TextPrompt<int>("[yellow]How many days back?[/]")
                        .DefaultValue(7)
                        .PromptStyle("green")
                        .Validate(days => days > 0
                            ? ValidationResult.Success()
                            : ValidationResult.Error("Days must be greater than 0.")))
                : 7);

            var weekEndDate = today;
            var weekStartDate = today.AddDays(-selectedDaysBack);
            var daySpan = weekEndDate.DayNumber - weekStartDate.DayNumber + 1;

            RenderPreRunSummary(selectedNewsletter, selectedModel, useCache, forceRefresh, clearCache, weekStartDate, weekEndDate, daySpan, nonInteractive);

            if (!nonInteractive && !settings.Yes)
            {
                if (!AnsiConsole.Confirm("Proceed with generation?", true))
                {
                    AnsiConsole.MarkupLine("[yellow]Generation cancelled.[/]");
                    return 0;
                }
            }

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

            if (!string.IsNullOrWhiteSpace(content))
            {
                content = PrefixNewsletterName(content, title, weekStartDate, weekEndDate, selectedModel);
                content = content.Replace('—', '-').Replace('–', '-');

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
                AnsiConsole.WriteLine();

                if (!Console.IsOutputRedirected)
                {
                    var preview = string.Join('\n', content.Split('\n').Take(25));
                    AnsiConsole.Write(
                        new Panel(Markup.Escape(preview))
                            .Header("[cornflowerblue] Preview (first 25 lines) [/]")
                            .Border(BoxBorder.Rounded)
                            .BorderColor(Color.Grey)
                            .Expand());
                }
            }
            else
            {
                metrics.Warnings.Add("No newsletter output was generated for this run.");
            }

            runStopwatch.Stop();
            metrics.TotalWallSeconds = runStopwatch.Elapsed.TotalSeconds;

            RenderRunDashboard(metrics, selectedNewsletter, selectedModel, useCache, weekStartDate, weekEndDate);

            runAgain = !nonInteractive && AnsiConsole.Confirm("Generate another newsletter?", false);
            if (runAgain)
            {
                AnsiConsole.WriteLine();
                AnsiConsole.Write(new Rule("[grey]Starting new run[/]").LeftJustified());
                AnsiConsole.WriteLine();
            }
        }
        while (runAgain);

        return 0;
    }

    private static void RenderHeader()
    {
        AnsiConsole.Write(
            new FigletText("Newsletter")
                .LeftJustified()
                .Color(Color.CornflowerBlue));

        AnsiConsole.Write(new Rule("[cornflowerblue]GitHub Copilot CLI & SDK Weekly Generator[/]")
            .LeftJustified());
        AnsiConsole.WriteLine();
    }

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

    private static void RenderPreRunSummary(
        NewsletterType newsletter,
        string model,
        bool useCache,
        bool forceRefresh,
        bool clearCache,
        DateOnly weekStart,
        DateOnly weekEnd,
        int daySpan,
        bool nonInteractive)
    {
        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Grey)
            .AddColumn("[bold]Setting[/]")
            .AddColumn("[bold]Value[/]");

        table.AddRow("Newsletter", Markup.Escape(GetNewsletterLabel(newsletter)));
        table.AddRow("Model", Markup.Escape(model));
        table.AddRow("Use cache", useCache ? "Yes" : "No");
        table.AddRow("Force refresh", forceRefresh ? "Yes" : "No");
        table.AddRow("Clear cache", clearCache ? "Yes" : "No");
        table.AddRow("Date range", $"{weekStart:yyyy-MM-dd} -> {weekEnd:yyyy-MM-dd} ({daySpan} days)");
        table.AddRow("Mode", nonInteractive ? "Non-interactive" : "Interactive");

        AnsiConsole.Write(new Panel(table)
            .Header("[cornflowerblue]Run Review[/]")
            .Border(BoxBorder.Rounded)
            .BorderColor(Color.Grey)
            .Expand());
    }

    private static void RenderRunDashboard(
        RunMetrics metrics,
        NewsletterType newsletter,
        string model,
        bool useCache,
        DateOnly weekStart,
        DateOnly weekEnd)
    {
        var totalWorkSeconds = metrics.StageSeconds.Values.Sum();
        var parallelSavedSeconds = Math.Max(0, totalWorkSeconds - metrics.TotalWallSeconds);

        var summaryTable = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Grey)
            .AddColumn("[bold]Metric[/]")
            .AddColumn("[bold]Value[/]");

        summaryTable.AddRow("Mode", $"{Markup.Escape(GetNewsletterLabel(newsletter))} [grey]({weekStart:yyyy-MM-dd} -> {weekEnd:yyyy-MM-dd})[/]");
        summaryTable.AddRow("Model", Markup.Escape(model));
        summaryTable.AddRow("SDK", $"Streaming: {(metrics.StreamingEnabled ? "On" : "Off")}, Reasoning: {Markup.Escape(metrics.ReasoningEffort)}");
        summaryTable.AddRow("Cache", $"{(useCache ? "Read/write" : "Force refresh")} [grey](hits {metrics.CacheHits}, misses {metrics.CacheMisses}, skips {metrics.CacheSkips})[/]");
        summaryTable.AddRow("Timing", $"Wall [white]{metrics.TotalWallSeconds:F1}s[/], work [white]{totalWorkSeconds:F1}s[/], saved [green]{parallelSavedSeconds:F1}s[/]");
        summaryTable.AddRow("Output", string.IsNullOrWhiteSpace(metrics.OutputPath)
            ? "(none)"
            : $"{Markup.Escape(metrics.OutputPath)} [grey]({metrics.OutputCharacters:N0} chars, {metrics.OutputLines:N0} lines, {metrics.OutputSections} sections)[/]");
        summaryTable.AddRow("Overwrite", metrics.OverwroteOutput ? "Yes" : "No");

        var sourceTable = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Grey)
            .AddColumn("[bold]Source[/]")
            .AddColumn("[bold]Raw[/]")
            .AddColumn("[bold]Filtered[/]")
            .AddColumn("[bold]Final[/]")
            .AddColumn("[bold]Notes[/]");

        foreach (var count in metrics.SourceCounts)
            sourceTable.AddRow(
                Markup.Escape(count.Source),
                Markup.Escape(count.RawCount),
                Markup.Escape(count.FilteredCount),
                Markup.Escape(count.FinalCount),
                Markup.Escape(count.Notes));

        var cacheTable = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Grey)
            .AddColumn("[bold]Section[/]")
            .AddColumn("[bold]Read[/]")
            .AddColumn("[bold]Save[/]")
            .AddColumn("[bold]Size[/]");

        foreach (var cacheMetric in metrics.CacheSections)
        {
            cacheTable.AddRow(
                Markup.Escape(cacheMetric.Key),
                FormatCacheOutcome(cacheMetric.ReadOutcome),
                FormatCacheOutcome(cacheMetric.SaveOutcome),
                cacheMetric.ContentLength is int length ? $"{length:N0} chars" : "-");
        }

        if (metrics.CacheSections.Count == 0)
            cacheTable.AddRow("(none)", "-", "-", "-");

        var timingTable = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Grey)
            .AddColumn("[bold]Stage[/]")
            .AddColumn(new TableColumn("[bold]Seconds[/]").RightAligned());

        foreach (var kvp in metrics.StageSeconds.OrderByDescending(k => k.Value))
            timingTable.AddRow(Markup.Escape(kvp.Key), $"{kvp.Value:F2}");

        if (metrics.StageSeconds.Count == 0)
            timingTable.AddRow("(none)", "0.00");

        var warningMarkup = metrics.Warnings.Count == 0
            ? "[green]No warnings. Clean run.[/]"
            : string.Join("\n", metrics.Warnings.Select(w => $"[yellow]•[/] {Markup.Escape(w)}"));

        var chart = new BarChart()
            .Width(48)
            .Label("[bold]Stage Duration (seconds)[/]")
            .CenterLabel();

        foreach (var kvp in metrics.StageSeconds.OrderByDescending(k => k.Value))
        {
            var color = kvp.Key.Contains("Fetch", StringComparison.OrdinalIgnoreCase)
                ? Color.Yellow
                : kvp.Key.Contains("output", StringComparison.OrdinalIgnoreCase)
                    ? Color.SpringGreen3
                    : Color.CornflowerBlue;
            chart.AddItem(kvp.Key, kvp.Value, color);
        }

        if (metrics.StageSeconds.Count == 0)
            chart.AddItem("(none)", 0, Color.Grey);

        AnsiConsole.Write(new Panel(summaryTable)
            .Header("[cornflowerblue]✨ Run Summary[/]")
            .Border(BoxBorder.Rounded)
            .BorderColor(Color.Grey)
            .Expand());
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Panel(new Markup(warningMarkup))
            .Header("[cornflowerblue]⚠ Signals[/]")
            .Border(BoxBorder.Rounded)
            .BorderColor(Color.Grey)
            .Expand());
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Panel(cacheTable)
            .Header("[cornflowerblue]💾 Cache by Section[/]")
            .Border(BoxBorder.Rounded)
            .BorderColor(Color.Grey)
            .Expand());
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Panel(sourceTable)
            .Header("[cornflowerblue]🧪 Source Pipeline[/]")
            .Border(BoxBorder.Rounded)
            .BorderColor(Color.Grey)
            .Expand());
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Panel(timingTable)
            .Header("[cornflowerblue]⏱ Stage Timing[/]")
            .Border(BoxBorder.Rounded)
            .BorderColor(Color.Grey)
            .Expand());
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Panel(chart)
            .Header("[cornflowerblue]📊 Timing Chart[/]")
            .Border(BoxBorder.Rounded)
            .BorderColor(Color.Grey)
            .Expand());
        AnsiConsole.WriteLine();
    }

    private static string FormatCacheOutcome(string? outcome) => outcome switch
    {
        "hit" => "[green]hit[/]",
        "saved" => "[green]saved[/]",
        "miss" => "[yellow]miss[/]",
        "mismatch" => "[yellow]mismatch[/]",
        "skip" => "[grey]skip[/]",
        "empty" => "[grey]empty[/]",
        "error" => "[red]error[/]",
        _ => "-"
    };

    private static void RenderFriendlyException(Exception ex, bool debug)
    {
        AnsiConsole.MarkupLine("[red]✗ Generation failed.[/]");

        if (debug)
        {
            AnsiConsole.WriteException(ex,
                ExceptionFormats.ShortenPaths |
                ExceptionFormats.ShortenMethods |
                ExceptionFormats.ShowLinks);
        }
        else
        {
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(ex.Message)}[/]");
        }

        var hints = new Panel(
            "- Verify Copilot auth: [white]copilot auth status[/]\n" +
            "- Verify CLI is on PATH: [white]copilot --version[/]\n" +
            "- Check network access to GitHub feeds")
            .Header("[yellow]Troubleshooting[/]")
            .Border(BoxBorder.Rounded)
            .BorderColor(Color.Grey);

        AnsiConsole.Write(hints);
    }

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
                    "GitHub Copilot CLI/SDK",
                    "VS Code Insiders"
                ]));

        return choice == "VS Code Insiders"
            ? NewsletterType.VSCode
            : NewsletterType.CopilotCliSdk;
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

    private static string GetNewsletterLabel(NewsletterType type) => type switch
    {
        NewsletterType.VSCode => "VS Code Insiders",
        _ => "GitHub Copilot CLI/SDK"
    };

    private static string GetNewsletterSlug(NewsletterType type) => type switch
    {
        NewsletterType.VSCode => "vscode-insiders",
        _ => "copilot-cli-sdk"
    };

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

    private static bool MentionsVsCode(ReleaseEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.Version) && string.IsNullOrWhiteSpace(entry.PlainText))
            return false;

        var combined = $"{entry.Version}\n{entry.PlainText}";
        return combined.Contains("vs code", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("vscode", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("visual studio code", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("insiders", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("code.visualstudio.com", StringComparison.OrdinalIgnoreCase);
    }

    public static async Task<List<ModelInfo>?> PrintCopilotStartupStatusAsync(RunMetrics? metrics = null)
    {
        string cliPath = "copilot";
        string versionStatus = "Unknown";
        string authStatus = "Unknown";
        bool isAuthenticated = false;
        string sdkStatus = "Unknown";
        List<ModelInfo>? models = null;

        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .SpinnerStyle(Style.Parse("cornflowerblue"))
            .StartAsync("Connecting to Copilot CLI...", async ctx =>
        {
            // Run CLI path/version discovery in parallel with SDK connection
            var cliTask = Task.Run(async () =>
            {
                var cliStopwatch = Stopwatch.StartNew();
                var path = await TryFindCopilotCliOnPathAsync() ?? "copilot";
                var versionResult = await TryRunProcessAsync(path, "--version");
                string version;
                if (versionResult.success && versionResult.exitCode == 0)
                    version = string.IsNullOrWhiteSpace(versionResult.standardOutput) ? "Available" : versionResult.standardOutput;
                else
                    version = string.IsNullOrWhiteSpace(versionResult.standardError) ? "Unavailable" : versionResult.standardError;
                cliStopwatch.Stop();
                return (path, version, cliStopwatch.Elapsed.TotalSeconds);
            });

            var sdkTask = Task.Run(async () =>
            {
                var sdkStopwatch = Stopwatch.StartNew();
                await using var client = new CopilotClient();
                var sdkAuthStatus = await client.GetAuthStatusAsync();

                var authed = !string.IsNullOrEmpty(sdkAuthStatus.Login);
                var auth = string.IsNullOrWhiteSpace(sdkAuthStatus.StatusMessage)
                    ? (authed ? "Authenticated" : "Not authenticated")
                    : sdkAuthStatus.StatusMessage;

                await client.StartAsync();

                // List models and ping in parallel once connected
                var modelsTask = client.ListModelsAsync();
                var pingTask = client.PingAsync().ContinueWith(t => t.IsCompletedSuccessfully ? "OK" : "Failed");

                await Task.WhenAll(modelsTask, pingTask);

                var m = await modelsTask;
                var ping = await pingTask;
                var status = m == null ? "Connected" : $"Connected ({m.Count} models available, ping: {ping})";
                sdkStopwatch.Stop();

                return (authed, auth, m, status, ping, sdkStopwatch.Elapsed.TotalSeconds);
            });

            try
            {
                await Task.WhenAll(cliTask, sdkTask);
            }
            catch
            {
                // Individual results handled below
            }

            if (cliTask.IsCompletedSuccessfully)
            {
                (cliPath, versionStatus, var cliSeconds) = cliTask.Result;
                metrics?.StageSeconds.TryAdd("Startup: CLI discovery", cliSeconds);
            }

            if (sdkTask.IsCompletedSuccessfully)
            {
                (isAuthenticated, authStatus, models, sdkStatus, var pingStatus, var sdkSeconds) = sdkTask.Result;
                metrics?.StageSeconds.TryAdd("Startup: SDK ready", sdkSeconds);
                if (!string.Equals(pingStatus, "OK", StringComparison.OrdinalIgnoreCase))
                    metrics?.Warnings.Add("Copilot SDK ping failed during startup checks.");
            }
            else if (sdkTask.IsFaulted)
            {
                var ex = sdkTask.Exception?.InnerException ?? sdkTask.Exception;
                authStatus = ex?.Message ?? "Unknown error";
                sdkStatus = $"Not ready: {Truncate(ex?.Message ?? "Unknown error", 120)}";
                metrics?.Warnings.Add($"Copilot SDK startup failed: {ex?.Message ?? "Unknown error"}");
            }
        });

        var statusTable = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Grey)
            .AddColumn("[bold]Copilot startup status[/]")
            .AddColumn("[bold]Details[/]");

        statusTable.AddRow("CLI path", Markup.Escape(cliPath));
        statusTable.AddRow("CLI version", Markup.Escape(versionStatus));
        statusTable.AddRow("Auth", Markup.Escape(isAuthenticated ? $"Authenticated: {authStatus}" : $"Not authenticated: {authStatus}"));
        statusTable.AddRow("SDK", Markup.Escape(sdkStatus));

        AnsiConsole.Write(statusTable);
        AnsiConsole.WriteLine();

        return models;
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

    private static async Task<string> SelectModelAsync(string? modelArg, List<ModelInfo>? cachedModels, bool nonInteractive)
    {
        if (!string.IsNullOrWhiteSpace(modelArg))
            return modelArg.Trim();

        const string fallbackModel = "gpt-4.1";

        if (nonInteractive)
            return fallbackModel;

        try
        {
            var models = cachedModels;

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
                        models = await client.ListModelsAsync();
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
                    .Title("[yellow]Select a model[/]")
                    .PageSize(12)
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

    private static async Task<(string? Content, string Title)> GenerateVsCodeNewsletterAsync(
        DateOnly weekStart,
        DateOnly weekEnd,
        CacheService cache,
        string selectedModel,
        ILoggerFactory loggerFactory,
        RunMetrics metrics,
        bool debug)
    {
        var defaultTitle = "VS Code Insiders Weekly Newsletter";

        var feedService = new AtomFeedService(loggerFactory.CreateLogger<AtomFeedService>());
        var vscodeService = new VSCodeReleaseNotesService();
        VSCodeReleaseNotes? releaseNotes = null;
        List<ReleaseEntry> vscodeBlogEntries = [];
        List<ReleaseEntry> changelogEntries = [];
        List<ReleaseEntry> githubBlogEntries = [];
        VSCodeReleaseNotesFetchResult? releaseNotesResult = null;
        FeedFetchResult? vscodeBlogResult = null;
        FeedFetchResult? changelogResult = null;
        FeedFetchResult? githubBlogResult = null;

        await AnsiConsole.Progress().AutoClear(false).HideCompleted(false).StartAsync(async ctx =>
        {
            const string notesLabel = "VS Code release notes";
            const string vscodeBlogLabel = "VS Code blog feed";
            const string changelogLabel = "Copilot changelog feed";
            const string githubBlogLabel = "GitHub blog feed";

            var notesTask = AddInactiveTask(ctx, notesLabel);
            var vscodeBlogTask = AddInactiveTask(ctx, vscodeBlogLabel);
            var changelogTask = AddInactiveTask(ctx, changelogLabel);
            var githubBlogTask = AddInactiveTask(ctx, githubBlogLabel);

            releaseNotesResult = await RunTrackedTaskAsync(
                notesTask,
                notesLabel,
                () => vscodeService.GetReleaseNotesFetchResultForDateRangeAsync(weekStart, weekEnd),
                metrics,
                "Fetch: VS Code release notes");
            releaseNotes = releaseNotesResult.ReleaseNotes;

            vscodeBlogResult = await RunTrackedTaskAsync(
                vscodeBlogTask,
                vscodeBlogLabel,
                () => feedService.FetchFeedWithMetricsAsync(
                    VSCodeBlogUrl,
                    weekStart,
                    weekEnd,
                    preferShortSummary: true,
                    maxContentChars: 1000),
                metrics,
                "Fetch: VS Code blog");
            vscodeBlogEntries = vscodeBlogResult.Entries;

            changelogResult = await RunTrackedTaskAsync(
                changelogTask,
                changelogLabel,
                () => feedService.FetchFeedWithMetricsAsync(
                    ChangelogCopilotUrl,
                    weekStart,
                    weekEnd,
                    maxContentChars: 1500),
                metrics,
                "Fetch: Copilot changelog");
            changelogEntries = changelogResult.Entries;

            githubBlogResult = await RunTrackedTaskAsync(
                githubBlogTask,
                githubBlogLabel,
                () => feedService.FetchFeedWithMetricsAsync(
                    BlogUrl,
                    weekStart,
                    weekEnd,
                    preferShortSummary: true,
                    maxContentChars: 1000),
                metrics,
                "Fetch: GitHub blog");
            githubBlogEntries = githubBlogResult.Entries;
        });

        var vscodeMentionEntries = vscodeBlogEntries.Where(MentionsVsCode).ToList();
        var changelogVsCodeEntries = changelogEntries.Where(MentionsVsCode).ToList();
        var githubBlogVsCodeEntries = githubBlogEntries.Where(MentionsVsCode).ToList();

        metrics.SourceCounts.Add(new SourceCount(
            "VS Code Insiders",
            $"{releaseNotesResult?.CandidateUrlCount ?? 0} files",
            $"{releaseNotesResult?.MatchedSectionCount ?? 0} sections",
            $"{releaseNotesResult?.UniqueFeatureCount ?? 0} features",
            $"{releaseNotesResult?.SuccessfulUrlCount ?? 0} files parsed successfully"));
        metrics.SourceCounts.Add(new SourceCount(
            "VS Code Blog",
            (vscodeBlogResult?.TotalItems ?? 0).ToString(),
            (vscodeBlogResult?.InRangeItems ?? 0).ToString(),
            vscodeMentionEntries.Count.ToString(),
            "Posts mentioning VS Code"));
        metrics.SourceCounts.Add(new SourceCount(
            "GitHub Changelog",
            (changelogResult?.TotalItems ?? 0).ToString(),
            (changelogResult?.InRangeItems ?? 0).ToString(),
            changelogVsCodeEntries.Count.ToString(),
            "Copilot entries mentioning VS Code"));
        metrics.SourceCounts.Add(new SourceCount(
            "GitHub Blog",
            (githubBlogResult?.TotalItems ?? 0).ToString(),
            (githubBlogResult?.InRangeItems ?? 0).ToString(),
            githubBlogVsCodeEntries.Count.ToString(),
            "Posts mentioning VS Code"));

        if (releaseNotes == null ||
            (releaseNotes.Features.Count == 0 &&
             vscodeMentionEntries.Count == 0 &&
             changelogVsCodeEntries.Count == 0 &&
             githubBlogVsCodeEntries.Count == 0))
        {
            AnsiConsole.MarkupLine($"[yellow]⚠[/] No VS Code-related items found in [bold]{weekStart:yyyy-MM-dd}[/] to [bold]{weekEnd:yyyy-MM-dd}[/].");
            return (null, defaultTitle);
        }

        var categorySummary = releaseNotes.Features
            .GroupBy(f => f.Category)
            .OrderByDescending(g => g.Count())
            .Take(4)
            .Select(g => $"{g.Key} ({g.Count()})");

        var vscodeTable = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Grey)
            .AddColumn(new TableColumn("[bold]Source[/]").LeftAligned())
            .AddColumn(new TableColumn("[bold]Items[/]").Centered())
            .AddColumn(new TableColumn("[bold]Top categories[/]").LeftAligned());

        vscodeTable.AddRow("[cornflowerblue]VS Code Insiders[/]", $"[green]{releaseNotes.Features.Count}[/]", string.Join(", ", categorySummary));
        vscodeTable.AddRow("[cornflowerblue]VS Code Blog[/]", $"[green]{vscodeMentionEntries.Count}[/]", "Posts mentioning VS Code");
        vscodeTable.AddRow("[cornflowerblue]GitHub Changelog[/]", $"[green]{changelogVsCodeEntries.Count}[/]", "Copilot changelog items mentioning VS Code");
        vscodeTable.AddRow("[cornflowerblue]GitHub Blog[/]", $"[green]{githubBlogVsCodeEntries.Count}[/]", "Posts mentioning VS Code");

        AnsiConsole.Write(vscodeTable);
        AnsiConsole.WriteLine();

        var newsletterService = new NewsletterService(loggerFactory.CreateLogger<NewsletterService>());
        string content = string.Empty;
        string title = defaultTitle;

        await AnsiConsole.Progress().AutoClear(false).HideCompleted(false).StartAsync(async ctx =>
        {
            const string sectionLabel = "Generate newsletter content";
            const string titleLabel = "Generate title";

            var sectionTask = AddInactiveTask(ctx, sectionLabel);
            var titleTask = AddInactiveTask(ctx, titleLabel);

            content = await RunTrackedTaskAsync(
                sectionTask,
                sectionLabel,
                () => newsletterService.GenerateVsCodeNewsletterAsync(
                    releaseNotes,
                    vscodeMentionEntries,
                    changelogVsCodeEntries,
                    githubBlogVsCodeEntries,
                    weekStart,
                    weekEnd,
                    cache,
                    selectedModel),
                metrics,
                "Generate: VS Code newsletter");

            var welcomeSummary = ExtractWelcomeSummary(content);
            var newsletterLabel = GetNewsletterLabel(NewsletterType.VSCode);
            title = await RunTrackedTaskAsync(
                titleTask,
                titleLabel,
                () => newsletterService.GenerateNewsletterTitleAsync(
                    welcomeSummary,
                    newsletterLabel,
                    cache,
                    selectedModel),
                metrics,
                "Generate: Newsletter title");
        });

        if (string.IsNullOrWhiteSpace(content))
        {
            AnsiConsole.MarkupLine("[yellow]⚠[/] Empty VS Code newsletter result.");
            return (null, defaultTitle);
        }

        return (content, title);
    }

    private static string ExtractWelcomeSummary(string content)
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

    private static async Task<(string? Content, string Title)> GenerateCopilotNewsletterAsync(
        DateOnly weekStart,
        DateOnly weekEnd,
        CacheService cache,
        string selectedModel,
        ILoggerFactory loggerFactory,
        RunMetrics metrics,
        bool debug)
    {
        var defaultTitle = "GitHub Copilot CLI/SDK Weekly Newsletter";
        var feedService = new AtomFeedService(loggerFactory.CreateLogger<AtomFeedService>());
        var log = loggerFactory.CreateLogger("CopilotNewsletter");

        List<ReleaseEntry> cliReleases = [];
        List<ReleaseEntry> sdkReleases = [];
        List<ReleaseEntry> changelogEntries = [];
        List<ReleaseEntry> blogEntries = [];
        FeedFetchResult? cliFetchResult = null;
        FeedFetchResult? sdkFetchResult = null;
        FeedFetchResult? changelogFetchResult = null;
        FeedFetchResult? blogFetchResult = null;

        await AnsiConsole.Progress().AutoClear(false).HideCompleted(false).StartAsync(async ctx =>
        {
            const string cliLabel = "Copilot CLI releases";
            const string sdkLabel = "Copilot SDK releases";
            const string changelogLabel = "Copilot changelog feed";
            const string blogLabel = "GitHub blog feed";

            var cliTask = AddInactiveTask(ctx, cliLabel);
            var sdkTask = AddInactiveTask(ctx, sdkLabel);
            var changelogTask = AddInactiveTask(ctx, changelogLabel);
            var blogTask = AddInactiveTask(ctx, blogLabel);

            cliFetchResult = await RunTrackedTaskAsync(
                cliTask,
                cliLabel,
                () => feedService.FetchFeedWithMetricsAsync(CliAtomUrl, weekStart, weekEnd),
                metrics,
                "Fetch: Copilot CLI releases");
            cliReleases = cliFetchResult.Entries;

            sdkFetchResult = await RunTrackedTaskAsync(
                sdkTask,
                sdkLabel,
                () => feedService.FetchFeedWithMetricsAsync(SdkAtomUrl, weekStart, weekEnd),
                metrics,
                "Fetch: Copilot SDK releases");
            sdkReleases = sdkFetchResult.Entries;

            changelogFetchResult = await RunTrackedTaskAsync(
                changelogTask,
                changelogLabel,
                () => feedService.FetchFeedWithMetricsAsync(
                    ChangelogCopilotUrl,
                    weekStart,
                    weekEnd,
                    maxContentChars: 1500),
                metrics,
                "Fetch: Copilot changelog");
            changelogEntries = changelogFetchResult.Entries;

            blogFetchResult = await RunTrackedTaskAsync(
                blogTask,
                blogLabel,
                () => feedService.FetchFeedWithMetricsAsync(
                    BlogUrl,
                    weekStart,
                    weekEnd,
                    categoryKeywords: ["copilot", "github copilot cli", "github cli"],
                    preferShortSummary: true,
                    maxContentChars: 800),
                metrics,
                "Fetch: GitHub blog");
            blogEntries = blogFetchResult.Entries;
        });

        var cliPreCount = cliReleases.Count;
        var sdkPreCount = sdkReleases.Count;
        cliReleases = AtomFeedService.ConsolidatePrereleases(cliReleases);
        sdkReleases = AtomFeedService.ConsolidatePrereleases(sdkReleases);

        log.LogInformation("ConsolidatePrereleases: CLI {Before}->{After}, SDK {SdkBefore}->{SdkAfter}",
            cliPreCount, cliReleases.Count, sdkPreCount, sdkReleases.Count);

        metrics.SourceCounts.Add(new SourceCount(
            "Copilot CLI releases",
            (cliFetchResult?.TotalItems ?? 0).ToString(),
            (cliFetchResult?.InRangeItems ?? 0).ToString(),
            cliReleases.Count.ToString(),
            $"{cliFetchResult?.MatchedItems ?? 0} matched before prerelease consolidation"));
        metrics.SourceCounts.Add(new SourceCount(
            "Copilot SDK releases",
            (sdkFetchResult?.TotalItems ?? 0).ToString(),
            (sdkFetchResult?.InRangeItems ?? 0).ToString(),
            sdkReleases.Count.ToString(),
            $"{sdkFetchResult?.MatchedItems ?? 0} matched before prerelease consolidation"));
        metrics.SourceCounts.Add(new SourceCount(
            "Changelog (Copilot)",
            (changelogFetchResult?.TotalItems ?? 0).ToString(),
            (changelogFetchResult?.InRangeItems ?? 0).ToString(),
            changelogEntries.Count.ToString(),
            "Feed items"));
        metrics.SourceCounts.Add(new SourceCount(
            "Blog (Copilot/CLI)",
            (blogFetchResult?.TotalItems ?? 0).ToString(),
            (blogFetchResult?.InRangeItems ?? 0).ToString(),
            blogEntries.Count.ToString(),
            "Filtered by category"));

        static string CountCell(int n) => n == 0 ? "[dim]0[/]" : $"[green]{n}[/]";
        static string ItemsCell(IEnumerable<ReleaseEntry> entries, int max = 3)
        {
            var titles = entries.Take(max)
                .Select(e => $"[white]{Markup.Escape(e.Version.Length > 40 ? e.Version[..40] + "..." : e.Version)}[/]");
            var list = string.Join(", ", titles);
            return list.Length == 0 ? "[dim]none[/]" : list;
        }

        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Grey)
            .AddColumn(new TableColumn("[bold]Source[/]").LeftAligned())
            .AddColumn(new TableColumn("[bold]Items[/]").Centered())
            .AddColumn(new TableColumn("[bold]Recent entries[/]").LeftAligned());

        table.AddRow("[cornflowerblue]Copilot CLI releases[/]", CountCell(cliReleases.Count), ItemsCell(cliReleases));
        table.AddRow("[cornflowerblue]Copilot SDK releases[/]", CountCell(sdkReleases.Count), ItemsCell(sdkReleases));
        table.AddRow("[cornflowerblue]Changelog (Copilot)[/]", CountCell(changelogEntries.Count), ItemsCell(changelogEntries));
        table.AddRow("[cornflowerblue]Blog (Copilot/CLI)[/]", CountCell(blogEntries.Count), ItemsCell(blogEntries));

        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();

        if (cliReleases.Count == 0 && sdkReleases.Count == 0 && changelogEntries.Count == 0 && blogEntries.Count == 0)
        {
            log.LogWarning("No items found for date range {Start} to {End}", weekStart, weekEnd);
            AnsiConsole.MarkupLine($"[yellow]⚠[/] No items found in [bold]{weekStart:yyyy-MM-dd}[/] to [bold]{weekEnd:yyyy-MM-dd}[/].");
            return (null, defaultTitle);
        }

        static string ExtractTLDRBullets(string releaseSection)
        {
            var lines = releaseSection.Split('\n');
            var bullets = new StringBuilder();
            bool inTLDR = false;

            foreach (var line in lines)
            {
                if (line.Contains("### GitHub Copilot CLI") || line.Contains("### GitHub Copilot SDK"))
                {
                    inTLDR = true;
                    continue;
                }

                if (line.StartsWith("## Releases") || line.StartsWith("---"))
                    inTLDR = false;

                if (inTLDR && line.TrimStart().StartsWith("-"))
                    bullets.AppendLine(line);
            }

            return bullets.ToString();
        }

        string newsSection = string.Empty;
        string releaseSection = string.Empty;
        string welcomeSummary = string.Empty;

        var newsletterService = new NewsletterService(loggerFactory.CreateLogger<NewsletterService>());
        await AnsiConsole.Progress().AutoClear(false).HideCompleted(false).StartAsync(async ctx =>
        {
            const string newsLabel = "News and announcements";
            const string releaseLabel = "Project updates";
            const string welcomeLabel = "Welcome summary";
            const string titleLabel = "Newsletter title";

            var newsTask = AddInactiveTask(ctx, newsLabel);
            var releaseTask = AddInactiveTask(ctx, releaseLabel);
            var welcomeTask = AddInactiveTask(ctx, welcomeLabel);
            var titleTask = AddInactiveTask(ctx, titleLabel);

            try
            {
                var newsSectionTask = (changelogEntries.Count > 0 || blogEntries.Count > 0)
                    ? RunTrackedTaskAsync(
                        newsTask,
                        newsLabel,
                        () => newsletterService.GenerateNewsAndAnnouncementsAsync(
                        changelogEntries,
                        blogEntries,
                        weekStart,
                        weekEnd,
                        cache,
                        selectedModel),
                        metrics,
                        "Generate: News and announcements")
                    : Task.FromResult(string.Empty);

                var releaseSectionTask = RunTrackedTaskAsync(
                    releaseTask,
                    releaseLabel,
                    () => newsletterService.GenerateReleaseSectionAsync(
                    cliReleases,
                    sdkReleases,
                    weekStart,
                    weekEnd,
                    cache,
                    selectedModel),
                    metrics,
                    "Generate: Project updates");

                await Task.WhenAll(newsSectionTask, releaseSectionTask);

                newsSection = await newsSectionTask;

                releaseSection = await releaseSectionTask;

                var releaseSummaryBullets = ExtractTLDRBullets(releaseSection);
                welcomeSummary = await RunTrackedTaskAsync(
                    welcomeTask,
                    welcomeLabel,
                    () => newsletterService.GenerateWelcomeSummaryAsync(
                        newsSection,
                        releaseSummaryBullets,
                        weekStart,
                        weekEnd,
                        cache,
                        selectedModel),
                    metrics,
                    "Generate: Welcome summary");

                var newsletterLabel = GetNewsletterLabel(NewsletterType.CopilotCliSdk);
                defaultTitle = await RunTrackedTaskAsync(
                    titleTask,
                    titleLabel,
                    () => newsletterService.GenerateNewsletterTitleAsync(
                        welcomeSummary,
                        newsletterLabel,
                        cache,
                        selectedModel),
                    metrics,
                    "Generate: Newsletter title");
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Error generating newsletter sections");
                RenderFriendlyException(ex, debug);
            }
        });

        if (string.IsNullOrEmpty(releaseSection))
        {
            log.LogWarning("releaseSection is empty, returning null");
            return (null, defaultTitle);
        }

        var contentBuilder = new StringBuilder();
        contentBuilder.AppendLine("Welcome");
        contentBuilder.AppendLine("--------");
        contentBuilder.AppendLine();
        contentBuilder.AppendLine("This is your weekly update for GitHub Copilot CLI & SDK! Feel free to forward internally and encourage your co-workers to subscribe at [https://aka.ms/copilot-cli-insiders/join](https://aka.ms/copilot-cli-insiders/join) and forward this newsletter around!");
        contentBuilder.AppendLine();
        contentBuilder.AppendLine(welcomeSummary);
        contentBuilder.AppendLine();
        contentBuilder.AppendLine("* * * * *");
        contentBuilder.AppendLine();

        if (!string.IsNullOrEmpty(newsSection))
        {
            contentBuilder.AppendLine(newsSection);
            contentBuilder.AppendLine();
            contentBuilder.AppendLine("* * * * *");
            contentBuilder.AppendLine();
        }

        contentBuilder.Append(releaseSection);
        return (contentBuilder.ToString(), defaultTitle);
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

        // Treat any non-empty content before the first heading as a "welcome" section.
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
}

internal sealed class RunMetrics
{
    public List<SourceCount> SourceCounts { get; } = [];
    public Dictionary<string, double> StageSeconds { get; } = [];
    public List<string> Warnings { get; } = [];
    public int CacheHits { get; set; }
    public int CacheMisses { get; set; }
    public int CacheSkips { get; set; }
    public IReadOnlyList<CacheSectionMetric> CacheSections { get; set; } = [];
    public double TotalWallSeconds { get; set; }
    public bool OverwroteOutput { get; set; }
    public string? OutputPath { get; set; }
    public int OutputCharacters { get; set; }
    public int OutputLines { get; set; }
    public int OutputSections { get; set; }
    public bool StreamingEnabled { get; set; } = true;
    public string ReasoningEffort { get; set; } = "low";
}

internal sealed record SourceCount(string Source, string RawCount, string FilteredCount, string FinalCount, string Notes);
