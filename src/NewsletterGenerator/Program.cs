using System.Diagnostics;
using System.Text;
using GitHub.Copilot.SDK;
using NewsletterGenerator.Models;
using NewsletterGenerator.Services;
using Spectre.Console;

// ── Header ────────────────────────────────────────────────────────────────────
AnsiConsole.Write(
    new FigletText("Newsletter")
        .LeftJustified()
        .Color(Color.CornflowerBlue));

AnsiConsole.Write(new Rule("[cornflowerblue]GitHub Copilot CLI & SDK Weekly Generator[/]")
    .LeftJustified());
AnsiConsole.WriteLine();

// ── Args ──────────────────────────────────────────────────────────────────────
bool clearCacheFlag = args.Contains("--clear-cache") || args.Contains("-c");
bool forceRefreshFlag = args.Contains("--force-refresh") || args.Contains("-f");
var modelArg = GetOptionValue(args, "--model", "-m");
var newsletterArg = GetOptionValue(args, "--newsletter", "-n");
var numericArgs = args.Where(a => !a.StartsWith("-")).ToArray();

bool runAgain;

do
{
    var availableModels = await PrintCopilotStartupStatusAsync();

    var selectedNewsletter = ResolveNewsletterType(newsletterArg) ?? PromptForNewsletterType();
    var selectedModel = await SelectModelAsync(modelArg, availableModels);

    var startupOptions = PromptForStartupOptions(clearCacheFlag, forceRefreshFlag);
    var clearCache = clearCacheFlag || startupOptions.ClearCache;
    var forceRefresh = forceRefreshFlag || startupOptions.ForceRefresh;
    var useCache = !forceRefresh;

    AnsiConsole.MarkupLine($"[dim]Newsletter:[/] [white]{GetNewsletterLabel(selectedNewsletter)}[/]");
    AnsiConsole.MarkupLine($"[dim]Model:[/] [white]{selectedModel}[/]");
    AnsiConsole.MarkupLine($"[dim]Use cache:[/] [white]{(useCache ? "Yes" : "No")}[/]");
    AnsiConsole.MarkupLine($"[dim]Force refresh:[/] [white]{(forceRefresh ? "Yes" : "No")}[/]");
    AnsiConsole.WriteLine();

    if (clearCache)
    {
        var cacheDir = Path.Combine(Directory.GetCurrentDirectory(), ".cache");
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

    DateOnly weekStartDate;
    DateOnly weekEndDate;
    var today = DateOnly.FromDateTime(DateTime.Now);
    int selectedDaysBack;

    if (numericArgs.Length > 0 && int.TryParse(numericArgs[0], out var daysBack) && daysBack > 0)
    {
        selectedDaysBack = daysBack;
        AnsiConsole.MarkupLine($"[dim]Using CLI range: {selectedDaysBack} days back from {today}[/]");
    }
    else
    {
        selectedDaysBack = AnsiConsole.Prompt(
            new TextPrompt<int>("[yellow]How many days back?[/]")
                .DefaultValue(7)
                .PromptStyle("green")
                .Validate(days => days > 0
                    ? ValidationResult.Success()
                    : ValidationResult.Error("Days must be greater than 0.")));

        AnsiConsole.MarkupLine($"[dim]Using interactive range: {selectedDaysBack} days back from {today}[/]");
    }

    weekEndDate = today;
    weekStartDate = today.AddDays(-selectedDaysBack);

    var daySpan = weekEndDate.DayNumber - weekStartDate.DayNumber + 1;
    AnsiConsole.MarkupLine($"[dim]Date range:[/] [white]{weekStartDate:yyyy-MM-dd}[/] [dim]→[/] [white]{weekEndDate:yyyy-MM-dd}[/] [dim]({daySpan} days)[/]");
    AnsiConsole.WriteLine();

    var cache = new CacheService(forceRefresh: forceRefresh);

    string? content;
    if (selectedNewsletter == NewsletterType.VSCode)
    {
        content = await GenerateVsCodeNewsletterAsync(weekStartDate, weekEndDate, cache, selectedModel);
    }
    else
    {
        content = await GenerateCopilotNewsletterAsync(weekStartDate, weekEndDate, cache, selectedModel);
    }

    if (!string.IsNullOrWhiteSpace(content))
    {
        content = PrefixNewsletterName(content, selectedNewsletter, weekStartDate, weekEndDate, selectedModel);
        content = content.Replace('—', '-').Replace('–', '-');

        var outputDir = Path.Combine(Directory.GetCurrentDirectory(), "output");
        Directory.CreateDirectory(outputDir);

        var filename = $"newsletter-{GetNewsletterSlug(selectedNewsletter)}-{weekEndDate:yyyy-MM-dd}.md";
        var outputPath = Path.Combine(outputDir, filename);

        if (File.Exists(outputPath))
        {
            AnsiConsole.MarkupLine($"[yellow]⚠[/] Overwriting existing file [link={outputPath}][underline]{outputPath}[/][/] ");
            File.SetAttributes(outputPath, FileAttributes.Normal);
        }

        await File.WriteAllTextAsync(outputPath, content, Encoding.UTF8);

        AnsiConsole.MarkupLine($"[green]✓[/] Newsletter for [white]{weekStartDate:yyyy-MM-dd}[/] to [white]{weekEndDate:yyyy-MM-dd}[/] written to [link={outputPath}][underline]{outputPath}[/][/]");
        AnsiConsole.WriteLine();

        var preview = string.Join('\n', content.Split('\n').Take(25));

        AnsiConsole.Write(
            new Panel(Markup.Escape(preview))
                .Header("[cornflowerblue] Preview (first 25 lines) [/]")
                .Border(BoxBorder.Rounded)
                .BorderColor(Color.Grey)
                .Expand());
    }

    runAgain = AnsiConsole.Confirm("Generate another newsletter?", false);
    if (runAgain)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule("[grey]Starting new run[/]").LeftJustified());
        AnsiConsole.WriteLine();
    }
}
while (runAgain);

static string? GetOptionValue(string[] allArgs, string longName, string shortName)
{
    for (var i = 0; i < allArgs.Length - 1; i++)
    {
        if (allArgs[i] == longName || allArgs[i] == shortName)
            return allArgs[i + 1];
    }

    return null;
}

static NewsletterType? ResolveNewsletterType(string? input)
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

static NewsletterType PromptForNewsletterType()
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

static string GetNewsletterLabel(NewsletterType type) => type switch
{
    NewsletterType.VSCode => "VS Code Insiders",
    _ => "GitHub Copilot CLI/SDK"
};

static (bool ClearCache, bool ForceRefresh) PromptForStartupOptions(bool clearCacheFromArg, bool forceRefreshFromArg)
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
        ClearCache: startupChoices.Contains("Clear cache before run"),
        ForceRefresh: startupChoices.Contains("Force refresh (ignore cache reads this run)"));
}

static string GetNewsletterSlug(NewsletterType type) => type switch
{
    NewsletterType.VSCode => "vscode-insiders",
    _ => "copilot-cli-sdk"
};

static string PrefixNewsletterName(
    string content,
    NewsletterType type,
    DateOnly weekStart,
    DateOnly weekEnd,
    string model)
{
    var sb = new StringBuilder();
    sb.AppendLine($"# {GetNewsletterLabel(type)} Weekly Newsletter");
    sb.AppendLine();
    sb.AppendLine($"> Coverage: {weekStart:yyyy-MM-dd} to {weekEnd:yyyy-MM-dd}");
    sb.AppendLine($"> Model: {model}");
    sb.AppendLine();
    sb.AppendLine(content.TrimStart());
    return sb.ToString();
}

static bool MentionsVsCode(ReleaseEntry entry)
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

static async Task<List<ModelInfo>?> PrintCopilotStartupStatusAsync()
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

static async Task<string?> TryFindCopilotCliOnPathAsync()
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

static string? FirstNonEmptyLine(string value)
{
    return value
        .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
        .Select(line => line.Trim())
        .FirstOrDefault(line => !string.IsNullOrWhiteSpace(line));
}

static async Task<(bool success, string standardOutput, string standardError, int exitCode)> TryRunProcessAsync(string fileName, string arguments)
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
        {
            return (false, "", "", -1);
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        process.WaitForExit();

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

static string Truncate(string value, int max)
{
    if (string.IsNullOrWhiteSpace(value))
        return "(none)";

    return value.Length <= max ? value : value[..max] + "...";
}

static async Task<string> SelectModelAsync(string? modelArg, List<ModelInfo>? cachedModels = null)
{
    if (!string.IsNullOrWhiteSpace(modelArg))
        return modelArg.Trim();

    const string fallbackModel = "gpt-4.1";

    try
    {
        List<ModelInfo>? models = cachedModels;

        if (models == null || models.Count == 0)
        {
            await AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .SpinnerStyle(Style.Parse("cornflowerblue"))
                .StartAsync("Querying available models...", async ctx =>
                {
                    ctx.Status("Starting Copilot client...");
                    await using var client = new CopilotClient();
                    await client.StartAsync();

                    ctx.Status("Querying models from Copilot...");
                    models = await client.ListModelsAsync();
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

static async Task<string?> GenerateVsCodeNewsletterAsync(
    DateOnly weekStart,
    DateOnly weekEnd,
    CacheService cache,
    string selectedModel)
{
    const string VSCodeBlogUrl = "https://code.visualstudio.com/feed.xml";
    const string ChangelogCopilotUrl = "https://github.blog/changelog/label/copilot/feed/";
    const string BlogUrl = "https://github.blog/feed/";

    var feedService = new AtomFeedService();
    var vscodeService = new VSCodeReleaseNotesService();
    VSCodeReleaseNotes? releaseNotes = null;
    List<ReleaseEntry> vscodeBlogEntries = [];
    List<ReleaseEntry> changelogEntries = [];
    List<ReleaseEntry> githubBlogEntries = [];

    await AnsiConsole.Status()
        .Spinner(Spinner.Known.Dots)
        .SpinnerStyle(Style.Parse("cornflowerblue"))
        .StartAsync("Fetching VS Code Insiders release notes...", async ctx =>
        {
            ctx.Status("Resolving [bold]VS Code Insiders[/] and parsing weekly updates...");
            releaseNotes = await vscodeService.GetReleaseNotesForDateRangeAsync(weekStart, weekEnd);

            ctx.Status("Fetching [bold]VS Code Blog[/] posts...");
            vscodeBlogEntries = await feedService.FetchFeedAsync(
                VSCodeBlogUrl,
                weekStart,
                weekEnd,
                preferShortSummary: true,
                maxContentChars: 1000);

            ctx.Status("Fetching [bold]GitHub Changelog[/] (Copilot label)...");
            changelogEntries = await feedService.FetchFeedAsync(
                ChangelogCopilotUrl,
                weekStart,
                weekEnd,
                maxContentChars: 1500);

            ctx.Status("Fetching [bold]GitHub Blog[/] posts...");
            githubBlogEntries = await feedService.FetchFeedAsync(
                BlogUrl,
                weekStart,
                weekEnd,
                preferShortSummary: true,
                maxContentChars: 1000);
        });

    var vscodeMentionEntries = vscodeBlogEntries
        .Where(MentionsVsCode)
        .ToList();

    var changelogVsCodeEntries = changelogEntries
        .Where(MentionsVsCode)
        .ToList();

    var githubBlogVsCodeEntries = githubBlogEntries
        .Where(MentionsVsCode)
        .ToList();

    if (releaseNotes == null ||
        (releaseNotes.Features.Count == 0 &&
         vscodeMentionEntries.Count == 0 &&
         changelogVsCodeEntries.Count == 0 &&
         githubBlogVsCodeEntries.Count == 0))
    {
        AnsiConsole.MarkupLine($"[yellow]⚠[/]  No VS Code-related items found in the date range [bold]{weekStart:yyyy-MM-dd}[/] to [bold]{weekEnd:yyyy-MM-dd}[/].");
        return null;
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

    vscodeTable.AddRow(
        "[cornflowerblue]VS Code Insiders[/]",
        $"[green]{releaseNotes?.Features.Count ?? 0}[/]",
        string.Join(", ", categorySummary));
    vscodeTable.AddRow(
        "[cornflowerblue]VS Code Blog[/]",
        $"[green]{vscodeMentionEntries.Count}[/]",
        "Posts mentioning VS Code");
    vscodeTable.AddRow(
        "[cornflowerblue]GitHub Changelog[/]",
        $"[green]{changelogVsCodeEntries.Count}[/]",
        "Copilot changelog items mentioning VS Code");
    vscodeTable.AddRow(
        "[cornflowerblue]GitHub Blog[/]",
        $"[green]{githubBlogVsCodeEntries.Count}[/]",
        "Posts mentioning VS Code");

    AnsiConsole.Write(vscodeTable);
    AnsiConsole.WriteLine();

    var newsletterService = new NewsletterService();
    var content = await AnsiConsole.Status()
        .Spinner(Spinner.Known.Star)
        .SpinnerStyle(Style.Parse("cornflowerblue"))
        .StartAsync("Generating newsletter via GitHub Copilot...", async ctx =>
        {
            ctx.Status("Generating VS Code newsletter...");
            return await newsletterService.GenerateVsCodeNewsletterAsync(
                releaseNotes!,
                vscodeMentionEntries,
                changelogVsCodeEntries,
                githubBlogVsCodeEntries,
                weekStart,
                weekEnd,
                cache,
                selectedModel);
        });

    if (string.IsNullOrWhiteSpace(content))
    {
        AnsiConsole.MarkupLine("[yellow]⚠[/] Empty VS Code newsletter result.");
        return null;
    }

    return content;
}

static async Task<string?> GenerateCopilotNewsletterAsync(
    DateOnly weekStart,
    DateOnly weekEnd,
    CacheService cache,
    string selectedModel)
{
    const string CliAtomUrl = "https://github.com/github/copilot-cli/releases.atom";
    const string SdkAtomUrl = "https://github.com/github/copilot-sdk/releases.atom";
    const string ChangelogCopilotUrl = "https://github.blog/changelog/label/copilot/feed/";
    const string BlogUrl = "https://github.blog/feed/";

    var feedService = new AtomFeedService();

    List<ReleaseEntry> cliReleases = [];
    List<ReleaseEntry> sdkReleases = [];
    List<ReleaseEntry> changelogEntries = [];
    List<ReleaseEntry> blogEntries = [];

    await AnsiConsole.Status()
        .Spinner(Spinner.Known.Dots)
        .SpinnerStyle(Style.Parse("cornflowerblue"))
        .StartAsync("Fetching feeds...", async ctx =>
        {
            ctx.Status("Fetching [bold]Copilot CLI[/] releases...");
            cliReleases = await feedService.FetchFeedAsync(CliAtomUrl, weekStart, weekEnd);

            ctx.Status("Fetching [bold]Copilot SDK[/] releases...");
            sdkReleases = await feedService.FetchFeedAsync(SdkAtomUrl, weekStart, weekEnd);

            ctx.Status("Fetching [bold]GitHub Changelog[/] (Copilot label)...");
            changelogEntries = await feedService.FetchFeedAsync(
                ChangelogCopilotUrl, weekStart, weekEnd,
                maxContentChars: 1500);

            ctx.Status("Fetching [bold]GitHub Blog[/] (Copilot/CLI posts)...");
            blogEntries = await feedService.FetchFeedAsync(
                BlogUrl, weekStart, weekEnd,
                categoryKeywords: ["copilot", "github copilot cli", "github cli"],
                preferShortSummary: true,
                maxContentChars: 800);
        });

    static string CountCell(int n) => n == 0 ? "[dim]0[/]" : $"[green]{n}[/]";
    static string ItemsCell(IEnumerable<ReleaseEntry> entries, int max = 3)
    {
        var titles = entries.Take(max).Select(e => $"[white]{Markup.Escape(e.Version.Length > 40 ? e.Version[..40] + "…" : e.Version)}[/]");
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
        AnsiConsole.MarkupLine($"[yellow]⚠[/]  No items found in the date range [bold]{weekStart:yyyy-MM-dd}[/] to [bold]{weekEnd:yyyy-MM-dd}[/].");
        return null;
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
            {
                inTLDR = false;
            }

            if (inTLDR && line.TrimStart().StartsWith("-"))
            {
                bullets.AppendLine(line);
            }
        }

        return bullets.ToString();
    }

    string newsSection = string.Empty;
    string releaseSection = string.Empty;
    string welcomeSummary = string.Empty;

    var newsletterService = new NewsletterService();
    await AnsiConsole.Status()
        .Spinner(Spinner.Known.Star)
        .SpinnerStyle(Style.Parse("cornflowerblue"))
        .StartAsync("Generating newsletter via GitHub Copilot...", async ctx =>
        {
            try
            {
                if (changelogEntries.Count > 0 || blogEntries.Count > 0)
                {
                    ctx.Status("Generating News and Announcements...");
                    newsSection = await newsletterService.GenerateNewsAndAnnouncementsAsync(
                        changelogEntries, blogEntries, weekStart, weekEnd, cache, selectedModel);
                }

                ctx.Status("Generating Project updates...");
                releaseSection = await newsletterService.GenerateReleaseSectionAsync(
                    cliReleases, sdkReleases, weekStart, weekEnd, cache, selectedModel);

                var releaseSummaryBullets = ExtractTLDRBullets(releaseSection);

                ctx.Status("Generating Welcome summary...");
                welcomeSummary = await newsletterService.GenerateWelcomeSummaryAsync(
                    newsSection, releaseSummaryBullets, weekStart, weekEnd, cache, selectedModel);
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]✗[/] Error: [red]{Markup.Escape(ex.Message)}[/]");
                AnsiConsole.MarkupLine("[dim]Make sure the GitHub Copilot CLI is installed and in your PATH.[/]");
            }
        });

    if (string.IsNullOrEmpty(releaseSection))
        return null;

    var contentBuilder = new StringBuilder();
    contentBuilder.AppendLine("Welcome");
    contentBuilder.AppendLine("--------");
    contentBuilder.AppendLine();
    contentBuilder.AppendLine("This is your weekly  update for GitHub Copilot CLI & SDK! Feel free to forward internally and encourage your co-workers to subscribe at [https://aka.ms/copilot-cli-insiders/join](https://aka.ms/copilot-cli-insiders/join) and forward this newsletter around!");
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
    return contentBuilder.ToString();
}
