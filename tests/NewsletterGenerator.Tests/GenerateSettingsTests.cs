using System.Reflection;
using Microsoft.Copilot;
using Microsoft.Extensions.Logging.Abstractions;
using NewsletterGenerator.Services;

namespace NewsletterGenerator.Tests;

public class GenerateSettingsTests
{
    [Fact]
    public void Validate_AllowsInfiniteSessionsFlagInNonInteractiveMode()
    {
        var settings = new GenerateSettings
        {
            NonInteractive = true,
            Newsletter = "copilot",
            Model = "gpt-5.3-codex",
            DaysBack = 7,
            InfiniteSessions = true
        };

        var result = settings.Validate();

        Assert.True(result.Successful);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [InlineData(null)]
    public void CreateSessionConfig_MapsInfiniteSessionsEnabledValue(bool? enabled)
    {
        var service = new NewsletterService(NullLogger<NewsletterService>.Instance);
        var method = typeof(NewsletterService).GetMethod(
            "CreateSessionConfig",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(method);

        var config = (CreateSessionOptions)method.Invoke(service, ["gpt-5.3-codex", "system prompt", "medium", enabled])!;

        if (enabled.HasValue)
        {
            Assert.NotNull(config.State?.InfiniteSessions);
            Assert.Equal(enabled.Value, config.State.InfiniteSessions.Enabled);
        }
        else
        {
            Assert.Null(config.State?.InfiniteSessions);
        }
    }

    [Fact]
    public void SessionOptions_PreserveGenerationSettingsOnCreateAndResume()
    {
        var service = new NewsletterService(NullLogger<NewsletterService>.Instance);
        var createMethod = typeof(NewsletterService).GetMethod(
            "CreateSessionConfig",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        var resumeMethod = typeof(NewsletterService).GetMethod(
            "CreateResumeSessionConfig",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        var create = (CreateSessionOptions)createMethod.Invoke(service, ["test-model", "system prompt", "high", null])!;
        var resume = (ResumeSessionOptions)resumeMethod.Invoke(service, ["test-model", "system prompt"])!;

        foreach (var options in new SessionOptionsBase[] { create, resume })
        {
            Assert.Equal("test-model", options.Model);
            Assert.Equal("newsletter-generator", options.ClientName);
            Assert.NotNull(options.Tools?.AvailableTools);
            Assert.Empty(options.Tools.AvailableTools);
            Assert.True(options.Events?.Streaming);
            Assert.NotNull(options.Permissions?.OnRequest);
            Assert.NotNull(options.Hooks?.Handlers?.OnErrorOccurred);
            Assert.NotNull(options.Hooks?.Handlers?.OnSessionStart);
            Assert.NotNull(options.Hooks?.Handlers?.OnSessionEnd);
            Assert.Equal(SystemMessageMode.Replace, options.SystemMessage?.Mode);
            Assert.Equal("system prompt", options.SystemMessage?.Content);
        }

        Assert.Equal("high", create.Inference?.ReasoningEffort);
        Assert.Null(resume.Inference?.ReasoningEffort);
    }
}
