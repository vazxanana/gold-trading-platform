# Gold edge literature review (deep-research run, 2026-07-15)

Multi-agent research sweep: 5 search angles, ~15 sources fetched, claims extracted and
put through 3-vote adversarial verification. Verification completed for 9 of 25 claims
before the session budget ran out; the rest are labeled UNVERIFIED (extracted from real
sources, but no refutation votes ran). Votes shown as confirm-refute.

## Confirmed (survived adversarial verification)

1. **Overnight-positive / day-negative return decomposition in gold** (3-0, twice)
   - COMEX gold front futures: overnight (close-to-open) returns significantly positive,
     day (open-to-close) returns significantly negative. Same asymmetry in London Fix
     spot, gold miners, gold ETFs/CEFs — so it is not a futures artifact and applies to
     spot XAUUSD.
   - "Economically important even with transaction costs" (2-1).
   - Source: *Overnight versus day returns in gold and gold related assets*,
     Journal of Economics and Finance 42 (2018). Sample 1985-2012.
   - **This is the academic confirmation of our three-era close/reopen-window finding
     and the basis of GoldCloseDrift.**

2. **London PM fix event structure** (2-1 each)
   - Volume/volatility spike immediately after the fixing starts (15:00 London), before
     results publish; return drift during the ~4-minute fix window accrued to informed
     participants.
   - Source: Caminschi & Heaney, *Fixing a Leaky Fixing*, J. Futures Markets 34 (2014).
   - Retail takeaway: explains the negative London-into-fix drift we measured, but the
     tradable part belonged to insiders and the mechanism was reformed in 2015. Do not
     build a fix-window rule.

3. **No day-of-week RETURN seasonality in London gold, 2003-2017** (3-0)
   - Daily seasonality found only in volatility (and only 2013-2017 sub-sample).
   - Source: *Identification of the daily seasonality in gold returns and volatilities:
     Evidence from Shanghai and London*, Resources Policy.
   - **Directly challenges our Thursday-positive finding. Treat Thursday as unproven;
     keep the Thursday size multiplier at 1.0.**

## Refuted (0-3 — do not act on)

- ">90% fix-direction predictability" as a retail-tradable momentum rule around 15:00
  London (the predictability belonged to fix participants with order-flow knowledge).
- Two misreadings of the post-reform "Fixing the Fix" study. Net message stands: the
  pre-2015 fix anomaly is regime-dead.

## Unverified but source-backed (votes never ran - candidates for our own testing)

- **Session volatility shape**: Tokyo open L-shape, London U-shape (peaks at open/close
  = fix windows), NY declining (Iwatsubo, Watkins & Xu, TOCOM/COMEX). Relevant to
  timing breakout entries and avoiding dead hours.
- **Night-session first-30-minutes momentum** (SHFE gold, 2025): the first half-hour
  return of the night session predicts subsequent session returns. Analog worth testing
  on XAUUSD: does the 22:00-22:30 UTC reopen return predict the rest of the overnight
  drift window?
- **FOMC**: gold's adjustment to FOMC shocks continues beyond 5 minutes (short-horizon
  post-announcement drift, stronger for dovish surprises). Pre-FOMC drift is documented
  in EQUITIES ONLY (Lucca & Moench) - no evidence in gold; do not extrapolate.
- **Jump risk is event-scheduled and negatively skewed**: 18-34% of intraday gold jumps
  occur on scheduled US releases (FOMC largest); negative jumps outnumber positive.
  Implementable time-only guard: avoid fresh entries in release windows (hardcoded
  calendar: FOMC 18:00/19:00 UTC, NFP first-Friday 12:30/13:30 UTC, CPI 12:30/13:30).
- **Regime robustness**: the overnight/day asymmetry reportedly holds in both rising
  and falling gold markets (contrasts with our down-day-reversion regime dependence).

## Practical conclusions for this repo

1. GoldCloseDrift trades the one edge with peer-reviewed, multi-decade, multi-market
   support. Test the literature's wider window against ours:
   current 21:00->00:00 (swap-dodging) vs post-PM-fix -> AM-fix
   (EntryHourUtc=15, EntryWindowHours=2, ExitHourUtc=10) which pays nightly swap.
2. Drop the Thursday idea (published null result; ours was likely regime/luck).
3. Candidate additions to GoldEdgeResearch: reopen first-30-min momentum;
   release-window jump exposure (entry-hour sensitivity around FOMC/NFP days).
4. Nothing found supports shorts, candle patterns, or fix-window scalping for retail.
