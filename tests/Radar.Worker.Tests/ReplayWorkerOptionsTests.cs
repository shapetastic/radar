using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using Radar.Application.Replay;

namespace Radar.Worker.Tests;

/// <summary>
/// Spec 139 — the <c>Radar:Replay</c> config surface: the opt-in gate, the from/to/step parse (UTC, tokens),
/// the deterministic default label, and fail-fast on anything that would otherwise silently replay the wrong
/// range.
/// </summary>
public sealed class ReplayWorkerOptionsTests
{
    private static ServiceProvider BuildProvider(params (string Key, string Value)[] settings)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)))
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IHostApplicationLifetime, FakeLifetime>();
        services.AddRadarWorker(configuration);
        return services.BuildServiceProvider();
    }

    private static (string Key, string Value)[] EnabledReplay(
        string from = "2026-05-01", string to = "2026-05-03", string step = "1d", string? label = null)
    {
        var settings = new List<(string, string)>
        {
            ("Radar:Replay:Enabled", "true"),
            ("Radar:Replay:From", from),
            ("Radar:Replay:To", to),
            ("Radar:Replay:Step", step),
        };

        if (label is not null)
        {
            settings.Add(("Radar:Replay:Label", label));
        }

        return [.. settings];
    }

    [Fact]
    public void ReplayDisabled_ByDefault_RegistersNothingReplayRelated()
    {
        using var provider = BuildProvider();

        // The whole feature is absent, so Worker's optional IReplayRunner? stays null and the default graph
        // is byte-for-byte unchanged.
        Assert.Null(provider.GetService<IReplayRunner>());
        Assert.Null(provider.GetService<ReplayPlan>());
        Assert.Null(provider.GetService<IReplayScoringStrategyFactory>());
        Assert.Null(provider.GetService<IReplayScoreSnapshotFileStoreFactory>());
    }

    [Fact]
    public void ReplayEnabled_RegistersThePlanAndTheRunner()
    {
        using var provider = BuildProvider(EnabledReplay());

        var plan = provider.GetRequiredService<ReplayPlan>();

        Assert.NotNull(provider.GetService<IReplayRunner>());
        Assert.Equal(3, plan.Series.Count);
        Assert.Equal(new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero), plan.Series.FromUtc);
        Assert.Equal(new DateTimeOffset(2026, 5, 3, 0, 0, 0, TimeSpan.Zero), plan.Series.ToUtc);
        Assert.Equal(TimeSpan.FromDays(1), plan.Series.Step);
    }

    [Fact]
    public void DatesWithoutAnOffset_AreReadAsUtc_NotMachineLocalTime()
    {
        // A replay's premise is a reproducible instant (AD-7), so "2026-05-01" must mean the same point in
        // time on a UTC CI runner and on a UTC+13 laptop.
        using var provider = BuildProvider(EnabledReplay(from: "2026-05-01T06:00:00", to: "2026-05-01T06:00:00"));

        var plan = provider.GetRequiredService<ReplayPlan>();

        Assert.Equal(new DateTimeOffset(2026, 5, 1, 6, 0, 0, TimeSpan.Zero), plan.Series.FromUtc);
        Assert.Equal(TimeSpan.Zero, plan.Series.Points[0].Offset);
    }

    [Theory]
    [InlineData("1d", 24 * 60)]
    [InlineData("12h", 12 * 60)]
    [InlineData("30m", 30)]
    [InlineData("01:00:00", 60)]
    public void Step_AcceptsUnitTokensAndPlainTimeSpans(string step, int expectedMinutes)
    {
        using var provider = BuildProvider(
            EnabledReplay(from: "2026-05-01", to: "2026-05-02", step: step));

        Assert.Equal(
            TimeSpan.FromMinutes(expectedMinutes),
            provider.GetRequiredService<ReplayPlan>().Series.Step);
    }

    [Fact]
    public void BlankLabel_DerivesADeterministicLabelFromTheSeries()
    {
        // Deterministic so re-running the same range overwrites the same output instead of accumulating
        // near-duplicate runs.
        using var provider = BuildProvider(EnabledReplay());

        Assert.Equal("20260501-20260503-1d", provider.GetRequiredService<ReplayPlan>().Label);
    }

    [Fact]
    public void ConfiguredLabel_IsUsedAndTrimmed()
    {
        using var provider = BuildProvider(EnabledReplay(label: "  my-experiment  "));

        Assert.Equal("my-experiment", provider.GetRequiredService<ReplayPlan>().Label);
    }

    [Fact]
    public void UnusableLabel_FailsFast()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => BuildProvider(EnabledReplay(label: "../escape")));

        Assert.Contains("Radar:Replay:Label", ex.Message);
    }

    [Theory]
    [InlineData("Radar:Replay:From")]
    [InlineData("Radar:Replay:To")]
    public void BlankBound_FailsFast(string blankKey)
    {
        var settings = EnabledReplay()
            .Select(s => s.Key == blankKey ? (s.Key, Value: string.Empty) : s)
            .ToArray();

        var ex = Assert.Throws<InvalidOperationException>(() => BuildProvider(settings));

        Assert.Contains(blankKey, ex.Message);
        Assert.Contains("required", ex.Message);
    }

    [Fact]
    public void UnparseableDate_FailsFast()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => BuildProvider(EnabledReplay(from: "last Tuesday")));

        Assert.Contains("Radar:Replay:From", ex.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("soon")]
    [InlineData("1w")]
    public void UnparseableStep_FailsFast(string step)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => BuildProvider(EnabledReplay(step: step)));

        Assert.Contains("Radar:Replay:Step", ex.Message);
    }

    [Fact]
    public void NonPositiveStep_FailsFast()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => BuildProvider(EnabledReplay(step: "0d")));

        Assert.Contains("Radar:Replay:From/To/Step", ex.Message);
    }

    [Fact]
    public void InvertedRange_FailsFast()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => BuildProvider(EnabledReplay(from: "2026-05-10", to: "2026-05-01")));

        Assert.Contains("Radar:Replay:From/To/Step", ex.Message);
    }
}
