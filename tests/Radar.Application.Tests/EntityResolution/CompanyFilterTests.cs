using Radar.Application.EntityResolution;

namespace Radar.Application.Tests.EntityResolution;

/// <summary>
/// Spec 161 — <see cref="CompanyFilter"/>: the canonicalisation of <c>Radar:Companies</c> and its two
/// fail-fast rules. Whether a ticker actually NAMES a seed company is the decorator's job (the first place
/// the seed is known); this type owns the shape of the list itself.
/// </summary>
public sealed class CompanyFilterTests
{
    [Fact]
    public void Canonicalises_ByTrimmingAndUpperCasing_PreservingConfiguredOrder()
    {
        var filter = CompanyFilter.FromTickers([" idt ", "cass"]);

        // Configured order is preserved (AD-3): the filter is not silently re-sorted.
        Assert.Equal(["IDT", "CASS"], filter.Tickers);
    }

    [Fact]
    public void CollapsesDuplicates_AfterCanonicalisation()
    {
        // "cass", "CASS" and " Cass " are the same ticker; de-duping AFTER canonicalisation is what makes
        // that true, and the first occurrence keeps its position.
        var filter = CompanyFilter.FromTickers(["cass", "IDT", "CASS", " Cass "]);

        Assert.Equal(["CASS", "IDT"], filter.Tickers);
    }

    [Fact]
    public void Describe_IsTheCommaSeparatedCanonicalList()
    {
        Assert.Equal("CASS,IDT", CompanyFilter.FromTickers(["cass", "idt"]).Describe());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankEntry_FailsFast_NamingTheConfigKey(string? blank)
    {
        // Fail fast, never fail open: silently dropping the entry is how a typo becomes a run that "worked"
        // and collected nothing.
        var ex = Assert.Throws<InvalidOperationException>(
            () => CompanyFilter.FromTickers(["CASS", blank]));

        Assert.Contains("Radar:Companies", ex.Message, StringComparison.Ordinal);
        // Same shape as the existing Radar:Collectors blank-entry message.
        Assert.Contains("must not be null, empty, or whitespace", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EmptyList_FailsFast_RatherThanResolvingToAnEmptyFilter()
    {
        // Unreachable through the composition root (it only builds a filter for a non-empty list) — the
        // guard is asserted anyway, because an empty filter would silently collect nothing.
        var ex = Assert.Throws<InvalidOperationException>(() => CompanyFilter.FromTickers([]));

        Assert.Contains("Radar:Companies", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Tickers_AreHandedOutReadOnly()
    {
        var filter = CompanyFilter.FromTickers(["CASS"]);

        // A process-lifetime singleton must not hand out a mutable backing array.
        Assert.Throws<NotSupportedException>(() => ((IList<string>)filter.Tickers)[0] = "MUTATED");
    }

    [Fact]
    public void NullEnumerable_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => CompanyFilter.FromTickers(null!));
    }
}
