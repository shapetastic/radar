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
    // RegulatoryApproval (spec 129): the openFDA 510(k)/PMA device-clearance collector's axis — a Positive,
    // routine-strength corroborating signal (a discrete, market-relevant regulatory gate). Appended before the
    // Other sentinel; SignalType is persisted by name, so placement is readability-only.
    RegulatoryApproval,
    // TrademarkActivity (spec 130): the USPTO trademark-activity collector's axis — a Neutral count-based signal
    // (a newly-filed trademark registers a brand/product name before launch). Deliberately Neutral by design: a
    // single-window filing COUNT cannot tell genuine brand-activity acceleration from an always-prolific filer,
    // so it never misfires bullish (directional surge detection is a deferred slice B — changes DIRECTION, not
    // this type name). Appended before the Other sentinel; SignalType is persisted by name, so placement is
    // readability-only.
    TrademarkActivity,
    // CorporateAction (spec 217): the assembly-time replacement for the keyword extractor's
    // StrategicPartnership read of an item-1.01 8-K that `acqscan-v1` recognised as a pending acquisition
    // OF THE COMPANY. It is minted ONLY by the read/assembly-time supersede (acq-supersede-v1) — no
    // collector, extractor or AI path ever produces it, and it is never persisted — and it is always
    // Neutral at strength 0: a $1.5B all-cash sale of the whole company is a corporate action, not a
    // partnership, and it must contribute nothing positive rather than contributing the wrong thing.
    // Appended before the Other sentinel; SignalType is persisted by name, so placement is
    // readability-only.
    CorporateAction,
    Other
}
