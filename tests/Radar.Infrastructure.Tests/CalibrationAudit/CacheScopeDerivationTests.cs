using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Radar.Infrastructure.DependencyInjection;
using Radar.Infrastructure.Filings;

namespace Radar.Infrastructure.Tests.CalibrationAudit;

/// <summary>
/// Spec 162: the calibration cohort is MODEL-SCOPED, resolved through the production
/// <c>AddFileAnalyzedFilingCache</c> scoping logic. These tests pin that the baseline reader identity
/// (spec 119: <c>{provider}:{effectiveModel}</c> = <c>openai:deepseek-ai/DeepSeek-V4-Flash</c>) derives
/// exactly the scope segment the spec names — the segment the audit console verifies before enumerating a
/// single record — and that the pin is not vacuous (a different identity derives a different segment).
/// </summary>
public sealed class CacheScopeDerivationTests
{
    private static string DeriveSegment(string modelIdentity)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddFileAnalyzedFilingCache("cache-root", modelIdentity);
        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<FileAnalyzedFilingCacheOptions>().ModelSegment;
    }

    [Fact]
    public void BaselineModelIdentity_DerivesThePinnedActiveScope()
    {
        Assert.Equal(
            "openai-deepseek-ai-deepseek-v4-flash-8f94f2dbe65fcb93",
            DeriveSegment("openai:deepseek-ai/DeepSeek-V4-Flash"));
    }

    [Fact]
    public void DifferentModelIdentity_DerivesADifferentScope()
    {
        // The 16-hex suffix hashes the EXACT raw identity, so even a case-only change is a new scope
        // (a model switch must be a clean cache miss, spec 118) — and the pin above cannot pass by accident.
        Assert.NotEqual(
            DeriveSegment("openai:deepseek-ai/DeepSeek-V4-Flash"),
            DeriveSegment("openai:deepseek-ai/deepseek-v4-flash"));
    }
}
