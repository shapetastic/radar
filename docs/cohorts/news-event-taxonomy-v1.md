# news-event-taxonomy-v1

The closed news-event-type vocabulary the spec-181 stage-1 typing layer types against.

- **Version:** `news-event-taxonomy-v1`
- **SHA-256 hash:** `078f53452ac8bf28526f29704f5d06a345bfae3b7bcbbf54661a2a8193555f5c`
- **Canonical string:** `radar:news-event-taxonomy-v1:` followed by the member names below joined by `|`,
  in exactly this order (the order is also the deterministic `DerivedPrimaryType` tie-break).
- **Declared in code:** `Radar.Application.NewsTyping.NewsEventTaxonomy` (the enum declaration IS the
  canonical order; the hash is pinned by `NewsEventTaxonomyTests`).

## Members (declaration order)

1. `EarningsOrGuidance`
2. `MergerAcquisitionOrStake`
3. `FinancingOrDilution`
4. `ProductOrTechnology`
5. `ContractOrCustomerWin`
6. `RegulatoryOrLegal`
7. `ManagementOrGovernance`
8. `AnalystOrRatingAction`
9. `MarketReaction`
10. `IndexOrTradingMechanics`
11. `ShortSellerOrCritique`
12. `DividendOrBuyback`
13. `PromotionalOrListicle`
14. `OtherSpecified`

`MarketReaction` is present per the spec-181 §3 review note: a stock falling after earnings is a
price-move report, not `IndexOrTradingMechanics` — conflating them would misfile the most common headline
kind there is. `PromotionalOrListicle` and `IndexOrTradingMechanics` are deliberately present as the
"coverage that says nothing about the business" buckets whose identification is half the spec's value.

## Provenance and audit status — stated honestly

v1 is **declared from the reviewed §3 strawman** (maintainer decision, 2026-08-23), not yet from audited
data. The §3 finalization procedure — a stratified sample of ≥200 archived observations typed with the
pilot reader(s) and audited by a human (merge types the model confuses, split types that hide distinct
behaviour, drop types with near-zero support), with stage-1 recall over the audited sample recorded as a
headline number beside the citation-drop rate — **still has to run against the first typings**. The tooling
that produces those first typings is what this slice ships.

If the audit produces a revision, it lands as `news-event-taxonomy-v2`: a new enum, a new version token, a
new hash, and a NEW cohort dimension. The taxonomy is immutable by convention — cohorts never pool across
taxonomy versions, so v1 typings stay exactly what they were.

Record audit sample references and decisions here (or in a sibling file this one links) when the audit
runs.
