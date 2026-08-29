using System.Globalization;
using System.Net;
using System.Text;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using Radar.Application.Abstractions.Persistence;
using Radar.Application.EntityResolution;
using Radar.Application.News;
using Radar.Infrastructure.News;
using Radar.Infrastructure.Sources;

using Xunit.Abstractions;

namespace Radar.IntegrationTests;

/// <summary>
/// Spec 198 §4 (a) — the READ-ONLY LIVE measurement of what the recency window actually does to the feed
/// (CLAUDE.md's "no measure ships without its live distribution"). It issues BOTH arms — unfiltered and
/// windowed at the shipped default — for every configured <c>newssearch</c> feed, through the PRODUCTION
/// <see cref="HttpNewsSearchReader"/>, and reports item counts, AGE distributions, projected retained-slot
/// usage and the projected new-versus-deduped split.
/// <para>
/// <b>It reports TWO populations, named, and never merges them.</b> The FULL OBSERVED RESPONSE (prefix plus
/// the spec-190 diagnostic tail, under the reader's unchanged 100-item ceiling) is what spec 198's
/// motivating table measured — 100 items, median age 71 days — so only that population can
/// corroborate it. The RETAINED PREFIX is what Radar's 25-slot budget consumes, so only that population can
/// answer the slot-usage and coverage questions; its item count is pinned at the cap by construction and
/// reporting it as "items returned" would state an artefact of the retention limit as a property of the
/// feed. The tail is already fetched, parsed and bounded, so reporting it costs NOTHING extra and admits
/// nothing (spec 198 §5).
/// </para>
/// <para>
/// <b>Nothing is admitted and nothing is persisted.</b> No evidence, observation, signal or score is
/// created; the archive is read ONLY to project the dedupe split against URLs Radar already holds. The
/// composition registers no score file store, no run store, no scoring-config store and no collector.
/// </para>
/// <para>
/// <b>It issues real requests, so it is ENV-GATED and skipped with a NAMED reason otherwise</b> (the
/// spec-196 §7 precedent). Set <c>RADAR_NEWS_RECENCY_LIVE_DATA_ROOT</c> to a Radar data root (the one
/// holding <c>companies.json</c> and <c>news-observations/</c>) to run it. Requests are paced by the
/// collector's own <see cref="NewsCollectorOptions.InterRequestDelay"/> and issued strictly sequentially —
/// two per company, which is the same per-company request count the baseline makes, doubled only because
/// this measures both arms.
/// </para>
/// <para>
/// The output is markdown on the test log, ready to paste into a PR body. Deterministic in everything but
/// the provider's own answer: fixed ordering by company id, invariant formatting.
/// </para>
/// </summary>
public sealed class NewsRecencyWindowLiveMeasurementTests(ITestOutputHelper output)
{
    internal const string DataRootVariable = "RADAR_NEWS_RECENCY_LIVE_DATA_ROOT";

    internal const string SkipReason =
        "Spec 198 §4 live feed measurement (issues REAL Google News RSS requests): set " + DataRootVariable
            + " to a Radar data root (companies.json, news-observations/) to run it.";

    /// <summary>The data root under measurement, or null when the variable is unset/not a directory.</summary>
    internal static string? DataRoot()
    {
        var root = Environment.GetEnvironmentVariable(DataRootVariable);
        return !string.IsNullOrWhiteSpace(root) && Directory.Exists(root) ? root : null;
    }

    /// <summary>The shipped window under measurement, and Radar's own retained-slot limit.</summary>
    private const int WindowDays = Radar.Application.Scoring.NewsQueryScoringIdentity.DefaultRecencyWindowDays;

    private const int RetainedSlots = 25;

    /// <summary>The baseline this measurement is compared against (the 2026-08-28 run named in spec 198).</summary>
    private const int BaselineNewObservations = 234;

    private const int BaselineCrossRunDeduped = 1370;

    [NewsRecencyLiveFact]
    public async Task LiveMeasurement_UnfilteredVersusWindowed_AcrossTheConfiguredFeedSet()
    {
        var root = DataRoot()!;
        var ct = CancellationToken.None;

        var originalCulture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        try
        {
            await using var provider = AttentionPolicyCounterfactualTests.BuildReadOnlyProvider(root);
            await provider.GetRequiredService<ICompanyUniverseSeeder>().SeedAsync(ct);

            var companies = provider.GetRequiredService<ICompanyRepository>();
            var feeds = (await companies.GetSourceFeedsAsync(ct))
                .Where(f => string.Equals(f.FeedType, "newssearch", StringComparison.Ordinal))
                .OrderBy(f => f.CompanyId)
                .ThenBy(f => f.Id)
                .ToList();

            // Every URL Radar already holds an observation for. The projected dedupe split is measured
            // against THIS, which is what the cross-run dedupe actually consults.
            var archivedUrls = (await provider.GetRequiredService<FileNewsObservationArchive>()
                    .GetAllAsync(ct))
                .Select(o => o.GoogleLandingUrl)
                .Where(u => !string.IsNullOrWhiteSpace(u))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var options = new NewsCollectorOptions();
            var reader = CreateReader();

            var rows = new List<FeedMeasurement>(feeds.Count);
            var first = true;
            foreach (var feed in feeds)
            {
                ct.ThrowIfCancellationRequested();

                var target = QueryFeedTarget.Parse(feed.Url);
                if (target is null)
                {
                    continue;
                }

                if (!first)
                {
                    await Task.Delay(options.InterRequestDelay, ct);
                }

                first = false;

                var unfiltered = await reader.ReadAsync(
                    new NewsSearchQuery(target.QueryPhrase, RetainedSlots, options.EnglishOnly, 0), ct);

                await Task.Delay(options.InterRequestDelay, ct);

                var windowed = await reader.ReadAsync(
                    new NewsSearchQuery(
                        target.QueryPhrase, RetainedSlots, options.EnglishOnly, WindowDays),
                    ct);

                rows.Add(new FeedMeasurement(
                    feed.CompanyId,
                    feed.Name,
                    target,
                    Arm.From(unfiltered, target, archivedUrls),
                    Arm.From(windowed, target, archivedUrls)));
            }

            var report = new StringBuilder();
            Render(report, rows);
            output.WriteLine(report.ToString());

            // The measurement is the deliverable; these guard only that it MEASURED something, so an empty
            // or misconfigured root can never be reported as a result.
            Assert.NotEmpty(feeds);
            Assert.NotEmpty(rows);
            Assert.All(rows, r => Assert.NotNull(r.Unfiltered));
            Assert.All(rows, r => Assert.NotNull(r.Windowed));
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    /// <summary>
    /// The production reader, over a plain gzip-capable handler — the same decompression the DI
    /// registration configures. Nothing here re-implements request building or RSS parsing.
    /// </summary>
    private static HttpNewsSearchReader CreateReader() =>
        new(
            new HttpClient(new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            }),
            NullLogger<HttpNewsSearchReader>.Instance,
            TimeProvider.System);

    /// <summary>
    /// One arm's answer for one feed, already reduced to the facts the report needs — over TWO deliberately
    /// separate populations, because they answer different questions and merging them would make one of them
    /// wrong.
    /// <list type="bullet">
    /// <item><b>The FULL OBSERVED RESPONSE</b> (<see cref="ValidItemsObserved"/>,
    /// <see cref="FullResponseAgesInDays"/>) — every structurally valid item the provider returned, under the
    /// reader's unchanged 100-item absolute parse ceiling. Since spec 190 the reader scans past the retained
    /// prefix and exposes the remainder as <c>DiagnosticTail</c>, so this costs NO extra request, page or
    /// article fetch. This is the population spec 198's motivating table measured (100 items, median age 71
    /// days), and reporting anything else here would make the harness unable to corroborate it.</item>
    /// <item><b>The RETAINED PREFIX</b> (<see cref="RetainedItems"/>,
    /// <see cref="RetainedAgesInDays"/>) — the first <see cref="RetainedSlots"/> items, which is what Radar's
    /// budget actually consumes and therefore what the slot-usage and coverage sections must reason about.
    /// Its item count is pinned at the cap by construction, so quoting it as "items returned" would report an
    /// artefact of the retention limit as a property of the feed.</item>
    /// </list>
    /// No tail item is admitted, mapped or persisted anywhere (spec 198 §5); the tail is read for counting
    /// only.
    /// </summary>
    private sealed record Arm(
        bool Success,
        string? FailureDetail,
        int RetainedItems,
        int ValidItemsObserved,
        IReadOnlyList<double> FullResponseAgesInDays,
        IReadOnlyList<double> RetainedAgesInDays,
        int RelevantItems,
        int RelevantRecentItems,
        int ProjectedRetained,
        int ProjectedNew,
        int ProjectedDeduped)
    {
        public static Arm From(
            NewsSearchReadResult result,
            QueryFeedTarget target,
            IReadOnlySet<string> archivedUrls)
        {
            if (!result.IsSuccess)
            {
                return new Arm(
                    false, result.Detail ?? result.Outcome.ToString(), 0, 0, [], [], 0, 0, 0, 0, 0);
            }

            // Ages over BOTH populations, from the SAME already-fetched response: the retained prefix, and
            // the prefix plus the spec-190 diagnostic tail (the whole observed response).
            var retainedAges = AgesOf(result.Items);
            var fullResponseAges = AgesOf(result.Items.Concat(result.DiagnosticTail));

            // The SAME admission mechanics the collector applies, restated: the relevance rule (title
            // contains the phrase or ticker, publisher suffix stripped), then URL dedupe, then the 25-slot
            // cap. Nothing is admitted — this only PROJECTS what would be.
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var relevant = 0;
            var relevantRecent = 0;
            var retained = 0;
            var projectedNew = 0;
            var projectedDeduped = 0;
            foreach (var item in result.Items)
            {
                if (!IsRelevant(item.Title, target) || !seen.Add(item.Url))
                {
                    continue;
                }

                relevant++;
                if (item.PublishedAt is { } published
                    && (item.RetrievedAt - published).TotalDays <= WindowDays)
                {
                    relevantRecent++;
                }

                if (retained >= RetainedSlots)
                {
                    continue;
                }

                retained++;
                if (archivedUrls.Contains(item.Url))
                {
                    projectedDeduped++;
                }
                else
                {
                    projectedNew++;
                }
            }

            return new Arm(
                true,
                null,
                result.Items.Count,
                result.ValidItemsObserved,
                fullResponseAges,
                retainedAges,
                relevant,
                relevantRecent,
                retained,
                projectedNew,
                projectedDeduped);
        }

        /// <summary>
        /// <c>RetrievedAt − PublishedAt</c> for every item carrying a parseable publication instant. An item
        /// with no parseable <c>pubDate</c> carries no age and is excluded from the age buckets ONLY — it is
        /// still counted everywhere items are counted, because an unknown age is not evidence of anything.
        /// </summary>
        private static List<double> AgesOf(IEnumerable<NewsArticleItem> items)
        {
            var ages = new List<double>();
            foreach (var item in items)
            {
                if (item.PublishedAt is { } published)
                {
                    ages.Add((item.RetrievedAt - published).TotalDays);
                }
            }

            return ages;
        }

        /// <summary>
        /// The collector's relevance rule, restated over the same normalization the shared
        /// <see cref="GoogleNewsHeadline"/> helper performs. It is a projection, not an admission: no item
        /// examined here becomes evidence, an observation candidate or a scoring input.
        /// </summary>
        private static bool IsRelevant(string? title, QueryFeedTarget target)
        {
            var normalized = Normalize(GoogleNewsHeadline.StripPublisherSuffix(title));
            if (normalized.Length == 0)
            {
                return false;
            }

            var phrase = Normalize(target.QueryPhrase);
            if (phrase.Length > 0 && normalized.Contains(phrase, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var ticker = Normalize(target.Ticker);
            return ticker.Length > 0 && normalized.Contains(ticker, StringComparison.OrdinalIgnoreCase);
        }

        private static string Normalize(string? value) =>
            string.IsNullOrWhiteSpace(value)
                ? string.Empty
                : string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    private sealed record FeedMeasurement(
        Guid CompanyId, string FeedName, QueryFeedTarget Target, Arm Unfiltered, Arm Windowed);

    private static void Render(StringBuilder report, IReadOnlyList<FeedMeasurement> rows)
    {
        var unfiltered = rows.Select(r => r.Unfiltered).Where(a => a.Success).ToList();
        var windowed = rows.Select(r => r.Windowed).Where(a => a.Success).ToList();

        report.AppendLine("## Spec 198 §4 — live news-feed recency measurement (read-only)");
        report.AppendLine();
        report.AppendLine(
            $"{rows.Count} `newssearch` feed(s) measured, two arms each: UNFILTERED (today's query) and "
                + $"WINDOWED (`when:{WindowDays}d`). Radar's retained-slot limit is {RetainedSlots} and is "
                + "unchanged; nothing here was admitted, mapped or persisted.");
        report.AppendLine();
        report.AppendLine(
            $"Successful reads: {unfiltered.Count} unfiltered, {windowed.Count} windowed "
                + $"(of {rows.Count} feeds).");
        report.AppendLine();

        report.AppendLine("### 0. Two populations, and why both are reported");
        report.AppendLine();
        report.AppendLine(
            "**The FULL OBSERVED RESPONSE** is every structurally valid item the provider returned, under "
                + "the reader's unchanged 100-item absolute parse ceiling. Since spec 190 the reader already "
                + "scans past the retained prefix and exposes the remainder as a diagnostic tail, so this "
                + "costs **no extra request, page or article fetch** and no tail item is admitted, mapped or "
                + "persisted anywhere. **This is the population spec 198's motivating table measured** (100 "
                + "items, median age 71 days), so it is the one that can corroborate it.");
        report.AppendLine();
        report.AppendLine(
            $"**The RETAINED PREFIX** is the first {RetainedSlots} items — what Radar's budget actually "
                + "consumes, and therefore what sections 4 (slot usage) and 5 (coverage) reason about. Its "
                + "item count is pinned at the cap **by construction**, so quoting it as `items returned` "
                + "would report an artefact of the retention limit as a property of the feed.");
        report.AppendLine();
        report.AppendLine(
            "**They are different questions and both are true.** A full-response median age of weeks and a "
                + "retained-prefix median age of days do not contradict each other: Google News returns "
                + "newest-first, so the prefix is the recent HEAD of a long historical tail. Neither number "
                + "is a correction of the other.");
        report.AppendLine();

        report.AppendLine("### 1. FULL OBSERVED RESPONSE — items returned per company");
        report.AppendLine();
        report.AppendLine(
            "Structurally valid items the provider returned, bounded only by the reader's 100-item parse "
                + "ceiling (unchanged).");
        report.AppendLine();
        report.AppendLine("| statistic | unfiltered | windowed |");
        report.AppendLine("| --- | ---: | ---: |");
        RenderStat(report, "min", unfiltered, windowed, a => a.ValidItemsObserved, Min);
        RenderStat(report, "median", unfiltered, windowed, a => a.ValidItemsObserved, Median);
        RenderStat(report, "max", unfiltered, windowed, a => a.ValidItemsObserved, Max);
        RenderStat(report, "total", unfiltered, windowed, a => a.ValidItemsObserved, v => v.Sum());
        report.AppendLine();

        report.AppendLine(
            "### 2. FULL OBSERVED RESPONSE — age distribution (`RetrievedAt − PublishedAt`)");
        report.AppendLine();
        report.AppendLine(
            "**This is the measurement that motivated the spec**, over the population the spec measured. "
                + "Items with no parseable `pubDate` carry no age and are excluded from these buckets only "
                + "— they are still counted everywhere items are counted.");
        report.AppendLine();
        RenderAgeTable(
            report,
            [.. unfiltered.SelectMany(a => a.FullResponseAgesInDays)],
            [.. windowed.SelectMany(a => a.FullResponseAgesInDays)]);

        report.AppendLine(
            $"### 3. RETAINED PREFIX — the {RetainedSlots} slots Radar's budget consumes");
        report.AppendLine();
        report.AppendLine(
            "The same responses, cut to the retained prefix. The item COUNTS are capped by construction; the "
                + "AGES are not, and they are what the collection budget actually spends itself on.");
        report.AppendLine();
        report.AppendLine("| statistic | unfiltered | windowed |");
        report.AppendLine("| --- | ---: | ---: |");
        RenderStat(report, "items retained (min)", unfiltered, windowed, a => a.RetainedItems, Min);
        RenderStat(report, "items retained (median)", unfiltered, windowed, a => a.RetainedItems, Median);
        RenderStat(report, "items retained (max)", unfiltered, windowed, a => a.RetainedItems, Max);
        RenderStat(
            report, "items retained (total)", unfiltered, windowed, a => a.RetainedItems, v => v.Sum());
        report.AppendLine();
        RenderAgeTable(
            report,
            [.. unfiltered.SelectMany(a => a.RetainedAgesInDays)],
            [.. windowed.SelectMany(a => a.RetainedAgesInDays)]);

        report.AppendLine($"### 4. Projected retained-slot usage (cap {RetainedSlots}, unchanged)");
        report.AppendLine();
        report.AppendLine(
            "Over the RETAINED PREFIX (section 3), which is the only population this question is about. "
                + "Projected by applying the collector's OWN relevance rule, URL dedupe and per-feed cap to "
                + "the same responses, then splitting the retained set against the URLs the observation "
                + "archive already holds. A projection, not an admission.");
        report.AppendLine();
        report.AppendLine("| quantity | unfiltered | windowed |");
        report.AppendLine("| --- | ---: | ---: |");
        RenderStat(report, "slots consumed (total)", unfiltered, windowed, a => a.ProjectedRetained, v => v.Sum());
        RenderStat(report, "median slots per company", unfiltered, windowed, a => a.ProjectedRetained, Median);
        RenderStat(report, "projected NEW", unfiltered, windowed, a => a.ProjectedNew, v => v.Sum());
        RenderStat(report, "projected cross-run DEDUPED", unfiltered, windowed, a => a.ProjectedDeduped, v => v.Sum());
        report.AppendLine();
        report.AppendLine(
            $"Baseline for comparison (2026-08-28 run): **{BaselineNewObservations} new / "
                + $"{BaselineCrossRunDeduped} cross-run deduped**.");
        report.AppendLine();

        report.AppendLine("### 5. Coverage — the criterion that must NOT regress");
        report.AppendLine();
        report.AppendLine(
            $"Genuinely recent (≤ {WindowDays} days old) company-relevant items each arm would ADMIT into "
                + $"the {RetainedSlots} retained slots — again the RETAINED PREFIX population, because "
                + "coverage is a question about what Radar would actually take in, not about what the "
                + "provider offered. **If the windowed arm admits fewer than the unfiltered arm, the window "
                + "is too narrow and the change must not ship.**");
        report.AppendLine();
        report.AppendLine("| quantity | unfiltered | windowed |");
        report.AppendLine("| --- | ---: | ---: |");
        RenderStat(report, "recent relevant items", unfiltered, windowed, a => a.RelevantRecentItems, v => v.Sum());
        RenderStat(report, "all relevant items", unfiltered, windowed, a => a.RelevantItems, v => v.Sum());
        report.AppendLine();

        // Measured over the FULL observed response: "the provider returned nothing at all", not "nothing
        // survived the retention cap" (which cannot happen at a zero-item response anyway, but the two are
        // different claims and only the first is what this section asserts).
        var zeroItem = rows
            .Where(r => r.Windowed.Success && r.Windowed.ValidItemsObserved == 0)
            .ToList();
        report.AppendLine("### 6. Companies the windowed arm returned ZERO items for");
        report.AppendLine();
        report.AppendLine(
            $"{zeroItem.Count} of {rows.Count}, counted over the FULL observed response. **This is EXPECTED "
                + "and is not a fault** — it means nothing was published about the company in the last "
                + $"{WindowDays} days. Those companies simply contribute no news evidence this run, exactly "
                + "as a quiet company always has.");
        if (zeroItem.Count > 0)
        {
            report.AppendLine();
            report.AppendLine(
                string.Join(", ", zeroItem.Take(30).Select(r => r.Target.QueryPhrase))
                    + (zeroItem.Count > 30 ? ", …" : string.Empty));
        }

        report.AppendLine();

        var failures = rows
            .Where(r => !r.Unfiltered.Success || !r.Windowed.Success)
            .ToList();
        report.AppendLine("### 7. Read failures (reported, never silently dropped)");
        report.AppendLine();
        report.AppendLine($"{failures.Count} feed(s) had at least one arm fail.");
        foreach (var failure in failures.Take(20))
        {
            report.AppendLine(
                $"- `{failure.Target.QueryPhrase}`: unfiltered "
                    + $"{failure.Unfiltered.FailureDetail ?? "ok"}, windowed "
                    + $"{failure.Windowed.FailureDetail ?? "ok"}");
        }

        report.AppendLine();
    }

    /// <summary>
    /// One age table over one population. Shared by the full-response and retained-prefix sections so the
    /// two cannot drift into bucketing differently — which would make them read as a contradiction
    /// rather than as two answers to two questions.
    /// </summary>
    private static void RenderAgeTable(
        StringBuilder report, IReadOnlyList<double> unfilteredAges, IReadOnlyList<double> windowedAges)
    {
        report.AppendLine("| bucket | unfiltered | windowed |");
        report.AppendLine("| --- | ---: | ---: |");
        foreach (var (label, upper) in new[]
                 {
                     ("≤ 1 day", 1.0), ("≤ 7 days", 7.0), ("≤ 30 days", 30.0),
                 })
        {
            report.AppendLine(
                $"| {label} | {Bucket(unfilteredAges, upper)} | {Bucket(windowedAges, upper)} |");
        }

        report.AppendLine(
            $"| > 30 days | {unfilteredAges.Count(a => a > 30.0)} | {windowedAges.Count(a => a > 30.0)} |");
        report.AppendLine($"| aged items counted | {unfilteredAges.Count} | {windowedAges.Count} |");
        report.AppendLine(
            $"| median age (days) | {Format(Median(unfilteredAges))} | {Format(Median(windowedAges))} |");
        report.AppendLine(
            $"| oldest item (days) | {Format(Max(unfilteredAges))} | {Format(Max(windowedAges))} |");
        report.AppendLine();
    }

    private static void RenderStat(
        StringBuilder report,
        string label,
        IReadOnlyList<Arm> unfiltered,
        IReadOnlyList<Arm> windowed,
        Func<Arm, int> selector,
        Func<IReadOnlyList<double>, double?> statistic)
    {
        var a = statistic([.. unfiltered.Select(x => (double)selector(x))]);
        var b = statistic([.. windowed.Select(x => (double)selector(x))]);
        report.AppendLine($"| {label} | {Format(a)} | {Format(b)} |");
    }

    private static string Bucket(IReadOnlyList<double> ages, double upperInclusive) =>
        ages.Count(a => a <= upperInclusive).ToString(CultureInfo.InvariantCulture);

    private static double? Min(IReadOnlyList<double> v) => v.Count == 0 ? null : v.Min();

    private static double? Max(IReadOnlyList<double> v) => v.Count == 0 ? null : v.Max();

    /// <summary>Mean of the two central values on an even count — stated because the convention is ambiguous.</summary>
    private static double? Median(IReadOnlyList<double> v)
    {
        if (v.Count == 0)
        {
            return null;
        }

        var sorted = v.Order().ToArray();
        var mid = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2.0;
    }

    /// <summary>A measured zero and an unmeasured one are different facts, so the report never prints one as the other.</summary>
    private static string Format(double? value) =>
        value is { } v ? v.ToString("0.##", CultureInfo.InvariantCulture) : "n/a";
}

/// <summary>
/// Runs the spec-198 §4 LIVE measurement only when a data root is supplied, and SKIPS WITH A NAMED REASON
/// otherwise — never silently. It issues real requests, so it must never run in CI by accident.
/// </summary>
public sealed class NewsRecencyLiveFactAttribute : FactAttribute
{
    public NewsRecencyLiveFactAttribute()
    {
        if (NewsRecencyWindowLiveMeasurementTests.DataRoot() is null)
        {
            Skip = NewsRecencyWindowLiveMeasurementTests.SkipReason;
        }
    }
}
