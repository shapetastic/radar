using Radar.Application.Acquisitions;
using Radar.Application.Scoring;
using Radar.Application.Tests.Acquisitions;
using Radar.Domain.Evidence;
using Radar.Domain.Signals;

namespace Radar.Application.Tests.Scoring;

/// <summary>
/// SPEC 217 §2 (v2 since spec 226) — <c>CorporateActionSupersede</c>: the assembly-time rewrite that stops a takeover scoring as a
/// partnership. The regression this pins is measured, not hypothetical: MarineMax's 2026-08-10 merger 8-K
/// minted a POSITIVE <c>StrategicPartnership</c> at strength 4 and trajectory rose 56 → 62.
/// </summary>
public sealed class CorporateActionSupersedeTests
{
    private static readonly Guid Company = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static readonly Guid MergerEvidence = Guid.Parse("db2132fb-c4b5-d2e7-6449-4716a6186a83");

    private static readonly DateTimeOffset Observed = new(2026, 8, 10, 8, 0, 12, TimeSpan.Zero);

    private static Signal Partnership(Guid evidenceId, int strength = 4) => new(
        Id: Guid.NewGuid(),
        EvidenceId: evidenceId,
        CompanyId: Company,
        CompanyMention: "MarineMax",
        Type: SignalType.StrategicPartnership,
        Direction: SignalDirection.Positive,
        Strength: strength,
        Novelty: 5,
        Confidence: 0.8m,
        SupportingExcerpt: "Entry into a Material Definitive Agreement.",
        Reason: "matched 'material definitive agreement'",
        ReviewStatus: SignalReviewStatus.Approved,
        ObservedAtUtc: Observed,
        CreatedAtUtc: Observed);

    private static Signal OfType(SignalType type, Guid evidenceId) => Partnership(evidenceId) with
    {
        Id = Guid.NewGuid(),
        Type = type,
    };

    private static EvidenceItem Evidence(Guid id) => new(
        Id: id,
        SourceType: EvidenceSourceType.Filing,
        SourceName: "MarineMax, Inc. — SEC filings (EDGAR)",
        SourceUrl: "https://www.sec.gov/x-index.htm",
        Title: "8-K [items: 1.01,7.01,9.01]",
        Summary: null,
        RawText: "8-K",
        ContentHash: id.ToString("N"),
        PublishedAtUtc: Observed,
        CollectedAtUtc: Observed,
        Quality: EvidenceQuality.High,
        MetadataJson: null);

    private static PendingAcquisitions WithRecognition() =>
        PendingAcquisitionsTests.From(
            PendingAcquisitionsTests.Record(companyId: Company, evidenceId: MergerEvidence));

    /// <summary>
    /// The v9 extractor's read of the same filing (spec 226): a Neutral CorporateAction at the rule strength 4,
    /// with the item-heading Reason.
    /// </summary>
    private static Signal V9CorporateAction(Guid evidenceId) => Partnership(evidenceId) with
    {
        Id = Guid.NewGuid(),
        Type = SignalType.CorporateAction,
        Direction = SignalDirection.Neutral,
        Confidence = 0.5m,
        Reason = Radar.Application.SignalExtraction.KeywordSignalReasons.ItemHeading(
            [Radar.Application.SignalExtraction.SecItemHeadingPhrases.MaterialDefinitiveAgreement]),
    };

    [Fact]
    public void Version_IsTheDeclaredSupersedeIdentity()
    {
        // v2 (spec 226): the match widened to StrategicPartnership OR CorporateAction.
        Assert.Equal("acq-supersede-v2", CorporateActionSupersede.Version);
    }

    [Fact]
    public void AccruedV8PartnershipRead_BecomesExactlyOneCorporateAction_CountedAsAPartnership()
    {
        var signal = Partnership(MergerEvidence);
        var result = CorporateActionSupersede.Apply(
            new List<ScoringSignal> { new(signal, Evidence(MergerEvidence)) }, WithRecognition(), Company);

        Assert.Single(result.Signals, s => s.Signal.Type == SignalType.CorporateAction);
        Assert.Equal(CorporateActionSupersedeOutcome.Rewrote, result.Outcome);
        Assert.Equal(1, result.TotalSuperseded);
        Assert.Equal(1, result.SupersededFromStrategicPartnership);
        Assert.Equal(0, result.SupersededFromCorporateAction);
        Assert.Equal(0, result.DuplicatesCollapsed);
    }

    [Fact]
    public void V9ExtractorCorporateActionRead_BecomesExactlyOneStrengthZeroCorporateAction_NamingTheAcquisition()
    {
        var signal = V9CorporateAction(MergerEvidence);
        var result = CorporateActionSupersede.Apply(
            new List<ScoringSignal> { new(signal, Evidence(MergerEvidence)) }, WithRecognition(), Company);

        var rewritten = Assert.Single(result.Signals).Signal;
        Assert.Equal(signal.Id, rewritten.Id);
        Assert.Equal(SignalType.CorporateAction, rewritten.Type);
        Assert.Equal(SignalDirection.Neutral, rewritten.Direction);
        Assert.Equal(0, rewritten.Strength);
        Assert.Equal(0, rewritten.Novelty);
        Assert.Contains("Safe Harbor Marinas, LLC", rewritten.Reason, StringComparison.Ordinal);
        Assert.Equal(CorporateActionSupersedeOutcome.Rewrote, result.Outcome);
        Assert.Equal(1, result.TotalSuperseded);
        Assert.Equal(0, result.SupersededFromStrategicPartnership);
        Assert.Equal(1, result.SupersededFromCorporateAction);
    }

    [Fact]
    public void AnAccruedV8AndAV9ReadOfTheSameFiling_CollapseToOneCorporateAction_AndTheDuplicateIsCounted()
    {
        // A v8 heading read and a v9 heading read of the same evidence do not coexist on the normal path
        // (evidence is extracted once); this pins the guard for the abnormal one (a lost-and-recollected raw
        // file). The normal-path duplicate is the next test.
        var v8 = Partnership(MergerEvidence) with { ObservedAtUtc = Observed };
        var v9 = V9CorporateAction(MergerEvidence) with { ObservedAtUtc = Observed.AddMinutes(1) };
        var other = OfType(SignalType.GuidanceChange, MergerEvidence);
        var input = new List<Signal> { v8, other, v9 };

        var result = CorporateActionSupersede.Apply(input, WithRecognition(), Company);

        var corporateAction = Assert.Single(result.Signals, s => s.Type == SignalType.CorporateAction);
        Assert.Equal(v8.Id, corporateAction.Id);                   // the earliest ObservedAtUtc survives
        Assert.Equal(0, corporateAction.Strength);
        Assert.Equal([v8.Id, other.Id], result.Signals.Select(s => s.Id));
        Assert.Equal(1, result.TotalSuperseded);
        Assert.Equal(1, result.SupersededFromStrategicPartnership);
        Assert.Equal(1, result.SupersededFromCorporateAction);
        Assert.Equal(1, result.DuplicatesCollapsed);
        Assert.Contains("1 further keyword read(s) of this filing were collapsed", corporateAction.Reason, StringComparison.Ordinal);
        Assert.Equal(corporateAction.Reason, result.SupersededReasons[v8.Id]);
    }

    [Fact]
    public void V9PartnershipPhrasePlusHeading_OneExtraction_CollapsesToOneCorporateAction_WithADeterministicSurvivor()
    {
        // THE NORMAL-PATH duplicate under v9: one extraction of a recognised 8-K whose text carries both a
        // "partnership" phrase (StrategicPartnership, Positive, strength 5) and an item heading (CorporateAction,
        // Neutral, strength 4) — two types, so first-match-per-type keeps both. Same evidence, same run, so the
        // two signals share ObservedAtUtc; the survivor must not depend on the order they arrive in.
        var partnership = Partnership(MergerEvidence, strength: 5) with
        {
            Id = Guid.Parse("00000000-0000-0000-0000-00000000000b"),
            Reason = Radar.Application.SignalExtraction.KeywordSignalReasons.MatchedPhrase("partnership"),
        };
        var heading = V9CorporateAction(MergerEvidence) with
        {
            Id = Guid.Parse("00000000-0000-0000-0000-00000000000a"),
        };
        Assert.Equal(partnership.ObservedAtUtc, heading.ObservedAtUtc);

        foreach (var input in new[] { new List<Signal> { partnership, heading }, new List<Signal> { heading, partnership } })
        {
            var result = CorporateActionSupersede.Apply(input, WithRecognition(), Company);

            var survivor = Assert.Single(result.Signals);
            Assert.Equal(heading.Id, survivor.Id);                   // equal instants ⇒ the lowest id, either order
            Assert.Equal(SignalType.CorporateAction, survivor.Type);
            Assert.Equal(SignalDirection.Neutral, survivor.Direction);
            Assert.Equal(0, survivor.Strength);
            Assert.Equal(1, result.TotalSuperseded);
            Assert.Equal(1, result.DuplicatesCollapsed);
            Assert.Equal(1, result.SupersededFromStrategicPartnership);
            Assert.Equal(1, result.SupersededFromCorporateAction);
            Assert.Equal(survivor.Reason, result.SupersededReasons[heading.Id]);
        }
    }

    [Fact]
    public void RecognitionWithNoSignalOverTheFiling_RewritesNothing_AndSaysSo()
    {
        var input = new List<Signal> { Partnership(Guid.NewGuid()) };

        var result = CorporateActionSupersede.Apply(input, WithRecognition(), Company);

        Assert.Same(input, result.Signals);
        Assert.Equal(0, result.TotalSuperseded);
        Assert.Equal(CorporateActionSupersedeOutcome.RecognisedFilingHasNoSignalInWindow, result.Outcome);
        Assert.True(result.RecognisedButNothingRewritten);
    }

    [Fact]
    public void RecognisedFilingWithOnlyNonRewritableSignals_RewritesNothing_OnItsOwnAxis()
    {
        var input = new List<Signal> { OfType(SignalType.GuidanceChange, MergerEvidence) };

        var result = CorporateActionSupersede.Apply(input, WithRecognition(), Company);

        Assert.Same(input, result.Signals);
        Assert.Equal(CorporateActionSupersedeOutcome.RecognisedFilingHasNoRewritableSignal, result.Outcome);
        Assert.True(result.RecognisedButNothingRewritten);
    }

    [Fact]
    public void AnOrdinaryV9CorporateActionOnAnotherFiling_KeepsItsStrength()
    {
        var elsewhere = V9CorporateAction(Guid.NewGuid());
        var input = new List<Signal> { elsewhere, Partnership(MergerEvidence) };

        var result = CorporateActionSupersede.Apply(input, WithRecognition(), Company);

        Assert.Equal(4, result.Signals[0].Strength);
        Assert.Equal(elsewhere, result.Signals[0]);
        Assert.Equal(1, result.TotalSuperseded);
    }

    [Fact]
    public void RecognisedFilingsPartnership_BecomesANeutralCorporateActionAtStrengthZero()
    {
        var signal = Partnership(MergerEvidence);
        var input = new List<ScoringSignal> { new(signal, Evidence(MergerEvidence)) };

        var result = CorporateActionSupersede.Apply(input, WithRecognition(), Company);

        var rewritten = Assert.Single(result.Signals).Signal;
        Assert.Equal(SignalType.CorporateAction, rewritten.Type);
        Assert.Equal(SignalDirection.Neutral, rewritten.Direction);
        Assert.Equal(0, rewritten.Strength);
        Assert.Equal(0, rewritten.Novelty);

        // The ID is PRESERVED on purpose: the persisted ScoreEvidenceLink still points at the signal on
        // disk, so provenance walks report → snapshot → signal → evidence unbroken and the rewrite is
        // visible as a CHANGE rather than as a disappearance.
        Assert.Equal(signal.Id, rewritten.Id);
        Assert.Equal(signal.EvidenceId, rewritten.EvidenceId);
        Assert.Equal(signal.ObservedAtUtc, rewritten.ObservedAtUtc);

        // Counted, and the reason names the acquisition (the contribution reason quotes it verbatim).
        Assert.Equal(1, result.TotalSuperseded);
        var reason = Assert.Contains(signal.Id, result.SupersededReasons);
        Assert.Contains(CorporateActionSupersede.Version, reason, StringComparison.Ordinal);
        Assert.Contains("Safe Harbor Marinas, LLC", reason, StringComparison.Ordinal);
        Assert.Contains("$53.00 per share in cash", reason, StringComparison.Ordinal);
        Assert.Equal(reason, rewritten.Reason);
    }

    [Fact]
    public void NothingElseIsTouched_NotEvenOnTheSameEvidence()
    {
        // The extractor's partnership read is the ONE thing a recognition contradicts. The filing's own
        // GuidanceChange, an insider filing, an article — all untouched.
        var other = OfType(SignalType.GuidanceChange, MergerEvidence);
        var elsewhere = Partnership(Guid.NewGuid());
        var input = new List<ScoringSignal>
        {
            new(Partnership(MergerEvidence), Evidence(MergerEvidence)),
            new(other, Evidence(MergerEvidence)),
            new(elsewhere, Evidence(elsewhere.EvidenceId)),
        };

        var result = CorporateActionSupersede.Apply(input, WithRecognition(), Company);

        Assert.Equal(3, result.Signals.Count);                       // a rewrite, never a removal
        Assert.Equal(1, result.TotalSuperseded);
        Assert.Equal(SignalType.GuidanceChange, result.Signals[1].Signal.Type);
        Assert.Equal(SignalType.StrategicPartnership, result.Signals[2].Signal.Type);
        Assert.Equal(SignalDirection.Positive, result.Signals[2].Signal.Direction);
    }

    [Fact]
    public void NoRecognitionForThisCompany_ReturnsTheInputInstance()
    {
        // The fast path is not an optimisation detail: it is what makes "byte-identical to pre-217 for every
        // company without a recognition" structural rather than asserted.
        var input = new List<ScoringSignal> { new(Partnership(MergerEvidence), Evidence(MergerEvidence)) };

        var untouched = CorporateActionSupersede.Apply(input, PendingAcquisitions.None, Company);
        Assert.Same(input, untouched.Signals);
        Assert.Equal(0, untouched.TotalSuperseded);

        var otherCompany = CorporateActionSupersede.Apply(input, WithRecognition(), Guid.NewGuid());
        Assert.Same(input, otherCompany.Signals);
    }

    [Fact]
    public void PreviousWindowOverload_RewritesToo_SoVelocityIsNotMovedByANonEvent()
    {
        var input = new List<Signal> { Partnership(MergerEvidence) };

        var result = CorporateActionSupersede.Apply(input, WithRecognition(), Company);

        Assert.Equal(SignalType.CorporateAction, Assert.Single(result.Signals).Type);
        Assert.Equal(0, result.Signals[0].Strength);
        Assert.Equal(1, result.TotalSuperseded);
    }

    [Fact]
    public void Apply_IsOrderIndependentAndPreservesRelativeOrdering()
    {
        var first = Partnership(Guid.NewGuid());
        var target = Partnership(MergerEvidence);
        var last = Partnership(Guid.NewGuid());
        var input = new List<Signal> { first, target, last };

        var result = CorporateActionSupersede.Apply(input, WithRecognition(), Company);

        Assert.Equal([first.Id, target.Id, last.Id], result.Signals.Select(s => s.Id));
        Assert.Equal(SignalType.CorporateAction, result.Signals[1].Type);
    }
}
