using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Radar.Application.Efficacy.Attention;

/// <summary>
/// Renders the AD-16 attention-arrival screen as JSON, CSV and Markdown (spec 169). Pure string production —
/// it touches no disk; <see cref="IAttentionArrivalArtifactStore"/> owns that (AD-5).
/// <para>
/// <b>Byte-identical re-runs are a hard requirement, and the rules that deliver it are:</b> no wall-clock
/// timestamp, no machine path, no absolute directory, no unordered collection, and every number formatted
/// with the invariant culture at a fixed precision. Re-running over unchanged stores must produce identical
/// output, because an artifact that churns cannot be diffed and a diff is how a change of meaning is noticed.
/// </para>
/// <para>
/// <b>Language.</b> JSON carries the three screen tokens verbatim (it is the machine-readable source of
/// truth); Markdown uses restrained human wording. No confidence or significance claim in either — the daily
/// windows overlap by construction. No advice vocabulary (AD-9), no price (AD-14), no promotion.
/// </para>
/// </summary>
public sealed class AttentionArrivalRenderer
{
    // Fixed 6-decimal invariant formatting everywhere a correlation or δ is written: round-trip "R" would
    // expose platform-dependent digits, and a culture-sensitive format would emit a comma decimal separator
    // on some machines — either would break byte-identical re-runs across environments.
    private const string RhoFormat = "0.000000";

    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            // Enums by NAME: the reason tokens ARE the contract a later slice (155) will consume, and an
            // integer ordinal would silently re-map if a member were ever inserted.
            Converters = { new JsonStringEnumConverter(namingPolicy: null, allowIntegerValues: false) },
        };
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }

    /// <summary>The machine-readable source of truth: every per-date row, per-company exclusion, reason token and count.</summary>
    public string RenderJson(AttentionArrivalScreenResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return JsonSerializer.Serialize(result, JsonOptions);
    }

    /// <summary>One row per candidate as-of date, with N, the correlations, δ and the drop counts.</summary>
    public string RenderCsv(AttentionArrivalScreenResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var builder = new StringBuilder();
        builder.Append("section,asOfDateUtc,asOfInstantUtc,eligible,exclusionReason,companiesConsidered,")
            .Append("companiesInExcludedCohort,companiesIncluded,rhoPrimary,rhoPersistence,delta,")
            .Append("rhoAttentionScore,rhoControl,primaryMinusControl");
        foreach (var baseline in result.BaselineStrategies)
        {
            builder.Append(",rho_").Append(CsvField.Escape(baseline));
        }

        builder.Append(",exclusionCounts").Append('\n');

        foreach (var section in new[] { result.Primary, result.Exploratory })
        {
            foreach (var row in section.Dates)
            {
                builder
                    .Append(CsvField.Escape(section.Label)).Append(',')
                    .Append(row.AsOfDateUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append(',')
                    .Append(row.AsOfInstantUtc.UtcDateTime.ToString("o", CultureInfo.InvariantCulture)).Append(',')
                    .Append(row.IsEligible ? "true" : "false").Append(',')
                    .Append(row.ExclusionReason.ToString()).Append(',')
                    .Append(row.CompaniesConsidered.ToString(CultureInfo.InvariantCulture)).Append(',')
                    .Append(row.CompaniesInExcludedCohort.ToString(CultureInfo.InvariantCulture)).Append(',')
                    .Append(row.CompaniesIncluded.ToString(CultureInfo.InvariantCulture)).Append(',')
                    .Append(Cell(row.PrimaryCorrelation)).Append(',')
                    .Append(Cell(row.PersistenceCorrelation)).Append(',')
                    .Append(Cell(row.IsDeltaDefined, row.Delta)).Append(',')
                    .Append(Cell(row.SecondaryAttentionScoreCorrelation)).Append(',')
                    .Append(Cell(row.ControlCorrelation)).Append(',')
                    .Append(Cell(row.IsPrimaryMinusControlDefined, row.PrimaryMinusControl));

                // Baselines in the fixed configured order, matched by NAME so a column can never silently
                // pick up a different arm's number.
                foreach (var baseline in result.BaselineStrategies)
                {
                    var diagnostic = row.BaselineCorrelations
                        .FirstOrDefault(d => string.Equals(d.Name, baseline, StringComparison.Ordinal));
                    builder.Append(',').Append(diagnostic is null ? string.Empty : Cell(diagnostic));
                }

                builder.Append(',').Append(CsvField.Escape(string.Join("; ", row.ExclusionCounts
                    .Select(c => $"{c.Reason}={c.Count.ToString(CultureInfo.InvariantCulture)}"))));
                builder.Append('\n');
            }
        }

        return builder.ToString();
    }

    /// <summary>The concise operator summary: the pinned boundary, the coverage limitation, the status, and the separate exploratory cohort.</summary>
    public string RenderMarkdown(AttentionArrivalScreenResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var builder = new StringBuilder();
        builder.Append("# Attention-arrival screen (AD-16)\n\n");

        builder.Append("Radar's precommitted test of the stealth thesis: does a high `")
            .Append(result.PrimaryStrategy)
            .Append("` score today rank companies by how many DISTINCT third-party publishers cover them over the next ")
            .Append(result.HorizonDays.ToString(CultureInfo.InvariantCulture))
            .Append(" days — better than simply knowing how many covered them over the previous ")
            .Append(result.HorizonDays.ToString(CultureInfo.InvariantCulture))
            .Append(" days? Attention is strongly autocorrelated, so beating that persistence comparator is ")
            .Append("the necessary bar.\n\n");

        builder.Append("- First eligible as-of date (precommitted, AD-16 §4): **")
            .Append(result.FirstEligibleAsOfDateUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
            .Append("**\n");
        builder.Append("- Horizon: **").Append(result.HorizonDays.ToString(CultureInfo.InvariantCulture))
            .Append(" calendar days**, no exit tolerance\n");
        builder.Append("- Minimum support: **")
            .Append(result.MinimumCompaniesPerDate.ToString(CultureInfo.InvariantCulture))
            .Append("** companies per date, **")
            .Append(result.MinimumEligibleDates.ToString(CultureInfo.InvariantCulture))
            .Append("** eligible dates before the screen resolves\n");
        builder.Append("- Primary arm: `").Append(result.PrimaryStrategy)
            .Append("` · formula control: `").Append(result.ControlStrategy).Append("`\n\n");

        if (result.Availability == AttentionEvaluationAvailability.Unavailable)
        {
            builder.Append("## Result: not evaluated\n\n");
            builder.Append("A prerequisite failed, so no screen status is reported. This is a configuration ")
                .Append("problem, **not** a pending accrual.\n\n");
            builder.Append("- Reason: `").Append(result.UnavailableReason.ToString()).Append("`\n");
            builder.Append("- Detail: ").Append(result.UnavailableDetail ?? "(none recorded)").Append("\n\n");
            AppendCoverageLimitation(builder, result);
            return builder.ToString();
        }

        builder.Append("## Result\n\n");
        builder.Append(StatusSentence(result)).Append("\n\n");
        builder.Append("- Candidate as-of dates: ")
            .Append(result.Primary.CandidateDates.ToString(CultureInfo.InvariantCulture))
            .Append("\n- Eligible as-of dates: ")
            .Append(result.Primary.EligibleDates.ToString(CultureInfo.InvariantCulture))
            .Append(" of ").Append(result.MinimumEligibleDates.ToString(CultureInfo.InvariantCulture))
            .Append(" required\n- Median daily δ (ρ primary − ρ persistence): ")
            .Append(result.Primary.IsMedianDeltaDefined
                ? result.Primary.MedianDelta.ToString(RhoFormat, CultureInfo.InvariantCulture)
                : "not yet defined")
            .Append("\n\n");

        builder.Append("The daily windows OVERLAP and are not independent, so this carries **no confidence or ")
            .Append("significance claim in either direction**. A median δ above zero clears one necessary ")
            .Append("screen; it is not evidence of efficacy, and AD-15 governs every positive claim ")
            .Append("regardless. A dependence-aware comparison is a separate, later layer.\n\n");

        AppendSectionTable(builder, "Primary screen", result.Primary, result);
        AppendCoverageLimitation(builder, result);

        builder.Append("## Exploratory cohort (reported separately, never pooled)\n\n");
        builder.Append("The event-enriched cohort is run through the SAME builders on a DISJOINT company set. ")
            .Append("Several of its members were proposed partly because of known events — current ")
            .Append("manifestations of the very predictor Radar scores — so pooling them into the primary ")
            .Append("would prove enrichment rather than discrimination. These rows can never satisfy the ")
            .Append("primary minimum and never change the primary status.\n\n");
        builder.Append("**Expect every row below to read `InsufficientCompanies`, permanently.** The cohort ")
            .Append("holds far fewer companies than the precommitted minimum of ")
            .Append(result.MinimumCompaniesPerDate.ToString(CultureInfo.InvariantCulture))
            .Append(", so this section reports COUNTS ONLY and cannot produce a correlation at the cohort's ")
            .Append("size. That is the intended design, not a defect and not missing data: a rank ")
            .Append("correlation over a handful of deliberately event-enriched names, printed beside the ")
            .Append("primary, would invite exactly the comparison this separation exists to prevent. The ")
            .Append("minimum is deliberately NOT lowered for this section.\n\n");
        AppendSectionTable(builder, "Exploratory", result.Exploratory, result);

        return builder.ToString();
    }

    private static string StatusSentence(AttentionArrivalScreenResult result) => result.ScreenStatus switch
    {
        AttentionScreenStatus.Pending =>
            "**Pending.** Fewer eligible as-of dates have accrued than the precommitted minimum. This is "
                + "expected accrual, not a defect and not a result.",
        AttentionScreenStatus.Miss =>
            "**Miss.** Over the precommitted minimum of eligible dates the median daily δ is at or below "
                + "zero: the arm did not rank forward publisher breadth better than the trailing-count "
                + "persistence comparator at the declared horizon. Under AD-16 this stands as recorded — it "
                + "may not be rescued by changing the outcome or the horizon after inspection.",
        AttentionScreenStatus.ClearsNecessaryScreen =>
            "**Clears the necessary screen.** Over the precommitted minimum of eligible dates the median "
                + "daily δ is above zero. This clears one necessary screen only. It is not proof of efficacy, "
                + "and it is not a recommendation about any company.",
        _ => "No status was produced.",
    };

    private static void AppendCoverageLimitation(
        StringBuilder builder, AttentionArrivalScreenResult result)
    {
        builder.Append("## What \"complete coverage\" does and does not mean\n\n");
        builder.Append("A company-date enters this screen only when Radar can PROVE it observed that ")
            .Append("company's third-party coverage across both the trailing and the forward window: every ")
            .Append("configured `").Append(result.AttentionCollector)
            .Append("` feed checked and succeeded, none of them truncated at its result limit, and no gap ")
            .Append("in the collection cadence. A window that failed, was capped, or was collected by a ")
            .Append("partial or score-only pass is dropped as `IncompleteAttentionCollection` — never ")
            .Append("counted as a publisher count of zero.\n\n");
        builder.Append("Per article, the same proof rule applies: it counts only when its evidence carries ")
            .Append("the collector stamp `").Append(result.AttentionCollector)
            .Append("` RECORDED at collection time. Attribution Radar re-derived afterwards (spec 151's ")
            .Append("legacy inference) cannot prove that article's collection was complete, so it drops the ")
            .Append("company-date as `UnresolvedComparatorProvenance`/`UnresolvedOutcomeProvenance` just as ")
            .Append("missing attribution does. This screen's metric is therefore invariant to the ")
            .Append("scoring-only legacy-attribution setting.\n\n");
        builder.Append("**This is an operational statement about Radar's CONFIGURED news source, not a claim ")
            .Append("that it indexes the whole web.** A publisher Radar's source never surfaced is invisible ")
            .Append("here regardless of coverage proof. Within a proved-complete window, an absence of ")
            .Append("coverage is a valid outcome of zero and stays in the sample — it is the central ")
            .Append("negative case, not missing data.\n\n");
    }

    private static void AppendSectionTable(
        StringBuilder builder,
        string heading,
        AttentionArrivalSection section,
        AttentionArrivalScreenResult result)
    {
        builder.Append("### ").Append(heading).Append("\n\n");

        if (section.Dates.Count == 0)
        {
            builder.Append("No candidate as-of date has accrued yet.\n\n");
            return;
        }

        builder.Append("| As-of date (UTC) | Eligible | N | ρ `").Append(result.PrimaryStrategy)
            .Append("` | ρ persistence | δ | Reason |\n");
        builder.Append("| --- | --- | ---: | ---: | ---: | ---: | --- |\n");

        foreach (var row in section.Dates)
        {
            builder
                .Append("| ").Append(row.AsOfDateUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
                .Append(" | ").Append(row.IsEligible ? "yes" : "no")
                .Append(" | ").Append(row.CompaniesIncluded.ToString(CultureInfo.InvariantCulture))
                .Append(" | ").Append(Display(row.PrimaryCorrelation))
                .Append(" | ").Append(Display(row.PersistenceCorrelation))
                .Append(" | ").Append(row.IsDeltaDefined
                    ? row.Delta.ToString(RhoFormat, CultureInfo.InvariantCulture)
                    : "—")
                .Append(" | ").Append(row.ExclusionReason == AttentionDateExclusionReason.None
                    ? "—"
                    : row.ExclusionReason.ToString())
                .Append(" |\n");
        }

        builder.Append('\n');
    }

    private static string Display(AttentionDiagnostic diagnostic) =>
        diagnostic.IsDefined
            ? diagnostic.Rho.ToString(RhoFormat, CultureInfo.InvariantCulture)
            : "—";

    // An undefined value renders as an EMPTY cell carrying no number at all. A 0 would be indistinguishable
    // from a genuine ρ of exactly zero, and NaN is never emitted.
    private static string Cell(AttentionDiagnostic diagnostic) =>
        Cell(diagnostic.IsDefined, diagnostic.Rho);

    private static string Cell(bool isDefined, double value) =>
        isDefined ? value.ToString(RhoFormat, CultureInfo.InvariantCulture) : string.Empty;
}
