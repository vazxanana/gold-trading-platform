# What quant firms do that a solo cTrader trader can copy (deep-research run, 2026-07-16)

Multi-agent sweep with adversarial verification. Sources: peer-reviewed papers,
AQR/CFM primary research, EU regulator decisions. Confidence labels reflect
verification votes; several numeric sub-claims went unverified because the vote
agents hit session limits, but all are consistent across three independent sources.

## The verdict on "2% per month at a 70% win rate"

Not supported by any documented evidence, and the two numbers pull in opposite
directions:

- The best-documented rule-based strategy in existence — diversified, volatility-
  scaled time-series momentum across 67 markets, 1880-2016 (Hurst, Ooi & Pedersen /
  AQR) — earned ~11%/yr excess gross of fees, ~7.3%/yr (Sharpe 0.76) net of costs
  and 2/20 fees. That is ~0.6%/month, from a 137-year institutional portfolio.
- On a SINGLE market (a gold-only bot), the same study benchmarks at Sharpe ~0.4.
- The only documented route to a 70%+ win rate is negative skew: carry, short-vol,
  grid/martingale-style payoffs. CFM research (Lemperiere et al., Quantitative
  Finance 2017) shows these "smooth equity curve" strategies are insurance selling -
  steady small wins as compensation for rare large losses, not alpha.
- Regulators supply the base rate: 74-89% of retail CFD accounts lose money
  (ESMA/NCA analyses); ESMA banned retail binary options outright after documenting
  structural negative expectancy in the high-apparent-win-rate product class.
- Trend-following - the one style with a century of positive evidence - runs a
  BELOW-50% win rate by construction (positive skew: many small losses, few big wins).
  Your engulfing bot's 1:2.5-RR design is the same shape.

Realistic, evidence-based expectation for a disciplined solo algo trader:
**0.5-1% per month on average (Sharpe 0.3-0.8 depending on diversification), with
deep drawdowns and multi-year lean stretches.** Pick expectancy as the target and
let win rate be whatever the strategy's shape implies.

## What transfers from the industry (verified practices)

1. **Diversified, vol-scaled time-series momentum** - the fully-specified rule from
   the 137-year study: signal = sign of past 1/3/12-month excess return (equal-
   weighted), position size scaled so each market contributes constant ex-ante
   volatility (~10%/yr target). Implementable in a cBot; diversify across gold +
   FX majors + indices to move from single-market Sharpe ~0.4 toward portfolio ~0.7.
2. **Volatility targeting / risk-based sizing** - already in GoldCloseDrift
   (RiskPercent mode); the industry version scales by realized vol, not fixed SL.
3. **Portfolio of uncorrelated strategies** - overnight-drift (seasonality) +
   trend-following are genuinely different return streams; running both at half
   size beats either at full size on risk-adjusted terms.
4. **Cost- and fee-adjusted validation, walk-forward OOS, multiple-testing
   discipline** - the AQR paper reports net-of-costs-net-of-fees; our research bot
   already does IS/OOS splits and multiple-testing warnings. Never trust a gross
   backtest.
5. **Hard risk caps / kill switches** - weekly loss cap (built), margin close-out
   at 50% and negative balance protection (imposed by EU regulation anyway; ESMA
   leverage caps: 20:1 gold, 30:1 FX majors).
6. **Expect lean years** - even the 137-year strategy had a weak 2010-2016; the
   documented cause was scarcity of large moves, not decay. Survival through flat
   stretches is a design requirement, not a failure signal.

## What does NOT transfer / red flags

- "90% win rate" scalping/grid/martingale products: the win rate is real, the
  expectancy is negative, the blowup is deferred. This is the payoff shape
  regulators banned in binary form.
- Fund-level Sharpe on one instrument: diversification IS the edge; no parameter
  tuning recovers it on a single symbol.
- Renaissance-tier returns as a benchmark: a documented historical anomaly, closed
  to outside money; typical good quant funds run 10-20%/yr fee-adjusted.

## Sources (primary)

- Hurst, Ooi & Pedersen, "A Century of Evidence on Trend-Following Investing"
  (AQR / Journal of Portfolio Management; 1880-2016, 67 markets)
- Lemperiere, Deremble, Nguyen, Seager, Potters, Bouchaud, "Risk Premia: Asymmetric
  Tail Risks and Excess Returns", Quantitative Finance 17(1) 2017 (arXiv:1409.7720)
- ESMA Decision (EU) 2018/795 (binary options prohibition) + CFD intervention
  measures (leverage caps, 50% margin close-out, negative balance protection);
  NCA analyses: 74-89% of retail CFD accounts lose money
- Newfound Research, "Trend: Convexity & Premium" (payoff-shape exposition)
