using System.Globalization;
using System.Text;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Radar.Application.Abstractions.Persistence;
using Radar.Application.Efficacy.FilingReads;
using Radar.Application.EntityResolution;
using Radar.Application.Filings;
using Radar.Infrastructure.DependencyInjection;
using Radar.Infrastructure.FileSystem;
using Radar.Infrastructure.Filings;
using Radar.Infrastructure.News;
using Radar.Infrastructure.NewsTyping;

using Xunit.Abstractions;

namespace Radar.IntegrationTests;

/// <summary>
/// Spec 218 §7 — the READ-ONLY LIVE measurement of what the AI directional filing read actually produces,
/// over an accrued Radar data root (CLAUDE.md's "no measure ships without its live distribution"). It runs the
/// SHIPPED <see cref="DirectionalFilingReadReporter"/> over the PRODUCTION file stores — the production
/// <see cref="FileAnalyzedFilingCache"/> (root + model segment), <see cref="FileRawEvidenceStore"/>,
/// <see cref="FileNewsTypingStore"/>, <see cref="FileNewsObservationArchive"/>,
/// <see cref="FilePriceHistoryStore"/> and the production company seed + resolver — so what it prints is what
/// the shipped code computes, never a second implementation of the measurement.
/// <para>
/// <b>Nothing is written.</b> Every store is opened for hydration only; the artifact store is deliberately
/// NEVER constructed or invoked and no efficacy directory is created. The reporter itself is read-only by
/// construction (it holds no write seam).
/// </para>
/// <para>
/// ENV-GATED and skipped with a NAMED reason otherwise (the spec-198/214/215 precedent): set
/// <c>RADAR_DIRECTIONAL_FILING_READ_LIVE_DATA_ROOT</c> to a Radar data root holding <c>filings-cache/</c>,
/// <c>evidence/raw/</c>, <c>news-typing/</c>, <c>news-observations/</c>, <c>prices/</c> and
/// <c>companies.json</c>. The optional <c>RADAR_DIRECTIONAL_FILING_READ_MODEL_SEGMENT</c> pins which
/// analyzed-filing model segment to read as CURRENT; when it is unset the segment is auto-detected and the
/// choice (and why it was made) is stated in the output. The output is markdown on the test log, ready to
/// paste into a PR body. Deterministic: fixed ordering, invariant formatting, no clock, no randomness.
/// </para>
/// </summary>
public sealed class DirectionalFilingReadLiveMeasurementTests(ITestOutputHelper output)
{
    internal const string DataRootVariable = "RADAR_DIRECTIONAL_FILING_READ_LIVE_DATA_ROOT";

    /// <summary>Optional: pins the analyzed-filing model segment to read as CURRENT (spec 118 layout).</summary>
    internal const string ModelSegmentVariable = "RADAR_DIRECTIONAL_FILING_READ_MODEL_SEGMENT";

    internal const string SkipReason =
        "Spec 218 §7 live directional-filing-read measurement (reads a live Radar data root): set "
            + DataRootVariable + " to a Radar data root (filings-cache/, evidence/raw/, news-typing/, "
            + "news-observations/, prices/, companies.json) to run it. Optionally set "
            + ModelSegmentVariable + " to pin the analyzed-filing model segment.";

    internal static string? DataRoot()
    {
        var root = Environment.GetEnvironmentVariable(DataRootVariable);
        return !string.IsNullOrWhiteSpace(root) && Directory.Exists(root) ? root : null;
    }

    [DirectionalFilingReadLiveFact]
    public async Task LiveDistribution_OfTheDirectionalFilingRead_Groundedness_AndDisagreement()
    {
        var root = DataRoot()!;
        var ct = CancellationToken.None;

        var originalCulture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        try
        {
            var cacheRoot = Path.Combine(root, "filings-cache");
            var (segment, segmentChoice) = ResolveModelSegment(cacheRoot);

            // ---- the PRODUCTION stores, hydration only ------------------------------------------------
            var corpus = new FileAnalyzedFilingCache(
                new FileAnalyzedFilingCacheOptions { RootDirectory = cacheRoot, ModelSegment = segment },
                NullLogger<FileAnalyzedFilingCache>.Instance);
            var evidence = new FileRawEvidenceStore(
                new FileRawEvidenceStoreOptions
                {
                    RootDirectory = Path.Combine(root, "evidence", "raw"),
                },
                NullLogger<FileRawEvidenceStore>.Instance);
            var typings = new FileNewsTypingStore(
                new FileNewsTypingStoreOptions { RootDirectory = Path.Combine(root, "news-typing") },
                NullLogger<FileNewsTypingStore>.Instance);
            var observations = new FileNewsObservationArchive(
                new NewsObservationArchiveOptions
                {
                    RootDirectory = Path.Combine(root, "news-observations"),
                },
                NullLogger<FileNewsObservationArchive>.Instance);
            var prices = new FilePriceHistoryStore(
                new FilePriceHistoryStoreOptions { RootDirectory = Path.Combine(root, "prices") },
                NullLogger<FilePriceHistoryStore>.Instance);

            // The company universe through the PRODUCTION seed source + resolver, so the company join here
            // resolves exactly as it does in a live run.
            var services = new ServiceCollection();
            services.AddLogging(b => b.SetMinimumLevel(LogLevel.None));
            services.AddInMemoryRadarPersistence();
            services.AddLocalFileCompanySeed(Path.Combine(root, "companies.json"));
            services.AddSingleton<ICompanyResolver, CompanyResolver>();
            await using var provider = services.BuildServiceProvider();
            var seeded = await provider.GetRequiredService<ICompanyUniverseSeeder>().SeedAsync(ct);

            // ---- the SHIPPED reporter ------------------------------------------------------------------
            var reporter = new DirectionalFilingReadReporter(
                evidence,
                provider.GetRequiredService<ICompanyRepository>(),
                provider.GetRequiredService<ICompanyResolver>(),
                prices,
                NullLogger<DirectionalFilingReadReporter>.Instance,
                corpus,
                typings,
                observations);

            var report = await reporter.BuildAsync(ct);

            output.WriteLine(RenderSpec7(report, root, segment, segmentChoice, seeded));
            output.WriteLine(string.Empty);
            output.WriteLine("<!-- ================ the SHIPPED artifact markdown, verbatim ================ -->");
            output.WriteLine(string.Empty);
            output.WriteLine(new DirectionalFilingReadRenderer().RenderMarkdown(report));

            AssertTheMeasurementReconciles(report);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    /// <summary>
    /// The assertions that make this a TEST rather than a print: the corpus was actually reachable, and every
    /// counted bucket reconciles to its stated denominator. If any of these fails, a read was dropped without
    /// being counted — the exact defect the slice exists to make impossible.
    /// </summary>
    private static void AssertTheMeasurementReconciles(DirectionalFilingReadReport report)
    {
        Assert.True(
            report.CorpusAvailability == FilingReadCorpusAvailability.Available,
            "the analyzed-filing read corpus was not reachable: " + report.CorpusAvailability + " — "
                + (report.CorpusUnavailableDetail ?? "(no detail)"));
        Assert.True(report.RecordsHydrated > 0, "no read records were hydrated — check the data root");

        // 1. Every scanned file became a record or a NAMED exclusion. Nothing is lost between the two.
        Assert.Equal(
            report.FilesScanned,
            report.RecordsHydrated
                + report.UnreadableOrUnparseableFiles
                + report.FileNameAccessionMismatchFiles
                + report.OutcomeSignalMismatchFiles);

        // 2. The rows ARE the hydrated records, and the primary denominator is exactly that count.
        Assert.Equal(report.RecordsHydrated, report.Rows.Count);
        Assert.Equal(report.RecordsHydrated, report.AllRecords.TotalReads);

        // 3. The direction classes partition the denominator.
        var all = report.AllRecords;
        Assert.Equal(
            all.TotalReads,
            all.PositiveCount + all.NegativeCount + all.OtherDirectionTokenCount + all.NoDirectionalCount);
        Assert.Equal(all.OtherDirectionTokenCount, all.OtherDirectionTokens.Sum(t => t.Count));

        // 4. Every no-directional read carries a NAMED cause or is counted as NOT RECORDED — never neither.
        Assert.Equal(
            all.NoDirectionalCount,
            all.NoSignalCauses.Sum(c => c.Count) + all.NoSignalCauseNotRecordedCount);

        // 5. The cacheVersion split and the per-company counts each cover the whole denominator.
        Assert.Equal(all.TotalReads, report.CacheVersions.Sum(v => v.Count));
        Assert.Equal(all.TotalReads, report.PerCompanyReadCounts.Sum(c => c.Count));
        Assert.Equal(all.TotalReads, report.ComparabilityScanStates.Sum(s => s.Count));
        Assert.True(
            report.CurrentCacheVersionRecords.TotalReads <= all.TotalReads,
            "the current-version subset cannot exceed the whole corpus");

        // 6. Both confidence populations account for every row they cover.
        Assert.Equal(
            all.PositiveCount + all.NegativeCount + all.OtherDirectionTokenCount,
            report.DirectionalConfidence.Count + report.DirectionalConfidence.NotRecordedCount);
        Assert.Equal(
            all.NoDirectionalCount,
            report.NoSignalConfidence.Count + report.NoSignalConfidence.NotRecordedCount);

        // 7. Groundedness: every directional read is either measured or a NAMED not-applicable.
        var g = report.Groundedness;
        Assert.Equal(all.PositiveCount + all.NegativeCount + all.OtherDirectionTokenCount, g.DirectionalReads);
        Assert.Equal(g.DirectionalReads, g.MeasuredReads + g.NotApplicableReads);
        Assert.Equal(g.NotApplicableReads, g.NotApplicableReasons.Sum(r => r.Count));
        Assert.True(
            g.ReasonNumericTokenFoundInEvidenceTextCount <= g.ReasonContainsNumericTokenCount,
            "a figure cannot be found in the evidence text more often than it was asserted");

        // 8. Disagreement: every directional read's news arm either ran or is a NAMED, counted exclusion,
        //    and the forward-return states partition the same denominator.
        var d = report.Disagreement;
        Assert.Equal(g.DirectionalReads, d.DirectionalReads);
        Assert.Equal(d.DirectionalReads, d.NewsArmEvaluatedReads + d.NewsArmExclusions.Sum(e => e.Count));
        Assert.Equal(d.DirectionalReads, d.ForwardReturnStates.Sum(s => s.Count));
        // 8b. The typing accounting reconciles EXACTLY: contributing + the four named exclusions == scanned.
        //     (A no-facts typing was an uncounted `continue` until the spec-218 review; 607 of 5,500 live
        //     records vanished from an accounting that looked complete.)
        Assert.Equal(
            d.TypingRecordsScanned,
            d.TypingsContributingFacts
                + d.TypingsWithNoFacts
                + d.TypingsWithNoCompanyId
                + d.TypingsWithNoArchivedObservation
                + d.TypingsWithNoPublishedAt);

        // 8c. Groundedness sub-counts: an excerpt either matched some candidate title or is a NAMED
        //     mismatch, and every named mismatch is either rendered or counted as omitted.
        Assert.Equal(
            g.MeasuredReads, g.ExcerptEqualsSomeEvidenceTitleCount + g.ExcerptMatchedNoEvidenceTitleCount);
        Assert.Equal(
            g.ExcerptMatchedNoEvidenceTitleCount,
            g.ExcerptMismatchAccessions.Count + g.ExcerptMismatchAccessionsOmitted);
        Assert.True(
            g.ReasonNumericTokenCoincidentalHeaderMatchCount
                + g.ReasonNumericTokenGenuineMatchCount >= g.ReasonNumericTokenFoundInEvidenceTextCount,
            "every read with a found figure must appear in the coincidental and/or the genuine count");

        Assert.True(
            d.PositiveReadsDisagreeing <= d.PositiveReadsWithNewsArm,
            "a Positive read cannot disagree unless its news arm was evaluated");
        Assert.Equal(
            all.TotalReads, d.ForwardReturnByDirection.Sum(x => x.ReadsInClass));
        Assert.All(
            d.ForwardReturnByDirection,
            x => Assert.True(x.Count <= x.ReadsInClass, "resolved windows cannot exceed the reads in class"));
    }

    /// <summary>
    /// Which model segment to read as CURRENT. Explicit env override wins; otherwise auto-detect (no
    /// sub-directory ⇒ the root layout; exactly one ⇒ that one; more than one ⇒ the alphabetically first,
    /// NAMED as ambiguous in the output so a reader knows a choice was made). The choice is always printed —
    /// it decides which files count as "outside the current model segment".
    /// </summary>
    private static (string Segment, string Choice) ResolveModelSegment(string cacheRoot)
    {
        var pinned = Environment.GetEnvironmentVariable(ModelSegmentVariable);
        if (!string.IsNullOrWhiteSpace(pinned))
        {
            return (pinned.Trim(), "pinned by " + ModelSegmentVariable);
        }

        if (!Directory.Exists(cacheRoot))
        {
            return (string.Empty, "the filings-cache directory does not exist");
        }

        var segments = Directory.EnumerateDirectories(cacheRoot)
            .Select(Path.GetFileName)
            .Where(s => !string.IsNullOrEmpty(s))
            .Select(s => s!)
            .Order(StringComparer.Ordinal)
            .ToList();

        return segments.Count switch
        {
            0 => (string.Empty, "auto-detected: no model sub-directory exists (pre-spec-118 root layout)"),
            1 => (segments[0], "auto-detected: the only model sub-directory under filings-cache/"),
            _ => (
                segments[0],
                "auto-detected AMBIGUOUS: " + segments.Count.ToString(CultureInfo.InvariantCulture)
                    + " model sub-directories exist (" + string.Join(", ", segments)
                    + "); the alphabetically first was read as CURRENT and the rest are NOT counted as "
                    + "outside-segment files (only files at the cache ROOT are) — set "
                    + ModelSegmentVariable + " to pin the one this run should describe"),
        };
    }

    /// <summary>
    /// The spec-218 §7 block, verbatim in the order §7 lists it, formatted for a PR body. It reads FIELDS off
    /// the shipped report — it recomputes nothing.
    /// </summary>
    private static string RenderSpec7(
        DirectionalFilingReadReport report,
        string root,
        string segment,
        string segmentChoice,
        int seededCompanies)
    {
        var all = report.AllRecords;
        var current = report.CurrentCacheVersionRecords;
        var g = report.Groundedness;
        var d = report.Disagreement;
        var directional = all.PositiveCount + all.NegativeCount + all.OtherDirectionTokenCount;

        var sb = new StringBuilder();
        sb.AppendLine("## Spec 218 §7 — live verification (directional filing read)");
        sb.AppendLine();
        sb.AppendLine(string.Create(
            CultureInfo.InvariantCulture,
            $"Data root: `{root}` · model segment: "
                + $"{(segment.Length == 0 ? "(none — cache root)" : "`" + segment + "`")} ({segmentChoice}) · "
                + $"seeded companies: {seededCompanies} · corpus availability: `{report.CorpusAvailability}`"));
        sb.AppendLine();
        sb.AppendLine(
            "Read-only: no artifact was written and no efficacy directory was created by this harness. "
                + "Everything below is produced by the SHIPPED `DirectionalFilingReadReporter`.");
        sb.AppendLine();

        // 1. total reads + direction counts + share WITH its denominator.
        sb.AppendLine("### 1. Direction (`" + report.DistributionVersion + "`)");
        sb.AppendLine();
        sb.AppendLine(string.Create(
            CultureInfo.InvariantCulture,
            $"- Total accrued read records (the denominator): **{all.TotalReads}** "
                + $"— from {report.FilesScanned} scanned file(s)"));
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"- `Positive`: {all.PositiveCount}"));
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"- `Negative`: {all.NegativeCount}"));
        sb.AppendLine(string.Create(
            CultureInfo.InvariantCulture,
            $"- other direction token: {all.OtherDirectionTokenCount}"
                + $"{TokenList(all.OtherDirectionTokens)}"));
        sb.AppendLine(string.Create(
            CultureInfo.InvariantCulture, $"- no directional signal: {all.NoDirectionalCount}"));
        sb.AppendLine(string.Create(
            CultureInfo.InvariantCulture,
            $"- **Positive share: {Share(all.PositiveShareOfDirectional)} of {directional} DIRECTIONAL "
                + $"reads** ({Share(all.PositiveShareOfAllReads)} of all {all.TotalReads} read records)"));
        sb.AppendLine(string.Create(
            CultureInfo.InvariantCulture,
            $"- window covered (filing dates): {DateCell(report.WindowStartUtc)} → "
                + $"{DateCell(report.WindowEndUtc)}"));
        sb.AppendLine();
        sb.AppendLine("No-directional cause, every enum member named and counted:");
        sb.AppendLine();
        foreach (var cause in all.NoSignalCauses)
        {
            sb.AppendLine(string.Create(
                CultureInfo.InvariantCulture,
                $"- `{cause.Name}`: {cause.Count} of {all.NoDirectionalCount}"));
        }

        sb.AppendLine(string.Create(
            CultureInfo.InvariantCulture,
            $"- cause NOT RECORDED (pre-204 record): {all.NoSignalCauseNotRecordedCount} of "
                + $"{all.NoDirectionalCount}"));
        sb.AppendLine();
        sb.AppendLine("Named, counted exclusions:");
        sb.AppendLine();
        sb.AppendLine(string.Create(
            CultureInfo.InvariantCulture,
            $"- unreadable / unparseable files: {report.UnreadableOrUnparseableFiles}"));
        sb.AppendLine(string.Create(
            CultureInfo.InvariantCulture,
            $"- `.json` at the cache ROOT outside the current model segment: "
                + $"{IntCell(report.OutsideCurrentModelSegmentFiles)}"));
        sb.AppendLine(string.Create(
            CultureInfo.InvariantCulture,
            $"- filename/accession mismatch: {report.FileNameAccessionMismatchFiles}"));
        sb.AppendLine(string.Create(
            CultureInfo.InvariantCulture,
            $"- outcome/signal mismatch: {report.OutcomeSignalMismatchFiles}"));
        sb.AppendLine(string.Create(
            CultureInfo.InvariantCulture,
            $"- no matching evidence record: {report.NoMatchingEvidenceRecordCount} of {all.TotalReads}"));
        sb.AppendLine(string.Create(
            CultureInfo.InvariantCulture,
            $"- company no longer resolves: {report.UnresolvedCompanyCount} of {all.TotalReads}"));
        sb.AppendLine(string.Create(
            CultureInfo.InvariantCulture,
            $"- filing date not recorded: {report.FilingDateNotRecordedCount} of {all.TotalReads}"));
        sb.AppendLine();

        // 2. cacheVersion split with BOTH denominators.
        sb.AppendLine("### 2. Cache-version split (both denominators)");
        sb.AppendLine();
        foreach (var version in report.CacheVersions)
        {
            sb.AppendLine(string.Create(
                CultureInfo.InvariantCulture,
                $"- {version.Name}: {version.Count} of {all.TotalReads}"));
        }

        sb.AppendLine(string.Create(
            CultureInfo.InvariantCulture,
            $"- current cache version: {report.CurrentCacheVersion}"));
        sb.AppendLine();
        sb.AppendLine(string.Create(
            CultureInfo.InvariantCulture,
            $"| denominator | reads | Positive | Negative | other token | no directional | Positive share of directional |"));
        sb.AppendLine("| --- | ---: | ---: | ---: | ---: | ---: | ---: |");
        foreach (var summary in new[] { all, current })
        {
            sb.AppendLine(string.Create(
                CultureInfo.InvariantCulture,
                $"| {summary.Denominator} | {summary.TotalReads} | {summary.PositiveCount} | "
                    + $"{summary.NegativeCount} | {summary.OtherDirectionTokenCount} | "
                    + $"{summary.NoDirectionalCount} | {Share(summary.PositiveShareOfDirectional)} |"));
        }

        sb.AppendLine();

        // 3. confidence quantiles + the capped count.
        sb.AppendLine("### 3. Confidence");
        sb.AppendLine();
        foreach (var summary in new[] { report.DirectionalConfidence, report.NoSignalConfidence })
        {
            sb.AppendLine(string.Create(
                CultureInfo.InvariantCulture,
                $"- {summary.Population} (from `{summary.SourceField}`): n={summary.Count}, "
                    + $"NOT RECORDED={summary.NotRecordedCount}; min {Num(summary.Min)} / p25 "
                    + $"{Num(summary.P25)} / median {Num(summary.Median)} / p75 {Num(summary.P75)} / max "
                    + $"{Num(summary.Max)}"));
            if (summary.Histogram.Count > 0)
            {
                sb.AppendLine("  - histogram: " + string.Join(
                    ", ",
                    summary.Histogram.Select(h => string.Create(
                        CultureInfo.InvariantCulture, $"{h.Value:0.####} ×{h.Count}"))));
            }
        }

        sb.AppendLine(string.Create(
            CultureInfo.InvariantCulture,
            $"- capped-confidence count: {IntCell(report.CappedConfidenceRecordedCount)} — "
                + $"{report.CappedConfidenceNote}"));
        sb.AppendLine("- comparability-scan state (the measured proxy): " + string.Join(
            ", ",
            report.ComparabilityScanStates.Select(s => string.Create(
                CultureInfo.InvariantCulture, $"`{s.Name}` {s.Count}"))));
        sb.AppendLine();

        // 4. groundedness.
        sb.AppendLine("### 4. Groundedness (`" + report.GroundednessVersion + "`)");
        sb.AppendLine();
        sb.AppendLine(string.Create(
            CultureInfo.InvariantCulture,
            $"- directional reads: {g.DirectionalReads}; measured (with a joined evidence record): "
                + $"{g.MeasuredReads}; NOT measurable: {g.NotApplicableReads}"
                + $"{TokenList(g.NotApplicableReasons)}"));
        sb.AppendLine(string.Create(
            CultureInfo.InvariantCulture,
            $"- `SupportingExcerpt` == the `Title` of AT LEAST ONE evidence record carrying the accession "
                + $"(ordinal): **{g.ExcerptEqualsSomeEvidenceTitleCount} of {g.MeasuredReads}**; matched NO "
                + $"candidate title: {g.ExcerptMatchedNoEvidenceTitleCount}"
                + $"{(g.ExcerptMismatchAccessions.Count == 0 ? string.Empty : " (" + string.Join(", ", g.ExcerptMismatchAccessions.Select(a => "`" + a + "`")) + ")")}"));
        sb.AppendLine(string.Create(
            CultureInfo.InvariantCulture,
            $"  - of the measured reads, {g.MeasuredReadsWithAmbiguousEvidenceJoin} have an AMBIGUOUS "
                + $"evidence join (>1 evidence record carries the accession, with differing titles). The "
                + $"comparison is made against the candidate SET, so an ambiguous join can never masquerade "
                + $"as a failed guard."));
        sb.AppendLine(string.Create(
            CultureInfo.InvariantCulture,
            $"- `Reason` contains a numeric token: **{g.ReasonContainsNumericTokenCount} of "
                + $"{g.MeasuredReads}**"));
        sb.AppendLine(string.Create(
            CultureInfo.InvariantCulture,
            $"- ...and that figure appears in the evidence record's stored text ({g.EvidenceFieldsSearched}): "
                + $"**{g.ReasonNumericTokenFoundInEvidenceTextCount} of "
                + $"{g.ReasonContainsNumericTokenCount}** — of which:"));
        sb.AppendLine(string.Create(
            CultureInfo.InvariantCulture,
            $"  - **reads with ≥1 COINCIDENTAL token: "
                + $"{g.ReasonNumericTokenCoincidentalHeaderMatchCount}** — the token also occurs in "
                + $"{g.StructuralHeaderDescription}, i.e. it is a year, a form-type digit or an accession "
                + $"fragment, NOT evidence of grounding"));
        sb.AppendLine(string.Create(
            CultureInfo.InvariantCulture,
            $"  - **reads with ≥1 GENUINE token: {g.ReasonNumericTokenGenuineMatchCount}** — the token "
                + $"occurs in the stored text but NOWHERE in that header. NOT a partition with the line "
                + $"above: both count READS over the same denominator and one read can hold BOTH classes"));
        sb.AppendLine(string.Create(
            CultureInfo.InvariantCulture,
            $"  - coincidental tokens: {TokenList(g.CoincidentalTokenCounts)}"));
        sb.AppendLine(string.Create(
            CultureInfo.InvariantCulture,
            $"  - genuine tokens: {TokenList(g.GenuineTokenCounts)}"));
        sb.AppendLine(string.Create(
            CultureInfo.InvariantCulture,
            $"- MEASURED POSITION: a filing evidence record's `rawText` IS the structural header (accession, "
                + $"form type, filing date, item codes; ~113 chars), so the filing BODY is not stored and a "
                + $"`Reason`'s figures cannot be checked against it. The raw found-in-text count must NOT be "
                + $"read as a grounding rate — and neither may the GENUINE count: it means only that the "
                + $"token is absent from the metadata envelope, while the SEC-supplied document title inside "
                + $"`rawText` still supplies quarter labels (a `Reason` naming Q4 matches `Q4'2024`). The "
                + $"token lists above are the only auditable statement of what matched."));
        sb.AppendLine();

        // 5. disagreement, with the excluded-for-no-coverage count stated SEPARATELY.
        sb.AppendLine("### 5. Disagreement (`" + d.VocabularyVersion + "`)");
        sb.AppendLine();
        sb.AppendLine(string.Create(
            CultureInfo.InvariantCulture,
            $"- Positive reads with an EVALUATED news arm: {d.PositiveReadsWithNewsArm}"));
        sb.AppendLine(string.Create(
            CultureInfo.InvariantCulture,
            $"- **disagreement count: {d.PositiveReadsDisagreeing}; rate: "
                + $"{Share(d.DisagreementRateOverEvaluatedPositiveReads)} of "
                + $"{d.PositiveReadsWithNewsArm} evaluated Positive reads**"));
        sb.AppendLine(string.Create(
            CultureInfo.InvariantCulture,
            $"- EXCLUDED for no coverage, stated separately — news arm not evaluated for "
                + $"{d.DirectionalReads - d.NewsArmEvaluatedReads} of {d.DirectionalReads} directional "
                + $"reads, by named reason:"));
        foreach (var reason in d.NewsArmExclusions)
        {
            sb.AppendLine(string.Create(
                CultureInfo.InvariantCulture,
                $"  - `{reason.Name}`: {reason.Count} of {d.DirectionalReads}"));
        }

        sb.AppendLine(string.Create(
            CultureInfo.InvariantCulture,
            $"- matched statements omitted by the per-read cap of "
                + $"{DirectionalFilingReadReporter.MaxMatchedStatementsPerRead} (counted, never silently "
                + $"truncated): {d.TotalMatchedStatementsOmitted}"));
        sb.AppendLine(string.Create(
            CultureInfo.InvariantCulture,
            $"- typing records scanned: {d.TypingRecordsScanned}; excluded — no company id: "
                + $"{d.TypingsWithNoCompanyId}, no archived observation: "
                + $"{d.TypingsWithNoArchivedObservation}, no publication instant: "
                + $"{d.TypingsWithNoPublishedAt}, NO FACTS: {d.TypingsWithNoFacts}; contributing: "
                + $"{d.TypingsContributingFacts}; reconciles to "
                + $"{d.TypingsContributingFacts + d.TypingsWithNoFacts + d.TypingsWithNoCompanyId + d.TypingsWithNoArchivedObservation + d.TypingsWithNoPublishedAt}"
                + $" of {d.TypingRecordsScanned} scanned; first typing record: "
                + $"{DateCell(d.FirstTypingRecordDateUtc)}"));
        if (d.MatchedVocabularyTermCounts.Count > 0)
        {
            sb.AppendLine("- vocabulary terms that matched: " + string.Join(
                ", ",
                d.MatchedVocabularyTermCounts.Select(t => string.Create(
                    CultureInfo.InvariantCulture, $"`{t.Name}` ×{t.Count}"))));
        }

        sb.AppendLine();

        // 6. forward return by direction, labelled DESCRIPTIVE.
        sb.AppendLine(string.Create(
            CultureInfo.InvariantCulture,
            $"### 6. Forward return by read direction — DESCRIPTIVE ({report.ForwardHorizonDays} CALENDAR "
                + $"days from the filing date, exit tolerance {report.ForwardExitToleranceDays} days)"));
        sb.AppendLine();
        sb.AppendLine(
            "AD-14: price is validation-only and read strictly downstream of scoring. No gate, no threshold, "
                + "no promotion and no demotion follows from these numbers.");
        sb.AppendLine();
        sb.AppendLine("| Direction | Reads | Windows resolved | Min | P25 | Median | P75 | Max | Mean |");
        sb.AppendLine("| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |");
        foreach (var dist in d.ForwardReturnByDirection)
        {
            sb.AppendLine(string.Create(
                CultureInfo.InvariantCulture,
                $"| {dist.Direction} | {dist.ReadsInClass} | {dist.Count} | {Pct(dist.Min)} | "
                    + $"{Pct(dist.P25)} | {Pct(dist.Median)} | {Pct(dist.P75)} | {Pct(dist.Max)} | "
                    + $"{Pct(dist.Mean)} |"));
        }

        sb.AppendLine();
        sb.AppendLine("Forward-return outcome over directional reads, every named state counted:");
        sb.AppendLine();
        foreach (var state in d.ForwardReturnStates)
        {
            sb.AppendLine(string.Create(
                CultureInfo.InvariantCulture,
                $"- `{state.Name}`: {state.Count} of {d.DirectionalReads}"));
        }

        sb.AppendLine();

        // 7. the worked example.
        var w = report.WorkedExample;
        sb.AppendLine(string.Create(
            CultureInfo.InvariantCulture,
            $"### 7. Worked example — {w.Ticker}, filing {w.FilingDate:yyyy-MM-dd}, labelled "
                + $"{w.LabelDate:yyyy-MM-dd}"));
        sb.AppendLine();
        if (!w.Resolved || w.Row is null)
        {
            sb.AppendLine("**NOT RESOLVABLE on this corpus**: "
                + (w.UnresolvedReason ?? "reason not recorded") + ". Nothing is fabricated in its place.");
        }
        else
        {
            var row = w.Row;
            sb.AppendLine(string.Create(
                CultureInfo.InvariantCulture,
                $"- accession `{row.Accession}` (cacheVersion {row.CacheVersion}) · direction "
                    + $"`{row.DirectionClass}` · confidence "
                    + $"{(row.Confidence is { } c ? c.ToString("0.####", CultureInfo.InvariantCulture) : "not recorded")} "
                    + $"(from `{row.ConfidenceSource}`)"));
            sb.AppendLine("- evidence title: " + Cell(row.EvidenceTitle));
            sb.AppendLine("- reason: " + Cell(row.Reason));
            sb.AppendLine(string.Create(
                CultureInfo.InvariantCulture,
                $"- excerpt == some candidate title: "
                    + $"{BoolCell(row.Groundedness.SupportingExcerptEqualsSomeEvidenceTitle)} "
                    + $"(candidates {row.EvidenceCandidateCount}) · "
                    + $"news arm `{row.Disagreement.NewsArm}` · typed facts in window "
                    + $"{IntCell(row.Disagreement.TypedFactsInWindow)} · adverse matches "
                    + $"{IntCell(row.Disagreement.NegativeMatchCount)}"));
            sb.AppendLine(string.Create(
                CultureInfo.InvariantCulture,
                $"- forward return (DESCRIPTIVE, {report.ForwardHorizonDays} calendar days): "
                    + $"{Pct(row.Disagreement.ForwardReturn21d)} (`{row.Disagreement.ForwardReturnState}`)"));
            sb.AppendLine();
            if (w.SameWindowNews.Count == 0)
            {
                sb.AppendLine("(no typed news in the same window)");
            }
            else
            {
                sb.AppendLine("| Published | Event types | Adverse match | Statement |");
                sb.AppendLine("| --- | --- | --- | --- |");
                foreach (var line in w.SameWindowNews)
                {
                    sb.AppendLine(string.Create(
                        CultureInfo.InvariantCulture,
                        $"| {line.PublishedDateUtc:yyyy-MM-dd} | {Cell(line.EventTypes)} | "
                            + $"{(line.MatchedAdverseVocabulary ? "yes" : "no")} | {Cell(line.Statement)} |"));
                }

                if (w.SameWindowNewsOmitted > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine(string.Create(
                        CultureInfo.InvariantCulture,
                        $"{w.SameWindowNewsOmitted} further same-window line(s) omitted by the rendering "
                            + $"cap — counted, never silently dropped."));
                }
            }
        }

        sb.AppendLine();
        sb.AppendLine(
            "Fingerprint: this slice moves NO pin — verify against `ScoringConfigFingerprintTests` rather "
                + "than trusting this sentence.");

        return sb.ToString();
    }

    /// <summary>A parenthesised, comma-joined "`name` xN" list, or empty when nothing matched.</summary>
    private static string TokenList(IReadOnlyList<FilingReadCount> counts) => counts.Count == 0
        ? string.Empty
        : " (" + string.Join(
            ", ",
            counts.Select(c => string.Create(CultureInfo.InvariantCulture, $"`{c.Name}` ×{c.Count}"))) + ")";

    private static string Share(double? share) => share is { } v
        ? v.ToString("P1", CultureInfo.InvariantCulture)
        : "not defined (empty denominator)";

    private static string Pct(double? value) => value is { } v
        ? v.ToString("+0.00%;-0.00%", CultureInfo.InvariantCulture)
        : "not recorded";

    private static string Num(double? value) => value is { } v
        ? v.ToString("0.####", CultureInfo.InvariantCulture)
        : "not recorded";

    private static string IntCell(int? value) => value is { } v
        ? v.ToString(CultureInfo.InvariantCulture)
        : "not recorded";

    private static string BoolCell(bool? value) => value is { } v ? (v ? "true" : "false") : "not recorded";

    private static string DateCell(DateOnly? value) => value is { } v
        ? v.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
        : "not recorded";

    private static string Cell(string? value) => string.IsNullOrEmpty(value)
        ? "not recorded"
        : value.Replace("|", "\\|", StringComparison.Ordinal)
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal);
}

/// <summary>Skips the spec-218 §7 live measurement with a named reason unless its data-root variable points at a directory.</summary>
public sealed class DirectionalFilingReadLiveFactAttribute : FactAttribute
{
    public DirectionalFilingReadLiveFactAttribute()
    {
        if (DirectionalFilingReadLiveMeasurementTests.DataRoot() is null)
        {
            Skip = DirectionalFilingReadLiveMeasurementTests.SkipReason;
        }
    }
}
