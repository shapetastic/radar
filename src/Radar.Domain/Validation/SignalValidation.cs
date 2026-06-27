using Radar.Domain.Signals;

namespace Radar.Domain.Validation;

public static class SignalValidation
{
    public static IReadOnlyList<string> Validate(Signal signal)
    {
        var errors = new List<string>();
        if (signal.Strength is < 1 or > 10)
            errors.Add("Strength must be between 1 and 10.");
        if (signal.Novelty is < 1 or > 10)
            errors.Add("Novelty must be between 1 and 10.");
        if (signal.Confidence is < 0m or > 1m)
            errors.Add("Confidence must be between 0 and 1.");
        if (string.IsNullOrWhiteSpace(signal.SupportingExcerpt))
            errors.Add("Supporting excerpt must not be empty.");
        if (signal.EvidenceId == Guid.Empty)
            errors.Add("Every signal must reference evidence.");
        return errors;
    }

    public static bool IsValid(Signal signal) => Validate(signal).Count == 0;
}
