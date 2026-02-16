using System.Text;
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
// Check for --clear-cache flag
bool clearCache = args.Contains("--clear-cache") || args.Contains("-c");

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

// Determine the week to use based on current day of week:
// Monday-Tuesday: previous complete week (Monday-Sunday)
// Wednesday-Sunday: current week (Monday through today)

DateOnly weekStartDate, weekEndDate;

// Filter out flag arguments to get numeric argument
var numericArgs = args.Where(a => !a.StartsWith("-")).ToArray();

if (numericArgs.Length > 0 && int.TryParse(numericArgs[0], out var daysBack))
{
    // Manual override: use specified days back
    var today = DateOnly.FromDateTime(DateTime.Now);
    weekEndDate = today;
    weekStartDate = today.AddDays(-daysBack);
    
    AnsiConsole.MarkupLine($"[dim]Manual override: {daysBack} days back from {today}[/]");
}
else
{
    // Auto-determine based on day of week
    var today = DateOnly.FromDateTime(DateTime.Now);
    var dayOfWeek = today.DayOfWeek;

    AnsiConsole.MarkupLine($"[dim]Today is {today:yyyy-MM-dd} ({dayOfWeek})[/]");

    // Determine which week: previous complete week (Mon-Tue) or current week through today (Wed-Sun)
    bool usePreviousWeek = dayOfWeek is DayOfWeek.Monday or DayOfWeek.Tuesday;

    AnsiConsole.MarkupLine($"[dim]Using {(usePreviousWeek ? "previous" : "current")} week logic[/]");

    // Calculate Monday of the current week
    int daysFromMonday = dayOfWeek == DayOfWeek.Sunday ? 6 : ((int)dayOfWeek - 1);
    var thisMonday = today.AddDays(-daysFromMonday);

    AnsiConsole.MarkupLine($"[dim]This Monday: {thisMonday:yyyy-MM-dd} (days from Monday: {daysFromMonday})[/]");

    if (usePreviousWeek)
    {
        // Previous complete week: Monday to Sunday
        weekStartDate = thisMonday.AddDays(-7);
        weekEndDate = thisMonday.AddDays(-1);
        
        AnsiConsole.MarkupLine($"[dim]Previous week: {weekStartDate:yyyy-MM-dd} to {weekEndDate:yyyy-MM-dd}[/]");
    }
    else
    {
        // Current week: Monday through today
        weekStartDate = thisMonday;
        weekEndDate = today;
        
        AnsiConsole.MarkupLine($"[dim]Current week (Mon-Today): {weekStartDate:yyyy-MM-dd} to {weekEndDate:yyyy-MM-dd}[/]");
    }
}

// Convert to DateTimeOffset for feed filtering (start of day to end of day UTC)
var weekStart = weekStartDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
var weekEnd = weekEndDate.ToDateTime(new TimeOnly(23, 59, 59), DateTimeKind.Utc);

// Atom feeds (GitHub release notes)
const string CliAtomUrl = "https://github.com/github/copilot-cli/releases.atom";
const string SdkAtomUrl = "https://github.com/github/copilot-sdk/releases.atom";

// RSS feeds (blog & changelog — filtered to Copilot-relevant entries)
const string ChangelogCopilotUrl = "https://github.blog/changelog/label/copilot/feed/";
const string BlogUrl = "https://github.blog/feed/";

var daySpan = weekEndDate.DayNumber - weekStartDate.DayNumber + 1;
AnsiConsole.MarkupLine($"[dim]Date range:[/] [white]{weekStartDate:yyyy-MM-dd}[/] [dim]→[/] [white]{weekEndDate:yyyy-MM-dd}[/] [dim]({daySpan} days)[/]");
AnsiConsole.WriteLine();

// ── Fetch feeds ───────────────────────────────────────────────────────────────
var feedService = new AtomFeedService();

List<NewsletterGenerator.Models.ReleaseEntry> cliReleases = [];
List<NewsletterGenerator.Models.ReleaseEntry> sdkReleases = [];
List<NewsletterGenerator.Models.ReleaseEntry> changelogEntries = [];
List<NewsletterGenerator.Models.ReleaseEntry> blogEntries = [];

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

// ── Feed summary table ────────────────────────────────────────────────────────
static string CountCell(int n) => n == 0 ? "[dim]0[/]" : $"[green]{n}[/]";
static string ItemsCell(IEnumerable<NewsletterGenerator.Models.ReleaseEntry> entries, int max = 3)
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
    AnsiConsole.MarkupLine($"[yellow]⚠[/]  No items found in the date range [bold]{weekStart:yyyy-MM-dd}[/] to [bold]{weekEnd:yyyy-MM-dd}[/]. Nothing to generate.");
    return;
}

// ── Generate via Copilot SDK ──────────────────────────────────────────────────
// Helper function to extract TLDR bullets
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

// Initialize cache service
var cache = new CacheService();

await AnsiConsole.Status()
    .Spinner(Spinner.Known.Star)
    .SpinnerStyle(Style.Parse("cornflowerblue"))
    .StartAsync("Generating newsletter via GitHub Copilot...", async ctx =>
    {
        var newsletterService = new NewsletterService();
        try
        {
            // Generate News and Announcements section
            if (changelogEntries.Count > 0 || blogEntries.Count > 0)
            {
                ctx.Status("Generating News and Announcements...");
                newsSection = await newsletterService.GenerateNewsAndAnnouncementsAsync(
                    changelogEntries, blogEntries, weekStart, weekEnd, cache);
            }

            // Generate Project updates section
            ctx.Status("Generating Project updates...");
            releaseSection = await newsletterService.GenerateReleaseSectionAsync(
                cliReleases, sdkReleases, weekStart, weekEnd, cache);

            // Extract TLDR bullets from release section (between "Project updates" and "## Releases")
            var releaseSummaryBullets = ExtractTLDRBullets(releaseSection);

            // Generate Welcome summary
            ctx.Status("Generating Welcome summary...");
            welcomeSummary = await newsletterService.GenerateWelcomeSummaryAsync(
                newsSection, releaseSummaryBullets, weekStart, weekEnd, cache);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]✗[/] Error: [red]{Markup.Escape(ex.Message)}[/]");
            AnsiConsole.MarkupLine("[dim]Make sure the GitHub Copilot CLI is installed and in your PATH.[/]");
        }
    });

if (string.IsNullOrEmpty(releaseSection))
    return;

// Combine sections
var content = new StringBuilder();

// Add Welcome section
content.AppendLine("Welcome");
content.AppendLine("--------");
content.AppendLine();
content.AppendLine("This is your weekly  update for GitHub Copilot CLI & SDK! Feel free to forward internally and encourage your co-workers to subscribe at [https://aka.ms/copilot-cli-insiders/join](https://aka.ms/copilot-cli-insiders/join) and forward this newsletter around!");
content.AppendLine();
content.AppendLine(welcomeSummary);
content.AppendLine();
content.AppendLine("* * * * *");
content.AppendLine();

// Add News section if present
if (!string.IsNullOrEmpty(newsSection))
{
    content.AppendLine(newsSection);
    content.AppendLine();
    content.AppendLine("* * * * *");
    content.AppendLine();
}

// Add Release section
content.Append(releaseSection);

// ── Write output ──────────────────────────────────────────────────────────────
var outputDir = Path.Combine(Directory.GetCurrentDirectory(), "output");
Directory.CreateDirectory(outputDir);

var filename = $"newsletter-{weekEndDate:yyyy-MM-dd}.md";
var outputPath = Path.Combine(outputDir, filename);

//Ensure no emdashes in content
content.Replace('—', '-').Replace('–', '-');

//Delete the file if it already exists
if (File.Exists(outputPath))
{
    AnsiConsole.MarkupLine($"[yellow]⚠[/] Overwriting existing file [link={outputPath}][underline]{outputPath}[/][/]");
    // Ensure file is not read-only
    File.SetAttributes(outputPath, FileAttributes.Normal);
}

await File.WriteAllTextAsync(outputPath, content.ToString(), System.Text.Encoding.UTF8);

AnsiConsole.MarkupLine($"[green]✓[/] Newsletter for [white]{weekStartDate:yyyy-MM-dd}[/] to [white]{weekEndDate:yyyy-MM-dd}[/] written to [link={outputPath}][underline]{outputPath}[/][/]");
AnsiConsole.WriteLine();

// ── Preview panel ─────────────────────────────────────────────────────────────
var preview = string.Join('\n', content.ToString().Split('\n').Take(25));

AnsiConsole.Write(
    new Panel(Markup.Escape(preview))
        .Header("[cornflowerblue] Preview (first 25 lines) [/]")
        .Border(BoxBorder.Rounded)
        .BorderColor(Color.Grey)
        .Expand());
