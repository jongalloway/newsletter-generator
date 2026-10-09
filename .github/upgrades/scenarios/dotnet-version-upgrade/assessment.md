# Projects and dependencies analysis

This document provides a comprehensive overview of the projects and their dependencies in the context of upgrading to .NETCoreApp,Version=v11.0.

Detailed findings live alongside this file in `assessment/`. This page is the index: read it first, then open only the documents you need.

## Table of Contents

- [Executive Summary](#executive-summary)
  - [High-level Metrics](#high-level-metrics)
  - [Projects Compatibility](#projects-compatibility)
  - [Package Compatibility](#package-compatibility)
  - [API Compatibility](#api-compatibility)
- [Top API Migration Challenges](#top-api-migration-challenges)
  - [Technologies and Features](#technologies-and-features)
  - [Most Frequent API Issues](#most-frequent-api-issues)
- [Detailed Reports](#detailed-reports)
  - [Projects Relationship Graph](assessment/project-graph.md)
  - [Aggregate NuGet packages details](assessment/nuget/aggregate-packages.md)
  - [Most Frequent API Issues (complete list)](assessment/api-issues/most-frequent-api-issues.md)
  - [Project Details](#project-details)

## Executive Summary

### High-level Metrics

| Metric | Count | Status |
| :--- | :---: | :--- |
| Total Projects | 2 | All require upgrade |
| Total NuGet Packages | 45 | 2 need upgrade |
| Total Code Files | 28 |  |
| Total Code Files with Incidents | 9 |  |
| Total Lines of Code | 8680 |  |
| Total Number of Issues | 71 |  |
| Proposed Target Framework | net11.0 |  |
| Estimated LOC to modify | 66+ | at least 0.8% of codebase |

### Projects Compatibility

| Project | Target Framework | Difficulty | Test Coverage | Package Issues | API Issues | Binding Issues | Est. LOC Impact | Description |
| :--- | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :--- |
| [src\NewsletterGenerator\NewsletterGenerator.csproj](assessment/projects/NewsletterGenerator.md) | net10.0 | 🟢 Low | 🧪 Recommended | 2 | 63 | 0 | 63+ | DotNetCoreApp, Sdk Style = True |
| [tests\NewsletterGenerator.Tests\NewsletterGenerator.Tests.csproj](assessment/projects/NewsletterGenerator.Tests.md) | net10.0 | 🟢 Low | — | 1 | 3 | 0 | 3+ | DotNetCoreApp, Sdk Style = True |

🧪 **Test Coverage** — projects risky enough to add behavior-locking tests before upgrading, to catch regressions the upgrade may introduce. Requires the **dotnet-test** plugin.

### Package Compatibility

| Status | Count | Percentage |
| :--- | :---: | :---: |
| ✅ Compatible | 43 | 95.6% |
| ⚠️ Incompatible | 0 | 0.0% |
| 🔄 Upgrade Recommended | 2 | 4.4% |
| ***Total NuGet Packages*** | ***45*** | ***100%*** |

### API Compatibility

| Category | Count | Impact |
| :--- | :---: | :--- |
| 🔴 Binary Incompatible | 0 | High - Require code changes |
| 🟡 Source Incompatible | 49 | Medium - Needs re-compilation and potential conflicting API error fixing |
| 🔵 Behavioral change | 17 | Low - Behavioral changes that may require testing at runtime |
| ✅ Compatible | 13517 |  |
| ***Total APIs Analyzed*** | ***13583*** |  |

## Top API Migration Challenges

### Technologies and Features

| Technology | Issues | Percentage | Migration Path |
| :--- | :---: | :---: | :--- |
| WCF Client APIs | 44 | 66.7% | WCF client-side APIs for building service clients that communicate with WCF services. These APIs are available as exact equivalents via NuGet packages - add System.ServiceModel.* NuGet packages (System.ServiceModel.Http, System.ServiceModel.Primitives, System.ServiceModel.NetTcp, etc.) |

### Most Frequent API Issues

| API | Count | Percentage | Category |
| :--- | :---: | :---: | :--- |
| P:System.ServiceModel.Syndication.TextSyndicationContent.Text | 7 | 10.6% | Source Incompatible |
| P:System.Uri.AbsolutePath | 7 | 10.6% | Behavioral Change |
| T:System.Uri | 7 | 10.6% | Behavioral Change |
| T:System.ServiceModel.Syndication.TextSyndicationContent | 6 | 9.1% | Source Incompatible |
| M:System.Threading.Tasks.Task.WhenAll(System.ReadOnlySpan{System.Threading.Tasks.Task}) | 3 | 4.5% | Source Incompatible |
| P:System.ServiceModel.Syndication.SyndicationItem.Summary | 3 | 4.5% | Source Incompatible |
| P:System.ServiceModel.Syndication.SyndicationItem.Title | 3 | 4.5% | Source Incompatible |
| M:System.String.Join(System.String,System.ReadOnlySpan{System.String}) | 2 | 3.0% | Source Incompatible |
| M:System.Uri.#ctor(System.String) | 2 | 3.0% | Behavioral Change |
| P:System.ServiceModel.Syndication.SyndicationItem.Links | 2 | 3.0% | Source Incompatible |

The table above is the top 10. See [the complete list](assessment/api-issues/most-frequent-api-issues.md) for every affected API.

## Detailed Reports

- [Projects Relationship Graph](assessment/project-graph.md)
- [Aggregate NuGet packages details](assessment/nuget/aggregate-packages.md)
- [Most Frequent API Issues (complete list)](assessment/api-issues/most-frequent-api-issues.md)

### Project Details

- [src\NewsletterGenerator\NewsletterGenerator.csproj](assessment/projects/NewsletterGenerator.md)
- [tests\NewsletterGenerator.Tests\NewsletterGenerator.Tests.csproj](assessment/projects/NewsletterGenerator.Tests.md)


