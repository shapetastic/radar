# Task: AI filing-read calibration audit — blinded second-reader protocol, harness, and the full-cohort run

> **RESEARCH SPEC (spec-156/158 genre): read-side only, no scoring change, no fingerprint input, no pin
> move.** Motivated by the external plausibility review (2026-07-29): the reader's self-reported confidence
> has never been empirically calibrated — "a model saying 0.90 should eventually mean approximately 90%
> correct in this class, not merely that the model sounded confident." Spec 160 (the comparability cap) is
> containment; this is the measurement underneath it. **A 30-filing blinded pilot was run 2026-07-29 before
> this spec was written, and its findings are the evidence base for the protocol below** — seed labels
> committed at `docs/162-calibration-pilot-labels.csv`.

## Pilot findings (n=30: 10 stratified incl. all 4 Negative reads + 20 population-representative Positives)

Protocol: each filing was labeled by a **blinded** `radar-skeptic-reviewer` agent (different model family
from the DeepSeek reader, adversarial prompt) given ONLY company + CIK + accession + the release text —
never the cached model answer. Batch 2 used pre-fetched local exhibits (see Harness), which cut agents from
6–18 web calls to exactly 1 file read.

1. **Direction is never inverted, but 10% of directional reads should have been Mixed** (3/30, all
   dirty-comparison prints read at face value: a one-time DTA write-off masquerading as deterioration, a
   prior-year one-off inflating a growth base, an acquisition distorting every line). The reader's failure
   mode is precisely characterized: it does not hallucinate direction; **it over-commits on
   comparability-broken headlines.**
2. **Confidence is systematically inflated**: reader mean 0.885 vs blinded-skeptic mean 0.762 (gap +0.124);
   the reader was the more confident party in 26/30. The reader clusters at 0.85–0.95 regardless of print
   quality; the skeptic grades 0.55–0.90. Notably the three disagreements sat at reader confidence
   0.75–0.85, NOT 0.95 — its highest-confidence reads all agreed, so confidence is not *meaningless*, just
   uncalibrated and compressed.
3. **Clean YoY comparisons are the exception: 5/30 (17%).** Dirty comparisons are the population norm, not a
   tail — validating spec 160's cap design and its expected high fire-rate.
4. **A cmpscan-v2 marker gap is now evidenced: acquisitions.** Multiple filings' dominant comparability item
   (HWKN ×2, DGII, AGYS, MMSI, STRL, PLUS) was acquisition/deconsolidation perimeter change — matched by NO
   `cmpscan-v1` phrase. Candidate v2 markers with evidence behind them: `acquisition`, `pro forma`,
   `deconsolidation`, `constant currency`/`organic` divergence language. (Do NOT change the table in this
   spec — that is a `cmpscan-v2` slice; this spec produces the measurement it needs.)
5. **Materiality is not encoded anywhere in Radar**: the skeptic graded low/moderate/high (e.g. ERII's
   strength-8 Negative was graded *low* — a pre-communicated, seasonally-smallest-quarter timing effect),
   while every AI read carries constant Strength 8. Confirms the review's "strength encodes event category,
   not economic materiality."

## What this spec builds

### 1. Harness scripts (`scripts/calibration-audit/`) — PowerShell, no .NET changes

- **`build-worksheet.ps1`**: joins `data/filings-cache/*.json` to `data/companies.json` (feed-name → CIK,
  read both as UTF-8 — an ANSI read silently breaks the em-dash join; learned in the pilot) producing
  `worksheet.csv`: accession, outcome, company, ticker, CIK, observedAt, model direction/confidence/reason
  (the SEALED columns). For `NoDirectionalSignal` records (no `companyMention`), recover the CIK by grepping
  the accession in `data/evidence/raw/filings/**` (the index `SourceUrl` carries it) — records whose CIK
  cannot be recovered are listed, not silently dropped.
- **`fetch-exhibits.ps1`**: for each worksheet row, fetch `index.json` from
  `www.sec.gov/Archives/edgar/data/{cik}/{accession-nodashes}/`, pick the EX-99.1 (`ex.?99|99d1|991` on
  `.htm`, with a **manual-override column** for the non-standard names the pilot hit — `fy25q3pressrelease.htm`,
  `exhibit99_1-*.htm`, `imax-*epr.htm`), fetch it, strip tags, write `exhibits/{ticker}-{accession}.txt`.
  **Paced ~2.5 req/s sequential, UA from `RADAR_SEC_UA`** (fail loudly if unset). Re-runnable: skips files
  that already exist and are >3,000 chars; a shorter file is refetched (the pilot caught a transient
  "SEC.gov maintenance" interstitial saved as a 674-char exhibit — size is the tripwire).
- **`analyze-labels.ps1`**: joins labels to sealed answers and emits the findings tables: direction
  confusion matrix (reader × skeptic, per class), reliability bins (reader confidence bucket → agreement
  rate), clean-rate, materiality × constant-strength cross-tab, and the adjudication queue (all
  disagreements + any label whose notes flag identification doubt).

### 2. The labeling protocol (documented in the spec + script headers; executed by agents, not by run-next)

- **Blinding is structural**: the labeling agent receives ONLY company, CIK, accession and the LOCAL
  pre-fetched exhibit path; it is instructed to read no other local file (repo files contain the sealed
  answers) and use no web. Local exhibits are the load-bearing fix: in the pilot's web round, SEC 403'd the
  agents' fetcher and one agent had to *infer* which release an accession mapped to — an identification risk
  the local file eliminates.
- **Label schema (JSON per filing)**: `direction` (Improving/Deteriorating/Mixed/Unknown),
  `directionConfidence` [0,1], `comparisonClean` (bool), `comparabilityItems` (name + amount each),
  `material` (high/moderate/low), `keyFacts`. Same schema as the committed pilot rows.
- **Second-opinion honesty rule, stated in the findings doc**: skeptic labels are an independent second AI
  opinion, NOT ground truth. Agreement is evidence; disagreement is a queue for **human adjudication** (the
  maintainer), and only adjudicated rows may be described as ground truth. REIT filings get the pilot's REIT
  framing note (judge on FFO/AFFO, not GAAP) — without it a REIT label is systematically wrong.
- **Concurrency cap: ≤5 labeling agents at a time.** The pilot's 20-at-once fan-out drew API-overload
  failures (7/20 died on 529s and needed retries); batches of 4–5 completed cleanly in ~1 min each.

### 3. The full-cohort run and findings doc

- Cohort: all remaining directional reads (149 − 30 already labeled = 119) **plus** a 30-filing sample of
  the 154 `NoDirectionalSignal` records (false-negative check: did the reader miss genuinely directional
  prints?). ~150 labels total, ≈50k tokens each.
- Output: `docs/162-findings-filing-read-calibration.md` — the confusion matrix, the reliability curve
  (stated per confidence bin with honest Ns), clean-rate, materiality distribution, the adjudicated
  disagreement set, and a **decisions section** feeding: (a) whether/how to remap reader confidence
  (a config magnitude → would move the AI-ON pins; its own slice), (b) the `cmpscan-v2` marker table,
  (c) the structured-comparison-extraction requirements (which failure classes it must fix), and
  (d) evidence-relative materiality (which denominators the labels show mattering).
- The complete label set is committed beside it (`docs/162-calibration-labels-full.csv`), pilot rows
  included, so the curve is recomputable.

## Constraints

- **Read-side only.** No file under `src/` changes; no scoring behaviour, descriptor, fingerprint or store
  is touched. The pins do not move.
- SEC discipline: all EDGAR traffic goes through `fetch-exhibits.ps1` (paced, real UA, sequential); labeling
  agents make zero SEC requests.
- Labels never flow back into scoring by side door: the findings doc informs SPECS, not runtime values.
- `docs/162-calibration-pilot-labels.csv` is append-only seed data — the full run appends, never rewrites.

## Out of scope, recorded not built

- The confidence remap itself (needs the full curve first; fingerprint-moving; own spec).
- `cmpscan-v2` (needs this measurement; own spec).
- Structured financial-comparison extraction (the deep fix; own arc, requirements come from these findings).
- Labeling the entire no-signal cohort (sampled at 30 here; extend only if the false-negative rate is
  non-trivial).

## Acceptance criteria

- [ ] Three harness scripts as specified, re-runnable, paced, UA-gated; worksheet covers directional AND
      no-signal records with recovered CIKs (unrecoverable ones listed).
- [ ] Protocol documented (blinding, schema, second-opinion honesty rule, REIT note, ≤5 concurrency).
- [ ] Pilot labels committed at `docs/162-calibration-pilot-labels.csv` (already done pre-spec; verify).
- [ ] Findings doc skeleton created with the pilot's five findings as its opening section.
- [ ] No change under `src/`; `dotnet build` / `dotnet test` untouched and green.
