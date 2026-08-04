using Radar.Application.Efficacy.Attention;
using Radar.Application.Efficacy.Claims;

namespace Radar.Application.Tests.Efficacy.Attention;

/// <summary>
/// The spec-170 screen→prerequisite mapping: TOTAL over every (Availability, ScreenStatus) combination —
/// including the states the evaluator never intends — with every uninterpretable state failing CLOSED as
/// <see cref="Ad16ScreenOutcome.Invalid"/>, never falling through to a Pending-like or satisfied branch.
/// </summary>
public sealed class Ad15AttentionPrerequisiteMapTests
{
    private static AttentionArrivalScreenResult Result(
        AttentionEvaluationAvailability availability, AttentionScreenStatus? status) =>
        AttentionArrivalScreenResult.Unavailable(
            AttentionEvaluationUnavailableReason.CohortConfigurationUnavailable, "test", "newssearch")
        with
        {
            Availability = availability,
            UnavailableReason = availability == AttentionEvaluationAvailability.Unavailable
                ? AttentionEvaluationUnavailableReason.CohortConfigurationUnavailable
                : AttentionEvaluationUnavailableReason.None,
            ScreenStatus = status,
        };

    /// <summary>
    /// EVERY (Availability, ScreenStatus) combination, including null and out-of-range values on both axes.
    /// The mapping's state machine must be total (spec 170 §1.1) — no combination may throw, and none may
    /// satisfy the prerequisite except a calculated Available screen.
    /// </summary>
    public static TheoryData<AttentionEvaluationAvailability, AttentionScreenStatus?, Ad16ScreenOutcome>
        AllCombinations()
    {
        var data = new TheoryData<AttentionEvaluationAvailability, AttentionScreenStatus?, Ad16ScreenOutcome>();

        AttentionScreenStatus?[] statuses =
        [
            null,
            AttentionScreenStatus.Pending,
            AttentionScreenStatus.Miss,
            AttentionScreenStatus.ClearsNecessaryScreen,
            (AttentionScreenStatus)999,
        ];

        foreach (var status in statuses)
        {
            // Available: the status decides; null/unrecognised is INVALID, not Pending-like.
            data.Add(
                AttentionEvaluationAvailability.Available,
                status,
                status switch
                {
                    AttentionScreenStatus.Pending => Ad16ScreenOutcome.Pending,
                    AttentionScreenStatus.Miss => Ad16ScreenOutcome.Miss,
                    AttentionScreenStatus.ClearsNecessaryScreen => Ad16ScreenOutcome.ClearsNecessaryScreen,
                    _ => Ad16ScreenOutcome.Invalid,
                });

            // Unavailable: a configuration failure regardless of any status the record might carry.
            data.Add(AttentionEvaluationAvailability.Unavailable, status, Ad16ScreenOutcome.Unavailable);

            // An availability value the mapper has never heard of: uninterpretable, fails closed.
            data.Add((AttentionEvaluationAvailability)999, status, Ad16ScreenOutcome.Invalid);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(AllCombinations))]
    public void From_IsTotal_AndEveryCombinationMapsToItsDeclaredOutcome(
        AttentionEvaluationAvailability availability,
        AttentionScreenStatus? status,
        Ad16ScreenOutcome expected)
    {
        var prerequisite = Ad15AttentionPrerequisiteMap.From(Result(availability, status));

        Assert.Equal(expected, prerequisite.Outcome);
        Assert.Equal(
            expected is Ad16ScreenOutcome.Miss or Ad16ScreenOutcome.ClearsNecessaryScreen,
            prerequisite.WasCalculated);
    }

    [Fact]
    public void From_AvailableWithNullStatus_IsInvalid_AndDoesNotSatisfyTheGate()
    {
        // The named acceptance case: Available + null status is representable even though the evaluator
        // does not intend it — it must be Invalid, and Invalid must not satisfy the composite gate.
        var prerequisite = Ad15AttentionPrerequisiteMap.From(
            Result(AttentionEvaluationAvailability.Available, status: null));

        Assert.Equal(Ad16ScreenOutcome.Invalid, prerequisite.Outcome);

        var verdict = Ad15ClaimGate.Evaluate(satisfiesPriceGate: true, [], prerequisite);
        Assert.False(verdict.Qualifies);
        Assert.Equal(Ad15GateReasonCodes.Ad16ScreenInvalid, Assert.Single(verdict.Reasons).Code);
    }
}
