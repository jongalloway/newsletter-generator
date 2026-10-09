using System.ComponentModel;
using NewsletterGenerator;
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
    [Description("Newsletter type: copilot, vscode, or devtech")]
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

    [Description("Enable infinite sessions for interactive revision loops")]
    [CommandOption("--infinite-sessions")]
    [DefaultValue(false)]
    public bool InfiniteSessions { get; init; }

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

internal sealed class ListModelsCommand : AsyncCommand<EmptyCommandSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, EmptyCommandSettings settings, CancellationToken cancellationToken)
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

internal sealed class ClearCacheCommand : Command<EmptyCommandSettings>
{
    public override int Execute(CommandContext context, EmptyCommandSettings settings, CancellationToken cancellationToken)
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

internal sealed class DoctorCommand : AsyncCommand<EmptyCommandSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, EmptyCommandSettings settings, CancellationToken cancellationToken)
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
