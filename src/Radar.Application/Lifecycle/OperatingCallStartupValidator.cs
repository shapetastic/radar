using Radar.Application.Scoring;

namespace Radar.Application.Lifecycle;

/// <summary>
/// Fails a run AT STARTUP on an invalid operating-calls file (spec 184 §2 rule 4), before any collection —
/// mirroring <c>StrategyIdentityGuard</c>'s "a misconfiguration costs no collection" posture. The Worker
/// invokes it as its first step; the report builder later re-runs the SAME <see cref="OperatingCallReducer"/>
/// validation, so a non-Worker composition still cannot render from an invalid file.
/// <para>
/// Deliberately inert (no file read at all) with a single configured strategy — the call layer does not
/// exist there (spec 184 §4) — and inert when no file exists (an undeclared call layer is a stated report
/// condition, not a startup failure).
/// </para>
/// </summary>
public interface IOperatingCallStartupValidator
{
    Task ValidateAsync(CancellationToken ct);
}

/// <inheritdoc />
public sealed class OperatingCallStartupValidator : IOperatingCallStartupValidator
{
    private readonly IOperatingCallSource _source;
    private readonly ScoringStrategySet _strategies;

    public OperatingCallStartupValidator(IOperatingCallSource source, ScoringStrategySet strategies)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(strategies);
        _source = source;
        _strategies = strategies;
    }

    public async Task ValidateAsync(CancellationToken ct)
    {
        if (_strategies.Strategies.Count <= 1)
        {
            return; // single strategy ⇒ the call layer is inert; no file is required or read (spec 184 §4)
        }

        var file = await _source.ReadAsync(ct).ConfigureAwait(false);
        if (file is null)
        {
            return; // undeclared is a rendered report condition, not a startup failure
        }

        OperatingCallReducer.Validate(file, _strategies.Strategies);
    }
}
