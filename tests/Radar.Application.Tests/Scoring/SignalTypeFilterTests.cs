using Radar.Application.Scoring;
using Radar.Domain.Signals;

namespace Radar.Application.Tests.Scoring;

/// <summary>
/// Spec 138 — the canonicalisation that makes a per-strategy signal-type set safe to fold into the
/// fingerprint. The load-bearing property is the FIRST test: the "all types" filter describes its input
/// VERBATIM, which is what keeps the pinned default fingerprints byte-identical.
/// </summary>
public sealed class SignalTypeFilterTests
{
    private static readonly SignalType[] EveryType = Enum.GetValues<SignalType>();

    [Fact]
    public void All_DescribesTheSourceDescriptorVerbatim()
    {
        // The whole no-op guarantee in one assertion: nothing is appended for the default filter, so the
        // engine hashes exactly the descriptor it hashed before spec 138 existed.
        const string descriptor = "rules=radar-keyword-rules-v6;collectors=a,b;";

        Assert.Same(descriptor, SignalTypeFilter.All.Describe(descriptor));
    }

    [Fact]
    public void All_IncludesEveryType()
    {
        Assert.True(SignalTypeFilter.All.IsAll);
        Assert.All(EveryType, t => Assert.True(SignalTypeFilter.All.Includes(t)));
    }

    [Fact]
    public void Create_Null_IsAll()
    {
        Assert.Same(SignalTypeFilter.All, SignalTypeFilter.Create(null));
    }

    [Fact]
    public void Create_Empty_IsAll()
    {
        Assert.Same(SignalTypeFilter.All, SignalTypeFilter.Create([]));
    }

    [Fact]
    public void Create_EveryMemberListedExplicitly_CanonicalisesToAll()
    {
        // The stated acceptance criterion: naming every type must be indistinguishable from naming none, or a
        // config that spells out the full set would silently fork the default fingerprint.
        var exhaustive = SignalTypeFilter.Create(EveryType);

        Assert.Same(SignalTypeFilter.All, exhaustive);
        Assert.Equal("src;", exhaustive.Describe("src;"));
    }

    [Fact]
    public void Create_EveryMemberInReverseOrderWithDuplicates_CanonicalisesToAll()
    {
        var messy = EveryType.Reverse().Concat(EveryType).ToList();

        Assert.Same(SignalTypeFilter.All, SignalTypeFilter.Create(messy));
    }

    [Fact]
    public void Create_ListOrder_DoesNotChangeTheDescriptor()
    {
        var forward = SignalTypeFilter.Create([SignalType.InsiderBuying, SignalType.PatentActivity]);
        var reversed = SignalTypeFilter.Create([SignalType.PatentActivity, SignalType.InsiderBuying]);

        Assert.Equal(forward.Describe("src;"), reversed.Describe("src;"));
        Assert.Equal(forward, reversed);
        Assert.Equal(forward.GetHashCode(), reversed.GetHashCode());
    }

    [Fact]
    public void Create_Duplicates_DoNotChangeTheDescriptor()
    {
        var once = SignalTypeFilter.Create([SignalType.InsiderBuying]);
        var thrice = SignalTypeFilter.Create(
            [SignalType.InsiderBuying, SignalType.InsiderBuying, SignalType.InsiderBuying]);

        Assert.Equal(once.Describe("src;"), thrice.Describe("src;"));
    }

    [Fact]
    public void Describe_ProperSubset_AppendsCanonicalNamesOrderedByEnumValue()
    {
        // Names (not numbers) because SignalType is persisted by name everywhere else in Radar; ordered by the
        // underlying value so config list order is irrelevant and appending a new member never reorders.
        var filter = SignalTypeFilter.Create([SignalType.PatentActivity, SignalType.CustomerWin]);

        Assert.Equal("src;signalTypes=CustomerWin,PatentActivity;", filter.Describe("src;"));
    }

    [Fact]
    public void ProperSubset_IncludesOnlyItsOwnTypes()
    {
        var filter = SignalTypeFilter.Create([SignalType.InsiderBuying]);

        Assert.False(filter.IsAll);
        Assert.True(filter.Includes(SignalType.InsiderBuying));
        Assert.False(filter.Includes(SignalType.CustomerWin));
        Assert.False(filter.Includes(SignalType.Other));
    }

    [Fact]
    public void DifferentSubsets_DescribeDifferently()
    {
        var a = SignalTypeFilter.Create([SignalType.CustomerWin]);
        var ab = SignalTypeFilter.Create([SignalType.CustomerWin, SignalType.ProductLaunch]);

        Assert.NotEqual(a.Describe("src;"), ab.Describe("src;"));
        Assert.NotEqual(a.Describe("src;"), SignalTypeFilter.All.Describe("src;"));
        Assert.NotEqual(ab.Describe("src;"), SignalTypeFilter.All.Describe("src;"));
    }

    [Fact]
    public void Types_RoundTripThroughCreate()
    {
        var filter = SignalTypeFilter.Create([SignalType.MediaAttention, SignalType.CustomerWin]);

        Assert.Equal([SignalType.CustomerWin, SignalType.MediaAttention], filter.Types);
        Assert.Equal(filter, SignalTypeFilter.Create(filter.Types));
        Assert.Same(SignalTypeFilter.All, SignalTypeFilter.Create(SignalTypeFilter.All.Types));
    }

    [Fact]
    public void Types_IsNotCastableBackToAMutableArray()
    {
        // All is a process-wide singleton and every engine holds its filter for the process lifetime, so
        // handing out the backing array behind IReadOnlyList<T> would let one caller corrupt every filter.
        Assert.IsNotType<SignalType[]>(SignalTypeFilter.All.Types);
        Assert.IsNotType<SignalType[]>(SignalTypeFilter.Create([SignalType.InsiderBuying]).Types);
    }

    [Fact]
    public void Create_UndeclaredEnumValue_IsRejected()
    {
        // A numeric cast is the only way an undeclared value can reach Application (Infrastructure rejects
        // numeric config strings outright); hashing one would stamp a strategy that can never match a signal.
        var ex = Assert.Throws<ArgumentException>(() => SignalTypeFilter.Create([(SignalType)9999]));

        Assert.Contains("9999", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ToString_IsCheapAndReadable()
    {
        Assert.Equal("all types", SignalTypeFilter.All.ToString());
        Assert.Equal(
            "CustomerWin,PatentActivity",
            SignalTypeFilter.Create([SignalType.PatentActivity, SignalType.CustomerWin]).ToString());
    }
}
