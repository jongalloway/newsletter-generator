# newsletter-generator

Automated weekly newsletter generator for GitHub Copilot CLI & SDK updates.

## What it does

This tool generates a curated weekly newsletter covering updates to GitHub Copilot CLI and SDK by:

1. **Fetching** release notes, changelog entries, and blog posts from GitHub
2. **Filtering** out low-value content (dependency bumps, CI changes, attributions, etc.)
3. **Summarizing** using GitHub Copilot SDK to create concise, factual summaries
4. **Caching** generated content to avoid regenerating unchanged sections

The output is a markdown newsletter with three main sections:

- **Welcome** - Brief opening paragraph summarizing the week's highlights
- **News and Announcements** - Curated changelog/blog items relevant to CLI/SDK users
- **Project Updates** - Release notes for GitHub Copilot CLI and SDK

## Date logic

The tool automatically determines the newsletter date range based on the current day:

- **Monday-Tuesday**: Previous complete week (Monday-Sunday)
- **Wednesday-Sunday**: Current week (Monday through today)

This ensures that when running on Sunday or Monday, you're still generating the newsletter for the previous week that just ended.

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

**Custom date range (N days back from today):**

```bash
dotnet run 7              # Last 7 days ending today
dotnet run 14             # Last 14 days ending today
```

**Combine flags:**

```bash
dotnet run --clear-cache 7    # Clear cache and generate for last 7 days
```

### Output

Generated newsletters are saved to:

```
src/NewsletterGenerator/output/newsletter-YYYY-MM-DD.md
```

The filename uses the end date of the coverage period.

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

Filtering rules and summarization prompts can be modified in:

- `Services/AtomFeedService.cs` - Regex filters for release notes
- `Services/NewsletterService.cs` - AI prompts and generation logic
