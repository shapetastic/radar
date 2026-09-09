using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Radar.Application.Efficacy.FilingReads;

/// <summary>
/// Renders the spec-218 measurement as JSON, CSV and Markdown. Pure string production — it touches no disk
/// (<see cref="IDirectionalFilingReadArtifactStore"/> owns that, AD-5).
/// <para>
/// <b>Byte-identical re-runs.</b> No wall-clock timestamp, no machine path, no absolute directory, no
/// unordered collection, invariant culture and fixed precision everywhere — an artifact that churns cannot be
/// diffed, and a diff is how a change of meaning gets noticed.
/// </para>
/// <para>
/// <b>Language.</b> Descriptive only. No advice vocabulary (AD-9), no promotion, no gate, no threshold, and
/// every price number is labelled DESCRIPTIVE (AD-14). A count with no measurement renders as
/// <c>not recorded</c>, never as <c>0</c>.
/// </para>
/// </summary>
public sealed class DirectionalFilingReadRenderer
{
    private const string ShareFormat = "0.0%";
    private const string ReturnFormat = "+0.00%;-0.00%";
    private const string ConfidenceFormat = "0.####";

    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            // Enums by NAME: the state tokens ARE the contract a later reader consumes, and an integer
            // ordinal would silently re-map if a member were ever inserted.
            Converters = { new JsonStringEnumConverter(namingPolicy: null, allowIntegerValues: false) },
        };
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }

    /// <summary>The machine-readable source of truth: every row, every named count, every null.</summary>
    public string RenderJson(DirectionalFilingReadReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        return JsonSerializer.Serialize(report, JsonOptions);
    }

    /// <summary>One row per accrued read, with every named state as its own column.</summary>
    public string RenderCsv(DirectionalFilingReadReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var builder = new StringBuilder();
        builder.Append("accession,sourceFileName,cacheVersion,isCurrentCacheVersion,outcome,directionClass,")
            .Append("directionToken,noSignalCause,confidence,confidenceSource,comparabilityScan,")
            .Append("capTriggeringMarkers,diagnosticOnlyMarkers,evidenceJoin,companyResolution,ticker,")
            .Append("companyName,filingDate,filingDateSource,evidenceCandidateCount,")
            .Append("excerptEqualsSomeEvidenceTitle,")
            .Append("reasonContainsNumericToken,reasonNumericTokens,reasonNumericTokensFoundInEvidenceText,")
            // The split travels with the raw count in EVERY format: a CSV-only reader must never see the
            // unsplit "found in evidence text" number standing alone (spec-218 review note 3).
            .Append("reasonNumericTokensCoincidental,reasonNumericTokensGenuine,")
            .Append("groundednessNotApplicableReason,newsArm,typedFactsInWindow,negativeMatchCount,")
            .Append("matchedStatementsOmitted,matchedVocabularyTerms,disagreesWithPositiveRead,")
            .Append("forwardReturnState,forwardReturn21dCalendarDays,forwardEntryDate,forwardExitDate")
            .Append('\n');

        foreach (var row in report.Rows)
        {
            builder
                .Append(CsvField.Escape(row.Accession)).Append(',')
                .Append(CsvField.Escape(row.SourceFileName)).Append(',')
                .Append(row.CacheVersion.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(row.IsCurrentCacheVersion ? "true" : "false").Append(',')
                .Append(row.Outcome.ToString()).Append(',')
                .Append(row.DirectionClass.ToString()).Append(',')
                .Append(CsvField.Escape(row.DirectionToken)).Append(',')
                .Append(row.NoSignalCauseRecorded ? row.NoSignalCause!.Value.ToString() : "not-recorded")
                .Append(',')
                .Append(Decimal(row.Confidence)).Append(',')
                .Append(row.ConfidenceSource.ToString()).Append(',')
                .Append(row.ComparabilityScan.ToString()).Append(',')
                .Append(Int(row.CapTriggeringMarkerCount)).Append(',')
                .Append(Int(row.DiagnosticOnlyMarkerCount)).Append(',')
                .Append(row.EvidenceJoin.ToString()).Append(',')
                .Append(row.CompanyResolution.ToString()).Append(',')
                .Append(CsvField.Escape(row.Ticker)).Append(',')
                .Append(CsvField.Escape(row.CompanyName)).Append(',')
                .Append(Date(row.FilingDate)).Append(',')
                .Append(row.FilingDateSource.ToString()).Append(',')
                .Append(row.EvidenceCandidateCount.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(Bool(row.Groundedness.SupportingExcerptEqualsSomeEvidenceTitle)).Append(',')
                .Append(Bool(row.Groundedness.ReasonContainsNumericToken)).Append(',')
                .Append(CsvField.Escape(string.Join(" ", row.Groundedness.ReasonNumericTokens))).Append(',')
                .Append(Int(row.Groundedness.ReasonNumericTokensFoundInEvidenceText)).Append(',')
                .Append(Int(row.Groundedness.ReasonNumericTokensMatchedCoincidentally)).Append(',')
                .Append(Int(row.Groundedness.ReasonNumericTokensMatchedGenuinely)).Append(',')
                .Append(CsvField.Escape(row.Groundedness.NotApplicableReason)).Append(',')
                .Append(row.Disagreement.NewsArm.ToString()).Append(',')
                .Append(Int(row.Disagreement.TypedFactsInWindow)).Append(',')
                .Append(Int(row.Disagreement.NegativeMatchCount)).Append(',')
                .Append(row.Disagreement.MatchedStatementsOmitted.ToString(CultureInfo.InvariantCulture))
                .Append(',')
                .Append(CsvField.Escape(string.Join("|", row.Disagreement.MatchedVocabularyTerms)))
                .Append(',')
                .Append(Bool(row.Disagreement.DisagreesWithPositiveRead)).Append(',')
                .Append(row.Disagreement.ForwardReturnState.ToString()).Append(',')
                .Append(Double(row.Disagreement.ForwardReturn21d)).Append(',')
                .Append(Date(row.Disagreement.ForwardEntryDate)).Append(',')
                .Append(Date(row.Disagreement.ForwardExitDate))
                .Append('\n');
        }

        return builder.ToString();
    }

    /// <summary>The human-readable report: the headline share with its denominator, then every named count.</summary>
    public string RenderMarkdown(DirectionalFilingReadReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var sb = new StringBuilder();
        sb.AppendLine("# What the AI directional filing read actually produces");
        sb.AppendLine();
        sb.AppendLine(
            "Read-only measurement over the accrued analyzed-filing read corpus. It changes no score, "
                + "prompt, schema, weight, formula or strategy, declares no gate and no threshold, and moves "
                + "no scoring fingerprint. Price appears only as a DESCRIPTIVE forward return (AD-14, "
                + "validation-only): nothing is promoted or demoted from it.");
        sb.AppendLine();
        sb.AppendLine(
            "Artifacts: `" + report.DistributionVersion + "` (section 1), `" + report.GroundednessVersion
                + "` (section 2), `" + report.DisagreementVersion + "` (section 3).");
        sb.AppendLine();

        RenderCorpusAccounting(sb, report);
        RenderHeadline(sb, report);
        RenderDirectionSection(sb, "Direction — all parseable accrued records", report.AllRecords);
        RenderDirectionSection(
            sb,
            "Direction — current cache version only (the second denominator, stated separately)",
            report.CurrentCacheVersionRecords);
        RenderConfidence(sb, report);
        RenderPerCompany(sb, report);
        RenderGroundedness(sb, report);
        RenderDisagreement(sb, report);
        RenderWorkedExample(sb, report);
        RenderVocabulary(sb);

        return sb.ToString();
    }

    private static void RenderCorpusAccounting(StringBuilder sb, DirectionalFilingReadReport report)
    {
        sb.AppendLine("## Corpus accounting");
        sb.AppendLine();
        sb.AppendLine("- Corpus availability: `" + report.CorpusAvailability + "`");
        if (report.CorpusUnavailableDetail is { } detail)
        {
            sb.AppendLine("- " + detail);
        }

        // A null segment has TWO causes and they are not the same claim: none is configured, or the seam was
        // never reached to ask. Only the first is a measurement.
        sb.AppendLine("- Model segment: " + (report.ModelSegment is { } segment
            ? "`" + segment + "`"
            : report.CorpusAvailability == FilingReadCorpusAvailability.SeamNotRegistered
                ? "NOT READ (no corpus seam is registered, so the configured segment was never resolved)"
                : "none configured (records live at the cache root)"));
        sb.AppendLine(Line("Files scanned inside the current segment", report.FilesScanned));
        sb.AppendLine(Line("Records hydrated (the primary denominator)", report.RecordsHydrated));
        sb.AppendLine(Line("EXCLUDED — unreadable / unparseable files", report.UnreadableOrUnparseableFiles));
        sb.AppendLine(
            "- EXCLUDED — `.json` files at the cache ROOT outside the current model segment: "
                + (report.OutsideCurrentModelSegmentFiles is { } outside
                    ? outside.ToString(CultureInfo.InvariantCulture)
                    : "not recorded (the cache root could not be enumerated)"));
        sb.AppendLine(Line(
            "EXCLUDED — filename/accession mismatch", report.FileNameAccessionMismatchFiles));
        sb.AppendLine(Line("EXCLUDED — outcome/signal mismatch", report.OutcomeSignalMismatchFiles));
        sb.AppendLine(Line(
            "EXCLUDED from the evidence join — reads with no matching evidence record",
            report.NoMatchingEvidenceRecordCount));
        sb.AppendLine(Line(
            "EXCLUDED from the company join — reads whose company no longer resolves",
            report.UnresolvedCompanyCount));
        sb.AppendLine(Line(
            "Reads with no filing date recorded (no evidence publication instant, no cache observation instant)",
            report.FilingDateNotRecordedCount));
        // These two are EVIDENCE-STORE counts, not per-read ones: with the store unloaded they were never
        // taken, and a rendered 0 would read as "the store is clean".
        sb.AppendLine(NotComputedLine(
            report,
            "Filing evidence records carrying no `accessionNumber` metadata (never joinable)",
            report.FilingEvidenceWithoutAccessionMetadata));
        sb.AppendLine(NotComputedLine(
            report,
            "Accessions carrying MORE THAN ONE filing evidence record (every candidate is kept; the join is "
                + "reported as ambiguous rather than collapsed to an arbitrary winner)",
            report.AccessionsWithMultipleEvidenceRecords));
        sb.AppendLine(Line(
            "Reads whose evidence join is AMBIGUOUS (more than one candidate)",
            report.AmbiguousEvidenceJoinCount));
        sb.AppendLine();

        sb.AppendLine("### Cache-version split (both denominators are reported; nothing is dropped)");
        sb.AppendLine();
        sb.AppendLine(
            "The production cache's stale-version rule is OUTCOME-SCOPED — a v"
                + (report.CurrentCacheVersion - 1).ToString(CultureInfo.InvariantCulture)
                + " record that produced a directional signal is still a HIT and is still replayed into "
                + "scoring — so excluding below-current records would report a distribution that does not "
                + "describe the reads Radar is actually using. The primary distribution therefore covers ALL "
                + "parseable records, and the current-version-only distribution is reported beside it.");
        sb.AppendLine();
        foreach (var version in report.CacheVersions)
        {
            sb.AppendLine(Line(version.Name, version.Count));
        }

        sb.AppendLine(Line(
            "current cache version", report.CurrentCacheVersion));
        sb.AppendLine();

        sb.AppendLine("### Comparability-scan state (spec 160)");
        sb.AppendLine();
        sb.AppendLine("- Capped-confidence count: " + report.CappedConfidenceNote);
        sb.AppendLine();
        foreach (var state in report.ComparabilityScanStates)
        {
            sb.AppendLine(Line(state.Name, state.Count));
        }

        sb.AppendLine();
    }

    private static void RenderHeadline(StringBuilder sb, DirectionalFilingReadReport report)
    {
        var all = report.AllRecords;
        var directional = all.PositiveCount + all.NegativeCount + all.OtherDirectionTokenCount;
        sb.AppendLine("## Headline");
        sb.AppendLine();
        sb.AppendLine(string.Create(
            CultureInfo.InvariantCulture,
            $"**Positive share of directional reads: {Share(all.PositiveShareOfDirectional)}** "
                + $"({all.PositiveCount} Positive of {directional} directional reads), over "
                + $"{all.TotalReads} accrued read record(s) in total."));
        sb.AppendLine();
        sb.AppendLine(
            "Window covered (filing dates present in the corpus): "
                + (report.WindowStartUtc is { } start && report.WindowEndUtc is { } end
                    ? "`" + start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "` to `"
                        + end.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "`"
                    : "not recorded (no read in this corpus carries a filing date)"));
        sb.AppendLine();
        sb.AppendLine(
            "`Strength` and `Novelty` are deliberately NOT reported as findings: both are fixed options on "
                + "the signal source, not model output.");
        sb.AppendLine();
    }

    private static void RenderDirectionSection(
        StringBuilder sb, string title, FilingReadDirectionSummary summary)
    {
        sb.AppendLine("## " + title);
        sb.AppendLine();
        sb.AppendLine("Denominator: **" + summary.Denominator + "** — "
            + summary.TotalReads.ToString(CultureInfo.InvariantCulture) + " record(s).");
        sb.AppendLine();
        sb.AppendLine(Line("Positive", summary.PositiveCount));
        sb.AppendLine(Line("Negative", summary.NegativeCount));
        sb.AppendLine(Line("other direction token", summary.OtherDirectionTokenCount));
        foreach (var token in summary.OtherDirectionTokens)
        {
            sb.AppendLine("  - token `" + token.Name + "`: "
                + token.Count.ToString(CultureInfo.InvariantCulture));
        }

        sb.AppendLine(Line("no directional signal", summary.NoDirectionalCount));
        sb.AppendLine("- Positive share of directional reads: " + Share(summary.PositiveShareOfDirectional));
        sb.AppendLine("- Positive share of ALL reads: " + Share(summary.PositiveShareOfAllReads));
        sb.AppendLine();
        sb.AppendLine("No-directional cause, every enum member named and counted:");
        sb.AppendLine();
        foreach (var cause in summary.NoSignalCauses)
        {
            sb.AppendLine(Line(cause.Name, cause.Count));
        }

        sb.AppendLine(Line(
            "cause NOT RECORDED (a pre-204 record — this is not a cause, it is the absence of one)",
            summary.NoSignalCauseNotRecordedCount));
        sb.AppendLine();
    }

    private static void RenderConfidence(StringBuilder sb, DirectionalFilingReadReport report)
    {
        sb.AppendLine("## Confidence");
        sb.AppendLine();
        sb.AppendLine(
            "Min/p25/p75/max are NEAREST-RANK order statistics (`ExactQuantile`); the median is the repo's "
                + "one median definition (`ExactMedianInterval.MedianOf` — the mean of the two middle order "
                + "statistics at even n). The two conventions are stated because they differ at even n.");
        sb.AppendLine();
        RenderConfidenceSummary(sb, report.DirectionalConfidence);
        RenderConfidenceSummary(sb, report.NoSignalConfidence);
    }

    private static void RenderConfidenceSummary(StringBuilder sb, FilingReadConfidenceSummary summary)
    {
        sb.AppendLine("### " + summary.Population + " (read from `" + summary.SourceField + "`)");
        sb.AppendLine();
        sb.AppendLine(Line("values recorded", summary.Count));
        sb.AppendLine(Line("NOT RECORDED (no confidence on the record)", summary.NotRecordedCount));
        sb.AppendLine("- min / p25 / median / p75 / max: "
            + Confidence(summary.Min) + " / " + Confidence(summary.P25) + " / "
            + Confidence(summary.Median) + " / " + Confidence(summary.P75) + " / "
            + Confidence(summary.Max));
        sb.AppendLine();
        if (summary.Histogram.Count == 0)
        {
            sb.AppendLine("(no values)");
            sb.AppendLine();
            return;
        }

        sb.AppendLine("| Confidence | Reads |");
        sb.AppendLine("| --- | --- |");
        foreach (var bucket in summary.Histogram)
        {
            sb.AppendLine("| " + bucket.Value.ToString(ConfidenceFormat, CultureInfo.InvariantCulture)
                + " | " + bucket.Count.ToString(CultureInfo.InvariantCulture) + " |");
        }

        sb.AppendLine();
    }

    private static void RenderPerCompany(StringBuilder sb, DirectionalFilingReadReport report)
    {
        sb.AppendLine("## Reads per company");
        sb.AppendLine();
        sb.AppendLine(
            "So a heavy contributor is visible rather than pooled away. Unresolved companies and reads with "
                + "no evidence record are their own named rows, never merged into a resolved company.");
        sb.AppendLine();
        if (report.PerCompanyReadCounts.Count == 0)
        {
            sb.AppendLine("(no reads)");
            sb.AppendLine();
            return;
        }

        sb.AppendLine("| Company | Reads |");
        sb.AppendLine("| --- | --- |");
        foreach (var company in report.PerCompanyReadCounts)
        {
            sb.AppendLine("| " + EscapePipes(company.Name) + " | "
                + company.Count.ToString(CultureInfo.InvariantCulture) + " |");
        }

        sb.AppendLine();
    }

    private static void RenderGroundedness(StringBuilder sb, DirectionalFilingReadReport report)
    {
        var g = report.Groundedness;
        sb.AppendLine("## Section 2 — groundedness of the `Reason` (`" + report.GroundednessVersion + "`)");
        sb.AppendLine();
        sb.AppendLine(
            "Descriptive. This section asserts no defect: it converts a documented design choice into a "
                + "standing number.");
        sb.AppendLine();
        if (!report.JoinStoresLoaded)
        {
            sb.AppendLine(
                "**NOT COMPUTED.** No read record hydrated, so the evidence store was not loaded and no "
                    + "excerpt or figure was looked up. The zeros below are zero READS, not a measured "
                    + "groundedness rate, and the evidence-metadata key list is absent because it was never "
                    + "collected — not because the envelope carries no keys.");
            sb.AppendLine();
        }

        sb.AppendLine(Line("directional reads", g.DirectionalReads));
        sb.AppendLine(Line("measured (a directional read WITH a joined evidence record)", g.MeasuredReads));
        sb.AppendLine(Line("EXCLUDED — not measurable", g.NotApplicableReads));
        foreach (var reason in g.NotApplicableReasons)
        {
            sb.AppendLine("  - " + reason.Name + ": " + reason.Count.ToString(CultureInfo.InvariantCulture));
        }

        sb.AppendLine();
        sb.AppendLine(string.Create(
            CultureInfo.InvariantCulture,
            $"- `SupportingExcerpt` byte-equal (ordinal) to the `Title` of AT LEAST ONE evidence record "
                + $"carrying the accession: **{g.ExcerptEqualsSomeEvidenceTitleCount} of "
                + $"{g.MeasuredReads}**. The signal source assigns the evidence title as the excerpt, and "
                + $"the mapper then verifies excerpt-in-evidence — a check that assignment cannot fail. The "
                + $"count puts that tautology in an artifact rather than only in a code comment."));
        sb.AppendLine(string.Create(
            CultureInfo.InvariantCulture,
            $"- matched NO candidate title: **{g.ExcerptMatchedNoEvidenceTitleCount} of "
                + $"{g.MeasuredReads}**"
                + $"{(g.ExcerptMismatchAccessions.Count == 0 ? string.Empty : " — accessions: " + string.Join(", ", g.ExcerptMismatchAccessions.Select(a => "`" + a + "`")))}"
                + $"{(g.ExcerptMismatchAccessionsOmitted == 0 ? string.Empty : string.Create(CultureInfo.InvariantCulture, $" (+{g.ExcerptMismatchAccessionsOmitted} further, counted not dropped)"))}"));
        sb.AppendLine(string.Create(
            CultureInfo.InvariantCulture,
            $"- of the measured reads, {g.MeasuredReadsWithAmbiguousEvidenceJoin} have an AMBIGUOUS evidence "
                + $"join (more than one evidence record carries the accession). The comparison is made "
                + $"against the candidate SET for exactly this reason: an accession is not unique in the "
                + $"store — a collector change left many filings with a short and a long title variant "
                + $"sharing one publication instant — so comparing against a single arbitrarily-chosen "
                + $"record would report which record a GUID tie-break happened to pick, not whether the "
                + $"guard holds."));
        sb.AppendLine(string.Create(
            CultureInfo.InvariantCulture,
            $"- `Reason` contains at least one numeric token: **{g.ReasonContainsNumericTokenCount} of "
                + $"{g.MeasuredReads}** (a proxy for \"the model asserted a figure\")."));
        sb.AppendLine(string.Create(
            CultureInfo.InvariantCulture,
            $"- ...and that figure appears in the evidence record's stored text ({g.EvidenceFieldsSearched}): "
                + $"**{g.ReasonNumericTokenFoundInEvidenceTextCount} of "
                + $"{g.ReasonContainsNumericTokenCount}** — of which:"));
        sb.AppendLine(string.Create(
            CultureInfo.InvariantCulture,
            $"  - **reads with ≥1 COINCIDENTAL token: {g.ReasonNumericTokenCoincidentalHeaderMatchCount} of "
                + $"{g.ReasonNumericTokenFoundInEvidenceTextCount}** — the token also occurs in "
                + $"{g.StructuralHeaderDescription}, so it is a year, a form-type digit or an accession "
                + $"fragment and is NOT evidence of grounding."));
        sb.AppendLine(string.Create(
            CultureInfo.InvariantCulture,
            $"  - **reads with ≥1 GENUINE token: {g.ReasonNumericTokenGenuineMatchCount} of "
                + $"{g.ReasonNumericTokenFoundInEvidenceTextCount}** — the token occurs in the stored text "
                + $"but NOWHERE in that header."));
        sb.AppendLine();
        sb.AppendLine(
            "These two are NOT a partition and need not sum to the line above them: they count READS holding "
                + "at least one token of each class over the same denominator, and one read whose `Reason` "
                + "carries several figures can hold BOTH. Use the token tables below to see what actually "
                + "matched.");
        sb.AppendLine();
        sb.AppendLine(
            "MEASURED POSITION, stated rather than left implied: a filing evidence record's `rawText` IS the "
                + "structural header — the accession, form type, filing date, item codes and the SEC-supplied "
                + "document title, roughly a hundred characters — so the filing BODY the model read is not on "
                + "disk and the figures in the `Reason` cannot be checked against it. Read the COINCIDENTAL "
                + "line above as the measurement of that: a `Reason` saying \"revenue up 8%\" \"matches\" "
                + "because the FORM TYPE is `8-K`, and a year matches because the filing date is in the "
                + "header. A GENUINE match is NOT a grounded figure either — it means only that the token is "
                + "absent from the metadata envelope, and the document title inside `rawText` still supplies "
                + "quarter labels (a `Reason` naming \"Q4\" matches `Q4'2024` in the title). NOTHING in this "
                + "section may be read as a grounding rate; the token tables below are the only auditable "
                + "statement of what matched. A figure matches only as a WHOLE figure (neither neighbour a "
                + "digit or a numeric separator), so \"40\" does not match inside \"1409\" or \"40.2\".");
        sb.AppendLine();
        RenderTokenHistogram(sb, "Coincidental header-match tokens", g.CoincidentalTokenCounts);
        RenderTokenHistogram(sb, "Genuine match tokens", g.GenuineTokenCounts);
        if (g.EvidenceMetadataKeysObserved.Count > 0)
        {
            sb.AppendLine("Evidence metadata keys observed (the header region searched): "
                + string.Join(", ", g.EvidenceMetadataKeysObserved.Select(k => "`" + k + "`")));
            sb.AppendLine();
        }
    }

    /// <summary>The matched-token histogram: the counts are what let a reader check the split, not trust it.</summary>
    private static void RenderTokenHistogram(
        StringBuilder sb, string title, IReadOnlyList<FilingReadCount> tokens)
    {
        sb.AppendLine("**" + title + "**");
        sb.AppendLine();
        if (tokens.Count == 0)
        {
            sb.AppendLine("(none)");
            sb.AppendLine();
            return;
        }

        sb.AppendLine("| Token | Matches |");
        sb.AppendLine("| --- | ---: |");
        foreach (var token in tokens)
        {
            sb.AppendLine("| `" + token.Name + "` | "
                + token.Count.ToString(CultureInfo.InvariantCulture) + " |");
        }

        sb.AppendLine();
    }

    private static void RenderDisagreement(StringBuilder sb, DirectionalFilingReadReport report)
    {
        var d = report.Disagreement;
        sb.AppendLine("## Section 3 — disagreement with the rest of the record (`" + d.VocabularyVersion
            + "`)");
        sb.AppendLine();
        sb.AppendLine(Line("directional reads", d.DirectionalReads));
        sb.AppendLine(Line("news arm EVALUATED (typed facts existed in the ±"
            + DirectionalFilingReadReporter.NewsWindowDays.ToString(CultureInfo.InvariantCulture)
            + "-day window)", d.NewsArmEvaluatedReads));
        sb.AppendLine("- news arm EXCLUDED, by named reason:");
        foreach (var reason in d.NewsArmExclusions)
        {
            sb.AppendLine("  - " + reason.Name + ": " + reason.Count.ToString(CultureInfo.InvariantCulture));
        }

        sb.AppendLine(Line("Positive reads WITH an evaluated news arm", d.PositiveReadsWithNewsArm));
        sb.AppendLine(Line("Positive reads disagreeing (≥1 adverse typed fact in window)",
            d.PositiveReadsDisagreeing));
        sb.AppendLine("- disagreement rate over evaluated Positive reads: "
            + Share(d.DisagreementRateOverEvaluatedPositiveReads));
        sb.AppendLine(Line(
            "matched statements omitted by the per-read cap of "
                + DirectionalFilingReadReporter.MaxMatchedStatementsPerRead.ToString(
                    CultureInfo.InvariantCulture)
                + " (counted, never silently truncated)",
            d.TotalMatchedStatementsOmitted));
        sb.AppendLine();
        sb.AppendLine(
            "A read whose window could not be evaluated is EXCLUDED and counted above — never scored as "
                + "agreeing. Typing began in 2026-08, so a pre-August read has no news arm at all and says "
                + "so per read (`NoTypingCoverageInWindowPreTypingEra`).");
        sb.AppendLine();
        if (report.JoinStoresLoaded)
        {
            sb.AppendLine(Line("typing records scanned", d.TypingRecordsScanned));
            sb.AppendLine(Line("CONTRIBUTING at least one typed fact", d.TypingsContributingFacts));
            sb.AppendLine(Line(
                "EXCLUDED — typings that produced NO fact (InsufficientContent, or all facts dropped in "
                    + "validation)",
                d.TypingsWithNoFacts));
            sb.AppendLine(Line("EXCLUDED — typings carrying no company id", d.TypingsWithNoCompanyId));
            sb.AppendLine(Line(
                "EXCLUDED — typings whose observation is not in the archive",
                d.TypingsWithNoArchivedObservation));
            sb.AppendLine(Line(
                "EXCLUDED — typings whose observation has no publication instant", d.TypingsWithNoPublishedAt));
            sb.AppendLine(string.Create(
                CultureInfo.InvariantCulture,
                $"- reconciliation: {d.TypingsContributingFacts} contributing + {d.TypingsWithNoFacts} "
                    + $"no-facts + {d.TypingsWithNoCompanyId} no-company + "
                    + $"{d.TypingsWithNoArchivedObservation} no-observation + {d.TypingsWithNoPublishedAt} "
                    + $"no-publication-instant = "
                    + $"{d.TypingsContributingFacts + d.TypingsWithNoFacts + d.TypingsWithNoCompanyId + d.TypingsWithNoArchivedObservation + d.TypingsWithNoPublishedAt} "
                    + $"of {d.TypingRecordsScanned} scanned"));
            sb.AppendLine("- first typing record date: " + (d.FirstTypingRecordDateUtc is { } first
                ? "`" + first.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "`"
                : "not recorded (the typing store holds no records)"));
        }
        else
        {
            // The typing store was never opened. Six zeros, a reconciliation that "balances", and a
            // first-typing-date line blaming an empty store would all be fabricated measurements — the
            // store may hold thousands of records this run simply did not look at.
            sb.AppendLine(
                "- typing accounting: **NOT COMPUTED** — no read record hydrated, so the news-typing store "
                    + "and the news-observation archive were not loaded (see the corpus accounting above). "
                    + "The store may hold any number of records; this run did not look.");
        }

        sb.AppendLine();

        if (d.MatchedVocabularyTermCounts.Count > 0)
        {
            sb.AppendLine("### Which vocabulary members actually matched");
            sb.AppendLine();
            sb.AppendLine(
                "Per-term counts ship beside the rate so a single over-broad member cannot silently drive "
                    + "it.");
            sb.AppendLine();
            sb.AppendLine("| Term | Reads matching |");
            sb.AppendLine("| --- | --- |");
            foreach (var term in d.MatchedVocabularyTermCounts)
            {
                sb.AppendLine("| `" + term.Name + "` | "
                    + term.Count.ToString(CultureInfo.InvariantCulture) + " |");
            }

            sb.AppendLine();
        }

        sb.AppendLine(string.Create(
            CultureInfo.InvariantCulture,
            $"### Forward return by read direction — DESCRIPTIVE ({report.ForwardHorizonDays} CALENDAR days "
                + $"from the filing date, exit tolerance {report.ForwardExitToleranceDays} days)"));
        sb.AppendLine();
        sb.AppendLine(
            "AD-14: price is validation-only and is read strictly downstream of scoring. No gate, no "
                + "threshold, no promotion and no demotion follows from these numbers. The window is 21 "
                + "CALENDAR days — not 21 sessions — because that is what the shared forward-return "
                + "primitive computes.");
        sb.AppendLine();
        sb.AppendLine("| Direction | Reads | Windows resolved | Min | P25 | Median | P75 | Max | Mean |");
        sb.AppendLine("| --- | --- | --- | --- | --- | --- | --- | --- | --- |");
        foreach (var dist in d.ForwardReturnByDirection)
        {
            sb.AppendLine("| " + dist.Direction + " | "
                + dist.ReadsInClass.ToString(CultureInfo.InvariantCulture) + " | "
                + dist.Count.ToString(CultureInfo.InvariantCulture) + " | "
                + Return(dist.Min) + " | " + Return(dist.P25) + " | " + Return(dist.Median) + " | "
                + Return(dist.P75) + " | " + Return(dist.Max) + " | " + Return(dist.Mean) + " |");
        }

        sb.AppendLine();
        sb.AppendLine("Forward-return outcomes over directional reads, every named state counted:");
        sb.AppendLine();
        foreach (var state in d.ForwardReturnStates)
        {
            sb.AppendLine(Line(state.Name, state.Count));
        }

        sb.AppendLine();
    }

    private static void RenderWorkedExample(StringBuilder sb, DirectionalFilingReadReport report)
    {
        var w = report.WorkedExample;
        sb.AppendLine(string.Create(
            CultureInfo.InvariantCulture,
            $"## Worked example — {w.Ticker}, filing {w.FilingDate:yyyy-MM-dd}, labelled {w.LabelDate:yyyy-MM-dd}"));
        sb.AppendLine();
        sb.AppendLine(
            "Rendered from the live corpus this run read — an illustration a human can check by hand, not a "
                + "fixture and not hard-coded prose.");
        sb.AppendLine();

        if (!w.Resolved || w.Row is null)
        {
            sb.AppendLine("**Not resolvable on this run**: " + (w.UnresolvedReason ?? "reason not recorded")
                + ". Nothing is fabricated in its place.");
            sb.AppendLine();
            return;
        }

        var row = w.Row;
        sb.AppendLine("- Accession: `" + row.Accession + "` (cacheVersion "
            + row.CacheVersion.ToString(CultureInfo.InvariantCulture) + ")");
        sb.AppendLine("- Evidence title: " + (row.EvidenceTitle is { } title
            ? EscapePipes(title)
            : "not recorded (no joined evidence record)"));
        sb.AppendLine("- Direction: `" + row.DirectionClass + "`"
            + (row.DirectionToken is { } token ? " (token `" + token + "`)" : string.Empty)
            + ", confidence " + Confidence(row.Confidence is { } c ? (double)c : null)
            + " (from `" + row.ConfidenceSource + "`)");
        sb.AppendLine("- Reason: " + (row.Reason is { } reason
            ? EscapePipes(reason)
            : "not recorded"));
        sb.AppendLine("- Supporting excerpt equals evidence title: "
            + Bool(row.Groundedness.SupportingExcerptEqualsSomeEvidenceTitle));
        sb.AppendLine(string.Create(
            CultureInfo.InvariantCulture,
            $"- Forward return over {report.ForwardHorizonDays} CALENDAR days (DESCRIPTIVE): "
                + $"{Return(row.Disagreement.ForwardReturn21d)} (`{row.Disagreement.ForwardReturnState}`)"));
        sb.AppendLine("- News arm: `" + row.Disagreement.NewsArm + "`, typed facts in window: "
            + Int(row.Disagreement.TypedFactsInWindow) + ", adverse matches: "
            + Int(row.Disagreement.NegativeMatchCount));
        sb.AppendLine();

        if (w.SameWindowNews.Count == 0)
        {
            sb.AppendLine("(no typed news in the same window)");
            sb.AppendLine();
            return;
        }

        sb.AppendLine("| Published | Event types | Adverse match | Statement |");
        sb.AppendLine("| --- | --- | --- | --- |");
        foreach (var line in w.SameWindowNews)
        {
            sb.AppendLine("| " + line.PublishedDateUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                + " | " + EscapePipes(line.EventTypes)
                + " | " + (line.MatchedAdverseVocabulary ? "yes" : "no")
                + " | " + EscapePipes(line.Statement) + " |");
        }

        if (w.SameWindowNewsOmitted > 0)
        {
            sb.AppendLine();
            sb.AppendLine(string.Create(
                CultureInfo.InvariantCulture,
                $"{w.SameWindowNewsOmitted} further same-window line(s) were omitted by the rendering cap of "
                    + $"{DirectionalFilingReadReporter.MaxWorkedExampleNewsLines} — counted here, never "
                    + $"silently dropped."));
        }

        sb.AppendLine();
    }

    private static void RenderVocabulary(StringBuilder sb)
    {
        sb.AppendLine("## The adverse vocabulary, verbatim (`"
            + FilingReadDisagreementVocabulary.Version + "`)");
        sb.AppendLine();
        sb.AppendLine(
            "A closed, versioned set declared in code because the news taxonomy carries no polarity. It is a "
                + "coarse LEXICAL PROXY, not a verdict: a match means Radar's own typed record contained "
                + "adverse-sounding language in the same window, never that the filing read was wrong. It "
                + "feeds no signal, no score and no fingerprint.");
        sb.AppendLine();
        sb.AppendLine("Event types:");
        sb.AppendLine();
        foreach (var member in FilingReadDisagreementVocabulary.NegativeEventTypes)
        {
            sb.AppendLine("- `" + member.EventType + "` — " + member.Justification);
        }

        sb.AppendLine();
        sb.AppendLine(
            "Every other taxonomy member is deliberately excluded: none of them carries polarity (a "
                + "regulatory event can be an approval, a market reaction can be a rise, an analyst action "
                + "can be an upgrade).");
        sb.AppendLine();
        sb.AppendLine("Phrases (matched case-insensitively with word boundaries on both sides):");
        sb.AppendLine();
        sb.AppendLine("| Phrase | Why it is in the set |");
        sb.AppendLine("| --- | --- |");
        foreach (var phrase in FilingReadDisagreementVocabulary.NegativePhrases)
        {
            sb.AppendLine("| `" + phrase.Phrase + "` | " + EscapePipes(phrase.Justification) + " |");
        }

        sb.AppendLine();
    }

    // ---------------------------------------------------------------------------------------------------
    // Formatting — a null NEVER renders as 0 or false.
    // ---------------------------------------------------------------------------------------------------

    private static string Line(string label, int count) =>
        "- " + label + ": " + count.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// A line for a count DERIVED from one of the join stores: it prints the number when the stores were
    /// loaded, and says NOT COMPUTED when they were not. A store-derived <c>0</c> from a build that never
    /// opened the store is a fabricated measurement, which CLAUDE.md forbids in rendered text as much as on
    /// a record.
    /// </summary>
    private static string NotComputedLine(DirectionalFilingReadReport report, string label, int count) =>
        report.JoinStoresLoaded
            ? Line(label, count)
            : "- " + label + ": NOT COMPUTED (the store was not loaded — no read record hydrated)";

    private static string Share(double? share) => share is { } value
        ? value.ToString(ShareFormat, CultureInfo.InvariantCulture)
        : "not defined (empty denominator)";

    private static string Return(double? value) => value is { } v
        ? v.ToString(ReturnFormat, CultureInfo.InvariantCulture)
        : "not recorded";

    private static string Confidence(double? value) => value is { } v
        ? v.ToString(ConfidenceFormat, CultureInfo.InvariantCulture)
        : "not recorded";

    private static string Int(int? value) => value is { } v
        ? v.ToString(CultureInfo.InvariantCulture)
        : "not recorded";

    private static string Decimal(decimal? value) => value is { } v
        ? v.ToString(ConfidenceFormat, CultureInfo.InvariantCulture)
        : "not-recorded";

    private static string Double(double? value) => value is { } v
        ? v.ToString("0.000000", CultureInfo.InvariantCulture)
        : "not-recorded";

    private static string Date(DateOnly? value) => value is { } v
        ? v.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
        : "not-recorded";

    private static string Bool(bool? value) => value is { } v ? (v ? "true" : "false") : "not-recorded";

    private static string EscapePipes(string value) =>
        value.Replace("|", "\\|", StringComparison.Ordinal)
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal);
}
