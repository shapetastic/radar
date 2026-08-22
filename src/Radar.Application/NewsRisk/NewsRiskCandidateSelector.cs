using Radar.Application.Reporting;
using Radar.Application.Scoring;

namespace Radar.Application.NewsRisk;

/// <summary>One selecting (strategy, rank, snapshot) fact — frozen into the assessment so the evaluator never re-derives selection.</summary>
public sealed record NewsRiskCandidateSelection(string StrategyName, int Rank, Guid ScoreSnapshotId);

/// <summary>One selected company with EVERY strategy/rank/snapshot that selected it (shared ancestry, never consensus).</summary>
public sealed record NewsRiskCandidate(
    Guid CompanyId,
    string CompanyName,
    string? Ticker,
    IReadOnlyList<NewsRiskCandidateSelection> Selections);

/// <summary>
/// Deterministic candidate traversal over the EXACT spec-176 structured sections (spec 179 §3):
/// <list type="number">
/// <item><see cref="StrategyPurpose.Research"/> sections only — Comparators are excluded;</item>
/// <item>primary first, then remaining Research sections in their existing configured order;</item>
/// <item>the first five evidence-linked rows per section, in existing rank order (rows are already
/// evidence-linked and already ranked by the report builder — nothing is re-ranked here);</item>
/// <item>dedupe by company id, RETAINING every selecting strategy/rank/snapshot;</item>
/// <item>stop at the cost budget in traversal order.</item>
/// </list>
/// This is a COST BUDGET, not a merged rank: no consensus count, average rank, Borda score or
/// cross-strategy score comparison is created, and repeated selection by related arms is recorded as shared
/// ancestry only.
/// </summary>
public static class NewsRiskCandidateSelector
{
    /// <summary>Rows taken per Research section, in existing rank order (spec 179 §3).</summary>
    public const int RowsPerSection = 5;

    public static IReadOnlyList<NewsRiskCandidate> Select(
        IReadOnlyList<StrategyReportSection> sections, int maxCompaniesPerRun)
    {
        ArgumentNullException.ThrowIfNull(sections);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxCompaniesPerRun, 1);

        // Primary first, then the remaining Research sections in their existing order — the same stable
        // partition the report builder itself uses (never a sort).
        var research = sections.Where(s => s.Purpose == StrategyPurpose.Research).ToList();
        var ordered = new List<StrategyReportSection>(research.Count);
        ordered.AddRange(research.Where(s => s.IsPrimary));
        ordered.AddRange(research.Where(s => !s.IsPrimary));

        var byCompany = new Dictionary<Guid, (string Name, string? Ticker, List<NewsRiskCandidateSelection> Selections)>();
        var traversalOrder = new List<Guid>();

        foreach (var section in ordered)
        {
            foreach (var row in section.Rows.Take(RowsPerSection))
            {
                if (byCompany.TryGetValue(row.CompanyId, out var existing))
                {
                    // Already selected: retain this section's selection provenance; the cap never bites
                    // here because no NEW company is added.
                    existing.Selections.Add(
                        new NewsRiskCandidateSelection(section.StrategyName, row.Rank, row.ScoreSnapshotId));
                    continue;
                }

                if (traversalOrder.Count >= maxCompaniesPerRun)
                {
                    // The budget caps NEW companies in traversal order; later sections may still attach
                    // provenance to companies already inside it (the branch above).
                    continue;
                }

                byCompany[row.CompanyId] = (
                    row.CompanyName,
                    row.Ticker,
                    [new NewsRiskCandidateSelection(section.StrategyName, row.Rank, row.ScoreSnapshotId)]);
                traversalOrder.Add(row.CompanyId);
            }
        }

        return traversalOrder
            .Select(id =>
            {
                var (name, ticker, selections) = byCompany[id];
                return new NewsRiskCandidate(id, name, ticker, selections);
            })
            .ToList();
    }
}
