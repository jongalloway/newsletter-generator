using System.Text;
using NewsletterGenerator.Models;
using NewsletterGenerator.Services;
using Spectre.Console;

namespace NewsletterGenerator;

internal static partial class NewsletterApp
{
    private static void RenderHeader()
    {
        Console.Write(HeaderAnsi);
        Console.WriteLine();

        AnsiConsole.Write(new Rule("[yellow]🤖[/] [cornflowerblue]GitHub Copilot CLI & SDK Weekly Generator[/] [yellow]📰[/]")
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
        var totalWorkSeconds = metrics.StageSeconds.Values.Sum();
        var parallelSavedSeconds = Math.Max(0, totalWorkSeconds - metrics.TotalWallSeconds);
        var usagePromptCount = metrics.CopilotUsage.Count;
        var usageInputTokens = metrics.CopilotUsage.Sum(m => m.InputTokens ?? 0);
        var usageOutputTokens = metrics.CopilotUsage.Sum(m => m.OutputTokens ?? 0);
        var usageTotalTokens = metrics.CopilotUsage.Sum(m => m.TotalTokens ?? 0);

        var summaryTable = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Grey)
            .AddColumn("[bold]Metric[/]")
            .AddColumn("[bold]Value[/]");

        summaryTable.AddRow("Mode", $"{Markup.Escape(GetNewsletterLabel(newsletter))} [grey]({weekStart:yyyy-MM-dd} -> {weekEnd:yyyy-MM-dd})[/]");
        summaryTable.AddRow("Model", Markup.Escape(model));
        summaryTable.AddRow("SDK", $"Streaming: {(metrics.StreamingEnabled ? "On" : "Off")}, Reasoning: {Markup.Escape(metrics.ReasoningEffort)}");
        summaryTable.AddRow("Cache", $"{(useCache ? "Read/write" : "Force refresh")} [grey](hits {metrics.CacheHits}, misses {metrics.CacheMisses}, skips {metrics.CacheSkips})[/]");
        if (metrics.PrereleaseCounts.Any(p => p.DetectedCount > 0))
        {
            var totalDetected = metrics.PrereleaseCounts.Sum(p => p.DetectedCount);
            var totalRolledUp = metrics.PrereleaseCounts.Sum(p => p.RolledUpCount);
            var totalSkipped = metrics.PrereleaseCounts.Sum(p => p.SkippedCount);
            summaryTable.AddRow("Prereleases", $"{totalDetected} detected [grey]({totalRolledUp} rolled up, {totalSkipped} skipped)[/]");
        }
        summaryTable.AddRow("Timing", $"Wall [white]{metrics.TotalWallSeconds:F1}s[/], work [white]{totalWorkSeconds:F1}s[/], saved [green]{parallelSavedSeconds:F1}s[/]");
        summaryTable.AddRow("Copilot usage", usagePromptCount == 0
            ? "No prompts sent"
            : $"{usagePromptCount} prompts [grey](in {usageInputTokens:N0}, out {usageOutputTokens:N0}, total {usageTotalTokens:N0})[/]");
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

        var usageTable = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Grey)
            .AddColumn("[bold]Operation[/]")
            .AddColumn("[bold]In[/]")
            .AddColumn("[bold]Out[/]")
            .AddColumn("[bold]Total[/]")
            .AddColumn("[bold]Chars[/]");

        foreach (var usage in metrics.CopilotUsage)
        {
            usageTable.AddRow(
                Markup.Escape(usage.Operation),
                usage.InputTokens?.ToString("N0") ?? "-",
                usage.OutputTokens?.ToString("N0") ?? "-",
                usage.TotalTokens?.ToString("N0") ?? "-",
                $"{usage.PromptCharacters:N0}/{usage.OutputCharacters:N0}");
        }

        if (metrics.CopilotUsage.Count == 0)
            usageTable.AddRow("(none)", "-", "-", "-", "-");

        Tree? releaseTree = null;
        if (metrics.PrereleaseCounts.Any(p => p.DetectedCount > 0))
        {
            releaseTree = new Tree("[bold]Release sources[/]")
                .Guide(TreeGuide.Line)
                .Style(Style.Parse("white"));

            foreach (var prerelease in metrics.PrereleaseCounts)
            {
                var sourceNode = releaseTree.AddNode($"[cornflowerblue]{Markup.Escape(prerelease.Source)}[/]");

                var rolledUpByParent = prerelease.RolledUpPrereleases
                    .GroupBy(p => p.ParentVersion, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.Select(p => p.PrereleaseVersion).ToList(), StringComparer.OrdinalIgnoreCase);

                foreach (var finalRelease in prerelease.FinalReleaseVersions)
                {
                    var finalNode = sourceNode.AddNode($"[white]{Markup.Escape(finalRelease)}[/]");

                    if (!rolledUpByParent.TryGetValue(finalRelease, out var childPrereleases))
                        continue;

                    foreach (var childPrerelease in childPrereleases)
                        finalNode.AddNode($"[grey]{Markup.Escape(childPrerelease)}[/]");
                }

                if (prerelease.SkippedPrereleases.Count > 0)
                {
                    var skippedNode = sourceNode.AddNode($"[yellow]Skipped prereleases[/] ({prerelease.SkippedCount})");
                    foreach (var skippedPrerelease in prerelease.SkippedPrereleases)
                        skippedNode.AddNode($"[yellow]{Markup.Escape(skippedPrerelease)}[/]");
                }
            }
        }

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
        AnsiConsole.Write(new Panel(usageTable)
            .Header("[cornflowerblue]🧠 Copilot Usage[/]")
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
        if (releaseTree is not null)
        {
            AnsiConsole.Write(new Panel(releaseTree)
                .Header("[cornflowerblue]🌳 Release Summary[/]")
                .Border(BoxBorder.Rounded)
                .BorderColor(Color.Grey)
                .Expand());
            AnsiConsole.WriteLine();
        }
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
            "- Verify Copilot auth: [white]dotnet run -- doctor[/]\n" +
            "- Verify CLI is on PATH: [white]copilot --version[/]\n" +
            "- Check network access to GitHub feeds")
            .Header("[yellow]Troubleshooting[/]")
            .Border(BoxBorder.Rounded)
            .BorderColor(Color.Grey);

        AnsiConsole.Write(hints);
    }

    private static void RenderGeneratedPreview(NewsletterType newsletter, string content)
    {
        if (Console.IsOutputRedirected)
            return;

        var preview = new StringBuilder();
        var title = ExtractMarkdownTitle(content);
        var welcome = ExtractWelcomeSummary(content);

        if (!string.IsNullOrWhiteSpace(title))
        {
            preview.AppendLine(title);
            preview.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(welcome))
        {
            preview.AppendLine(welcome);
            preview.AppendLine();
        }

        foreach (var section in ExtractPreviewSections(newsletter, content).Take(2))
        {
            preview.AppendLine(section.Heading);
            foreach (var item in section.Items.Take(3))
                preview.AppendLine($"- {item}");
            preview.AppendLine();
        }

        AnsiConsole.Write(new Panel(Markup.Escape(preview.ToString().TrimEnd()))
            .Header("[cornflowerblue]Preview[/]")
            .Border(BoxBorder.Rounded)
            .BorderColor(Color.Grey)
            .Expand());
        AnsiConsole.WriteLine();
    }

    private static void RenderAggregateDashboard(AggregateRunMetrics aggregate)
    {
        if (Console.IsOutputRedirected)
            return;

        var summaryTable = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Grey)
            .AddColumn("[bold]Metric[/]")
            .AddColumn("[bold]Value[/]");

        var runMix = string.Join(", ", aggregate.RunsByNewsletter.Select(kvp => $"{Markup.Escape(GetNewsletterLabel(kvp.Key))}: {kvp.Value}"));
        summaryTable.AddRow("Runs", aggregate.TotalRuns.ToString());
        summaryTable.AddRow("Newsletter mix", string.IsNullOrWhiteSpace(runMix) ? "(none)" : runMix);
        summaryTable.AddRow("Revisions applied", aggregate.RevisedRuns.ToString());
        summaryTable.AddRow("Copied to clipboard", aggregate.ClipboardSuccessRuns.ToString());
        summaryTable.AddRow("Cache", $"hits {aggregate.CacheHits}, misses {aggregate.CacheMisses}, skips {aggregate.CacheSkips}");
        summaryTable.AddRow("Copilot usage", $"{aggregate.CopilotPromptCount} prompts, in {aggregate.CopilotInputTokens:N0}, out {aggregate.CopilotOutputTokens:N0}, total {aggregate.CopilotTotalTokens:N0}");
        summaryTable.AddRow("Output", $"{aggregate.OutputCharacters:N0} chars, {aggregate.OutputLines:N0} lines");
        summaryTable.AddRow("Timing", $"total {aggregate.TotalWallSeconds:F1}s, avg {aggregate.AverageWallSeconds:F1}s per run");
        summaryTable.AddRow("Warnings", aggregate.WarningCount.ToString());

        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Panel(summaryTable)
            .Header("[cornflowerblue]Aggregate Metrics[/]")
            .Border(BoxBorder.Rounded)
            .BorderColor(Color.Grey)
            .Expand());
        AnsiConsole.WriteLine();
    }

    private static string ExtractMarkdownTitle(string content)
    {
        return content.Split('\n')
            .FirstOrDefault(line => line.StartsWith("# ", StringComparison.Ordinal))?
            .Substring(2)
            .Trim() ?? string.Empty;
    }

    private static IReadOnlyList<PreviewSection> ExtractPreviewSections(NewsletterType newsletter, string content)
    {
        var sections = new List<PreviewSection>();
        var lines = content.Split('\n');
        string? currentHeading = null;
        List<string> currentLines = [];

        void FlushSection()
        {
            if (string.IsNullOrWhiteSpace(currentHeading))
                return;

            if (!ShouldIncludePreviewSection(currentHeading))
                return;

            var items = BuildPreviewItems(newsletter, currentHeading, currentLines);
            if (items.Count > 0)
                sections.Add(new PreviewSection(currentHeading, items));
        }

        foreach (var line in lines)
        {
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                FlushSection();
                currentHeading = line[3..].Trim();
                currentLines = [];
                continue;
            }

            if (currentHeading is not null)
                currentLines.Add(line);
        }

        FlushSection();
        return sections;
    }

    private static readonly HashSet<string> ExcludedPreviewSections = new(StringComparer.OrdinalIgnoreCase)
    {
        "Welcome", "News and Announcements", "Office Hours",
        "Get Started", "Training and Courses", "Stay Up To Date & Engage"
    };

    private static bool ShouldIncludePreviewSection(string heading)
        => !ExcludedPreviewSections.Contains(heading);

    private static List<string> BuildPreviewItems(NewsletterType newsletter, string heading, List<string> lines)
    {
        var items = new List<string>();

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("### Releases", StringComparison.Ordinal))
                break;

            if (trimmed.StartsWith("- ", StringComparison.Ordinal))
                items.Add(CleanPreviewText(trimmed[2..]));
        }

        if (items.Count > 0)
            return items;

        var paragraphLines = new List<string>();
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                if (paragraphLines.Count > 0)
                    break;
                continue;
            }

            if (trimmed.StartsWith("### ", StringComparison.Ordinal) || trimmed.StartsWith("---", StringComparison.Ordinal))
                continue;

            paragraphLines.Add(trimmed);
        }

        if (paragraphLines.Count > 0)
            items.Add(CleanPreviewText(string.Join(' ', paragraphLines)));

        return items;
    }

    private static string CleanPreviewText(string text)
    {
        return text
            .Replace("**", string.Empty, StringComparison.Ordinal)
            .Replace("`", string.Empty, StringComparison.Ordinal)
            .Trim();
    }

}
