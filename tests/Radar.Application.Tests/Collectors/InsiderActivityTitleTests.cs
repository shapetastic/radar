using System.Globalization;

using Radar.Application.Collectors;
using Radar.Domain.Signals;

namespace Radar.Application.Tests.Collectors;

/// <summary>
/// Spec 224 amendment — <see cref="InsiderActivityTitle"/> is the ONE definition of the Form 4 evidence text
/// shapes: <see cref="InsiderActivityTitle.Compose"/> writes them (byte-identical to the collector's
/// pre-extraction text, because evidence identity is the title+body hash) and
/// <see cref="InsiderActivityTitle.TryParseOwner"/> reads the owner back out of a stored title. The anonymous
/// placeholder and every unknown shape read as "not recorded", never a guess, and nothing throws.
/// </summary>
public sealed class InsiderActivityTitleTests
{
    [Fact]
    public void AnonymousOwner_IsPinnedByteExact()
    {
        Assert.Equal("An insider", InsiderActivityTitle.AnonymousOwner);
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    [InlineData("fr-FR")]
    public void Compose_AllThreeShapes_AreByteExact_InvariantN0_InAnyCulture(string culture)
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo(culture);

            var purchase = InsiderActivityTitle.Compose(
                SignalDirection.Positive, "Yeh Jenny C", 12_840m, 2_481_000.4m, "2026-07-07", "0001-26-000001");
            Assert.Equal(
                "Form 4 — insider open-market purchase: Yeh Jenny C bought 12,840 shares (~$2,481,000) (2026-07-07)",
                purchase.Title);
            Assert.Equal(
                "Form 4 accession 0001-26-000001 filed 2026-07-07: insider open-market purchase — Yeh Jenny C "
                    + "bought 12,840 shares (~$2,481,000).",
                purchase.RawText);

            var sale = InsiderActivityTitle.Compose(
                SignalDirection.Negative, "STANG ERIC B", 23_212m, 500_000m, "2026-07-13", "0001-26-000002");
            Assert.Equal(
                "Form 4 — insider open-market sale: STANG ERIC B sold 23,212 shares (~$500,000) (2026-07-13)",
                sale.Title);
            Assert.Equal(
                "Form 4 accession 0001-26-000002 filed 2026-07-13: insider open-market sale — STANG ERIC B sold "
                    + "23,212 shares (~$500,000).",
                sale.RawText);

            var routine = InsiderActivityTitle.Compose(
                SignalDirection.Neutral, "Mann Russell", 1_000m, 5_000m, "2026-07-09", "0001-26-000003");
            Assert.Equal("Form 4 — insider stock transaction (routine): Mann Russell (2026-07-09)", routine.Title);
            Assert.Equal(
                "Form 4 accession 0001-26-000003 filed 2026-07-09: insider stock transaction (routine) — Mann Russell.",
                routine.RawText);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Compose_BlankOwner_WritesThePlaceholder(string? owner)
    {
        var (title, rawText) = InsiderActivityTitle.Compose(
            SignalDirection.Neutral, owner, 0m, 0m, "2026-07-09", "acc");

        Assert.Equal("Form 4 — insider stock transaction (routine): An insider (2026-07-09)", title);
        Assert.Equal("Form 4 accession acc filed 2026-07-09: insider stock transaction (routine) — An insider.", rawText);
    }

    [Fact]
    public void Compose_NonBlankOwner_IsWrittenVerbatim_Untrimmed()
    {
        // The collector has always interpolated the reader's name as-is; trimming here would move identity.
        var (title, _) = InsiderActivityTitle.Compose(SignalDirection.Neutral, " Mann Russell ", 0m, 0m, "d", "a");

        Assert.Equal("Form 4 — insider stock transaction (routine):  Mann Russell  (d)", title);
        Assert.Equal("Mann Russell", InsiderActivityTitle.TryParseOwner(title));
    }

    [Theory]
    [InlineData(SignalDirection.Positive)]
    [InlineData(SignalDirection.Negative)]
    [InlineData(SignalDirection.Neutral)]
    public void TryParseOwner_IsTheInverseOfCompose_ForEveryShape(SignalDirection direction)
    {
        foreach (var owner in new[] { "STANG ERIC B", "Abrahamsen Danielle Nicole", "Smith John (Trust)", "O'Neil J. Jr." })
        {
            var (title, _) = InsiderActivityTitle.Compose(direction, owner, 1_234m, 56_789m, "2026-06-26", "acc");
            Assert.Equal(owner, InsiderActivityTitle.TryParseOwner(title));
        }
    }

    [Theory]
    [InlineData(SignalDirection.Positive)]
    [InlineData(SignalDirection.Negative)]
    [InlineData(SignalDirection.Neutral)]
    public void TryParseOwner_ThePlaceholder_IsNeverAnIdentity(SignalDirection direction)
    {
        var (title, _) = InsiderActivityTitle.Compose(direction, null, 1m, 1m, "2026-06-26", "acc");

        Assert.Null(InsiderActivityTitle.TryParseOwner(title));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Untitled")]
    [InlineData("Form 4 insider filing")]
    [InlineData("Form 4 — insider open-market sale: STANG ERIC B sold 1,000 shares")]
    [InlineData("Form 4 — insider open-market sale: STANG ERIC B bought 1,000 shares (~$5) (2026-01-01)")]
    [InlineData("Form 4 — insider open-market purchase:  bought 1,000 shares (~$5) (2026-01-01)")]
    [InlineData("Form 4 — insider stock transaction (routine): (2026-01-01)")]
    [InlineData("Form 4 — insider stock transaction (routine): STANG ERIC B (2026-01-01) trailing")]
    [InlineData("8-K — insider open-market sale: STANG ERIC B sold 1,000 shares (~$5) (2026-01-01)")]
    [InlineData("form 4 — insider open-market sale: STANG ERIC B sold 1,000 shares (~$5) (2026-01-01)")]
    public void TryParseOwner_AnUnknownShape_IsNotRecorded_NeverThrows(string? title)
    {
        Assert.Null(InsiderActivityTitle.TryParseOwner(title));
    }
}
