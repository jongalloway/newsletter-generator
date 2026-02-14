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
int daysBack = args.Length > 0 && int.TryParse(args[0], out var d) ? d : 7;

// Atom feeds (GitHub release notes)
const string CliAtomUrl = "https://github.com/github/copilot-cli/releases.atom";
const string SdkAtomUrl = "https://github.com/github/copilot-sdk/releases.atom";

// RSS feeds (blog & changelog — filtered to Copilot-relevant entries)
const string ChangelogCopilotUrl = "https://github.blog/changelog/label/copilot/feed/";
const string BlogUrl = "https://github.blog/feed/";

var weekEnd = DateTimeOffset.UtcNow;
var weekStart = weekEnd.AddDays(-daysBack);

AnsiConsole.MarkupLine($"[dim]Date range:[/] [white]{weekStart:yyyy-MM-dd}[/] [dim]→[/] [white]{weekEnd:yyyy-MM-dd}[/] [dim]({daysBack} days)[/]");
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
        cliReleases = await feedService.FetchFeedAsync(CliAtomUrl, daysBack);

        ctx.Status("Fetching [bold]Copilot SDK[/] releases...");
        sdkReleases = await feedService.FetchFeedAsync(SdkAtomUrl, daysBack);

        ctx.Status("Fetching [bold]GitHub Changelog[/] (Copilot label)...");
        changelogEntries = await feedService.FetchFeedAsync(
            ChangelogCopilotUrl, daysBack,
            maxContentChars: 1500);

        ctx.Status("Fetching [bold]GitHub Blog[/] (Copilot/CLI posts)...");
        blogEntries = await feedService.FetchFeedAsync(
            BlogUrl, daysBack,
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
    AnsiConsole.MarkupLine($"[yellow]⚠[/]  No items found in the past [bold]{daysBack}[/] days. Nothing to generate.");
    return;
}

// ── Generate via Copilot SDK ──────────────────────────────────────────────────
string content = string.Empty;

await AnsiConsole.Status()
    .Spinner(Spinner.Known.Star)
    .SpinnerStyle(Style.Parse("cornflowerblue"))
    .StartAsync("Generating newsletter via GitHub Copilot...", async ctx =>
    {
        var newsletterService = new NewsletterService();
        try
        {
            content = await newsletterService.GenerateReleaseSectionAsync(
                cliReleases, sdkReleases, changelogEntries, blogEntries, weekStart, weekEnd);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]✗[/] Error: [red]{Markup.Escape(ex.Message)}[/]");
            AnsiConsole.MarkupLine("[dim]Make sure the GitHub Copilot CLI is installed and in your PATH.[/]");
        }
    });

if (string.IsNullOrEmpty(content))
    return;

// ── Write output ──────────────────────────────────────────────────────────────
var outputDir = Path.Combine(Directory.GetCurrentDirectory(), "output");
Directory.CreateDirectory(outputDir);

var filename = $"newsletter-{DateTime.UtcNow:yyyy-MM-dd}.md";
var outputPath = Path.Combine(outputDir, filename);

await File.WriteAllTextAsync(outputPath, content);

AnsiConsole.MarkupLine($"[green]✓[/] Newsletter written to [link={outputPath}][underline]{outputPath}[/][/]");
AnsiConsole.WriteLine();

// ── Preview panel ─────────────────────────────────────────────────────────────
var preview = string.Join('\n', content.Split('\n').Take(25));

AnsiConsole.Write(
    new Panel(Markup.Escape(preview))
        .Header("[cornflowerblue] Preview (first 25 lines) [/]")
        .Border(BoxBorder.Rounded)
        .BorderColor(Color.Grey)
        .Expand());
