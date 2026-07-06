using System.Text.Json;

using Radar.Infrastructure.Sec;

namespace Radar.Infrastructure.Tests.Sec;

public sealed class SecRecentFilingsTests
{
    // A mixed columnar filings.recent block: forms 4 / 8-K / SC 13D / SC 13G / 4, newest-first. Row 3
    // (index 3, "SC 13D") has a blank accession and row 4 ("bad-acceptance 4") has an unparseable acceptance,
    // so both are skipped by the flattener regardless of predicate.
    private const string MixedRecent = """
        {
          "form": ["4", "8-K", "SC 13G", "SC 13D", "4", "SC 13D/A"],
          "filingDate": ["2026-06-06", "2026-06-05", "2026-06-04", "2026-06-03", "2026-06-02", "2026-06-01"],
          "acceptanceDateTime": ["2026-06-06T20:00:00.000Z", "2026-06-05T16:30:00.000Z", "2026-06-04T18:00:00.000Z", "2026-06-03T18:00:00.000Z", "not-a-date", "2026-06-01T09:00:00.000Z"],
          "accessionNumber": ["acc-4a", "acc-8k", "acc-13g", "", "acc-4b", "acc-13da"],
          "primaryDocument": ["form4.xml", "doc.htm", "sc13g.htm", "sc13d.htm", "form4b.xml", "sc13da.htm"]
        }
        """;

    private static JsonElement Recent(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    [Fact]
    public void Flatten_FiltersByPredicate_NewestFirst_SkipsBadRows()
    {
        var recent = Recent(MixedRecent);

        // 13D/13G predicate: SC 13G (index 2), SC 13D at index 3 (blank accession → skipped),
        // SC 13D/A (index 5). Only the two well-formed rows survive.
        var rows = SecRecentFilings.Flatten(
            recent, Sec13DGFormType.IsBeneficialOwnershipForm, maxRows: 20, CancellationToken.None);

        Assert.Equal(2, rows.Count);
        Assert.Equal("acc-13g", rows[0].Accession);   // newest-first order preserved
        Assert.Equal("SC 13G", rows[0].Form);
        Assert.Equal("acc-13da", rows[1].Accession);
        Assert.Equal("SC 13D/A", rows[1].Form);
    }

    [Fact]
    public void Flatten_HonoursMaxRows_TakingNewestFirst()
    {
        var recent = Recent(MixedRecent);

        var rows = SecRecentFilings.Flatten(
            recent, Sec13DGFormType.IsBeneficialOwnershipForm, maxRows: 1, CancellationToken.None);

        var row = Assert.Single(rows);
        Assert.Equal("acc-13g", row.Accession);
    }

    [Fact]
    public void Flatten_ParsesAcceptanceAsUtc()
    {
        var recent = Recent(MixedRecent);

        var rows = SecRecentFilings.Flatten(
            recent, Sec13DGFormType.IsBeneficialOwnershipForm, maxRows: 20, CancellationToken.None);

        Assert.Equal(
            new DateTimeOffset(2026, 6, 4, 18, 0, 0, TimeSpan.Zero), rows[0].AcceptanceDateTimeUtc);
        Assert.Equal(TimeSpan.Zero, rows[0].AcceptanceDateTimeUtc.Offset);
    }

    [Fact]
    public void Flatten_SkipsRowWithBlankAccession()
    {
        var recent = Recent(MixedRecent);

        var rows = SecRecentFilings.Flatten(
            recent, Sec13DGFormType.IsBeneficialOwnershipForm, maxRows: 20, CancellationToken.None);

        // The SC 13D at index 3 had a blank accession — it must not surface.
        Assert.DoesNotContain(rows, r => r.Form == "SC 13D");
    }

    [Fact]
    public void Flatten_SkipsRowWithUnparseableAcceptance()
    {
        var recent = Recent(MixedRecent);

        // form == "4" predicate: index 0 (acc-4a, good) and index 4 (acc-4b, bad acceptance → skipped).
        var rows = SecRecentFilings.Flatten(
            recent, form => string.Equals(form, "4", StringComparison.Ordinal), maxRows: 20, CancellationToken.None);

        var row = Assert.Single(rows);
        Assert.Equal("acc-4a", row.Accession);
    }

    [Fact]
    public void Flatten_Form4Predicate_MatchesOldParseForm4Behaviour()
    {
        // Parity guard for the HttpSecForm4Reader migration: the same columnar shape + form == "4" predicate
        // returns exactly the Form 4 rows (newest-first, blank-accession/unparseable-acceptance skipped) that
        // the old private ParseForm4Rows produced.
        var recent = Recent(MixedRecent);

        var rows = SecRecentFilings.Flatten(
            recent, form => string.Equals(form, "4", StringComparison.Ordinal), maxRows: 20, CancellationToken.None);

        var row = Assert.Single(rows); // acc-4b skipped (bad acceptance); acc-4a survives
        Assert.Equal("acc-4a", row.Accession);
        Assert.Equal("2026-06-06", row.FilingDate);
        Assert.Equal("form4.xml", row.PrimaryDocument);
        Assert.Equal("4", row.Form);
    }

    [Fact]
    public void Flatten_MissingColumns_ReturnsEmpty()
    {
        // A recent block with no arrays at all yields no rows (never throws).
        var rows = SecRecentFilings.Flatten(
            Recent("{}"), _ => true, maxRows: 20, CancellationToken.None);

        Assert.Empty(rows);
    }
}
