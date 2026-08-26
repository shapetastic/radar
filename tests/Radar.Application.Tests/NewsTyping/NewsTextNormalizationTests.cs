using Radar.Application.NewsTyping;

namespace Radar.Application.Tests.NewsTyping;

/// <summary>
/// Spec 191 §1 extracted the fact layer's statement normalization into the shared
/// <see cref="NewsTextNormalization"/> so the observation ↔ evidence join uses the SAME primitive rather
/// than a second copy. These tests pin that the extraction was behaviour-preserving: the shared rule and
/// <see cref="FactFamilyBuilder.NormalizeStatement"/> agree byte-for-byte, and the builder's
/// <see cref="FactFamilyBuilder.IdentityString"/> — the stage-2 judgment cohort discriminator — did not
/// move. A change here re-keys every accrued fact family and re-judges every company.
/// </summary>
public sealed class NewsTextNormalizationTests
{
    [Fact]
    public void Version_IsThePinnedRuleIdentity_AndTheBuilderReExportsIt()
    {
        Assert.Equal("statement-normalization-v1", NewsTextNormalization.Version);
        Assert.Equal(NewsTextNormalization.Version, FactFamilyBuilder.NormalizationVersion);
    }

    [Fact]
    public void FactFamilyBuilderIdentityString_IsByteIdenticalAfterTheExtraction()
    {
        // The literal, spelled out rather than composed, exactly as FactFamilyBuilderTests pins it. Spec 191
        // must not move it: it composes the stage-2 cohort key.
        Assert.Equal(
            "fact-family-v2|normalization=statement-normalization-v1|similarity=token-set-jaccard"
                + "|threshold=0.6|temporalWindowDays=7|segmentation=full-history"
                + "|anchor=first-member-utc-date+event-types|projection=window-members",
            FactFamilyBuilder.IdentityString);
    }

    [Theory]
    // Lowercasing + punctuation-to-space + empty-entry removal.
    [InlineData("Acme Corp. announces Q3 results!", "acme corp announces q3 results")]
    // Digits survive (the contradiction rule depends on them).
    [InlineData("revenue up 12%", "revenue up 12")]
    // Negation tokens survive.
    [InlineData("Acme does NOT expect a recall", "acme does not expect a recall")]
    // Non-ASCII letters are NOT letters/digits under char.IsAsciiLetterOrDigit, so they become spaces.
    [InlineData("Café — Zürich", "caf z rich")]
    public void Normalize_MatchesTheFactLayersRule(string input, string expected)
    {
        Assert.Equal(expected, NewsTextNormalization.Normalize(input));
        Assert.Equal(
            NewsTextNormalization.Normalize(input), FactFamilyBuilder.NormalizeStatement(input));
    }

    [Fact]
    public void Normalize_IsATokenSetJoin_SoARepeatedTokenAppearsOnce()
    {
        // DELIBERATE and load-bearing: the rule joins a HashSet, so a repeated token appears exactly once.
        // Every accrued family id was derived from this string — "fixing" it would re-key them all.
        Assert.Equal("alpha beta", NewsTextNormalization.Normalize("alpha beta alpha"));
        Assert.Equal(2, NewsTextNormalization.Tokens("alpha beta alpha").Count);
    }

    [Fact]
    public void Normalize_IsNotOrderInsensitive_FirstOccurrenceOrderSurvives()
    {
        // Recorded because it is easy to assume otherwise from "token SET": HashSet<string> enumerates in
        // first-insertion order for an add-only set, so two orderings of the same tokens normalize to
        // DIFFERENT strings. The spec-191 join is unaffected — in production the observation headline and
        // the evidence title are the same source string — but a caller must not rely on order-insensitivity.
        Assert.Equal("alpha beta", NewsTextNormalization.Normalize("alpha beta"));
        Assert.Equal("beta alpha", NewsTextNormalization.Normalize("beta alpha"));
    }

    [Fact]
    public void Normalize_BlankAndPunctuationOnlyInput_YieldsAnEmptyKey()
    {
        // The join treats an empty key as "never joins" (fail-closed), so this is the case that matters.
        Assert.Equal(string.Empty, NewsTextNormalization.Normalize(string.Empty));
        Assert.Equal(string.Empty, NewsTextNormalization.Normalize("   "));
        Assert.Equal(string.Empty, NewsTextNormalization.Normalize("—  ---  ***"));
    }

    [Fact]
    public void Tokens_AreOrdinal()
    {
        var tokens = NewsTextNormalization.Tokens("Alpha alpha ALPHA");

        Assert.Equal(["alpha"], tokens);
    }
}
