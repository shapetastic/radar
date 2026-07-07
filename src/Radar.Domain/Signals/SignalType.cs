namespace Radar.Domain.Signals;

public enum SignalType
{
    CustomerWin,
    StrategicPartnership,
    ExecutiveHire,
    ProductLaunch,
    CapitalRaise,
    GuidanceChange,
    GovernmentContract,
    HiringExpansion,
    // HiringActivity (spec 103): the ATS job-board collector's hiring axis. Deliberately a NEW, honest name —
    // it records hiring ACTIVITY (an open-role snapshot), not a proven surge. The pre-existing HiringExpansion
    // member above stays reserved/untouched (referenced only by the schema spec; no extractor rule/collector).
    // SignalType is persisted by name, so placement here (adjacent to the hiring axis) is readability-only.
    HiringActivity,
    InsiderBuying,
    InstitutionalOwnership,
    PatentActivity,
    DeveloperAdoption,
    MediaAttention,
    Other
}
