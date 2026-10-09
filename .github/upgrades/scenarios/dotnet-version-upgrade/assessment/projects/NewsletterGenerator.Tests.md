# tests\NewsletterGenerator.Tests\NewsletterGenerator.Tests.csproj

[← Back to the assessment index](../../assessment.md)

## Project Info

- **Current Target Framework:** net10.0
- **Proposed Target Framework:** net11.0
- **SDK-style**: True
- **Project Kind:** DotNetCoreApp
- **Dependencies**: 1
- **Dependants**: 0
- **Number of Files**: 12
- **Number of Files with Incidents**: 2
- **Lines of Code**: 1883
- **Estimated LOC to modify**: 3+ (at least 0.2% of the project)

## Related Projects

**Depends on (1)** — projects this one references:

- [src/NewsletterGenerator/NewsletterGenerator.csproj](../projects/NewsletterGenerator.md)

## Dependency Graph

Legend:
📦 SDK-style project
⚙️ Classic project

```mermaid
flowchart TB
    subgraph current["NewsletterGenerator.Tests.csproj"]
        MAIN["<b>📦&nbsp;NewsletterGenerator.Tests.csproj</b><br/><small>net10.0</small>"]
        click MAIN "../projects/NewsletterGenerator.Tests.md"
    end
    subgraph downstream["Dependencies (1)"]
        P1["<b>📦&nbsp;NewsletterGenerator.csproj</b><br/><small>net10.0</small>"]
        click P1 "../projects/NewsletterGenerator.md"
    end
    MAIN --> P1

```

## API Compatibility

| Category | Count | Impact |
| :--- | :---: | :--- |
| 🔴 Binary Incompatible | 0 | High - Require code changes |
| 🟡 Source Incompatible | 2 | Medium - Needs re-compilation and potential conflicting API error fixing |
| 🔵 Behavioral change | 1 | Low - Behavioral changes that may require testing at runtime |
| ✅ Compatible | 2077 |  |
| ***Total APIs Analyzed*** | ***2080*** |  |

## NuGet Package Issues

| Package | Current Version | Suggested Version | Severity | Issue |
| :--- | :---: | :---: | :---: | :--- |
| Microsoft.Extensions.Logging.Abstractions | 10.0.10 | 10.0.12 | 🟡 Potential | NuGet package upgrade is recommended |

Every project affected by these packages, and the versions the repository settles on: [aggregate NuGet packages](../nuget/aggregate-packages.md).

