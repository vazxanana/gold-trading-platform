# Wick vs Close-Price Market Structure — Deep Backtest

**Question:** which price source identifies market structure better — swing
pivots on candle WICKS (high/low) or on CLOSING price (line chart)?

**Method.** Identical structure engine for both: strength-k fractal pivots
(k = 2..5), only the last high/low level kept, break = candle CLOSE through
the level (both methods), trend = direction of last break. Compared on:
regime stability (flips), forward-direction agreement (does the trend label
predict the next 20 bars), per-bar directional edge, a trend-flip trading
simulation with costs, BOS follow-through, and sweep-fade quality (wick
through level without close → fade toward the level, 20-bar horizon).

**Data.** No XAUUSD source was reachable from this environment (histdata,
stooq, yahoo, binance, kraken, coinbase, bitfinex, okx all blocked; only
GitHub raw). Used: BTCUSDT H1/H4/D1 2018–2022 (43k H1 bars) and EURUSD
H1/H4 2017–2019 (6.2k bars). Method-level conclusions cross-validated on
two very different markets; re-verify magnitudes on gold in the cTrader
tester.

## Results (20 market × strength combos)

| Metric | Wick wins | Mean wick | Mean close |
|---|---|---|---|
| Fewer trend flips (stability) | **20/20** | 33.2 /1000 bars | 50.1 /1000 bars |
| Forward agreement (20-bar) | **15/20** | 50.1% | 49.3% |
| Directional edge per bar | **13/20** | 2.7 bp | 1.4 bp |
| BOS follow-through win% | 11/20 | 48.8% | 48.3% |
| ChoCh follow-through win% | 11/20 | 49.4% | 48.9% |
| Flip-strategy Sharpe | 7/20 | -0.06 | -0.14 |

Key detail by timeframe:

- **Daily (BTC-D1):** wick structure clearly superior — agreement 52–55%,
  edge 9–18 bp/bar (close: 3–12), BOS follow-through t-stats 1.8–2.2 vs
  0.05–1.4. The higher the timeframe, the more the wick extreme matters.
- **H4 (BTC):** close-based structure breaks EARLIER and its flip strategy
  scores higher Sharpe (0.6–0.9 vs 0.4–0.7) — close reacts faster in
  momentum. But wick still labels the regime more accurately and with 33%
  fewer whipsaws.
- **H1 (both markets):** neither method has real edge — raw structure
  trend-following on H1 is noise without additional filters (matches the
  session-edge findings: the filter, not the structure source, is the H1
  edge).
- **Sweep quality (the SweepReversal logic):** wick-level sweeps on BTC-H4
  fade profitably (+15…+20 bp mean, 53–55% win); close-level sweeps there
  are NEGATIVE (-12 bp at k=3). A wick extreme is where resting liquidity
  actually sits; a close level is not — close-based "sweeps" carry no
  liquidity information.

## Verdict

**Wicks identify market structure better; closes confirm breaks better.**

1. Build levels from WICK extremes (high/low pivots): the structure regime
   is ~34% more stable, agrees with forward direction more often, dominates
   on Daily, and its sweeps are real liquidity events that mean-revert.
2. Confirm breaks with CANDLE CLOSURE through the level — close-through is
   what carries follow-through; wick-through alone is a sweep, and fading
   it (on H4) tests positive.
3. The only thing close-based structure buys is earlier break detection on
   mid timeframes — a speed/stability trade-off, not better identification.

This hybrid — wick levels + close confirmation + sweep classification — is
exactly the rule set already implemented in StructureBasics v2.8: the data
supports keeping it.
