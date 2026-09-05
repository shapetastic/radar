namespace Radar.Application.Reporting;

using Radar.Domain.Evidence;
using Radar.Domain.Signals;

/// <summary>
/// One contributing signal behind a company entry (provenance for the "why noticed" block, and — since
/// spec 210 — the support the action policy's corroboration floor names when it counts this signal's type).
/// <para>
/// The three trailing members are provenance the floor's rationale renders and are NULLABLE and DEFAULTED
/// by design: <c>null</c> means <b>not recorded</b>, never a silent <c>false</c>/default. A missing
/// observation date renders as "date unknown", a missing source class as "source unknown" and a missing
/// judgment flag as "judgment unknown" — never as "not judgment-derived". Trailing AND defaulted so every
/// positional construction site that predates spec 210 compiles unchanged.
/// </para>
/// </summary>
/// <param name="ObservedAtUtc">The signal's <see cref="Signal.ObservedAtUtc"/>; null = not recorded.</param>
/// <param name="SourceType">
/// The CANONICAL <see cref="EvidenceSourceType"/> of the evidence the signal cites (never an informal
/// class); null = the evidence was not loaded or the ref was built without it.
/// </param>
/// <param name="IsJudgmentDerived">
/// Whether the signal is a judgment-derived news signal per the ONE parser
/// (<c>NewsDirectionalSignalMetadata.IsJudgmentDerived</c>); null = not evaluated.
/// </param>
public sealed record ReportSignalRef(
    Guid SignalId,
    SignalType Type,
    SignalDirection Direction,
    string Reason,
    DateTimeOffset? ObservedAtUtc = null,
    EvidenceSourceType? SourceType = null,
    bool? IsJudgmentDerived = null);
