# Project Radar Philosophy

## Mission

Radar finds companies whose business trajectory may be improving before the market fully notices.

Radar is not a trading bot. It is not a recommendation engine. It is a research assistant.

---

## Principles

### Signals before stories

Do not start with a narrative and search for confirmation. Start with evidence and let patterns emerge.

### Evidence before opinions

Every score must be traceable to evidence.

### Trajectory before popularity

The best opportunities may not be today's best-known companies. Radar looks for improving direction.

### Explain every score

A score without explanation is invalid.

### Preserve provenance

Never lose the link between raw evidence, extracted signal, score, and report.

### AI assists; humans decide

AI can collect, extract, summarize and challenge. **AI does NOT score** — the scoring formula is
deterministic code by design (AD-6), and `ScoringEngine` has no AI dependency on its path. Humans decide what
deserves capital.

**AD-14 — price is validation data, never an input.** Price, market cap and volume must never enter the
evidence → signal → score path. They exist only to test, after the fact, whether a score preceded a move.
This is enforced structurally, not by convention: a type-graph guard fails the build if anything reachable
from the scoring closure can reach a price type. `FollowingTier` is curated from coverage evidence for the
same reason — it would otherwise be a back door for market cap.

### Avoid hype loops

Social momentum is not conviction. It may be a warning sign.

### Prefer small useful systems

Build a working skeleton first. Enrich signals over time.

### Caution is a feature

Radar should surface upside, but it should also protect against narrative traps, dilution, weak balance sheets, and promotional noise.

---

## Research Question

Every Radar run asks:

> Which companies became materially more interesting recently, and what evidence supports that?

---

## Output Language

Radar may say:

- Investigate
- Watch
- Ignore
- Needs more evidence
- Thesis improving
- Thesis deteriorating

Radar must not say:

- Buy
- Sell
- Guaranteed upside
- Safe bet
