using System.Text.Json;

using Radar.Application.Collectors;
using Radar.Application.Scoring;
using Radar.Domain.Evidence;
using Radar.Domain.Signals;
using Radar.TestSupport;

namespace Radar.Application.Tests.Scoring;

/// <summary>
/// Spec 146 — <c>radar-formula-v9</c>: a strategy's score is a weighted array of channels.
/// <para>
/// The assertions that matter most are the ones a future reader will be tempted to "fix": that a dark
/// channel costs its whole share and the surviving weights are NOT renormalised, and that breadth is
/// POSITIVE here (v8's inverse attention discount is deliberately not carried over).
/// </para>
/// </summary>
public sealed class RadarScoreFormulaV9Tests
{
    private static readonly DateTimeOffset WindowStart = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset WindowEnd = new(2026, 1, 31, 0, 0, 0, TimeSpan.Zero);

    private sealed class FuncWeights(Func<string?, double> fn) : IAttentionSourceWeights
    {
        public double WeightFor(string? sourceName) => fn(sourceName);
        public string CanonicalDescriptor() => "test-func-weights";
    }

    /// <summary>Every publisher counts as a full genuine outlet, so reach == distinct-publisher count.</summary>
    private static readonly IAttentionSourceWeights AllGenuine = new FuncWeights(_ => 1.0);

    private static RadarScoreFormulaV9 Formula(
        params ScoringChannel[] channels) =>
        new(new ScoringWeights(), AllGenuine, ScoringChannelSet.Create(channels, "test-strategy"));

    /// <summary>
    /// Builds a signal whose evidence carries a RECORDED COLLECTOR (spec 146) — the provenance a collector
    /// channel selects on. <paramref name="collector"/> null reproduces LEGACY evidence, which predates the
    /// recording and is never backfilled.
    /// </summary>
    private static ScoringSignal BuildSignal(
        string? collector,
        int strength = 6,
        SignalDirection direction = SignalDirection.Positive,
        decimal confidence = 0.8m,
        SignalType type = SignalType.CustomerWin,
        EvidenceQuality quality = EvidenceQuality.High,
        EvidenceSourceType sourceType = EvidenceSourceType.PressRelease,
        string sourceName = "Acme Newsroom",
        DateTimeOffset? observedAt = null)
    {
        var metadata = collector is null
            ? null
            : EvidenceMetadata.Compose(
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [CollectionProvenanceMetadata.MetadataKey] = collector,
                },
                []);

        var evidence = new EvidenceBuilder()
            .WithQuality(quality)
            .WithSourceType(sourceType)
            .WithSourceName(sourceName)
            .WithMetadataJson(metadata)
            .Build();

        var signal = new SignalBuilder()
            .WithEvidenceId(evidence.Id)
            .WithStrength(strength)
            .WithDirection(direction)
            .WithConfidence(confidence)
            .WithType(type)
            .WithObservedAtUtc(observedAt ?? new DateTimeOffset(2026, 1, 15, 0, 0, 0, TimeSpan.Zero))
            .Build();

        return new ScoringSignal(signal, evidence);
    }

    private static ScoringInput InputFrom(
        IReadOnlyList<ScoringSignal>? current = null,
        IReadOnlyList<Signal>? previous = null,
        IReadOnlyList<string>? enabledCollectors = null) => new(
        CompanyId: Guid.NewGuid(),
        WindowStartUtc: WindowStart,
        WindowEndUtc: WindowEnd,
        Signals: current ?? Array.Empty<ScoringSignal>(),
        PreviousSignals: previous ?? Array.Empty<Signal>())
    {
        EnabledCollectors = enabledCollectors ?? Array.Empty<string>(),
    };

    private static JsonElement Breakdown(ScoreComputation result, string channelName) =>
        JsonDocument.Parse(result.ComponentJson).RootElement
            .GetProperty("Channels")
            .EnumerateArray()
            .Single(c => c.GetProperty("Name").GetString() == channelName);

    // ---------------------------------------------------------------------------------------------------
    // Identity / construction
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public void Version_IsRadarFormulaV9_AndAppearsInExplanation()
    {
        var formula = Formula(ScoringChannel.Collector("patents", ["patents"], 1.0, 3));

        Assert.Equal("radar-formula-v9", formula.Version);
        Assert.Equal(ScoreFormulaVersions.V9, formula.Version);

        var result = formula.Compute(InputFrom([BuildSignal("patents")]));
        Assert.Contains("radar-formula-v9", result.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public void Constructor_RejectsNullsAndAnEmptyChannelSet()
    {
        var channels = ScoringChannelSet.Create(
            [ScoringChannel.Collector("p", ["patents"], 1.0, 3)], "s");

        Assert.Throws<ArgumentNullException>(() => new RadarScoreFormulaV9(null!, AllGenuine, channels));
        Assert.Throws<ArgumentNullException>(
            () => new RadarScoreFormulaV9(new ScoringWeights(), null!, channels));
        Assert.Throws<ArgumentNullException>(
            () => new RadarScoreFormulaV9(new ScoringWeights(), AllGenuine, null!));

        // A v9 strategy with no channels could only ever score 0 — that is a misconfiguration, not a score.
        Assert.Throws<InvalidOperationException>(
            () => new RadarScoreFormulaV9(new ScoringWeights(), AllGenuine, ScoringChannelSet.Empty));
    }

    [Fact]
    public void Constructor_InvalidWeight_Throws()
    {
        var channels = ScoringChannelSet.Create(
            [ScoringChannel.Collector("p", ["patents"], 1.0, 3)], "s");

        Assert.Throws<InvalidOperationException>(
            () => new RadarScoreFormulaV9(
                new ScoringWeights { OpportunityAttentionDivisor = 0 }, AllGenuine, channels));
    }

    // ---------------------------------------------------------------------------------------------------
    // Composition + range
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public void Composite_IsTheWeightedSumOfChannelScores_AndLandsInOpportunityScoreOverZeroToOneHundred()
    {
        // Range reconciliation, asserted rather than assumed: ScoreComponents is five ints in [0,100], and
        // the v9 composite (a double in [0,1]) is mapped as composite·100 into OpportunityScore.
        var formula = Formula(
            ScoringChannel.Collector("patents", ["patents"], 0.5, 3),
            ScoringChannel.Collector("insider", ["sec-form4"], 0.5, 2));

        var result = formula.Compute(InputFrom(
        [
            BuildSignal("patents"),
            BuildSignal("sec-form4"),
        ]));

        using var doc = JsonDocument.Parse(result.ComponentJson);
        var composite = doc.RootElement.GetProperty("Composite").GetDouble();
        var patents = Breakdown(result, "patents");
        var insider = Breakdown(result, "insider");

        Assert.Equal(
            patents.GetProperty("WeightedContribution").GetDouble()
                + insider.GetProperty("WeightedContribution").GetDouble(),
            composite,
            12);
        Assert.InRange(composite, 0.0, 1.0);
        Assert.Equal((int)Math.Round(100 * composite, MidpointRounding.AwayFromZero), result.Components.OpportunityScore);
        Assert.InRange(result.Components.OpportunityScore, 0, 100);
    }

    [Fact]
    public void EveryChannelScore_IsInZeroToOne()
    {
        var formula = Formula(
            ScoringChannel.Collector("chatty", ["rss"], 0.5, 3),
            ScoringChannel.Breadth("attention", 0.5, 3));

        var signals = Enumerable.Range(0, 40)
            .Select(i => BuildSignal(
                "rss",
                sourceType: EvidenceSourceType.NewsArticle,
                sourceName: $"outlet-{i}"))
            .ToList();

        var result = formula.Compute(InputFrom(signals));

        using var doc = JsonDocument.Parse(result.ComponentJson);
        foreach (var channel in doc.RootElement.GetProperty("Channels").EnumerateArray())
        {
            Assert.InRange(channel.GetProperty("Score").GetDouble(), 0.0, 1.0);
        }
    }

    [Fact]
    public void AllComponents_StayInZeroToOneHundred_ForAnEmptyWindow()
    {
        var formula = Formula(
            ScoringChannel.Collector("patents", ["patents"], 0.6, 3),
            ScoringChannel.Breadth("attention", 0.4, 3));

        var result = formula.Compute(InputFrom());

        // Mirrors v8's empty-window behaviour exactly: all zeros, a valid explanation and ComponentJson, and
        // no contributions.
        Assert.Equal(new ScoreComponents(0, 0, 0, 0, 0), result.Components);
        Assert.NotEmpty(result.Explanation);
        Assert.Empty(result.Contributions);
        Assert.Equal(0.0, JsonDocument.Parse(result.ComponentJson).RootElement
            .GetProperty("Composite").GetDouble());
    }

    // ---------------------------------------------------------------------------------------------------
    // THE core invariant: absence costs something, and weights are never renormalised
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public void DarkChannel_ContributesZero_AndTheSurvivingWeightsAreNotRenormalised()
    {
        // DO NOT "FIX" THIS TEST BY RENORMALISING. Renormalising the surviving weights when a channel is dark
        // is the obvious-looking change a future reader will reach for, and it would erase exactly the
        // penalty this formula exists to create: a strategy that declared patents and got none must be down
        // by up to its patents share, not quietly rescaled back up to a full score.
        var formula = Formula(
            ScoringChannel.Collector("patents", ["patents"], 0.5, 3),
            ScoringChannel.Collector("insider", ["sec-form4"], 0.5, 2));

        // Only the insider channel fires. Its own sub-score is unchanged by the other channel being dark…
        var withDark = formula.Compute(InputFrom([BuildSignal("sec-form4")]));
        var insider = Breakdown(withDark, "insider").GetProperty("Score").GetDouble();
        var patents = Breakdown(withDark, "patents");

        Assert.Equal(0.0, patents.GetProperty("Score").GetDouble());
        Assert.Equal(0.0, patents.GetProperty("WeightedContribution").GetDouble());
        Assert.Equal(0, patents.GetProperty("SignalCount").GetInt32());

        // …and the composite is 0.5·insider, NOT insider. Renormalising would make these two equal.
        var composite = JsonDocument.Parse(withDark.ComponentJson).RootElement
            .GetProperty("Composite").GetDouble();
        Assert.Equal(0.5 * insider, composite, 12);
        Assert.NotEqual(insider, composite);
    }

    [Fact]
    public void TwoOfThreeChannelsDark_CapsTheCompositeAtTheSurvivingShare()
    {
        // The stronger form of the same invariant: with two of three channels dark the composite cannot
        // exceed the surviving channel's declared share, however loud that channel is.
        var formula = Formula(
            ScoringChannel.Collector("patents", ["patents"], 0.5, 3),
            ScoringChannel.Collector("insider", ["sec-form4"], 0.3, 2),
            ScoringChannel.Breadth("attention", 0.2, 3));

        // A torrent on the ONE live collector channel; no breadth (first-party evidence only).
        var signals = Enumerable.Range(0, 200).Select(_ => BuildSignal("patents")).ToList();
        var result = formula.Compute(InputFrom(signals));

        var composite = JsonDocument.Parse(result.ComponentJson).RootElement
            .GetProperty("Composite").GetDouble();

        Assert.Equal(0.0, Breakdown(result, "insider").GetProperty("Score").GetDouble());
        Assert.Equal(0.0, Breakdown(result, "attention").GetProperty("Score").GetDouble());
        Assert.True(composite <= 0.5, $"composite {composite} must not exceed the surviving 0.50 share");
        Assert.InRange(result.Components.OpportunityScore, 0, 50);
    }

    [Fact]
    public void AStrategyThatNeverDeclaredTheDarkChannel_IsUnaffectedByIt()
    {
        // Asserted SIDE BY SIDE, because "absence costs something" is only fair if it costs it to the
        // strategy that made the claim and to nobody else.
        var declaresPatents = Formula(
            ScoringChannel.Collector("patents", ["patents"], 0.5, 3),
            ScoringChannel.Collector("insider", ["sec-form4"], 0.5, 2));
        var neverDeclaredPatents = Formula(
            ScoringChannel.Collector("insider", ["sec-form4"], 1.0, 2));

        var input = InputFrom([BuildSignal("sec-form4")]);

        var penalised = declaresPatents.Compute(input);
        var unaffected = neverDeclaredPatents.Compute(input);

        var insiderScore = Breakdown(unaffected, "insider").GetProperty("Score").GetDouble();
        var unaffectedComposite = JsonDocument.Parse(unaffected.ComponentJson).RootElement
            .GetProperty("Composite").GetDouble();
        var penalisedComposite = JsonDocument.Parse(penalised.ComponentJson).RootElement
            .GetProperty("Composite").GetDouble();

        // The strategy that never mentioned patents gets its channel's full share…
        Assert.Equal(insiderScore, unaffectedComposite, 12);
        // …while the one that did is down by exactly the dark 0.50 share.
        Assert.Equal(0.5 * insiderScore, penalisedComposite, 12);
        Assert.True(unaffectedComposite > penalisedComposite);
    }

    [Fact]
    public void LegacyEvidenceWithNoRecordedCollector_ContributesZero_AndDoesNotThrow()
    {
        // Accrued history is never backfilled (specs 142/145), so a collector channel sees nothing for it.
        // That must read as a quiet channel, not as a crash.
        var formula = Formula(ScoringChannel.Collector("patents", ["patents"], 1.0, 3));

        var result = formula.Compute(InputFrom([BuildSignal(collector: null), BuildSignal(collector: null)]));

        Assert.Equal(0.0, Breakdown(result, "patents").GetProperty("Score").GetDouble());
        Assert.Equal(0, result.Components.OpportunityScore);
        // Provenance still names every signal, and says WHY it fed no channel.
        Assert.Equal(2, result.Contributions.Count);
        Assert.All(
            result.Contributions,
            c => Assert.Contains("no recorded collector", c.ContributionReason, StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------------------------------------------
    // Per-channel saturation
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public void PerChannelSaturation_LetsAHighTrafficAndALowTrafficChannelBothLandSensiblyInRange()
    {
        // The reason saturation is MANDATORY per channel: RSS emits constantly and Form 4 rarely. With one
        // shared saturation the chatty channel pins at ~1.0 and the rare one is stranded at the floor, and
        // the weights become decorative. With its own saturation each channel can express a full share.
        var formula = Formula(
            ScoringChannel.Collector("chatty", ["rss"], 0.5, saturation: 400),
            ScoringChannel.Collector("rare", ["sec-form4"], 0.5, saturation: 4));

        var signals = Enumerable.Range(0, 60).Select(_ => BuildSignal("rss")).ToList();
        signals.Add(BuildSignal("sec-form4"));

        var result = formula.Compute(InputFrom(signals));

        var chatty = Breakdown(result, "chatty").GetProperty("Score").GetDouble();
        var rare = Breakdown(result, "rare").GetProperty("Score").GetDouble();

        // 60 chatty signals and ONE rare one land in the same broad band — neither pinned at 1.0 nor at 0.
        Assert.InRange(chatty, 0.05, 0.95);
        Assert.InRange(rare, 0.05, 0.95);
    }

    [Fact]
    public void ChannelScore_RisesMonotonicallyWithActivity_AndIsBoundedBelowOne()
    {
        var formula = Formula(ScoringChannel.Collector("patents", ["patents"], 1.0, 3));

        var scores = new[] { 1, 5, 20, 100 }
            .Select(n => JsonDocument.Parse(
                    formula.Compute(InputFrom(
                        Enumerable.Range(0, n).Select(_ => BuildSignal("patents")).ToList())).ComponentJson)
                .RootElement.GetProperty("Composite").GetDouble())
            .ToArray();

        for (var i = 1; i < scores.Length; i++)
        {
            Assert.True(scores[i] > scores[i - 1], $"{scores[i]} must exceed {scores[i - 1]}");
        }

        Assert.True(scores[^1] < 1.0);
    }

    // ---------------------------------------------------------------------------------------------------
    // Direction
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public void ChannelDirection_PositiveEarnsMoreThanNeutral_WhichEarnsMoreThanNegative()
    {
        var formula = Formula(ScoringChannel.Collector("patents", ["patents"], 1.0, 3));

        double Score(SignalDirection direction) => JsonDocument.Parse(
                formula.Compute(InputFrom(
                    Enumerable.Range(0, 5)
                        .Select(_ => BuildSignal("patents", direction: direction))
                        .ToList())).ComponentJson)
            .RootElement.GetProperty("Composite").GetDouble();

        var positive = Score(SignalDirection.Positive);
        var neutral = Score(SignalDirection.Neutral);
        var negative = Score(SignalDirection.Negative);

        Assert.True(positive > neutral, $"{positive} must exceed neutral {neutral}");
        Assert.True(neutral > negative, $"{neutral} must exceed negative {negative}");
    }

    [Fact]
    public void ChannelWithNoDirectionalMass_SitsAtExactlyHalfItsSaturatedShare()
    {
        // A channel of purely Neutral signals is neither rewarded nor punished for direction: the factor is
        // exactly 0.5, so the channel earns half of whatever its activity saturated to. Neutral signals still
        // count as ACTIVITY (something happened on that channel), which is the same split v8 makes.
        var formula = Formula(ScoringChannel.Collector("patents", ["patents"], 1.0, 3));

        var result = formula.Compute(InputFrom(
            Enumerable.Range(0, 4)
                .Select(_ => BuildSignal("patents", direction: SignalDirection.Neutral))
                .ToList()));

        var channel = Breakdown(result, "patents");
        var score = channel.GetProperty("Score").GetDouble();

        Assert.True(score > 0, "neutral signals are still activity on the channel");
        Assert.True(score < 0.5, "…but the direction factor caps them at half the saturated share");
        Assert.Equal(4, channel.GetProperty("SignalCount").GetInt32());
    }

    // ---------------------------------------------------------------------------------------------------
    // Breadth is a positively-weighted channel (the inversion v9 exists to correct)
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public void BreadthChannel_ContributesMore_WithMoreGenuineBreadth()
    {
        // THE DIRECTION CORRECTION. In v8, AttentionScore enters Opportunity as an INVERSE discount, so a
        // company noticed by more outlets scores LOWER. In v9 breadth is its own positively-weighted channel:
        // more genuine distinct-publisher reach contributes MORE. Do not carry v8's inversion into v9.
        var formula = Formula(ScoringChannel.Breadth("attention", 1.0, 3));

        ScoreComputation Over(int publishers) => formula.Compute(InputFrom(
            Enumerable.Range(0, publishers)
                .Select(i => BuildSignal(
                    "newssearch",
                    sourceType: EvidenceSourceType.NewsArticle,
                    sourceName: $"outlet-{i}"))
                .ToList()));

        var narrow = Over(1);
        var wide = Over(12);

        var narrowScore = Breakdown(narrow, "attention").GetProperty("Score").GetDouble();
        var wideScore = Breakdown(wide, "attention").GetProperty("Score").GetDouble();

        Assert.True(wideScore > narrowScore, $"wide {wideScore} must exceed narrow {narrowScore}");
        Assert.True(wide.Components.OpportunityScore > narrow.Components.OpportunityScore);
    }

    [Fact]
    public void BreadthChannel_ConsumesEverySignalTheStrategyGates_RegardlessOfCollector()
    {
        // Breadth is inherently cross-source: it cannot be scoped to a collector without losing its meaning,
        // so it reads the whole gated set even when the collectors involved are budgeted nowhere.
        var formula = Formula(ScoringChannel.Breadth("attention", 1.0, 3));

        var result = formula.Compute(InputFrom(
        [
            BuildSignal("newssearch", sourceType: EvidenceSourceType.NewsArticle, sourceName: "reuters"),
            BuildSignal("gdelt", sourceType: EvidenceSourceType.NewsArticle, sourceName: "bloomberg"),
            BuildSignal(collector: null, sourceType: EvidenceSourceType.NewsArticle, sourceName: "wsj"),
        ]));

        var channel = Breakdown(result, "attention");
        Assert.Equal(3, channel.GetProperty("SignalCount").GetInt32());
        // reach 3 (three distinct genuine publishers), saturation 3 ⇒ 3/(3+3) = 0.5.
        Assert.Equal(0.5, channel.GetProperty("Score").GetDouble(), 12);
    }

    [Fact]
    public void BreadthChannel_IsAttributedOnlyToTheSignalsThatFedItsReach()
    {
        // Provenance must be TRUE of the signal it names: a first-party press release is not market
        // attention and did not feed the breadth term, so naming the breadth channel on its contribution
        // would be a lie. A signal consumed by BOTH a collector channel and breadth names both.
        var formula = Formula(
            ScoringChannel.Collector("news", ["newssearch"], 0.5, 3),
            ScoringChannel.Breadth("attention", 0.5, 3));

        var thirdParty = BuildSignal(
            "newssearch", sourceType: EvidenceSourceType.NewsArticle, sourceName: "reuters");
        var firstParty = BuildSignal(
            "newssearch", sourceType: EvidenceSourceType.PressRelease, sourceName: "Acme Newsroom");

        var result = formula.Compute(InputFrom([thirdParty, firstParty]));

        // Consumed by both channels — the reason names both (channels are listed in canonical name order).
        Assert.Contains(
            "channel attention + news", result.Contributions[0].ContributionReason, StringComparison.Ordinal);
        // Consumed by the collector channel only.
        Assert.Contains("channel news", result.Contributions[1].ContributionReason, StringComparison.Ordinal);
        Assert.DoesNotContain("attention", result.Contributions[1].ContributionReason, StringComparison.Ordinal);

        Assert.Equal(1, Breakdown(result, "attention").GetProperty("SignalCount").GetInt32());
        Assert.Equal(2, Breakdown(result, "news").GetProperty("SignalCount").GetInt32());
    }

    // ---------------------------------------------------------------------------------------------------
    // Provenance
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public void Provenance_DistinguishesRanAndFoundNothing_FromDidNotRun()
    {
        // A 0 is a 0 either way — Radar scores evidence and absence of evidence is not evidence — but which
        // it was must be recoverable after the fact.
        var formula = Formula(
            ScoringChannel.Collector("patents", ["patents"], 0.5, 3),
            ScoringChannel.Collector("fda", ["fda"], 0.5, 3));

        var result = formula.Compute(InputFrom(
            current: [],
            enabledCollectors: ["patents", "sec-form4"]));

        var ranButQuiet = Breakdown(result, "patents");
        var neverRan = Breakdown(result, "fda");

        Assert.Equal(["patents"], ranButQuiet.GetProperty("CollectorsRan").EnumerateArray().Select(e => e.GetString()));
        Assert.Empty(ranButQuiet.GetProperty("CollectorsNotRun").EnumerateArray());

        Assert.Empty(neverRan.GetProperty("CollectorsRan").EnumerateArray());
        Assert.Equal(["fda"], neverRan.GetProperty("CollectorsNotRun").EnumerateArray().Select(e => e.GetString()));

        // Both scored 0 — the distinction is provenance, never a score difference.
        Assert.Equal(0.0, ranButQuiet.GetProperty("Score").GetDouble());
        Assert.Equal(0.0, neverRan.GetProperty("Score").GetDouble());
    }

    [Fact]
    public void EveryCurrentWindowSignal_GetsExactlyOneContribution_InInputOrder_NamingItsChannel()
    {
        // The IScoreFormula contract, unchanged from v8 — plus the channel attribution that makes
        // evidence → signal → channel → score traceable.
        var formula = Formula(
            ScoringChannel.Collector("patents", ["patents"], 0.5, 3),
            ScoringChannel.Collector("insider", ["sec-form4"], 0.5, 2));

        var signals = new[]
        {
            BuildSignal("patents"),
            BuildSignal("sec-form4"),
            BuildSignal("gdelt"),
        };

        var previous = new[] { BuildSignal("patents").Signal };
        var result = formula.Compute(InputFrom(signals, previous));

        Assert.Equal(3, result.Contributions.Count);
        Assert.Equal(
            signals.Select(s => s.Signal.Id),
            result.Contributions.Select(c => c.SignalId));
        Assert.Equal(
            signals.Select(s => s.Evidence.Id),
            result.Contributions.Select(c => c.EvidenceId));

        Assert.Contains("channel patents", result.Contributions[0].ContributionReason, StringComparison.Ordinal);
        Assert.Contains("channel insider", result.Contributions[1].ContributionReason, StringComparison.Ordinal);
        // A signal from a collector nobody budgeted for is named as such rather than silently unexplained.
        Assert.Contains(
            "not budgeted by this strategy", result.Contributions[2].ContributionReason, StringComparison.Ordinal);
    }

    [Fact]
    public void Explanation_NamesEveryChannel_AndFlagsTheDarkOnes()
    {
        var formula = Formula(
            ScoringChannel.Collector("patents", ["patents"], 0.4, 3),
            ScoringChannel.Collector("insider", ["sec-form4"], 0.3, 2),
            ScoringChannel.Breadth("attention", 0.3, 3));

        // Only the patents channel has anything: the insider channel consumed no signals, and the breadth
        // channel has zero reach (first-party evidence is not market attention).
        var result = formula.Compute(InputFrom([BuildSignal("patents")]));

        Assert.Contains("patents", result.Explanation, StringComparison.Ordinal);
        Assert.Contains("insider", result.Explanation, StringComparison.Ordinal);
        Assert.Contains("attention", result.Explanation, StringComparison.Ordinal);
        // Both dark channels are flagged, including the breadth one — "dark" means "nothing to measure",
        // which for breadth is zero reach rather than zero consumed signals.
        Assert.Equal(2, result.Explanation.Split("(dark)").Length - 1);
        Assert.True(Breakdown(result, "insider").GetProperty("Dark").GetBoolean());
        Assert.True(Breakdown(result, "attention").GetProperty("Dark").GetBoolean());
        Assert.False(Breakdown(result, "patents").GetProperty("Dark").GetBoolean());
    }

    [Fact]
    public void Dark_MeansNothingToMeasure_NotMerelyScoredLow()
    {
        // A channel of uniformly NEGATIVE signals scores low — but it is NOT dark: it measured something, and
        // what it measured was a deteriorating trajectory. Conflating "scored low" with "had nothing to
        // measure" would make the provenance lie about why a share was lost, which is precisely the question
        // the per-channel breakdown exists to answer.
        var formula = Formula(ScoringChannel.Collector("patents", ["patents"], 1.0, 3));

        ScoreComputation Over(SignalDirection direction) => formula.Compute(InputFrom(
            Enumerable.Range(0, 4)
                .Select(_ => BuildSignal("patents", direction: direction))
                .ToList()));

        var negative = Breakdown(Over(SignalDirection.Negative), "patents");
        var neutral = Breakdown(Over(SignalDirection.Neutral), "patents");
        var empty = Breakdown(formula.Compute(InputFrom()), "patents");

        Assert.False(negative.GetProperty("Dark").GetBoolean());
        Assert.Equal(4, negative.GetProperty("SignalCount").GetInt32());
        Assert.True(negative.GetProperty("Score").GetDouble() > 0);
        Assert.True(negative.GetProperty("Score").GetDouble() < neutral.GetProperty("Score").GetDouble());

        // …whereas a channel that consumed nothing at all IS dark, and scores exactly 0.
        Assert.True(empty.GetProperty("Dark").GetBoolean());
        Assert.Equal(0.0, empty.GetProperty("Score").GetDouble());
        Assert.DoesNotContain(
            "(dark)", Over(SignalDirection.Negative).Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public void ComponentJson_StillDeserializesAsScoreComponents()
    {
        // The enrichment must not break any reader that treats ComponentJson as the five-component shape:
        // the five properties keep their names and come first, and extra properties are ignored.
        var formula = Formula(ScoringChannel.Collector("patents", ["patents"], 1.0, 3));

        var result = formula.Compute(InputFrom([BuildSignal("patents")]));
        var roundTripped = JsonSerializer.Deserialize<ScoreComponents>(result.ComponentJson);

        Assert.Equal(result.Components, roundTripped);
        Assert.Equal(
            ScoreFormulaVersions.V9,
            JsonDocument.Parse(result.ComponentJson).RootElement.GetProperty("Formula").GetString());
    }

    [Fact]
    public void ComponentJson_CarriesEachChannelsWeightSaturationAndDeclaredCollectors()
    {
        var formula = Formula(
            ScoringChannel.Collector("filings", ["sec-edgar", "sec-form4"], 0.7, 2.5),
            ScoringChannel.Breadth("attention", 0.3, 3));

        var result = formula.Compute(InputFrom([BuildSignal("sec-edgar")]));
        var filings = Breakdown(result, "filings");

        Assert.Equal("collector", filings.GetProperty("Kind").GetString());
        Assert.Equal(0.7, filings.GetProperty("Weight").GetDouble());
        Assert.Equal(2.5, filings.GetProperty("Saturation").GetDouble());
        Assert.Equal(1, filings.GetProperty("SignalCount").GetInt32());
        Assert.Equal(
            ["sec-edgar", "sec-form4"],
            filings.GetProperty("Collectors").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(
            "breadth", Breakdown(result, "attention").GetProperty("Kind").GetString());
    }

    // ---------------------------------------------------------------------------------------------------
    // The other four components keep their v8 meanings
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public void TheOtherFourComponents_MatchV8_OverTheSameGatedSet()
    {
        // Only OpportunityScore changes meaning in v9. Trajectory / Attention / EvidenceConfidence /
        // SignalVelocity keep their v8 values so the weekly report's action thresholds stay valid and a v9
        // strategy's snapshot stays legible next to a v8 one.
        var weights = new ScoringWeights();
        var v8 = new RadarScoreFormulaV8(weights, AllGenuine);
        var v9 = Formula(
            ScoringChannel.Collector("patents", ["patents"], 0.5, 3),
            ScoringChannel.Breadth("attention", 0.5, 3));

        var signals = new[]
        {
            BuildSignal("patents", direction: SignalDirection.Positive, strength: 7),
            BuildSignal("newssearch", sourceType: EvidenceSourceType.NewsArticle, sourceName: "reuters"),
            BuildSignal("gdelt", direction: SignalDirection.Negative, strength: 3),
        };
        var previous = new[] { BuildSignal("patents").Signal, BuildSignal("patents").Signal };
        var input = InputFrom(signals, previous);

        var a = v8.Compute(input).Components;
        var b = v9.Compute(input).Components;

        Assert.Equal(a.TrajectoryScore, b.TrajectoryScore);
        Assert.Equal(a.AttentionScore, b.AttentionScore);
        Assert.Equal(a.EvidenceConfidenceScore, b.EvidenceConfidenceScore);
        Assert.Equal(a.SignalVelocityScore, b.SignalVelocityScore);
        // Opportunity is the one that differs: v8 discounts by attention, v9 composes the channel budget.
        Assert.NotEqual(a.OpportunityScore, b.OpportunityScore);
    }

    [Fact]
    public void Compute_IsPureAndDeterministic()
    {
        var formula = Formula(
            ScoringChannel.Collector("patents", ["patents"], 0.5, 3),
            ScoringChannel.Breadth("attention", 0.5, 3));

        var input = InputFrom(
            [BuildSignal("patents"), BuildSignal("newssearch", sourceType: EvidenceSourceType.NewsArticle)],
            enabledCollectors: ["patents"]);

        var a = formula.Compute(input);
        var b = formula.Compute(input);

        Assert.Equal(a.Components, b.Components);
        Assert.Equal(a.Explanation, b.Explanation);
        Assert.Equal(a.ComponentJson, b.ComponentJson);
        Assert.Equal(
            a.Contributions.Select(c => (c.SignalId, c.EvidenceId, c.ContributionReason, c.ContributionWeight)),
            b.Contributions.Select(c => (c.SignalId, c.EvidenceId, c.ContributionReason, c.ContributionWeight)));
    }
}
