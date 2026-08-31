using Radar.Application.Filings;
using Radar.Application.Signals;
using Radar.Domain.Signals;
using Radar.TestSupport;

namespace Radar.Application.Tests.Signals;

/// <summary>
/// The SHARED stable cross-run identity (spec 85's key, extracted in spec 142; the spec-205 filing-read
/// provenance bit appended) used by both the previous-window disk read and the durable repository read, so
/// the two can never drift apart.
/// </summary>
public sealed class SignalCrossRunDedupeTests
{
    private static readonly Guid Company = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid Evidence = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001");
    private static readonly DateTimeOffset Observed = new(2026, 2, 6, 9, 30, 0, TimeSpan.Zero);

    private static Signal Copy(string id, DateTimeOffset createdAt) => new SignalBuilder()
        .WithId(Guid.Parse(id))
        .WithEvidenceId(Evidence)
        .WithCompanyId(Company)
        .WithType(SignalType.CustomerWin)
        .WithDirection(SignalDirection.Positive)
        .WithObservedAtUtc(Observed)
        .WithCreatedAtUtc(createdAt)
        .Build();

    /// <summary>A real spec-204 read envelope, composed through the ONE producer (never a hand-rolled bag).</summary>
    private static string ReadEnvelope(decimal confidence = 0.3m) =>
        FilingReadSignalMetadata.Compose(
            FilingNoSignalCause.Unknown, "Unknown", confidence, "openai:test-model");

    [Fact]
    public void Key_IsCompanyEvidenceTypeDirectionAndFilingReadBit()
    {
        var signal = Copy("00000000-0000-0000-0000-000000000001", Observed);

        Assert.Equal(
            (Company, Evidence, SignalType.CustomerWin, SignalDirection.Positive, false),
            SignalCrossRunDedupe.Key(signal));
    }

    [Fact]
    public void Key_IgnoresIdAndTimestampsAndExtractorDerivedFields()
    {
        var a = Copy("00000000-0000-0000-0000-000000000001", Observed);
        var b = Copy("ffffffff-0000-0000-0000-000000000002", Observed.AddDays(9)) with
        {
            Strength = 9,
            Novelty = 1,
            Confidence = 0.1m,
            Reason = "different reason",
        };

        Assert.Equal(SignalCrossRunDedupe.Key(a), SignalCrossRunDedupe.Key(b));
    }

    [Fact]
    public void Key_DistinguishesTheDistinctSignalsOneEvidenceItemCanProduce()
    {
        var win = Copy("00000000-0000-0000-0000-000000000001", Observed);
        var guidance = win with { Type = SignalType.GuidanceChange };
        var neutral = guidance with { Direction = SignalDirection.Neutral };

        Assert.NotEqual(SignalCrossRunDedupe.Key(win), SignalCrossRunDedupe.Key(guidance));
        Assert.NotEqual(SignalCrossRunDedupe.Key(guidance), SignalCrossRunDedupe.Key(neutral));
    }

    // ---- spec 205: the filing-read provenance bit — one boolean, never the envelope's values -------------

    [Fact]
    public void Key_KeywordNeutralAndFilingReadNeutral_AreDistinctProvenanceClasses()
    {
        // The defect this bit closes: a keyword Neutral and a spec-204 AI-read Neutral over the SAME filing
        // shared one key, so the durable collapse kept the earliest (the keyword) and discarded the read
        // before GuidanceChangeSupersede — the rule built to prefer it — could apply its filingReadOutcome
        // preference.
        var keyword = Copy("00000000-0000-0000-0000-000000000001", Observed) with
        {
            Type = SignalType.GuidanceChange,
            Direction = SignalDirection.Neutral,
            MetadataJson = null,
        };
        var read = keyword with
        {
            Id = Guid.Parse("ffffffff-0000-0000-0000-000000000002"),
            MetadataJson = ReadEnvelope(),
        };

        Assert.NotEqual(SignalCrossRunDedupe.Key(keyword), SignalCrossRunDedupe.Key(read));
        Assert.True(SignalCrossRunDedupe.Key(read).FilingReadOutcomeRecorded);
        Assert.False(SignalCrossRunDedupe.Key(keyword).FilingReadOutcomeRecorded);
    }

    [Fact]
    public void Key_IsABooleanClass_NotTheEnvelopeValues_SoSameReadCopiesShareOneKey()
    {
        // Deliberately a provenance-class BIT: two persisted copies of the same AI read key identically even
        // when the envelope's recorded values differ (here the confidence) — repeated copies of one read must
        // still collapse across runs, and identity must never grow into hashed extractor metadata.
        var a = Copy("00000000-0000-0000-0000-000000000001", Observed) with
        {
            Type = SignalType.GuidanceChange,
            Direction = SignalDirection.Neutral,
            MetadataJson = ReadEnvelope(0.3m),
        };
        var b = a with
        {
            Id = Guid.Parse("ffffffff-0000-0000-0000-000000000002"),
            MetadataJson = ReadEnvelope(0.55m),
        };

        Assert.Equal(SignalCrossRunDedupe.Key(a), SignalCrossRunDedupe.Key(b));
    }

    [Fact]
    public void Key_ReadBitIsFalseForNonGuidanceChange_EvenIfAnEnvelopeIsPresent()
    {
        // Every non-GuidanceChange signal keeps the exact spec-142 identity it had: the bit is gated on the
        // signal TYPE first, so a stray envelope on another type cannot split its cross-run copies.
        var withEnvelope = Copy("00000000-0000-0000-0000-000000000001", Observed) with
        {
            MetadataJson = ReadEnvelope(),
        };

        Assert.False(SignalCrossRunDedupe.Key(withEnvelope).FilingReadOutcomeRecorded);
        Assert.Equal(
            SignalCrossRunDedupe.Key(Copy("00000000-0000-0000-0000-000000000001", Observed)),
            SignalCrossRunDedupe.Key(withEnvelope));
    }

    [Fact]
    public void Collapse_KeepsKeywordAndReadNeutralApart_WhileSameReadCopiesStillCollapse()
    {
        var keyword = Copy("00000000-0000-0000-0000-000000000001", Observed) with
        {
            Type = SignalType.GuidanceChange,
            Direction = SignalDirection.Neutral,
            MetadataJson = null,
        };
        var readCopy1 = keyword with
        {
            Id = Guid.Parse("88888888-0000-0000-0000-000000000002"),
            MetadataJson = ReadEnvelope(),
        };
        var readCopy2 = readCopy1 with { Id = Guid.Parse("ffffffff-0000-0000-0000-000000000003") };

        var collapsed = SignalCrossRunDedupe.Collapse(
            [keyword, readCopy1, readCopy2], SignalCopySurvivor.EarliestKnown);

        // Two provenance classes survive (keyword + read); the read's cross-run copies collapsed to one.
        Assert.Equal(2, collapsed.Count);
        Assert.Contains(collapsed, s => s.Id == keyword.Id);
        Assert.Contains(collapsed, s => s.Id == readCopy1.Id);
    }

    [Fact]
    public void Collapse_LowestId_KeepsTheLowestGuid()
    {
        var lowest = Copy("00000000-0000-0000-0000-000000000001", Observed.AddDays(5));
        var middle = Copy("88888888-0000-0000-0000-000000000002", Observed.AddDays(1));
        var highest = Copy("ffffffff-0000-0000-0000-000000000003", Observed.AddDays(3));

        var collapsed = Assert.Single(
            SignalCrossRunDedupe.Collapse([highest, middle, lowest], SignalCopySurvivor.LowestId));

        Assert.Equal(lowest.Id, collapsed.Id);
    }

    [Fact]
    public void Collapse_EarliestKnown_KeepsTheEarliestCreatedAt()
    {
        var earliest = Copy("ffffffff-0000-0000-0000-000000000003", Observed.AddDays(1));
        var middle = Copy("88888888-0000-0000-0000-000000000002", Observed.AddDays(3));
        var latest = Copy("00000000-0000-0000-0000-000000000001", Observed.AddDays(5));

        var collapsed = Assert.Single(
            SignalCrossRunDedupe.Collapse([latest, middle, earliest], SignalCopySurvivor.EarliestKnown));

        // The latest copy has the LOWEST Guid, so a lowest-Id tie-break would have picked it.
        Assert.Equal(earliest.Id, collapsed.Id);
    }

    [Fact]
    public void Collapse_EarliestKnown_TieBreaksOnLowestIdWhenCreatedAtIsEqual()
    {
        var low = Copy("00000000-0000-0000-0000-000000000001", Observed);
        var high = Copy("ffffffff-0000-0000-0000-000000000002", Observed);

        var collapsed = Assert.Single(
            SignalCrossRunDedupe.Collapse([high, low], SignalCopySurvivor.EarliestKnown));

        Assert.Equal(low.Id, collapsed.Id);
    }

    [Fact]
    public void Collapse_IsOrderIndependent()
    {
        var a = Copy("00000000-0000-0000-0000-000000000001", Observed.AddDays(5));
        var b = Copy("88888888-0000-0000-0000-000000000002", Observed.AddDays(1));
        var c = Copy("ffffffff-0000-0000-0000-000000000003", Observed.AddDays(3));

        foreach (var rule in Enum.GetValues<SignalCopySurvivor>())
        {
            var forward = SignalCrossRunDedupe.Collapse([a, b, c], rule).Single().Id;
            var reverse = SignalCrossRunDedupe.Collapse([c, b, a], rule).Single().Id;
            Assert.Equal(forward, reverse);
        }
    }

    [Fact]
    public void Collapse_LeavesDistinctSignalsAlone()
    {
        var win = Copy("00000000-0000-0000-0000-000000000001", Observed);
        var guidance = Copy("00000000-0000-0000-0000-000000000002", Observed) with
        {
            Type = SignalType.GuidanceChange,
        };

        Assert.Equal(
            2, SignalCrossRunDedupe.Collapse([win, guidance], SignalCopySurvivor.EarliestKnown).Count);
    }
}
