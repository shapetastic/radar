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
    // CorporateAction (spec 217, widened by spec 226): an event of a corporate-action TYPE that carries no
    // direction. Two producers, both always Neutral:
    //   * the keyword extractor (spec 226, radar-keyword-rules-v9) mints it — and it IS persisted — for the
    //     SEC 8-K Item 1.01 / 2.01 heading phrases, at the rule strength 4: a heading records that a material
    //     agreement, an acquisition or a disposal happened, never whether it is good or bad for the business;
    //   * the read/assembly-time supersede (acq-supersede-v2) rewrites the extractor's read of an item-1.01
    //     8-K that `acqscan-v1` recognised as a pending acquisition OF THE COMPANY — an accrued v8
    //     StrategicPartnership or a v9 CorporateAction — into ONE CorporateAction at strength 0 naming the
    //     acquisition; that rewrite is never persisted.
    // (⚠ Amended in place by spec 226: this comment previously said it was minted ONLY by the supersede, never
    // persisted and always strength 0 — true until the v9 rule table.) Appended before the Other sentinel;
    // SignalType is persisted by name, so placement is readability-only.
    CorporateAction,
    Other
}
