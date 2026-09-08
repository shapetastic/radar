# Skeptic review — Argan, Inc. (AGX), the first `Investigate` on the Lead arm (2026-09-08)

**Provenance.** Produced by the `radar-skeptic-reviewer` agent on 2026-09-08 at the maintainer's request
("run skeptic on argan"), against the 2026-09-07 weekly report (`data/reports/weekly/radar-weekly-2026-09-07.md`,
entry "### 1. Argan, Inc. (AGX)"), the Lead-arm score snapshot
`data/scores/strategies/disclosure-led-v11/5cf9dce6-4874-45ec-a7e7-1db8b1db1b58/e5b1b53f-47f2-42ba-822a-681482257bcc.json`,
the judgment record
`data/news-risk/judgments/openai-deepseek-ai-deepseek-v4-flash/5cf9dce6-4874-45ec-a7e7-1db8b1db1b58/928eb9f8-380b-7838-e80d-ad9283cbf066.json`,
and the public sources listed at the end. The agent was told that the `Investigate` label is a FIXED operating
threshold (Opportunity ≥ 20 on the v11 arm, chosen by prevalence — spec 212), not validated evidence of
opportunity, and that the v11 arm draws Opportunity from the SEC filings channel only.

**What this is and is not.** A devil's-advocate read of one company's evidence — a research prompt for a
human, recorded so the reasoning is auditable. It is not financial advice, not a Radar output, and nothing in
it feeds a score, a signal or a label. The two findings about Radar's own pipeline (the judge inverted the
backlog direction for want of a comparison fact; the Lead arm's #1 coincides with the earnings-only
comparator's #1) are recorded here as observations, not yet as specs.

Outside-fact caveat: figures quoted from the public sources (backlog history, insider-sale totals, price
range, margins by quarter) are as read by the agent on 2026-09-08 and were NOT independently re-verified by
Radar; anyone acting on them should check the cited filings first.

---

## Radar skeptic review — Argan, Inc. (AGX), 2026-09-07 Lead-arm Investigate

The headline finding before the JSON: the snapshot's `componentJson` shows `SignalCount: 1, RecordedSignals: 1`
on the only budgeted channel. Opportunity 20 is one 8-K, scored once, multiplied by a notedness discount of
0.764. The Stock Titan "backlog hits $2.5B" positive (weight 2, superseding the ordinary attention event) is
coverage of that same 8-K. The other 17 links carry weight 0 or are unbudgeted. And the judgment rationale
reads "Backlog reached $2.5B, indicating strong future demand" — but backlog was $2.929B on Jan 31 2026 and
~$2.8B on Apr 30, so the judge inverted the direction of the one forward-looking number in the print because
no comparison fact was in its (capped, `searchEnumeration: Failed`) family bundle.

```json
{
  "status": "THESIS_CHALLENGED",
  "summary": "AGX's Investigate label rests on exactly one event: the 2026-09-02 8-K (record Q2 FY27 revenue $384.0M, GM 19.3%, NI $53.3M, $1.03B cash, no debt). Every positive weight in the snapshot traces to that filing — the filings-channel signal (weight 7) and the judgment-derived MediaAttention (weight 2) are the same press release read twice. The business itself is real and well-capitalised, but the thesis Radar implies ('trajectory improving, under-followed, before the market notices') is contradicted on all three legs by what the filings-only arm cannot see: (1) backlog has fallen $2.93B -> ~$2.8B -> $2.52B across FY27 with no major power award in Q2 and an implied Q2 book-to-bill of roughly 0.3x; (2) the stock re-rated from a 52-week low of $197 to a high of $805.75 and has since roughly halved to ~$418 — the market noticed a year ago and is now discounting the backlog roll-off; (3) insiders sold ~$119M in H1 2026 (chairman $37M in June at $726–760, none flagged 10b5-1, zero insider purchases), of which Radar's 60-day window captured one $3.3M director sale and the v11 arm gave it no budget. The AI judge read the $2.5B backlog as 'strong future demand' without knowing it was down 14% YTD. The Lead arm here reproduces the baseline-earnings-only comparator (AGX #1 there at 50), which is precisely what the comparator section warns against. Investigate is warranted only as a narrowly framed research prompt — 'investigate the backlog trajectory and margin normalisation' — not as corroboration of an improving thesis.",
  "riskScore": 62,
  "keyRisks": [
    {
      "category": "Evidence weakness — one event echoed",
      "severity": "HIGH",
      "message": "Snapshot componentJson: 1 channel budgeted, SignalCount 1, RecordedSignals 1, composite 0.257 x notedness 0.764 = Opportunity 20, landing exactly at the fixed threshold. Evidence 80 is misleading: of 19 links, 16 are Neutral MediaAttention at weight 0 (13F reposts, GuruFocus GF-score pieces, a StockInvest forecast), 1 is the unbudgeted Form 4, and the 2 positives are the same 8-K. The '(collapsed 12 same-event media items)' on the Stock Titan link confirms the echo. Velocity 11 with 'no change vs last run' says this is an earnings-calendar artefact that will decay as the 8-K ages, not an accelerating signal.",
      "evidenceNeeded": "A second independent positive event inside the window that is not the 8-K — e.g. an 8-K item 1.01 for a new gigawatt-scale EPC award, or a 10-Q backlog figure that rises quarter over quarter."
    },
    {
      "category": "Revenue quality — backlog direction and conversion",
      "severity": "HIGH",
      "message": "Backlog $2.929B (Jan 31 2026) -> ~$2.8B (Apr 30) -> $2.518B (Jul 31): down 14% YTD, down ~10% QoQ. Q2 burned $384M of revenue against a $282M backlog decline, implying roughly $100M of gross bookings (~0.27x book-to-bill) even after '$260M of scope additions'. Management: no major power award in Q2, expects 'a handful' of new projects over 7–15 months, no target given. Record revenue is backlog burn, not backlog growth. Backlog is ~80% gas, ~11% renewable, ~8% industrial; the 405-MW solar project is at substantial completion and rolls off. Management also warned Q3 sequential growth will be limited and Industrial revenue declines for the rest of the year.",
      "evidenceNeeded": "Q3 FY27 backlog (10-Q ~December 2026) at or above $2.5B with a named new combined-cycle award; otherwise FY28 revenue is a function of a shrinking book."
    },
    {
      "category": "Revenue quality — customer and project concentration",
      "severity": "HIGH",
      "message": "FY2026 10-K: three Power customers = 23%, 16%, 11% of consolidated revenue (50% combined; FY2025 was 28/13/10). The power backlog (~$2.3B) is dominated by four US gas plants totalling >4.1 GW, two of them in Texas (Sandow Lakes 1.2 GW, CPV 1.4 GW). Any single project slipping on turbine delivery, PPA, financing or ERCOT interconnection is a >10%-of-revenue event. The Q2 10-Q's concentration table exists (Note 16) but the percentages for the current period were not extractable here — a human must read it.",
      "evidenceNeeded": "Note 16 of the Q2 FY27 10-Q (https://www.sec.gov/Archives/edgar/data/0000100591/000110465926104745/agx-20260731x10q.htm): current-period customer percentages, plus contract-liability balances by project."
    },
    {
      "category": "Margin quality — sequential compression and estimate deterioration",
      "severity": "MEDIUM",
      "message": "The AI read celebrated GM 18.6% -> 19.3% year over year. Sequentially it is 25% (Q4 FY26) -> 21% (Q1 FY27) -> 19.3% (Q2). Industrial segment GM fell to 7.3% because 'estimates to complete on a couple of projects declined from initial estimates' — the classic EPC early warning. Pending unapproved contract variations grew to $27.2M from $11.4M. A UK subsidiary is litigating a $9.8M letter-of-credit draw by an overseas project owner. Management guides Industrial margins 'below historical norms for one or two quarters'. None of this is visible to an 8-K-only read that compares against the prior-year quarter.",
      "evidenceNeeded": "Power segment GM held >= 20% in Q3; no loss provision; contract variations pending approval not rising further; resolution of the $9.8M LC dispute."
    },
    {
      "category": "Balance sheet — cash is partly customer money",
      "severity": "MEDIUM",
      "message": "'Increasing cash and investments' is true ($895M -> $1.028B) but net liquidity is $440M. The ~$588M gap is largely billings in excess of costs — customer advances on mid-cycle projects. This float swells during peak construction and unwinds as the four gas plants complete. No debt and no dilution (diluted shares 14.164M vs 14.131M) are genuine strengths; a 'strong balance sheet' read that treats gross cash as retained earnings is not. Buybacks were only $6.7M in Q2 while the stock fell ~45% — management did not lean in.",
      "evidenceNeeded": "Net liquidity trend over the next two quarters as backlog converts; contract liabilities vs contract assets in the 10-Q."
    },
    {
      "category": "Valuation — already re-rated, now de-rating",
      "severity": "HIGH",
      "message": "Price $417.99 (Sep 5), market cap $5.86B, trailing P/E 33, forward P/E ~33, 52-week range $197–$805.75. This is a cyclical fixed-scope EPC contractor at peak-cycle margins trading at ~20x EV/annualised adjusted EBITDA. The stock quadrupled inside twelve months and has since given back roughly half; the post-8-K after-hours pop to $442.50 was fully retraced within three sessions. The Seeking Alpha 'extends losing streak with another 6% drop' item in Radar's own evidence — typed Neutral — is the market pricing the backlog decline while Radar prices the earnings beat. 'Before the market notices' is falsified by the price history.",
      "evidenceNeeded": "A human should reconcile what the current multiple implies for FY28 revenue against a $2.5B-and-falling backlog with a 12–24 month burn."
    },
    {
      "category": "Governance — insider selling pattern",
      "severity": "HIGH",
      "message": "Radar holds one Form 4 (Ronald Jr., 5,716 sh, $3.31M, Jul 31, no 10b5-1 flag) and the v11 arm gives it zero budget. Outside the 60-day window (starts Jul 9): non-executive chairman Griffin sold 50,000 sh (~$37M) on Jun 18/22 at $726–760, aff10b5One = 0; CEO Watson ~$19.5M; Leimkuhler ~$14.9M; at least six other sellers; ~$119.4M total in H1 2026 per Simply Wall St. Ronald Jr. has sold three times since April and now holds 2,533 direct shares — he has sold most of his direct position. No insider purchase anywhere in 2026. The weekly report's '1 discretionary sale filing, $3,313,222' understates the aggregate insider flow by roughly 35x, and the spec-209 insider-flow aggregate truth is the mechanism that should surface this.",
      "evidenceNeeded": "Form 4s filed after the Sep 2 print. Continued discretionary selling by the CEO or chairman at ~$420 (roughly half the June price) would be a materially stronger negative tell than the June sales at the top."
    },
    {
      "category": "Competitive / supply-chain risk",
      "severity": "MEDIUM",
      "message": "Gas-turbine lead times are five to seven years; GE Vernova, Siemens Energy and Mitsubishi Power (>75% share) are quoting late-2028 to 2030 delivery. Argan's next awards depend on developers securing turbine slots, PPAs, permits and financing — management listed exactly these prerequisites and said no new major project was captured in Q2. Project capex per kW has roughly doubled in fifteen months; if Gemma's contracts carry fixed-price exposure to escalating equipment and labour, margin risk rises with the boom. Argan is a small EPC competing for the same gas-plant work as Kiewit, Bechtel, Quanta and Fluor.",
      "evidenceNeeded": "Contract structure (cost-plus vs fixed-price) for the four gas plants; whether turbines for those plants are already delivered or slotted; any 8-K award naming a turbine OEM and delivery date."
    },
    {
      "category": "Hype risk",
      "severity": "MEDIUM",
      "message": "The retail narrative is 'AI data-center power'. Radar's themes ('power plant construction, energy EPC') are sober, but the coverage it collapsed is not — 'Freedom Broker turns bullish', 'AI-proof growth', GF-score pieces. Management itself said 'recent regulatory scrutiny and media reports involving data-center development have not changed customer or developer behavior', which is the kind of reassurance that only appears once the question is being asked. The 'Following: Small (under-followed)' tag is a curation label; a $5.9B company with a 4x 52-week range, Renaissance Technologies in the 13F churn and five-plus sell-side analysts is not under-followed in any measured sense.",
      "evidenceNeeded": "The mature attention read for AGX should be checked against measured analyst count and 13F holder count rather than the curated tier."
    },
    {
      "category": "Macro sensitivity",
      "severity": "MEDIUM",
      "message": "Single-cycle exposure: US gas-fired generation build-out driven by hyperscaler capex. A hyperscaler capex pause, a data-center permitting or ratepayer backlash (Texas is already scrutinising large-load interconnects), rate-driven project financing stress, or a turbine-delivery slip all hit the same four projects. The 405-MW solar and the Irish biofuel plant are small diversifiers. The Teledata acquisition (ValCor) and the second fabrication facility (>$10M/quarter of Industrial revenue) are too small to offset a power-award drought.",
      "evidenceNeeded": "Hyperscaler capex guidance through year-end; ERCOT large-load interconnection policy changes; any developer-side project cancellation in the Texas gas queue."
    },
    {
      "category": "Signal labelling",
      "severity": "LOW",
      "message": "The filings signal is typed GuidanceChange (Positive). The EX-99.1 press release contains no forward guidance; management explicitly declined a backlog target on the call. The correct family is EarningsTrajectory. A GuidanceChange label on a no-guidance release is a mislabel the signal reviewer should look at, because 'guidance raised' and 'record quarter, book-to-bill 0.3x' are very different research prompts.",
      "evidenceNeeded": "Compare the extractor's signal-type mapping for 8-K item 2.02 against the actual presence of outlook language in the exhibit."
    }
  ],
  "questionsBeforeInvestigation": [
    "What did the judgment see? The judge cited 'backlog reached $2.5B, indicating strong future demand' with searchEnumeration Failed and familyBundle Capped. Did any fact in its 50 families carry the Jan 31 ($2.93B) or Apr 30 (~$2.8B) figure? If not, the trajectory 'Improving' read is built on a fact the judge could not contextualise, and the reviewer should ask whether a period-over-period comparison belongs in the fact schema.",
    "Why is the Lead arm's #1 identical to baseline-earnings-only's #1 (AGX at 50) and disclosure-led-v10-control's #1 (AGX at 20)? If the arm is reproducing the earnings comparator on this name, the Investigate label is an earnings screen, not a disclosure-led thesis.",
    "Would Opportunity have crossed 20 if the arm budgeted sec-form4? The snapshot records the Form 4 at weight -2 but 'no channel'. With the June selling wave inside a 120-day window, what does the long-window profile say?",
    "Is 'Following: Small (under-followed)' a measured quantity or a curation tier carried from the universe file? For a name that moved $197 to $805 in a year the label is doing work it has not earned.",
    "What in Radar's evidence would have looked different if the quarter had been a record with a RISING backlog? If the answer is 'nothing', the pipeline cannot distinguish the two cases and the label carries no information about the forward book."
  ],
  "thesisBreakers": [
    "Backlog at Jan 31 2027 below ~$2.0B with no gigawatt-scale gas award announced — FY28 revenue then declines from a record base and the earnings-trajectory read reverses mechanically.",
    "Any one of the four gas plants (Sandow Lakes, CPV, the 860-MW or 700-MW combined-cycle) reporting a turbine-delivery, financing or interconnection delay — each is a >10%-of-revenue customer.",
    "Power segment gross margin below ~18% or a loss provision on any project; Industrial estimate-to-complete deterioration spreading beyond 'a couple of projects'.",
    "Net liquidity falling while gross cash stays high — the signature of customer advances unwinding as projects complete.",
    "Post-print discretionary Form 4 sales by the CEO or chairman at ~$420, i.e. continued selling at half the June price.",
    "A hyperscaler capex pullback or a Texas large-load policy change that stalls the developer pipeline management is counting on for its 'handful' of awards over 7–15 months."
  ]
}
```

### Plain-language verdict on the label

Investigate is defensible as a research prompt only if the prompt is reframed. Radar's evidence supports "a
well-capitalised EPC contractor just printed a record quarter." It does not support "trajectory improving
before the market notices." The market noticed twelve months ago, re-rated the stock four-fold, watched insiders
sell ~$119M into the top, and is now de-rating it against a backlog that has fallen three quarters running. The
one event Radar scored — and echoed through a second channel and an AI judgment that misread the backlog's
direction — is the lagging indicator; the leading indicator (bookings) is negative and the filings-only arm has
no channel that would ever see it until a 10-Q lands.

What a human should check first, in order: (1) Note 16 of the Q2 10-Q for current customer concentration;
(2) the backlog roll — the Q3 FY27 number in December is the single most decision-relevant fact; (3) whether the
four gas plants' turbines are delivered or slotted; (4) Form 4s after Sep 2.

### Sources

- [Argan Q2 FY2027 press release, EX-99.1 (SEC)](https://www.sec.gov/Archives/edgar/data/100591/000110465926104735/agx-20260902xex99d1.htm)
- [Argan 10-Q for quarter ended Jul 31 2026 (SEC)](https://www.sec.gov/Archives/edgar/data/0000100591/000110465926104745/agx-20260731x10q.htm)
- [Argan 10-K FY2026 — customer concentration (SEC)](https://www.sec.gov/Archives/edgar/data/100591/000110465926035216/agx-20260131x10k.htm)
- [Form 4 — Ronald Jr., Jul 31 2026 sale (SEC)](https://www.sec.gov/Archives/edgar/data/0000100591/000170991426000028/form4-08042026_040816.xml)
- [Form 4 — Griffin, Jun 18/22 2026 sales (SEC)](https://www.sec.gov/Archives/edgar/data/0000100591/000138412326000006/form4-06232026_050601.xml)
- [Argan Q2 earnings call highlights (Yahoo Finance)](https://finance.yahoo.com/markets/stocks/articles/argan-q2-earnings-call-highlights-220345054.html)
- [Argan Q2 FY2027 slides (Investing.com)](https://www.investing.com/news/company-news/argan-q2-fy2027-slides-record-revenue-electrification-demand-drives-growth-93CH-4886836)
- [Argan falls as investors weigh insider selling and backlog pullback (Quiver Quantitative)](https://www.quiverquant.com/news/Argan+Falls+as+Investors+Weigh+Insider+Selling+and+a+Small+Backlog+Pullback)
- [Argan down 6.4% after insider sales raise valuation questions (Simply Wall St)](https://simplywall.st/stocks/us/capital-goods/nyse-agx/argan/news/argan-agx-is-down-64-after-insider-sales-raise-valuation-que)
- [Top Argan insiders cash out (TipRanks)](https://www.tipranks.com/news/insider-trading/top-argan-insiders-quietly-cash-out-millions-in-high-profile-stock-sales-insider-trading-news)
- [AGX valuation snapshot (StockAnalysis)](https://stockanalysis.com/stocks/agx/)
- [US power boom triggers global gas turbine shortage (OilPrice)](https://oilprice.com/Energy/Energy-General/US-Power-Boom-Triggers-Global-Gas-Turbine-Shortage.html)
- [Gas turbine supply constraints (RMI)](https://rmi.org/gas-turbine-supply-constraints-threaten-grid-reliability-more-affordable-near-term-solutions-can-help/)
- [Backlog for natural gas turbines expands (RBN Energy)](https://rbnenergy.com/daily-posts/blog/backlog-natural-gas-turbines-expands-surging-demand-supply-constraints)
- [Is Argan undervalued after its sector-led selloff (Yahoo Finance)](https://finance.yahoo.com/markets/stocks/articles/argan-agx-undervalued-sector-led-061615421.html)

---

## Observations about Radar itself (recorded, not yet specs)

1. **The judge read a level as a trend.** "Backlog reached $2.5B, indicating strong future demand" came from a
   capped family bundle with no prior-period backlog fact. A stage-2 judgment cannot read a stock figure
   directionally without a comparison fact in the schema; today it will call any large number "strong".
2. **On this name the Lead arm IS the earnings comparator.** AGX is #1 on `disclosure-led-v11` (20),
   `disclosure-led-v10-control` (20) and `baseline-earnings-only` (50). A filings-only budget over a 60-day
   window reduces to "who printed a record quarter most recently" whenever no other filing event lands.
3. **The insider aggregate is window-bound by design and said so** — "1 filing; 1 discretionary sale filing,
   $3,313,222" is exactly what the 60-day store holds. The ~$119M H1 flow lies outside the window. Spec 209 §4
   (forward capture) would not change this; only a longer insider look-back would.
4. **The label did what spec 212 said it would.** Investigate at 20 is a triage line by prevalence. The first
   name it surfaced is a real, well-capitalised company whose thesis a skeptic can challenge in ten minutes —
   which is the intended use of the label, and a reminder that it is not evidence.
