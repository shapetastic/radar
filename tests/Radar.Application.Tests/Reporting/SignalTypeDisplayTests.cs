using Radar.Application.Reporting;
using Radar.Domain.Signals;

namespace Radar.Application.Tests.Reporting;

/// <summary>
/// Spec 211 — the ONE shared presentation mapping from a stored <see cref="SignalType"/> to the label the
/// report prints (<see cref="SignalTypeDisplay"/>), used by both the renderer and the action policy. The
/// unit pins on the mapping live here; the render-path proofs (that the renderer and the policy actually
/// route through it) stay in the renderer/policy tests, and <c>SignalTypeDisplayGuardrailTests</c> fails on
/// a second private copy.
/// </summary>
public sealed class SignalTypeDisplayTests
{
    private static readonly string[] ForbiddenWords = ["buy", "sell", "guaranteed", "safe bet", "upside"];

    [Fact]
    public void GuidanceChange_IsLabelled_EarningsTrajectory()
    {
        // Spec 167: the stored token is a taxonomy misnomer (a plain earnings filing / an as-reported
        // trajectory read), so it prints as EarningsTrajectory.
        Assert.Equal("EarningsTrajectory", SignalTypeDisplay.Label(SignalType.GuidanceChange));
    }

    [Fact]
    public void InsiderBuying_IsLabelled_InsiderActivity()
    {
        // Spec 209: the stored member covers every Form 4, including 10b5-1 plan filings whose transaction
        // direction is not read, so it prints as InsiderActivity.
        Assert.Equal("InsiderActivity", SignalTypeDisplay.Label(SignalType.InsiderBuying));
    }

    [Theory]
    [InlineData(SignalType.StrategicPartnership, "StrategicPartnership")]
    [InlineData(SignalType.MediaAttention, "MediaAttention")]
    [InlineData(SignalType.CustomerWin, "CustomerWin")]
    [InlineData(SignalType.Other, "Other")]
    public void EveryOtherMember_PassesThroughAsItsOwnName(SignalType type, string expected)
    {
        Assert.Equal(expected, SignalTypeDisplay.Label(type));
    }

    [Fact]
    public void ExactlyTwoMembers_AreRelabelled_AndEveryOtherMemberIsVerbatim()
    {
        // The closed set of relabels: adding a third must be a deliberate edit to this pin, never a drift.
        foreach (var type in Enum.GetValues<SignalType>())
        {
            var label = SignalTypeDisplay.Label(type);
            if (type is SignalType.GuidanceChange or SignalType.InsiderBuying)
            {
                Assert.NotEqual(type.ToString(), label);
            }
            else
            {
                Assert.Equal(type.ToString(), label);
            }
        }
    }

    [Fact]
    public void NoLabel_ContainsAForbiddenReportLanguageSubstring()
    {
        // The stored InsiderBuying member contains "buy"; its label must not, and no other label may
        // introduce a forbidden substring either (the report-language rule is case-insensitive).
        foreach (var type in Enum.GetValues<SignalType>())
        {
            var label = SignalTypeDisplay.Label(type);
            foreach (var forbidden in ForbiddenWords)
            {
                Assert.DoesNotContain(forbidden, label, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    // --- RewriteStoredProvenance: whole-word InsiderBuying only (spec 209 semantics, unchanged by 211) ---

    [Theory]
    [InlineData("InsiderBuying (Neutral), strength 3, novelty 4", "InsiderActivity (Neutral), strength 3, novelty 4")]
    [InlineData("InsiderBuying", "InsiderActivity")]
    [InlineData("see InsiderBuying", "see InsiderActivity")]
    [InlineData("InsiderBuying (Neutral); InsiderBuying again", "InsiderActivity (Neutral); InsiderActivity again")]
    [InlineData("(InsiderBuying)", "(InsiderActivity)")]
    public void RewriteStoredProvenance_RewritesEveryWholeWordOccurrence_IncludingAtStartAndEnd(
        string stored, string expected)
    {
        Assert.Equal(expected, SignalTypeDisplay.RewriteStoredProvenance(stored));
    }

    [Theory]
    [InlineData("NotInsiderBuyingX (Neutral)")]
    [InlineData("InsiderBuyingX")]
    [InlineData("XInsiderBuying")]
    [InlineData("insiderbuying (Neutral)")]
    public void RewriteStoredProvenance_LeavesNonWholeWordTokens_Verbatim(string stored)
    {
        // The moved spec-209 pin: NotInsiderBuyingX is not the whole word and must not be touched (the
        // render-path row for it stays in MarkdownWeeklyReportInsiderActivityTests).
        Assert.Equal(stored, SignalTypeDisplay.RewriteStoredProvenance(stored));
    }

    [Fact]
    public void RewriteStoredProvenance_LeavesStoredGuidanceChange_Verbatim()
    {
        // Spec 167's stance stands: only the InsiderBuying token is rewritten inside stored text; the
        // GuidanceChange token stays byte-verbatim and the renderer's legend explains it.
        const string stored = "GuidanceChange (Positive), strength 8, confidence 0.90";
        Assert.Equal(stored, SignalTypeDisplay.RewriteStoredProvenance(stored));
    }

    [Fact]
    public void RewriteStoredProvenance_LeavesTextWithoutTheToken_Verbatim()
    {
        const string stored = "CustomerWin (Positive), strength 6";
        Assert.Equal(stored, SignalTypeDisplay.RewriteStoredProvenance(stored));
    }
}
