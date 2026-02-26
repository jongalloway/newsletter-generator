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

            var availableModels = await PrintCopilotStartupStatusAsync();

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
                }
                else
                {
                    AnsiConsole.MarkupLine("[dim]No cache to clear[/]");
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

                if (metrics.OverwroteOutput)
                {
                    AnsiConsole.MarkupLine($"[yellow]⚠[/] Overwriting existing file [underline]{Markup.Escape(outputPath)}[/]");
                    File.SetAttributes(outputPath, FileAttributes.Normal);
                }

                await File.WriteAllTextAsync(outputPath, content, Encoding.UTF8);

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
        var summaryTable = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Grey)
            .AddColumn("[bold]Metric[/]")
            .AddColumn("[bold]Value[/]");

        summaryTable.AddRow("Newsletter", Markup.Escape(GetNewsletterLabel(newsletter)));
        summaryTable.AddRow("Model", Markup.Escape(model));
        summaryTable.AddRow("Date range", $"{weekStart:yyyy-MM-dd} -> {weekEnd:yyyy-MM-dd}");
        summaryTable.AddRow("Cache mode", useCache ? "Read/write" : "Force refresh");
        summaryTable.AddRow("Cache hits", metrics.CacheHits.ToString());
        summaryTable.AddRow("Cache misses", metrics.CacheMisses.ToString());
        summaryTable.AddRow("Cache skips", metrics.CacheSkips.ToString());
        summaryTable.AddRow("Output file", string.IsNullOrWhiteSpace(metrics.OutputPath) ? "(none)" : Markup.Escape(metrics.OutputPath));
        summaryTable.AddRow("Overwrite", metrics.OverwroteOutput ? "Yes" : "No");

        var sourceTable = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Grey)
            .AddColumn("[bold]Source[/]")
            .AddColumn("[bold]Items[/]")
            .AddColumn("[bold]Notes[/]");

        foreach (var count in metrics.SourceCounts)
            sourceTable.AddRow(Markup.Escape(count.Source), count.Count.ToString(), Markup.Escape(count.Notes));

        var chart = new BarChart()
            .Width(70)
            .Label("[bold]Stage Duration (seconds)[/]")
            .CenterLabel();

        foreach (var kvp in metrics.StageSeconds.OrderByDescending(k => k.Value))
            chart.AddItem(kvp.Key, kvp.Value, Color.CornflowerBlue);

        var layout = new Layout("Root")
            .SplitRows(
                new Layout("Top").Update(summaryTable),
                new Layout("Middle").Update(sourceTable),
                new Layout("Bottom").Update(new Panel(chart).Header("[cornflowerblue]Run Dashboard[/]").Border(BoxBorder.Rounded).BorderColor(Color.Grey))
            );

        AnsiConsole.Write(layout);
        AnsiConsole.WriteLine();
    }

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

    public static async Task<List<ModelInfo>?> PrintCopilotStartupStatusAsync()
    {
        var cliPath = await TryFindCopilotCliOnPathAsync() ?? "copilot";

        string versionStatus;
        var versionResult = await TryRunProcessAsync(cliPath, "--version");
        if (versionResult.success && versionResult.exitCode == 0)
            versionStatus = string.IsNullOrWhiteSpace(versionResult.standardOutput) ? "Available" : versionResult.standardOutput;
        else
            versionStatus = string.IsNullOrWhiteSpace(versionResult.standardError) ? "Unavailable" : versionResult.standardError;

        string authStatus;
        bool isAuthenticated;
        string sdkStatus;
        List<ModelInfo>? models = null;

        try
        {
            await using var client = new CopilotClient();
            var sdkAuthStatus = await client.GetAuthStatusAsync();

            isAuthenticated = !string.IsNullOrEmpty(sdkAuthStatus.Login);
            authStatus = string.IsNullOrWhiteSpace(sdkAuthStatus.StatusMessage)
                ? (isAuthenticated ? "Authenticated" : "Not authenticated")
                : sdkAuthStatus.StatusMessage;

            await client.StartAsync();
            models = await client.ListModelsAsync();
            sdkStatus = models == null ? "Connected" : $"Connected ({models.Count} models available)";
        }
        catch (Exception ex)
        {
            isAuthenticated = false;
            authStatus = ex.Message;
            sdkStatus = $"Not ready: {Truncate(ex.Message, 120)}";
        }

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
                        var task = ctx.AddTask("[cornflowerblue]Querying available models[/]", maxValue: 100);
                        await using var client = new CopilotClient();
                        task.Increment(40);
                        await client.StartAsync();
                        task.Increment(30);
                        models = await client.ListModelsAsync();
                        task.Increment(30);
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

        var fetchStopwatch = Stopwatch.StartNew();
        await AnsiConsole.Progress().AutoClear(false).HideCompleted(false).StartAsync(async ctx =>
        {
            var notesTask = ctx.AddTask("[cornflowerblue]VS Code release notes[/]", maxValue: 100);
            var vscodeBlogTask = ctx.AddTask("[cornflowerblue]VS Code blog feed[/]", maxValue: 100);
            var changelogTask = ctx.AddTask("[cornflowerblue]Copilot changelog feed[/]", maxValue: 100);
            var githubBlogTask = ctx.AddTask("[cornflowerblue]GitHub blog feed[/]", maxValue: 100);

            releaseNotes = await vscodeService.GetReleaseNotesForDateRangeAsync(weekStart, weekEnd);
            notesTask.Increment(100);

            vscodeBlogEntries = await feedService.FetchFeedAsync(
                VSCodeBlogUrl,
                weekStart,
                weekEnd,
                preferShortSummary: true,
                maxContentChars: 1000);
            vscodeBlogTask.Increment(100);

            changelogEntries = await feedService.FetchFeedAsync(
                ChangelogCopilotUrl,
                weekStart,
                weekEnd,
                maxContentChars: 1500);
            changelogTask.Increment(100);

            githubBlogEntries = await feedService.FetchFeedAsync(
                BlogUrl,
                weekStart,
                weekEnd,
                preferShortSummary: true,
                maxContentChars: 1000);
            githubBlogTask.Increment(100);
        });
        fetchStopwatch.Stop();
        metrics.StageSeconds["Fetch sources"] = fetchStopwatch.Elapsed.TotalSeconds;

        var vscodeMentionEntries = vscodeBlogEntries.Where(MentionsVsCode).ToList();
        var changelogVsCodeEntries = changelogEntries.Where(MentionsVsCode).ToList();
        var githubBlogVsCodeEntries = githubBlogEntries.Where(MentionsVsCode).ToList();

        metrics.SourceCounts.Add(new SourceCount("VS Code Insiders", releaseNotes?.Features.Count ?? 0, "Parsed features"));
        metrics.SourceCounts.Add(new SourceCount("VS Code Blog", vscodeMentionEntries.Count, "Mentions VS Code"));
        metrics.SourceCounts.Add(new SourceCount("GitHub Changelog", changelogVsCodeEntries.Count, "Copilot entries mentioning VS Code"));
        metrics.SourceCounts.Add(new SourceCount("GitHub Blog", githubBlogVsCodeEntries.Count, "Posts mentioning VS Code"));

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

        var generationStopwatch = Stopwatch.StartNew();
        await AnsiConsole.Progress().AutoClear(false).HideCompleted(false).StartAsync(async ctx =>
        {
            var sectionTask = ctx.AddTask("[cornflowerblue]Generate newsletter content[/]", maxValue: 100);
            var titleTask = ctx.AddTask("[cornflowerblue]Generate title[/]", maxValue: 100);

            content = await newsletterService.GenerateVsCodeNewsletterAsync(
                releaseNotes,
                vscodeMentionEntries,
                changelogVsCodeEntries,
                githubBlogVsCodeEntries,
                weekStart,
                weekEnd,
                cache,
                selectedModel);
            sectionTask.Increment(100);

            var welcomeSummary = ExtractWelcomeSummary(content);
            var newsletterLabel = GetNewsletterLabel(NewsletterType.VSCode);
            title = await newsletterService.GenerateNewsletterTitleAsync(
                welcomeSummary,
                newsletterLabel,
                cache,
                selectedModel);
            titleTask.Increment(100);
        });
        generationStopwatch.Stop();
        metrics.StageSeconds["Generate content"] = generationStopwatch.Elapsed.TotalSeconds;

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

        var fetchStopwatch = Stopwatch.StartNew();
        await AnsiConsole.Progress().AutoClear(false).HideCompleted(false).StartAsync(async ctx =>
        {
            var cliTask = ctx.AddTask("[cornflowerblue]Copilot CLI releases[/]", maxValue: 100);
            var sdkTask = ctx.AddTask("[cornflowerblue]Copilot SDK releases[/]", maxValue: 100);
            var changelogTask = ctx.AddTask("[cornflowerblue]Copilot changelog feed[/]", maxValue: 100);
            var blogTask = ctx.AddTask("[cornflowerblue]GitHub blog feed[/]", maxValue: 100);

            cliReleases = await feedService.FetchFeedAsync(CliAtomUrl, weekStart, weekEnd);
            cliTask.Increment(100);

            sdkReleases = await feedService.FetchFeedAsync(SdkAtomUrl, weekStart, weekEnd);
            sdkTask.Increment(100);

            changelogEntries = await feedService.FetchFeedAsync(
                ChangelogCopilotUrl,
                weekStart,
                weekEnd,
                maxContentChars: 1500);
            changelogTask.Increment(100);

            blogEntries = await feedService.FetchFeedAsync(
                BlogUrl,
                weekStart,
                weekEnd,
                categoryKeywords: ["copilot", "github copilot cli", "github cli"],
                preferShortSummary: true,
                maxContentChars: 800);
            blogTask.Increment(100);
        });
        fetchStopwatch.Stop();
        metrics.StageSeconds["Fetch sources"] = fetchStopwatch.Elapsed.TotalSeconds;

        var cliPreCount = cliReleases.Count;
        var sdkPreCount = sdkReleases.Count;
        cliReleases = AtomFeedService.ConsolidatePrereleases(cliReleases);
        sdkReleases = AtomFeedService.ConsolidatePrereleases(sdkReleases);

        log.LogInformation("ConsolidatePrereleases: CLI {Before}->{After}, SDK {SdkBefore}->{SdkAfter}",
            cliPreCount, cliReleases.Count, sdkPreCount, sdkReleases.Count);

        metrics.SourceCounts.Add(new SourceCount("Copilot CLI releases", cliReleases.Count, "After prerelease consolidation"));
        metrics.SourceCounts.Add(new SourceCount("Copilot SDK releases", sdkReleases.Count, "After prerelease consolidation"));
        metrics.SourceCounts.Add(new SourceCount("Changelog (Copilot)", changelogEntries.Count, "Feed items"));
        metrics.SourceCounts.Add(new SourceCount("Blog (Copilot/CLI)", blogEntries.Count, "Filtered by category"));

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
        var generationStopwatch = Stopwatch.StartNew();

        await AnsiConsole.Progress().AutoClear(false).HideCompleted(false).StartAsync(async ctx =>
        {
            var newsTask = ctx.AddTask("[cornflowerblue]News and announcements[/]", maxValue: 100);
            var releaseTask = ctx.AddTask("[cornflowerblue]Project updates[/]", maxValue: 100);
            var welcomeTask = ctx.AddTask("[cornflowerblue]Welcome summary[/]", maxValue: 100);
            var titleTask = ctx.AddTask("[cornflowerblue]Newsletter title[/]", maxValue: 100);

            try
            {
                if (changelogEntries.Count > 0 || blogEntries.Count > 0)
                {
                    newsSection = await newsletterService.GenerateNewsAndAnnouncementsAsync(
                        changelogEntries,
                        blogEntries,
                        weekStart,
                        weekEnd,
                        cache,
                        selectedModel);
                }
                newsTask.Increment(100);

                releaseSection = await newsletterService.GenerateReleaseSectionAsync(
                    cliReleases,
                    sdkReleases,
                    weekStart,
                    weekEnd,
                    cache,
                    selectedModel);
                releaseTask.Increment(100);

                var releaseSummaryBullets = ExtractTLDRBullets(releaseSection);
                welcomeSummary = await newsletterService.GenerateWelcomeSummaryAsync(
                    newsSection,
                    releaseSummaryBullets,
                    weekStart,
                    weekEnd,
                    cache,
                    selectedModel);
                welcomeTask.Increment(100);

                var newsletterLabel = GetNewsletterLabel(NewsletterType.CopilotCliSdk);
                defaultTitle = await newsletterService.GenerateNewsletterTitleAsync(
                    welcomeSummary,
                    newsletterLabel,
                    cache,
                    selectedModel);
                titleTask.Increment(100);
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Error generating newsletter sections");
                RenderFriendlyException(ex, debug);
            }
        });

        generationStopwatch.Stop();
        metrics.StageSeconds["Generate content"] = generationStopwatch.Elapsed.TotalSeconds;

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
}

internal sealed class RunMetrics
{
    public List<SourceCount> SourceCounts { get; } = [];
    public Dictionary<string, double> StageSeconds { get; } = [];
    public int CacheHits { get; set; }
    public int CacheMisses { get; set; }
    public int CacheSkips { get; set; }
    public bool OverwroteOutput { get; set; }
    public string? OutputPath { get; set; }
}

internal sealed record SourceCount(string Source, int Count, string Notes);
