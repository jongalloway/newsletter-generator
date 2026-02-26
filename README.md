# newsletter-generator

Automated weekly newsletter generator for GitHub Copilot CLI/SDK or VS Code Insiders updates.

> **Note:** This tool was built for generating internal team newsletters, but all source data comes from public release feeds and blog posts. You're welcome to fork and adapt it for your own newsletter needs.

## What it does

This tool generates a curated weekly newsletter by:

1. **Fetching** release notes, changelog entries, and blog posts from GitHub
2. **Filtering** out low-value content (dependency bumps, CI changes, attributions, etc.)
3. **Summarizing** using GitHub Copilot SDK to create concise, factual summaries
4. **Caching** generated content to avoid regenerating unchanged sections

At startup, you'll choose:

- **Newsletter type** - GitHub Copilot CLI/SDK or VS Code Insiders
- **Copilot model** - The model used for newsletter generation prompts
- **Cache behavior** - Use cache, clear cache, or force refresh for the run

While loading model choices, the app shows a spinner/status animation while querying available models.

The output is a markdown newsletter with these main sections:

- **Welcome** - Brief opening paragraph summarizing the week's highlights
- **News and Announcements** - Curated changelog/blog items (for the Copilot CLI/SDK newsletter)
- **Project Updates** - Product-specific release highlights

## Date logic

At startup, the tool asks how many days back to include (default: **7**).

- End date is always today
- Start date is today minus the selected number of days

You can still pass a numeric CLI argument to skip the prompt.

## Usage

### Basic usage

```bash
cd src/NewsletterGenerator
dotnet run
```

This generates a newsletter using the automatic date logic described above.

### Command-line options

**Clear cache and regenerate everything:**

```bash
dotnet run --clear-cache
dotnet run -c
```

**Force refresh (ignore cache reads this run):**

```bash
dotnet run --force-refresh
dotnet run -f
```

**Custom date range (N days back from today):**

```bash
dotnet run 7              # Last 7 days ending today
dotnet run 14             # Last 14 days ending today
```

**Combine flags:**

```bash
dotnet run --clear-cache 7    # Clear cache and generate for last 7 days
```

**Choose newsletter type via CLI:**

```bash
dotnet run --newsletter copilot
dotnet run --newsletter vscode
```

**Choose model via CLI:**

```bash
dotnet run --model gpt-5.3-codex
dotnet run --model gpt-4.1
```

### Output

Generated newsletters are saved to:

```
src/NewsletterGenerator/output/newsletter-copilot-cli-sdk-YYYY-MM-DD.md
src/NewsletterGenerator/output/newsletter-vscode-insiders-YYYY-MM-DD.md
```

The filename uses the end date of the coverage period and includes the newsletter slug.

After each generation run, the app prompts whether to start again from the beginning (new newsletter/model/options selection).

## Caching

The tool caches:

- Feed data (atom/RSS feeds)
- Generated summaries for each section

Cache files are stored in `.cache/` and use SHA256 hashing to detect changes in source data. When source data changes, that section is regenerated. Use `--clear-cache` to force regeneration of all content.

## Configuration

The tool fetches from these sources:

- **CLI Releases**: <https://github.com/github/copilot-cli/releases.atom>
- **SDK Releases**: <https://github.com/github/copilot-sdk/releases.atom>
- **Changelog**: <https://github.blog/changelog/label/copilot/feed/>
- **Blog**: <https://github.blog/feed/>
- **VS Code Insiders**: <https://aka.ms/vscode/updates/insiders> (resolved to the current release notes markdown)
- **VS Code Blog**: <https://code.visualstudio.com/feed.xml> (for VS Code newsletter mode)
- **GitHub Changelog + Blog VS Code mentions**: Copilot changelog entries and blog posts that mention VS Code (for VS Code newsletter mode)

Filtering rules and summarization prompts can be modified in:

- `Services/AtomFeedService.cs` - Regex filters for release notes
- `Services/NewsletterService.cs` - AI prompts and generation logic
