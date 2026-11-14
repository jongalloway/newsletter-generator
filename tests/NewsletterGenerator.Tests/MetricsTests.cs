using NewsletterGenerator;
using NewsletterGenerator.Models;
using NewsletterGenerator.Services;

namespace NewsletterGenerator.Tests;

public class MetricsTests
{
    [Fact]
    public void CopilotTokenTotal_FormatsUnavailableCompleteAndPartialCounts()
    {
        var unavailable = CopilotTokenTotal.FromMetrics(
            [CreateUsageMetric(inputTokens: null), CreateUsageMetric(inputTokens: null)],
            metric => metric.InputTokens);
        var measuredZero = CopilotTokenTotal.FromMetrics(
            [CreateUsageMetric(inputTokens: 0)],
            metric => metric.InputTokens);
        var complete = CopilotTokenTotal.FromMetrics(
            [CreateUsageMetric(inputTokens: 120), CreateUsageMetric(inputTokens: 80)],
            metric => metric.InputTokens);
        var partial = CopilotTokenTotal.FromMetrics(
            [CreateUsageMetric(inputTokens: 120), CreateUsageMetric(inputTokens: null)],
            metric => metric.InputTokens);

        Assert.Equal("n/a", unavailable.Format());
        Assert.Equal("0", measuredZero.Format());
        Assert.Equal("200", complete.Format());
        Assert.Equal("120 partial (1/2)", partial.Format());
    }

    [Fact]
    public void AggregateRunMetrics_PreservesUnavailableAndPartialTokenCounts()
    {
        var aggregate = new AggregateRunMetrics();
        var metrics = new RunMetrics();
        metrics.CopilotUsage.Add(CreateUsageMetric(inputTokens: 120, outputTokens: null, totalTokens: null));
        metrics.CopilotUsage.Add(CreateUsageMetric(inputTokens: null, outputTokens: 40, totalTokens: null));

        aggregate.AddRun(metrics);

        Assert.Equal("120 partial (1/2)", aggregate.CopilotInputTokens.Format());
        Assert.Equal("40 partial (1/2)", aggregate.CopilotOutputTokens.Format());
        Assert.Equal("n/a", aggregate.CopilotTotalTokens.Format());
    }

    private static CopilotUsageMetric CreateUsageMetric(
        long? inputTokens = null,
        long? outputTokens = null,
        long? totalTokens = null) =>
        new(
            "test",
            "session",
            null,
            0,
            0,
            inputTokens,
            outputTokens,
            totalTokens,
            DateTimeOffset.UtcNow);
}
