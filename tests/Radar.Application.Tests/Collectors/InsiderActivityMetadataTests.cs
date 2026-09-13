using Radar.Application.Collectors;
using Radar.Domain.Evidence;
using Radar.TestSupport;

namespace Radar.Application.Tests.Collectors;

/// <summary>
/// Spec 209 — the ONE shared Application-level contract for insider (Form 4) evidence metadata: the keys
/// Infrastructure writes through, the closed set of classification tokens (persisted data, pinned
/// byte-exact), and the defensive <see cref="InsiderActivityMetadata.TryRead"/> projection whose every
/// <c>null</c> means "not captured", never a default.
/// </summary>
public sealed class InsiderActivityMetadataTests
{
    private static EvidenceItem Evidence(string? metadataJson) =>
        new EvidenceBuilder().WithMetadataJson(metadataJson).Build();

    private static string Form4Envelope(params (string Key, string Value)[] extra)
    {
        var pairs = new List<string> { "\"form\":\"4\"" };
        pairs.AddRange(extra.Select(kv => $"\"{kv.Key}\":\"{kv.Value}\""));
        return "{\"metadata\":{" + string.Join(",", pairs) + "},\"companyHints\":[]}";
    }

    // --- the persisted tokens and keys: byte-exact pins (renaming any would orphan accrued evidence) ---

    [Fact]
    public void ClassificationTokens_ArePinnedByteExact()
    {
        Assert.Equal("plan-10b5-1", InsiderActivityMetadata.Plan10b51);
        Assert.Equal("discretionary-buy", InsiderActivityMetadata.DiscretionaryBuy);
        Assert.Equal("discretionary-sale", InsiderActivityMetadata.DiscretionarySale);
        Assert.Equal("mixed-buy-sell", InsiderActivityMetadata.MixedBuySell);
        Assert.Equal("no-discretionary-transactions", InsiderActivityMetadata.NoDiscretionaryTransactions);
        Assert.Equal(
            ["plan-10b5-1", "discretionary-buy", "discretionary-sale", "mixed-buy-sell",
                "no-discretionary-transactions"],
            InsiderActivityMetadata.AllClassificationReasons);
    }

    [Fact]
    public void MetadataKeys_ArePinnedByteExact()
    {
        Assert.Equal("insiderClassificationReason", InsiderActivityMetadata.ClassificationReasonKey);
        Assert.Equal("insiderNetValue", InsiderActivityMetadata.NetValueKey);
        Assert.Equal("insiderCluster", InsiderActivityMetadata.ClusterKey);
        Assert.Equal("insiderDirection", InsiderActivityMetadata.DirectionKey);
        Assert.Equal("form", InsiderActivityMetadata.FormKey);
        Assert.Equal("filingDate", InsiderActivityMetadata.FilingDateKey);
        Assert.Equal("4", InsiderActivityMetadata.Form4);
    }

    // --- TryRead: not a Form 4 ---

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    [InlineData("{\"metadata\":{\"quality\":\"High\"},\"companyHints\":[]}")]
    [InlineData("{\"metadata\":{\"form\":\"8-K\",\"filingDate\":\"2026-08-05\"},\"companyHints\":[]}")]
    [InlineData("{\"metadata\":{\"form\":\"4/A\"},\"companyHints\":[]}")]
    public void TryRead_NotAForm4OrUnreadable_ReturnsNull(string? metadataJson)
    {
        Assert.Null(InsiderActivityMetadata.TryRead(Evidence(metadataJson)));
    }

    [Fact]
    public void TryRead_NullEvidence_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => InsiderActivityMetadata.TryRead(null!));
    }

    // --- TryRead: each token round-trips verbatim ---

    [Theory]
    [InlineData(InsiderActivityMetadata.Plan10b51)]
    [InlineData(InsiderActivityMetadata.DiscretionaryBuy)]
    [InlineData(InsiderActivityMetadata.DiscretionarySale)]
    [InlineData(InsiderActivityMetadata.MixedBuySell)]
    [InlineData(InsiderActivityMetadata.NoDiscretionaryTransactions)]
    public void TryRead_Form4WithToken_ReturnsTheTokenVerbatim(string token)
    {
        var read = InsiderActivityMetadata.TryRead(
            Evidence(Form4Envelope(("insiderClassificationReason", token))));

        Assert.NotNull(read);
        Assert.Equal(token, read.ClassificationReason);
    }

    [Fact]
    public void TryRead_UnrecognisedToken_IsReturnedNotDropped()
    {
        var read = InsiderActivityMetadata.TryRead(
            Evidence(Form4Envelope(("insiderClassificationReason", "something-new"))));

        Assert.NotNull(read);
        Assert.Equal("something-new", read.ClassificationReason);
        Assert.DoesNotContain("something-new", InsiderActivityMetadata.AllClassificationReasons);
    }

    [Theory]
    [InlineData(false, "")]
    [InlineData(true, "")]
    [InlineData(true, "   ")]
    public void TryRead_LegacyForm4WithoutToken_ClassificationReasonIsNull(bool keyPresent, string value)
    {
        var json = keyPresent
            ? Form4Envelope(("insiderClassificationReason", value), ("filingDate", "2026-06-12"))
            : Form4Envelope(("filingDate", "2026-06-12"));

        var read = InsiderActivityMetadata.TryRead(Evidence(json));

        Assert.NotNull(read);
        Assert.Null(read.ClassificationReason);
        Assert.Equal(new DateOnly(2026, 6, 12), read.FilingDate);
    }

    // --- TryRead: the spec-224 owner identity and the cluster flag ---

    [Fact]
    public void OwnerKeys_ArePinnedByteExact()
    {
        Assert.Equal("insiderOwnerName", InsiderActivityMetadata.OwnerNameKey);
        Assert.Equal("insiderOwnerCik", InsiderActivityMetadata.OwnerCikKey);
    }

    [Fact]
    public void TryRead_OwnerNameAndCik_AreTrimmed()
    {
        var read = InsiderActivityMetadata.TryRead(Evidence(Form4Envelope(
            ("insiderOwnerName", "  STANG ERIC B "), ("insiderOwnerCik", " 0001234567 "))));

        Assert.NotNull(read);
        Assert.Equal("STANG ERIC B", read.OwnerName);
        Assert.Equal("0001234567", read.OwnerCik);
    }

    [Theory]
    [InlineData(false, "")]
    [InlineData(true, "")]
    [InlineData(true, "   ")]
    public void TryRead_NoOwnerKeys_AndATitleInNoCollectorShape_OwnerIsNotRecorded(bool keyPresent, string value)
    {
        var json = keyPresent
            ? Form4Envelope(("insiderOwnerName", value), ("insiderOwnerCik", value))
            : Form4Envelope();

        // The builder's default title ("Untitled") is no Form 4 shape, so the fallback recovers nothing.
        var read = InsiderActivityMetadata.TryRead(Evidence(json));

        Assert.NotNull(read);
        Assert.Null(read.OwnerName);
        Assert.Null(read.OwnerCik);
        Assert.Equal(InsiderOwnerSource.NotRecorded, read.OwnerSource);
    }

    [Fact]
    public void TryRead_OwnerNameKey_IsSourcedFromMetadata()
    {
        var read = InsiderActivityMetadata.TryRead(Evidence(Form4Envelope(("insiderOwnerName", "STANG ERIC B"))));

        Assert.NotNull(read);
        Assert.Equal(InsiderOwnerSource.Metadata, read.OwnerSource);
    }

    // --- TryRead: the spec-224 amendment's title fallback for accrued pre-224 evidence ---

    [Theory]
    [InlineData("Form 4 — insider open-market sale: STANG ERIC B sold 27,666 shares (~$1,500,000) (2026-06-26)", "STANG ERIC B")]
    [InlineData("Form 4 — insider open-market purchase: Yeh Jenny C bought 12,840 shares (~$250,000) (2026-07-07)", "Yeh Jenny C")]
    [InlineData("Form 4 — insider stock transaction (routine): WEINER LEIGH R (2012-12-20)", "WEINER LEIGH R")]
    public void TryRead_LegacyForm4WithoutOwnerKeys_RecoversTheOwnerFromTheTitle(string title, string owner)
    {
        // The exact envelope an accrued pre-224 Form 4 carries: no owner keys at all.
        var evidence = new EvidenceBuilder()
            .WithTitle(title)
            .WithMetadataJson(Form4Envelope(("insiderClassificationReason", "discretionary-sale")))
            .Build();

        var read = InsiderActivityMetadata.TryRead(evidence);

        Assert.NotNull(read);
        Assert.Equal(owner, read.OwnerName);
        Assert.Null(read.OwnerCik); // a title carries no CIK
        Assert.Equal(InsiderOwnerSource.Title, read.OwnerSource);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TryRead_BlankOrAbsentOwnerKey_FallsBackToTheTitle(string? keyValue)
    {
        var json = keyValue is null ? Form4Envelope() : Form4Envelope(("insiderOwnerName", keyValue));
        var evidence = new EvidenceBuilder()
            .WithTitle("Form 4 — insider open-market sale: STANG ERIC B sold 5 shares (~$9) (2026-06-26)")
            .WithMetadataJson(json)
            .Build();

        var read = InsiderActivityMetadata.TryRead(evidence);

        Assert.NotNull(read);
        Assert.Equal("STANG ERIC B", read.OwnerName);
        Assert.Equal(InsiderOwnerSource.Title, read.OwnerSource);
    }

    [Fact]
    public void TryRead_StructuredMetadata_AlwaysWinsOverTheTitle()
    {
        var evidence = new EvidenceBuilder()
            .WithTitle("Form 4 — insider open-market sale: SOMEONE ELSE sold 5 shares (~$9) (2026-06-26)")
            .WithMetadataJson(Form4Envelope(("insiderOwnerName", "STANG ERIC B"), ("insiderOwnerCik", "0001234567")))
            .Build();

        var read = InsiderActivityMetadata.TryRead(evidence);

        Assert.NotNull(read);
        Assert.Equal("STANG ERIC B", read.OwnerName);
        Assert.Equal("0001234567", read.OwnerCik);
        Assert.Equal(InsiderOwnerSource.Metadata, read.OwnerSource);
    }

    [Fact]
    public void TryRead_CikKeyWithoutNameKey_TakesTheNameFromTheTitle_AndTheCikFromMetadata()
    {
        var evidence = new EvidenceBuilder()
            .WithTitle("Form 4 — insider open-market sale: STANG ERIC B sold 5 shares (~$9) (2026-06-26)")
            .WithMetadataJson(Form4Envelope(("insiderOwnerCik", "0001234567")))
            .Build();

        var read = InsiderActivityMetadata.TryRead(evidence);

        Assert.NotNull(read);
        Assert.Equal("STANG ERIC B", read.OwnerName);
        Assert.Equal("0001234567", read.OwnerCik);
        Assert.Equal(InsiderOwnerSource.Title, read.OwnerSource);
    }

    [Theory]
    [InlineData("Form 4 — insider open-market sale: An insider sold 5 shares (~$9) (2026-06-26)")]
    [InlineData("Form 4 — insider stock transaction (routine): An insider (2026-06-26)")]
    public void TryRead_TitleNamingOnlyTheAnonymousPlaceholder_IsNotRecorded(string title)
    {
        var evidence = new EvidenceBuilder().WithTitle(title).WithMetadataJson(Form4Envelope()).Build();

        var read = InsiderActivityMetadata.TryRead(evidence);

        Assert.NotNull(read);
        Assert.Null(read.OwnerName);
        Assert.Equal(InsiderOwnerSource.NotRecorded, read.OwnerSource);
    }

    [Fact]
    public void TryRead_ATitleInTheCollectorShape_OnANonForm4Envelope_IsStillNotAForm4()
    {
        var evidence = new EvidenceBuilder()
            .WithTitle("Form 4 — insider open-market sale: STANG ERIC B sold 5 shares (~$9) (2026-06-26)")
            .WithMetadataJson("{\"metadata\":{\"form\":\"8-K\"},\"companyHints\":[]}")
            .Build();

        Assert.Null(InsiderActivityMetadata.TryRead(evidence));
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("TRUE", true)]
    [InlineData(" 1 ", true)]
    [InlineData("false", false)]
    [InlineData("0", false)]
    [InlineData("", false)]
    public void TryRead_ClusterFlag_FollowsTheExtractorsRule(string value, bool expected)
    {
        var read = InsiderActivityMetadata.TryRead(Evidence(Form4Envelope(("insiderCluster", value))));

        Assert.NotNull(read);
        Assert.Equal(expected, read.HasCluster);
    }

    [Fact]
    public void TryRead_ClusterKeyAbsent_IsFalse()
    {
        var read = InsiderActivityMetadata.TryRead(Evidence(Form4Envelope()));

        Assert.NotNull(read);
        Assert.False(read.HasCluster);
    }

    // --- TryRead: the captured value ---

    [Fact]
    public void TryRead_NetValue_ParsesInvariantCulture()
    {
        var read = InsiderActivityMetadata.TryRead(
            Evidence(Form4Envelope(("insiderNetValue", "3313222.5"))));

        Assert.NotNull(read);
        Assert.Equal(3313222.5m, read.NetValue);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("1,2,3,4x")]
    public void TryRead_NetValueAbsentOrUnparseable_IsNullNeverZero(string? value)
    {
        var json = value is null ? Form4Envelope() : Form4Envelope(("insiderNetValue", value));

        var read = InsiderActivityMetadata.TryRead(Evidence(json));

        Assert.NotNull(read);
        Assert.Null(read.NetValue);
    }

    // --- TryRead: the filing date ---

    [Fact]
    public void TryRead_FilingDate_ParsesYyyyMmDd()
    {
        var read = InsiderActivityMetadata.TryRead(Evidence(Form4Envelope(("filingDate", "2026-08-05"))));

        Assert.NotNull(read);
        Assert.Equal(new DateOnly(2026, 8, 5), read.FilingDate);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("05/08/2026")]
    [InlineData("2026-8-5")]
    [InlineData("not-a-date")]
    public void TryRead_FilingDateAbsentOrNotIsoDate_IsNull(string? value)
    {
        var json = value is null ? Form4Envelope() : Form4Envelope(("filingDate", value));

        var read = InsiderActivityMetadata.TryRead(Evidence(json));

        Assert.NotNull(read);
        Assert.Null(read.FilingDate);
    }

    [Fact]
    public void TryRead_FullProductionShape_ReadsEveryField()
    {
        // The exact shape SecForm4Collector.MapToEvidence writes for a discretionary sale.
        const string json =
            "{\"metadata\":{\"quality\":\"High\",\"secFeedUrl\":\"https://data.sec.gov/submissions/CIK0000100.json\","
            + "\"accessionNumber\":\"0001-26-000001\",\"form\":\"4\",\"filingDate\":\"2026-08-04\","
            + "\"insiderDirection\":\"Negative\",\"insiderClassificationReason\":\"discretionary-sale\","
            + "\"insiderNetValue\":\"3313222\"},\"companyHints\":[\"AGX\"]}";

        var read = InsiderActivityMetadata.TryRead(Evidence(json));

        Assert.Equal(
            new InsiderActivityRead(
                InsiderActivityMetadata.DiscretionarySale, 3313222m, new DateOnly(2026, 8, 4)),
            read);
    }
}
