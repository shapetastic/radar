using Radar.Application.Efficacy.FilingReads;
using Radar.Application.Filings;
using Radar.Application.Prices;
using Radar.Domain.Evidence;

using static Radar.Application.Tests.Efficacy.FilingReads.FilingReadTestFakes;

namespace Radar.Application.Tests.Efficacy.FilingReads;

/// <summary>
/// Spec 218: every count has a named denominator, and no code path drops a read without counting it. These
/// tests are written against the counting rules, not against a happy path — the point of the slice is that
/// nothing disappears.
/// </summary>
public sealed class DirectionalFilingReadReporterTests
{
    private static readonly DateOnly FilingDate = new(2026, 8, 3);

    [Fact]
    public async Task DirectionCounts_SplitPositiveNegativeAndOtherToken_WithBothShares()
    {
        var corpus = FakeAnalyzedFilingReadCorpus.Of(
            Directional("a", direction: "Positive"),
            Directional("b", direction: "Positive"),
            Directional("c", direction: "Negative"),
            Directional("d", direction: "Sideways"),
            NoSignal("e", FilingNoSignalCause.Mixed));

        var report = await Reporter(corpus).BuildAsync(CancellationToken.None);

        Assert.Equal(5, report.AllRecords.TotalReads);
        Assert.Equal(2, report.AllRecords.PositiveCount);
        Assert.Equal(1, report.AllRecords.NegativeCount);
        Assert.Equal(1, report.AllRecords.OtherDirectionTokenCount);
        Assert.Equal(1, report.AllRecords.NoDirectionalCount);
        // The unexpected token is NAMED, not pooled into an "other" bucket with no identity.
        Assert.Equal("Sideways", Assert.Single(report.AllRecords.OtherDirectionTokens).Name);
        Assert.Equal(2.0 / 4.0, report.AllRecords.PositiveShareOfDirectional);
        Assert.Equal(2.0 / 5.0, report.AllRecords.PositiveShareOfAllReads);
    }

    [Fact]
    public async Task EveryNoSignalCause_IsNamedAndCountedIndividually_IncludingStructuralZeros()
    {
        var corpus = FakeAnalyzedFilingReadCorpus.Of(
            NoSignal("a", FilingNoSignalCause.Unknown),
            NoSignal("b", FilingNoSignalCause.Mixed),
            NoSignal("c", FilingNoSignalCause.Mixed),
            NoSignal("d", FilingNoSignalCause.BelowConfidence));

        var report = await Reporter(corpus).BuildAsync(CancellationToken.None);

        var causes = report.AllRecords.NoSignalCauses.ToDictionary(c => c.Name, c => c.Count);
        Assert.Equal(Enum.GetValues<FilingNoSignalCause>().Length, causes.Count);
        Assert.Equal(1, causes[nameof(FilingNoSignalCause.Unknown)]);
        Assert.Equal(2, causes[nameof(FilingNoSignalCause.Mixed)]);
        Assert.Equal(1, causes[nameof(FilingNoSignalCause.BelowConfidence)]);
        // EmptyBody is never persisted, but it is part of the vocabulary: a measured zero, not a missing row.
        Assert.Equal(0, causes[nameof(FilingNoSignalCause.EmptyBody)]);
    }

    [Fact]
    public async Task ANullNoSignalCause_IsCountedAsNotRecorded_NeverAsACause()
    {
        var corpus = FakeAnalyzedFilingReadCorpus.Of(
            NoSignal("a", cause: null),
            NoSignal("b", cause: null),
            NoSignal("c", FilingNoSignalCause.Unknown));

        var report = await Reporter(corpus).BuildAsync(CancellationToken.None);

        Assert.Equal(2, report.AllRecords.NoSignalCauseNotRecordedCount);
        // The two not-recorded rows must NOT have been folded into Unknown (the enum's zero value).
        var unknown = report.AllRecords.NoSignalCauses
            .Single(c => c.Name == nameof(FilingNoSignalCause.Unknown));
        Assert.Equal(1, unknown.Count);
        Assert.Equal(2, report.Rows.Count(r => !r.NoSignalCauseRecorded));
    }

    [Fact]
    public async Task CacheVersionSplit_IsCounted_AndBothDenominatorsAreReported()
    {
        var stale = AnalyzedFilingRecord.CurrentCacheVersion - 1;
        var corpus = FakeAnalyzedFilingReadCorpus.Of(
            Directional("a", cacheVersion: stale),
            Directional("b", cacheVersion: stale),
            Directional("c", cacheVersion: AnalyzedFilingRecord.CurrentCacheVersion));

        var report = await Reporter(corpus).BuildAsync(CancellationToken.None);

        // PRIMARY denominator: every parseable record, stale ones included (the production cache still
        // replays a stale produced-signal record, so excluding them would describe reads Radar does not use).
        Assert.Equal(3, report.AllRecords.TotalReads);
        Assert.Equal(1, report.CurrentCacheVersionRecords.TotalReads);
        Assert.Contains("all parseable", report.AllRecords.Denominator, StringComparison.Ordinal);
        Assert.Contains("cacheVersion", report.CurrentCacheVersionRecords.Denominator, StringComparison.Ordinal);

        var versions = report.CacheVersions.ToDictionary(v => v.Name, v => v.Count);
        Assert.Equal(2, versions["cacheVersion=" + stale]);
        Assert.Equal(1, versions["cacheVersion=" + AnalyzedFilingRecord.CurrentCacheVersion]);
    }

    [Fact]
    public async Task CorpusExclusionCounts_RideOutOntoTheReport()
    {
        var corpus = new FakeAnalyzedFilingReadCorpus(new AnalyzedFilingCorpus(
            Entries: [new AnalyzedFilingCorpusEntry(Directional("a"), "a.json")],
            ModelSegment: "segment",
            CorpusDirectoryExists: true,
            EnumerationFailed: false,
            FilesScanned: 4,
            UnreadableOrUnparseableFiles: 1,
            OutsideCurrentModelSegmentFiles: 5,
            FileNameAccessionMismatchFiles: 1,
            OutcomeSignalMismatchFiles: 1));

        var report = await Reporter(corpus).BuildAsync(CancellationToken.None);

        Assert.Equal(FilingReadCorpusAvailability.Available, report.CorpusAvailability);
        Assert.Equal(4, report.FilesScanned);
        Assert.Equal(1, report.RecordsHydrated);
        Assert.Equal(1, report.UnreadableOrUnparseableFiles);
        Assert.Equal(5, report.OutsideCurrentModelSegmentFiles);
        Assert.Equal(1, report.FileNameAccessionMismatchFiles);
        Assert.Equal(1, report.OutcomeSignalMismatchFiles);
    }

    [Fact]
    public async Task ANullCorpusSeam_IsReportedAsSeamNotRegistered_NotAsZeroReads()
    {
        var report = await Reporter(corpus: null).BuildAsync(CancellationToken.None);

        Assert.Equal(FilingReadCorpusAvailability.SeamNotRegistered, report.CorpusAvailability);
        Assert.NotNull(report.CorpusUnavailableDetail);
        Assert.Contains("NOT a measured zero", report.CorpusUnavailableDetail!, StringComparison.Ordinal);
        // The outside-segment count is NOT RECORDED here, never a fabricated 0.
        Assert.Null(report.OutsideCurrentModelSegmentFiles);
    }

    [Fact]
    public async Task TheCappedConfidenceCount_IsNullWithAStatedReason_NeverAFabricatedZero()
    {
        var report = await Reporter(FakeAnalyzedFilingReadCorpus.Of(Directional("a")))
            .BuildAsync(CancellationToken.None);

        Assert.Null(report.CappedConfidenceRecordedCount);
        Assert.Contains("NOT RECORDED", report.CappedConfidenceNote, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ComparabilityScanState_DistinguishesNotScannedFromScannedClean()
    {
        var corpus = FakeAnalyzedFilingReadCorpus.Of(
            Directional("a"),
            Directional("b", markers: new ComparabilityMarkers([], [])),
            Directional("c", markers: new ComparabilityMarkers(["restated"], [])),
            Directional("d", markers: new ComparabilityMarkers([], ["adjusted"])));

        var report = await Reporter(corpus).BuildAsync(CancellationToken.None);

        var states = report.ComparabilityScanStates.ToDictionary(s => s.Name, s => s.Count);
        Assert.Equal(1, states[nameof(FilingReadComparabilityScanState.NotScanned)]);
        Assert.Equal(1, states[nameof(FilingReadComparabilityScanState.ScannedClean)]);
        Assert.Equal(1, states[nameof(FilingReadComparabilityScanState.CapTriggeringMarkersMatched)]);
        Assert.Equal(1, states[nameof(FilingReadComparabilityScanState.DiagnosticOnlyMarkersMatched)]);
    }

    [Fact]
    public async Task ConfidenceSummaries_NameTheFieldEachColumnCameFrom()
    {
        var corpus = FakeAnalyzedFilingReadCorpus.Of(
            Directional("a", confidence: 0.95m),
            Directional("b", confidence: 0.65m),
            NoSignal("c", FilingNoSignalCause.BelowConfidence, readConfidence: 0.4m),
            NoSignal("d", cause: null));

        var report = await Reporter(corpus).BuildAsync(CancellationToken.None);

        Assert.Equal("signal.confidence", report.DirectionalConfidence.SourceField);
        Assert.Equal(2, report.DirectionalConfidence.Count);
        Assert.Equal(0.65, report.DirectionalConfidence.Min);
        Assert.Equal(0.95, report.DirectionalConfidence.Max);

        Assert.Equal("record.readConfidence", report.NoSignalConfidence.SourceField);
        Assert.Equal(1, report.NoSignalConfidence.Count);
        // The pre-204 record carries no confidence: NOT RECORDED, counted separately from the measured one.
        Assert.Equal(1, report.NoSignalConfidence.NotRecordedCount);
    }

    [Fact]
    public async Task PerCompanyCounts_GiveUnresolvedAndUnjoinedReadsTheirOwnNamedRows()
    {
        var evidence = new List<EvidenceItem>
        {
            FilingEvidence("a", FilingDate),
            FilingEvidence("b", FilingDate, ticker: "ZZZZ"),
        };
        var corpus = FakeAnalyzedFilingReadCorpus.Of(
            Directional("a"),
            Directional("b"),
            Directional("c"));

        var report = await Reporter(corpus, evidence).BuildAsync(CancellationToken.None);

        var counts = report.PerCompanyReadCounts.ToDictionary(c => c.Name, c => c.Count);
        Assert.Equal(1, counts["POWL — Powell Industries"]);
        Assert.Equal(1, counts["(company no longer resolves)"]);
        Assert.Equal(1, counts["(no matching evidence record)"]);
        Assert.Equal(1, report.UnresolvedCompanyCount);
        Assert.Equal(1, report.NoMatchingEvidenceRecordCount);
    }

    // -----------------------------------------------------------------------------------------------
    // Section 2 — groundedness
    // -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task ExcerptEqualsTitle_IsCounted_AndTheTautologyIsVisible()
    {
        const string Title = "8-K (2026-08-03) [items: 2.02,8.01] Results of Operations";
        var evidence = new List<EvidenceItem>
        {
            FilingEvidence("a", FilingDate, title: Title),
            FilingEvidence("b", FilingDate, title: Title),
        };
        var corpus = FakeAnalyzedFilingReadCorpus.Of(
            Directional("a", excerpt: Title),
            Directional("b", excerpt: "something the source did not assign"));

        var report = await Reporter(corpus, evidence).BuildAsync(CancellationToken.None);

        Assert.Equal(2, report.Groundedness.MeasuredReads);
        Assert.Equal(1, report.Groundedness.ExcerptEqualsSomeEvidenceTitleCount);
        Assert.Equal(1, report.Groundedness.ExcerptMatchedNoEvidenceTitleCount);
        // A genuine mismatch is NAMED, not merely counted — a reader must be able to check it.
        Assert.Equal(["b"], report.Groundedness.ExcerptMismatchAccessions);
        Assert.Equal(0, report.Groundedness.ExcerptMismatchAccessionsOmitted);
    }

    [Fact]
    public async Task TwoEvidenceRecordsForOneAccession_AreBothCandidates_SoTheExcerptMatchesTheSET()
    {
        // The spec-218 review's blocking finding: an accession is NOT unique in the store. A collector change
        // left 71 accessions with a SHORT and a LONG title variant sharing one publication instant, and 12
        // directional reads land on such an accession. Collapsing to one record made "excerpt != title" a
        // GUID tie-break artefact (2, 5 or 8 apparent failures depending on the tie-break), not a property of
        // the read. This test FAILS under any arbitrary pick: the excerpt matches only the SECOND candidate.
        const string ShortTitle = "8-K — 8-K (2026-08-03) [items: 2.02,9.01]";
        const string LongTitle =
            "8-K (2026-08-03) [items: 2.02,9.01] Items: Results of Operations and Financial Condition.";

        // Ids are PINNED so the deterministic (published desc, id asc) order is not a coin flip: the SHORT
        // title sorts first, so any single-record pick deterministically chooses the record whose title the
        // excerpt does NOT equal, and the excerpt-equals-title assertion below fails.
        var evidence = new List<EvidenceItem>
        {
            FilingEvidence(
                "a", FilingDate, title: ShortTitle,
                id: new Guid("00000000-0000-0000-0000-000000000001")),
            FilingEvidence(
                "a", FilingDate, title: LongTitle,
                id: new Guid("00000000-0000-0000-0000-000000000002")),
        };
        var corpus = FakeAnalyzedFilingReadCorpus.Of(Directional("a", excerpt: LongTitle));

        var report = await Reporter(corpus, evidence).BuildAsync(CancellationToken.None);

        var row = Assert.Single(report.Rows);
        Assert.Equal(FilingReadEvidenceJoin.JoinedAmbiguous, row.EvidenceJoin);
        Assert.Equal(2, row.EvidenceCandidateCount);
        Assert.True(row.Groundedness.SupportingExcerptEqualsSomeEvidenceTitle);
        Assert.Equal(1, report.Groundedness.ExcerptEqualsSomeEvidenceTitleCount);
        Assert.Equal(0, report.Groundedness.ExcerptMatchedNoEvidenceTitleCount);
        // The ambiguity itself is a NAMED, counted state — it can never masquerade as a failed guard.
        Assert.Equal(1, report.AmbiguousEvidenceJoinCount);
        Assert.Equal(1, report.AccessionsWithMultipleEvidenceRecords);
        Assert.Equal(1, report.Groundedness.MeasuredReadsWithAmbiguousEvidenceJoin);
        // The rendered title is the candidate the read is CONSISTENT with, never an arbitrary sibling.
        Assert.Equal(LongTitle, row.EvidenceTitle);
    }

    [Fact]
    public async Task AnAmbiguousJoin_SearchesEveryCandidatesText_NotOneArbitraryWinner()
    {
        // The same collapse also supplied the rawText haystack. A figure present only in the SECOND
        // candidate's text must still be found.
        var evidence = new List<EvidenceItem>
        {
            FilingEvidence(
                "a", FilingDate, title: "short", rawText: "nothing numeric here",
                id: new Guid("00000000-0000-0000-0000-000000000001")),
            FilingEvidence(
                "a", FilingDate, title: "long", rawText: "revenue rose 40 percent",
                id: new Guid("00000000-0000-0000-0000-000000000002")),
        };
        var corpus = FakeAnalyzedFilingReadCorpus.Of(
            Directional("a", excerpt: "long", reason: "Revenue rose 40% year over year."));

        var report = await Reporter(corpus, evidence).BuildAsync(CancellationToken.None);

        var row = Assert.Single(report.Rows);
        Assert.Equal(1, row.Groundedness.ReasonNumericTokensFoundInEvidenceText);
        Assert.True(row.Groundedness.AnyReasonNumericTokenFoundInEvidenceText);
    }

    [Fact]
    public async Task AFigureMatchingTheMetadataHeader_IsCountedAsCOINCIDENTAL_NeverAsGrounding()
    {
        // The spec-218 review's second blocking finding: 79 of 238 live "grounded" figures were the filing
        // YEAR, the form-type digit of `8-K`, or an accession fragment, all sitting in the structural header
        // that a filing evidence record's rawText IS. A Reason saying "revenue up 8%" "matched" because the
        // FORM TYPE is 8-K. The split makes that visible instead of rendering it as a grounding rate.
        var evidence = new List<EvidenceItem>
        {
            FilingEvidence(
                "a",
                FilingDate,
                title: "8-K (2026-08-03) [items: 2.02,8.01] Results of Operations",
                rawText: "8-K filing accession 0000080420-26-000103 filed 2026-08-03: 8-K. "
                    + "8-K item codes: 2.02,8.01,9.01."),
        };
        var corpus = FakeAnalyzedFilingReadCorpus.Of(
            Directional("a", reason: "Revenue up 8%, gross profit up 8%, guidance raised for 2026."));

        var report = await Reporter(corpus, evidence).BuildAsync(CancellationToken.None);

        var row = Assert.Single(report.Rows);
        // "8" is the form-type digit and "2026" is the filing year: both found, both COINCIDENTAL.
        Assert.Equal(2, row.Groundedness.ReasonNumericTokensFoundInEvidenceText);
        Assert.Equal(2, row.Groundedness.ReasonNumericTokensMatchedCoincidentally);
        Assert.Equal(0, row.Groundedness.ReasonNumericTokensMatchedGenuinely);
        Assert.All(row.Groundedness.MatchedNumericTokens, t => Assert.True(t.CoincidentalHeaderMatch));

        Assert.Equal(1, report.Groundedness.ReasonNumericTokenFoundInEvidenceTextCount);
        Assert.Equal(1, report.Groundedness.ReasonNumericTokenCoincidentalHeaderMatchCount);
        Assert.Equal(0, report.Groundedness.ReasonNumericTokenGenuineMatchCount);
        Assert.Equal(
            ["2026", "8"],
            report.Groundedness.CoincidentalTokenCounts.Select(t => t.Name).Order(StringComparer.Ordinal));
        Assert.Empty(report.Groundedness.GenuineTokenCounts);

        // The markdown must never call that a grounding rate, and must show the tokens.
        var markdown = new DirectionalFilingReadRenderer().RenderMarkdown(report);
        Assert.Contains(
            "reads with ≥1 COINCIDENTAL token: 1 of 1", markdown, StringComparison.Ordinal);
        Assert.Contains("reads with ≥1 GENUINE token: 0 of 1", markdown, StringComparison.Ordinal);
        Assert.Contains("Coincidental header-match tokens", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("A near-zero count here", markdown, StringComparison.Ordinal);
        // The two classes are NOT a partition and the artifact must say so: one read carrying several
        // figures can hold both a coincidental and a genuine token.
        Assert.Contains("NOT a partition", markdown, StringComparison.Ordinal);
        // A genuine match must not be readable as "this read was grounded".
        Assert.Contains("A GENUINE match is NOT a grounded figure", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFigureAbsentFromTheMetadataHeader_IsCountedAsGENUINE()
    {
        // The positive control: without it the coincidental classifier could mark everything coincidental
        // and still pass. The summary text lives only in the stored text, never in the metadata envelope.
        var evidence = new List<EvidenceItem>
        {
            FilingEvidence(
                "a",
                FilingDate,
                rawText: "8-K filing accession 0000080420-26-000103 filed 2026-08-03",
                summary: "Backlog reached 1234 units."),
        };
        var corpus = FakeAnalyzedFilingReadCorpus.Of(
            Directional("a", reason: "Backlog reached 1234 units."));

        var report = await Reporter(corpus, evidence).BuildAsync(CancellationToken.None);

        var row = Assert.Single(report.Rows);
        Assert.Equal(1, row.Groundedness.ReasonNumericTokensFoundInEvidenceText);
        Assert.Equal(0, row.Groundedness.ReasonNumericTokensMatchedCoincidentally);
        Assert.Equal(1, row.Groundedness.ReasonNumericTokensMatchedGenuinely);
        Assert.Equal(1, report.Groundedness.ReasonNumericTokenGenuineMatchCount);
        Assert.Equal("1234", Assert.Single(report.Groundedness.GenuineTokenCounts).Name);
    }

    [Fact]
    public async Task ReasonFigures_AreCounted_AndAreNotFoundInAnEvidenceBodyThatIsNotStored()
    {
        var evidence = new List<EvidenceItem>
        {
            // The real shape: rawText holds accession/form/items, never the filing body.
            FilingEvidence("a", FilingDate, rawText: "accession 0001-26-000001 form 8-K items 2.02,8.01"),
            FilingEvidence("b", FilingDate, rawText: "revenue rose 38 percent"),
            FilingEvidence("c", FilingDate),
        };
        var corpus = FakeAnalyzedFilingReadCorpus.Of(
            Directional("a", reason: "Revenue rose 40% year over year."),
            Directional("b", reason: "Revenue rose 38% year over year."),
            Directional("c", reason: "Management tone improved materially."));

        var report = await Reporter(corpus, evidence).BuildAsync(CancellationToken.None);

        Assert.Equal(3, report.Groundedness.MeasuredReads);
        Assert.Equal(2, report.Groundedness.ReasonContainsNumericTokenCount);
        // Only the one whose figure genuinely appears in the stored text counts as found.
        Assert.Equal(1, report.Groundedness.ReasonNumericTokenFoundInEvidenceTextCount);
        Assert.Equal("rawText + title + summary", report.Groundedness.EvidenceFieldsSearched);

        // The read that asserted NO figure records the follow-up question as not applicable — a null, not a
        // measured false ("nothing to look for" is not "we looked and found nothing").
        var noFigure = report.Rows.Single(r => r.Accession == "c");
        Assert.False(noFigure.Groundedness.ReasonContainsNumericToken);
        Assert.Null(noFigure.Groundedness.ReasonNumericTokensFoundInEvidenceText);
        Assert.Null(noFigure.Groundedness.AnyReasonNumericTokenFoundInEvidenceText);
    }

    [Fact]
    public async Task AReadWithNoJoinedEvidence_IsANamedGroundednessExclusion_NotAFailure()
    {
        var corpus = FakeAnalyzedFilingReadCorpus.Of(Directional("a"));

        var report = await Reporter(corpus).BuildAsync(CancellationToken.None);

        Assert.Equal(0, report.Groundedness.MeasuredReads);
        Assert.Equal(1, report.Groundedness.NotApplicableReads);
        Assert.Equal(
            "no-matching-evidence-record",
            Assert.Single(report.Groundedness.NotApplicableReasons).Name);
        // Not a measured false: the questions were never asked.
        Assert.Null(Assert.Single(report.Rows).Groundedness.SupportingExcerptEqualsSomeEvidenceTitle);
    }

    [Theory]
    [InlineData("Trajectory rose 38→60 (+22)", new[] { "38", "60", "22" })]
    [InlineData("Revenue of $1,234.5 million", new[] { "1,234.5" })]
    [InlineData("No figures here", new string[0])]
    public void ExtractNumericTokens_TakesMaximalFigures_Deduplicated(string reason, string[] expected) =>
        Assert.Equal(expected, DirectionalFilingReadReporter.ExtractNumericTokens(reason));

    [Theory]
    [InlineData("revenue rose 40 percent", "40", true)]
    [InlineData("order backlog of 1409 units", "40", false)]
    [InlineData("margin of 40.2 percent", "40", false)]
    public void ContainsNumericToken_MatchesWholeFiguresOnly(
        string haystack, string token, bool expected) =>
        Assert.Equal(expected, DirectionalFilingReadReporter.ContainsNumericToken(haystack, token));

    // -----------------------------------------------------------------------------------------------
    // Section 3 — disagreement
    // -----------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(-3, true)]
    [InlineData(3, true)]
    [InlineData(-4, false)]
    [InlineData(4, false)]
    public async Task TheNewsWindow_IsInclusiveAtBothEdges_AndExcludesTheDayBeyond(
        int offsetDays, bool expectedInWindow)
    {
        var observationId = Guid.NewGuid();
        var published = FilingDate.AddDays(offsetDays);
        var report = await Reporter(
                FakeAnalyzedFilingReadCorpus.Of(Directional("a")),
                [FilingEvidence("a", FilingDate)],
                typings:
                [
                    Typing(
                        observationId,
                        CompanyId,
                        new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero),
                        "Shares fell after the release."),
                ],
                observations:
                [
                    Observation(
                        observationId,
                        new DateTimeOffset(published.Year, published.Month, published.Day, 9, 0, 0, TimeSpan.Zero)),
                ])
            .BuildAsync(CancellationToken.None);

        var row = Assert.Single(report.Rows);
        if (expectedInWindow)
        {
            Assert.Equal(FilingReadNewsArm.Evaluated, row.Disagreement.NewsArm);
            Assert.Equal(1, row.Disagreement.TypedFactsInWindow);
            Assert.True(row.Disagreement.DisagreesWithPositiveRead);
        }
        else
        {
            Assert.Equal(FilingReadNewsArm.NoTypedFactsInWindow, row.Disagreement.NewsArm);
            // An unevaluated window is NEVER scored as agreement.
            Assert.Null(row.Disagreement.DisagreesWithPositiveRead);
        }
    }

    [Fact]
    public async Task AReadBeforeTypingBegan_IsNamedPreTypingEra_NotScoredAsAgreeing()
    {
        var observationId = Guid.NewGuid();
        var report = await Reporter(
                FakeAnalyzedFilingReadCorpus.Of(Directional("a")),
                [FilingEvidence("a", new DateOnly(2026, 6, 1))],
                typings:
                [
                    Typing(
                        observationId,
                        CompanyId,
                        new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero),
                        "Shares fell."),
                ],
                observations:
                [
                    Observation(observationId, new DateTimeOffset(2026, 8, 1, 9, 0, 0, TimeSpan.Zero)),
                ])
            .BuildAsync(CancellationToken.None);

        var row = Assert.Single(report.Rows);
        Assert.Equal(FilingReadNewsArm.NoTypingCoverageInWindowPreTypingEra, row.Disagreement.NewsArm);
        Assert.Null(row.Disagreement.DisagreesWithPositiveRead);
        Assert.Equal(new DateOnly(2026, 8, 1), report.Disagreement.FirstTypingRecordDateUtc);
    }

    [Fact]
    public async Task MoreThanFiveMatchedStatements_CountsTheRemainder_NeverSilentlyTruncating()
    {
        var observationId = Guid.NewGuid();
        var statements = Enumerable.Range(1, 8)
            .Select(i => "Shares fell " + i + " percent on the day.")
            .ToArray();

        var report = await Reporter(
                FakeAnalyzedFilingReadCorpus.Of(Directional("a")),
                [FilingEvidence("a", FilingDate)],
                typings:
                [
                    Typing(
                        observationId,
                        CompanyId,
                        new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero),
                        statements),
                ],
                observations:
                [
                    Observation(observationId, new DateTimeOffset(2026, 8, 3, 9, 0, 0, TimeSpan.Zero)),
                ])
            .BuildAsync(CancellationToken.None);

        var row = Assert.Single(report.Rows);
        Assert.Equal(8, row.Disagreement.NegativeMatchCount);
        Assert.Equal(DirectionalFilingReadReporter.MaxMatchedStatementsPerRead,
            row.Disagreement.MatchedStatements.Count);
        Assert.Equal(3, row.Disagreement.MatchedStatementsOmitted);
        Assert.Equal(3, report.Disagreement.TotalMatchedStatementsOmitted);
    }

    [Fact]
    public async Task WhenTheNewsStoresAreNotRegistered_TheArmIsNamed_NotScoredAsAgreeing()
    {
        var report = await Reporter(
                FakeAnalyzedFilingReadCorpus.Of(Directional("a")),
                [FilingEvidence("a", FilingDate)],
                registerNewsStores: false)
            .BuildAsync(CancellationToken.None);

        var row = Assert.Single(report.Rows);
        Assert.Equal(FilingReadNewsArm.NewsStoresNotRegistered, row.Disagreement.NewsArm);
        Assert.Null(row.Disagreement.DisagreesWithPositiveRead);
        Assert.Equal(
            1,
            report.Disagreement.NewsArmExclusions
                .Single(e => e.Name == nameof(FilingReadNewsArm.NewsStoresNotRegistered)).Count);
    }

    [Fact]
    public async Task TypingsThatCannotJoinTheirObservation_AreCountedByNamedReason()
    {
        var missingObservation = Guid.NewGuid();
        var nullPublished = Guid.NewGuid();
        var report = await Reporter(
                FakeAnalyzedFilingReadCorpus.Of(Directional("a")),
                [FilingEvidence("a", FilingDate)],
                typings:
                [
                    Typing(missingObservation, CompanyId, new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero), "x"),
                    Typing(nullPublished, CompanyId, new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero), "y"),
                    Typing(Guid.NewGuid(), companyId: null, new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero), "z"),
                ],
                observations: [Observation(nullPublished, publishedAt: null)])
            .BuildAsync(CancellationToken.None);

        Assert.Equal(3, report.Disagreement.TypingRecordsScanned);
        Assert.Equal(1, report.Disagreement.TypingsWithNoCompanyId);
        Assert.Equal(1, report.Disagreement.TypingsWithNoArchivedObservation);
        Assert.Equal(1, report.Disagreement.TypingsWithNoPublishedAt);
    }

    [Fact]
    public async Task ATypingWithZeroFacts_IsCounted_NotSilentlySkipped()
    {
        // The spec-218 review's third blocking finding: `if (typing.Facts.Count == 0) continue;` was an
        // uncounted drop — 607 of 5,500 live typings vanished from an accounting that printed "scanned 5,500"
        // beside three named exclusions. The five terms must now reconcile EXACTLY to the scanned count.
        var withFacts = Guid.NewGuid();
        var withoutFacts = Guid.NewGuid();
        var created = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);
        var published = new DateTimeOffset(2026, 8, 3, 9, 0, 0, TimeSpan.Zero);

        var report = await Reporter(
                FakeAnalyzedFilingReadCorpus.Of(Directional("a")),
                [FilingEvidence("a", FilingDate)],
                typings:
                [
                    Typing(withFacts, CompanyId, created, "Shares fell after the release."),
                    Typing(withoutFacts, CompanyId, created),
                ],
                observations: [Observation(withFacts, published), Observation(withoutFacts, published)])
            .BuildAsync(CancellationToken.None);

        var d = report.Disagreement;
        Assert.Equal(2, d.TypingRecordsScanned);
        Assert.Equal(1, d.TypingsContributingFacts);
        Assert.Equal(1, d.TypingsWithNoFacts);
        Assert.Equal(
            d.TypingRecordsScanned,
            d.TypingsContributingFacts
                + d.TypingsWithNoFacts
                + d.TypingsWithNoCompanyId
                + d.TypingsWithNoArchivedObservation
                + d.TypingsWithNoPublishedAt);

        // And it is RENDERED, not merely held: an invisible count is the same defect one layer up.
        var markdown = new DirectionalFilingReadRenderer().RenderMarkdown(report);
        Assert.Contains("typings that produced NO fact", markdown, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("reconciliation: 1 contributing + 1 no-facts", markdown, StringComparison.Ordinal);
    }

    [Theory]
    // Bars exist, but none falls inside the forward window.
    [InlineData(0, FilingReadForwardReturnState.NoForwardBar)]
    // A single bar just after the filing date: entry and exit would be the same bar.
    [InlineData(1, FilingReadForwardReturnState.SingleForwardBar)]
    // Two bars, but the last falls short of the horizon by more than the tolerance.
    [InlineData(2, FilingReadForwardReturnState.PartialWindow)]
    // A full window whose entry bar has a non-positive price.
    [InlineData(3, FilingReadForwardReturnState.NonPositiveEntryPrice)]
    // A bar close enough to the horizon end: a full window.
    [InlineData(4, FilingReadForwardReturnState.Computed)]
    public async Task EveryForwardReturnOutcome_MapsToItsOwnNamedCountedState(
        int shape, FilingReadForwardReturnState expected)
    {
        PriceBar[] bars = shape switch
        {
            0 => [Bar(FilingDate.AddDays(-5), 100m)],
            1 => [Bar(FilingDate.AddDays(1), 100m)],
            2 => [Bar(FilingDate.AddDays(1), 100m), Bar(FilingDate.AddDays(4), 110m)],
            3 => [Bar(FilingDate.AddDays(1), 0m), Bar(FilingDate.AddDays(20), 110m)],
            _ => [Bar(FilingDate.AddDays(1), 100m), Bar(FilingDate.AddDays(20), 110m)],
        };

        var prices = new FakeFilingReadPriceStore().With("POWL", bars);
        var report = await Reporter(
                FakeAnalyzedFilingReadCorpus.Of(Directional("a")),
                [FilingEvidence("a", FilingDate)],
                prices: prices)
            .BuildAsync(CancellationToken.None);

        var row = Assert.Single(report.Rows);
        Assert.Equal(expected, row.Disagreement.ForwardReturnState);
        Assert.Equal(
            1,
            report.Disagreement.ForwardReturnStates.Single(s => s.Name == expected.ToString()).Count);
    }

    [Fact]
    public async Task NoPriceHistoryAndNoTicker_AreDistinctNamedForwardReturnStates()
    {
        var noHistory = await Reporter(
                FakeAnalyzedFilingReadCorpus.Of(Directional("a")),
                [FilingEvidence("a", FilingDate)])
            .BuildAsync(CancellationToken.None);
        Assert.Equal(
            FilingReadForwardReturnState.NoPriceHistory,
            Assert.Single(noHistory.Rows).Disagreement.ForwardReturnState);

        var tickerless = Company(ticker: null!, name: "Powell Industries");
        var noTicker = await Reporter(
                FakeAnalyzedFilingReadCorpus.Of(Directional("a")),
                [FilingEvidence("a", FilingDate, ticker: "Powell Industries")],
                companies: [tickerless])
            .BuildAsync(CancellationToken.None);
        Assert.Equal(
            FilingReadForwardReturnState.NoTicker,
            Assert.Single(noTicker.Rows).Disagreement.ForwardReturnState);
    }

    [Fact]
    public async Task AReadWithNoFilingDate_IsNamedNotRecorded_OnBothArms()
    {
        var corpus = FakeAnalyzedFilingReadCorpus.Of(Directional("a"));

        var report = await Reporter(corpus).BuildAsync(CancellationToken.None);

        var row = Assert.Single(report.Rows);
        Assert.Equal(FilingReadFilingDateSource.NotRecorded, row.FilingDateSource);
        Assert.Null(row.FilingDate);
        Assert.Equal(FilingReadForwardReturnState.FilingDateNotRecorded, row.Disagreement.ForwardReturnState);
        Assert.Equal(1, report.FilingDateNotRecordedCount);
    }

    [Fact]
    public async Task TheFilingDate_FallsBackToTheCacheObservationInstant_WithTheSourceNamed()
    {
        var observed = new DateTimeOffset(2026, 8, 3, 21, 0, 0, TimeSpan.Zero);
        var corpus = FakeAnalyzedFilingReadCorpus.Of(Directional("a", observedAt: observed));

        var report = await Reporter(corpus).BuildAsync(CancellationToken.None);

        var row = Assert.Single(report.Rows);
        Assert.Equal(FilingReadFilingDateSource.CacheObservedAt, row.FilingDateSource);
        Assert.Equal(FilingDate, row.FilingDate);
    }

    // -----------------------------------------------------------------------------------------------
    // Section 4 — the worked example
    // -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheWorkedExample_ResolvesFromLiveData_WithItsSameWindowNewsLines()
    {
        var observationId = Guid.NewGuid();
        var report = await Reporter(
                FakeAnalyzedFilingReadCorpus.Of(Directional("a")),
                [FilingEvidence("a", DirectionalFilingReadReporter.WorkedExampleFilingDate)],
                typings:
                [
                    Typing(
                        observationId,
                        CompanyId,
                        new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero),
                        "POWL is down 9.0% after a record data centre deal and an earnings miss."),
                ],
                observations:
                [
                    Observation(observationId, new DateTimeOffset(2026, 8, 4, 9, 0, 0, TimeSpan.Zero)),
                ])
            .BuildAsync(CancellationToken.None);

        Assert.True(report.WorkedExample.Resolved);
        Assert.Null(report.WorkedExample.UnresolvedReason);
        Assert.Equal("a", report.WorkedExample.Row!.Accession);
        var line = Assert.Single(report.WorkedExample.SameWindowNews);
        Assert.True(line.MatchedAdverseVocabulary);
        Assert.Equal(0, report.WorkedExample.SameWindowNewsOmitted);
    }

    [Fact]
    public async Task TheWorkedExample_SaysWhyItCouldNotBeResolved_RatherThanFabricatingARow()
    {
        var report = await Reporter(FakeAnalyzedFilingReadCorpus.Of(Directional("a")))
            .BuildAsync(CancellationToken.None);

        Assert.False(report.WorkedExample.Resolved);
        Assert.Null(report.WorkedExample.Row);
        Assert.NotNull(report.WorkedExample.UnresolvedReason);
        Assert.Contains(
            DirectionalFilingReadReporter.WorkedExampleTicker,
            report.WorkedExample.UnresolvedReason!,
            StringComparison.Ordinal);
    }

    // -----------------------------------------------------------------------------------------------
    // Rendering
    // -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheMarkdown_StatesTheHeadlineShareWithItsDenominator_AndRendersTheVocabularyVerbatim()
    {
        var corpus = FakeAnalyzedFilingReadCorpus.Of(
            Directional("a"), Directional("b"), Directional("c", direction: "Negative"));

        var report = await Reporter(corpus).BuildAsync(CancellationToken.None);
        var markdown = new DirectionalFilingReadRenderer().RenderMarkdown(report);

        Assert.Contains("**Positive share of directional reads: 66.7%**", markdown, StringComparison.Ordinal);
        Assert.Contains("2 Positive of 3 directional reads", markdown, StringComparison.Ordinal);
        Assert.Contains("DESCRIPTIVE", markdown, StringComparison.Ordinal);
        Assert.Contains("CALENDAR days", markdown, StringComparison.Ordinal);
        foreach (var phrase in FilingReadDisagreementVocabulary.NegativePhrases)
        {
            Assert.Contains("`" + phrase.Phrase + "`", markdown, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task TheRenderers_AreDeterministic_AndNeverRenderANullAsZero()
    {
        var report = await Reporter(FakeAnalyzedFilingReadCorpus.Of(Directional("a")))
            .BuildAsync(CancellationToken.None);
        var renderer = new DirectionalFilingReadRenderer();

        Assert.Equal(renderer.RenderMarkdown(report), renderer.RenderMarkdown(report));
        Assert.Equal(renderer.RenderCsv(report), renderer.RenderCsv(report));
        Assert.Equal(renderer.RenderJson(report), renderer.RenderJson(report));

        var csv = renderer.RenderCsv(report);
        Assert.Contains("not-recorded", csv, StringComparison.Ordinal);
        Assert.Contains("\"cappedConfidenceRecordedCount\": null", renderer.RenderJson(report),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheCsvHeaderAndEveryRow_StayInLockstep_AndCarryTheCoincidentalGenuineSplit()
    {
        // The split must travel with the raw found-in-text number in EVERY format: a CSV-only reader must
        // never meet the unsplit count standing alone. Header and rows are asserted together, because a
        // column added to one and not the other silently shifts every value after it.
        var evidence = new List<EvidenceItem> { FilingEvidence("a", FilingDate) };
        var corpus = FakeAnalyzedFilingReadCorpus.Of(
            Directional("a", reason: "Revenue up 8% and 1234 units."),
            NoSignal("b", FilingNoSignalCause.Mixed));

        var report = await Reporter(corpus, evidence).BuildAsync(CancellationToken.None);
        var csv = new DirectionalFilingReadRenderer().RenderCsv(report);
        var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        var header = lines[0].Split(',');
        Assert.Equal(ExpectedCsvColumnCount, header.Length);
        Assert.Contains("reasonNumericTokensFoundInEvidenceText", header);
        Assert.Contains("reasonNumericTokensCoincidental", header);
        Assert.Contains("reasonNumericTokensGenuine", header);

        Assert.Equal(report.Rows.Count, lines.Length - 1);
        foreach (var line in lines.Skip(1))
        {
            Assert.Equal(ExpectedCsvColumnCount, CountCsvFields(line));
        }

        // The directional row carries the split; the no-signal row carries "not recorded" for all three —
        // never a fabricated 0.
        var directionalRow = lines[1].Split(',');
        var coincidental = Array.IndexOf(header, "reasonNumericTokensCoincidental");
        var genuine = Array.IndexOf(header, "reasonNumericTokensGenuine");
        Assert.Equal("1", directionalRow[coincidental]);
        Assert.Equal("0", directionalRow[genuine]);
        Assert.Equal("not recorded", lines[2].Split(',')[coincidental]);
        Assert.Equal("not recorded", lines[2].Split(',')[genuine]);
    }

    /// <summary>The CSV's column count — 35 before the spec-218 review added the coincidental/genuine split.</summary>
    private const int ExpectedCsvColumnCount = 37;

    /// <summary>Counts CSV fields honouring the shared quoting rule, so an escaped comma is not a column.</summary>
    private static int CountCsvFields(string line)
    {
        var fields = 1;
        var inQuotes = false;
        foreach (var c in line)
        {
            if (c == '"')
            {
                inQuotes = !inQuotes;
            }
            else if (c == ',' && !inQuotes)
            {
                fields++;
            }
        }

        return fields;
    }

    private static PriceBar Bar(DateOnly date, decimal close) =>
        new(date, Open: close, High: close, Low: close, Close: close, AdjClose: close, Volume: 1000);
}
