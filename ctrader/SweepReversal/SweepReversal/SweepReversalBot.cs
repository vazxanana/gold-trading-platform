// ─── SWEEP REVERSAL BOT ─────────────────────────────────────────────────────
// cTrader twin of tradingview/SweepReversal.pine — trade the liquidity
// sweep itself:
//
//   • Two structure layers (H4 + Daily) built from fractal pivots
//     (strength 3). Every confirmed swing high/low is a level.
//   • A level taken by an H1 WICK only (no layer close through) is a
//     liquidity Sweep — and that IS the signal:
//         swept HIGH → SELL toward the nearest LIVE swing LOW of the
//                      SAME timeframe below price
//         swept LOW  → BUY toward the nearest LIVE swing HIGH of the
//                      SAME timeframe above price
//   • SL beyond the sweep wick (the sweeping H1 bar's extreme + buffer).
//   • If the swept level later gets CLOSED through by the layer candle
//     (sweep upgrades to BOS) the reversal premise is dead — the position
//     closes immediately.
//   • One position at a time; each level fires at most once per sweep.
//   • Optional H4 trend filter (off by default — sweep reversals are
//     counter-move by nature).
//
// Mechanics: decisions on COMPLETED bars only. H1 is the master clock;
// H4/D1 state advances only up to each H1 bar's open time (mirrors Pine
// request.security f()[1] + lookahead_on visibility). Orders are only
// executed for the newest completed H1 bar — historical preload just
// builds state. All price distances are USD, converted via Symbol.PipSize.

using System;
using System.Collections.Generic;
using cAlgo.API;
using cAlgo.API.Internals;

namespace cAlgo.Robots
{
    [Robot(AccessRights = AccessRights.None)]
    public class SweepReversalBot : Robot
    {
        private const string Label = "SweepRev";

        // ── Signal ──────────────────────────────────────────────────────────
        [Parameter("Swing strength (bars each side)", Group = "Signal", DefaultValue = 3, MinValue = 2)]
        public int SwingStrength { get; set; }

        [Parameter("Trade sweeps of H4 levels", Group = "Signal", DefaultValue = true)]
        public bool TradeA { get; set; }

        [Parameter("Trade sweeps of Daily levels", Group = "Signal", DefaultValue = true)]
        public bool TradeB { get; set; }

        [Parameter("Signals only with the H4 trend", Group = "Signal", DefaultValue = false)]
        public bool TrendFilter { get; set; }

        [Parameter("Close when swept level breaks (BOS)", Group = "Signal", DefaultValue = true)]
        public bool CloseOnPremiseDead { get; set; }

        [Parameter("Max levels kept per layer", Group = "Signal", DefaultValue = 8, MinValue = 2)]
        public int MaxKeep { get; set; }

        // ── Risk ────────────────────────────────────────────────────────────
        [Parameter("Quantity (lots)", Group = "Risk", DefaultValue = 0.01, MinValue = 0.01, Step = 0.01)]
        public double QuantityLots { get; set; }

        [Parameter("SL buffer beyond sweep wick (USD)", Group = "Risk", DefaultValue = 0.5, MinValue = 0)]
        public double SlBufferUsd { get; set; }

        [Parameter("Max SL distance (USD, 0 = off)", Group = "Risk", DefaultValue = 0, MinValue = 0)]
        public double MaxSlUsd { get; set; }

        [Parameter("Verbose log", Group = "Risk", DefaultValue = true)]
        public bool Verbose { get; set; }

        [Parameter("Draw on chart (levels, sweeps, TP/SL)", Group = "Risk", DefaultValue = true)]
        public bool Draw { get; set; }

        // ── chart colors (same palette as the Pine script) ──────────────────
        private static readonly Color ColUp = Color.FromArgb(255, 74, 222, 128);      // #4ADE80
        private static readonly Color ColDn = Color.FromArgb(255, 251, 113, 133);     // #FB7185
        private static readonly Color ColBos = Color.FromArgb(255, 96, 165, 250);     // #60A5FA
        private static readonly Color ColSweep = Color.FromArgb(255, 251, 191, 36);   // #FBBF24

        // ── state ───────────────────────────────────────────────────────────
        private Bars _h1, _h4, _d1;
        private int _h1Done;

        private sealed class Layer
        {
            public Bars Bars;
            public string Name;
            public int Done = -1;
            public double LastClose = double.NaN;
            public DateTime LastPivotLowT = DateTime.MinValue;
            public DateTime LastPivotHighT = DateTime.MinValue;
            public List<Level> Levels = new List<Level>();
        }

        private sealed class Level
        {
            public double Price;
            public bool IsLow;
            public int State;                       // 0 live, 2 done, 3 swept
            public DateTime PivotT;
            public string Key;
        }

        private Layer _a, _b;
        private int _trend;                          // 1 up, -1 down (last H4 BOS)

        // the open trade's premise: the swept level (NaN when flat)
        private double _posLvl = double.NaN;

        // active TP/SL lines of the open trade
        private string _tpName, _slName;
        private double _tpPrice, _slPrice;
        private DateTime _tpStart;
        private Color _tpCol;

        protected override void OnStart()
        {
            _h1 = MarketData.GetBars(TimeFrame.Hour);
            _h4 = MarketData.GetBars(TimeFrame.Hour4);
            _d1 = MarketData.GetBars(TimeFrame.Daily);
            _a = new Layer { Bars = _h4, Name = "H4" };
            _b = new Layer { Bars = _d1, Name = "Daily" };
            _h1Done = -1;
            Print("[START] SweepReversal | swS={0} tradeA={1} tradeB={2} trendFilter={3} premiseClose={4} lots={5} slBuf={6} maxSl={7}",
                SwingStrength, TradeA, TradeB, TrendFilter, CloseOnPremiseDead, QuantityLots, SlBufferUsd, MaxSlUsd);
        }

        protected override void OnBar()
        {
            int lastComplete = _h1.Count - 2;
            for (int h = _h1Done + 1; h <= lastComplete; h++)
            {
                DateTime h1Open = _h1.OpenTimes[h];
                AdvanceLayer(_a, h1Open);
                AdvanceLayer(_b, h1Open);
                ProcessH1Bar(h, h == lastComplete);
                _h1Done = h;
            }
        }

        private void AdvanceLayer(Layer ly, DateTime upTo)
        {
            Bars b = ly.Bars;
            while (ly.Done + 2 < b.Count && b.OpenTimes[ly.Done + 2] <= upTo)
            {
                int idx = ly.Done + 1;
                ly.LastClose = b.ClosePrices[idx];

                int c = idx - SwingStrength;
                if (c >= SwingStrength)
                {
                    bool isPl = true, isPh = true;
                    for (int k = c - SwingStrength; k <= c + SwingStrength; k++)
                    {
                        if (k == c) continue;
                        if (b.LowPrices[k] <= b.LowPrices[c]) isPl = false;
                        if (b.HighPrices[k] >= b.HighPrices[c]) isPh = false;
                    }
                    DateTime pt = b.OpenTimes[c];
                    if (isPl && pt != ly.LastPivotLowT)
                    {
                        ly.LastPivotLowT = pt;
                        var lv = new Level { Price = b.LowPrices[c], IsLow = true, State = 0, PivotT = pt, Key = string.Format("{0}-L-{1}", ly.Name, pt.Ticks) };
                        ly.Levels.Add(lv);
                        DrawLevel(ly, lv, b.OpenTimes[idx]);
                    }
                    if (isPh && pt != ly.LastPivotHighT)
                    {
                        ly.LastPivotHighT = pt;
                        var lv = new Level { Price = b.HighPrices[c], IsLow = false, State = 0, PivotT = pt, Key = string.Format("{0}-H-{1}", ly.Name, pt.Ticks) };
                        ly.Levels.Add(lv);
                        DrawLevel(ly, lv, b.OpenTimes[idx]);
                    }
                    while (ly.Levels.Count > 2 * MaxKeep)
                        ly.Levels.RemoveAt(0);
                }
                ly.Done = idx;
            }
        }

        private void DrawLevel(Layer ly, Level lv, DateTime rightT)
        {
            if (!Draw) return;
            Chart.DrawTrendLine(lv.Key + "-ln", lv.PivotT, lv.Price, rightT, lv.Price, Color.Silver, 1, LineStyle.Solid);
            Chart.DrawText(lv.Key + "-cap", ly.Name, lv.PivotT, lv.Price, Color.White);
        }

        private void MarkBreak(Level lv, DateTime t, bool bos)
        {
            if (!Draw) return;
            Chart.DrawText(lv.Key + "-brk", bos ? "BOS" : "Sweep", t, lv.Price, bos ? ColBos : ColSweep);
        }

        private void ProcessH1Bar(int h, bool liveBar)
        {
            double hi = _h1.HighPrices[h], lo = _h1.LowPrices[h];
            DateTime t = _h1.OpenTimes[h];

            RunLevels(_a, hi, lo, t, true, TradeA, liveBar);
            RunLevels(_b, hi, lo, t, false, TradeB, liveBar);

            // extend the open trade's dotted TP/SL lines to the current bar
            if (Draw && _tpName != null)
            {
                if (Positions.FindAll(Label, SymbolName).Length > 0)
                {
                    Chart.DrawTrendLine(_tpName, _tpStart, _tpPrice, t, _tpPrice, _tpCol, 2, LineStyle.Dots);
                    Chart.DrawTrendLine(_slName, _tpStart, _slPrice, t, _slPrice, Color.Gray, 1, LineStyle.Dots);
                }
                else
                {
                    _tpName = null;
                    _slName = null;
                }
            }

            if (Positions.FindAll(Label, SymbolName).Length == 0)
                _posLvl = double.NaN;
        }

        private void RunLevels(Layer ly, double hi, double lo, DateTime t, bool drivesTrend, bool tradable, bool liveBar)
        {
            foreach (var lv in ly.Levels)
            {
                bool closeThrough = !double.IsNaN(ly.LastClose) &&
                    (lv.IsLow ? ly.LastClose < lv.Price : ly.LastClose > lv.Price);
                bool wickThrough = lv.IsLow ? lo < lv.Price : hi > lv.Price;

                if (lv.State == 0 && !closeThrough && !wickThrough)
                {
                    DrawLevel(ly, lv, t);   // still live — extend the line
                    continue;
                }

                if (lv.State == 0 && (closeThrough || wickThrough))
                {
                    DrawLevel(ly, lv, t);   // freeze the line at the break bar
                    if (closeThrough)
                    {
                        if (drivesTrend)
                            _trend = lv.IsLow ? -1 : 1;
                        lv.State = 2;
                        MarkBreak(lv, t, true);
                        if (Verbose)
                            Print("[BOS] {0} {1} {2:F2} @ {3:yyyy-MM-dd HH:mm}", ly.Name, lv.IsLow ? "low" : "high", lv.Price, t);
                    }
                    else
                    {
                        // liquidity sweep → reversal signal, target the
                        // opposite side of the SAME timeframe
                        lv.State = 3;
                        MarkBreak(lv, t, false);
                        if (Verbose)
                            Print("[SWEEP] {0} {1} {2:F2} @ {3:yyyy-MM-dd HH:mm}", ly.Name, lv.IsLow ? "low" : "high", lv.Price, t);
                        bool sBull = lv.IsLow;
                        if (tradable && (!TrendFilter || _trend == (sBull ? 1 : -1)))
                        {
                            double slP = sBull ? lo - SlBufferUsd : hi + SlBufferUsd;
                            if (liveBar)
                                Fire(sBull, ly, lv, slP, t);
                            else if (Verbose)
                                Print("[SIG-HIST] {0} (preload) @ {1:yyyy-MM-dd HH:mm}", sBull ? "BUY" : "SELL", t);
                        }
                    }
                }
                else if (lv.State == 3 && closeThrough)
                {
                    // sweep upgrades to BOS: structure actually broke
                    if (drivesTrend)
                        _trend = lv.IsLow ? -1 : 1;
                    lv.State = 2;
                    MarkBreak(lv, t, true);
                    if (Verbose)
                        Print("[BOS] {0} {1} {2:F2} (sweep upgrade) @ {3:yyyy-MM-dd HH:mm}", ly.Name, lv.IsLow ? "low" : "high", lv.Price, t);
                    if (CloseOnPremiseDead && !double.IsNaN(_posLvl) && _posLvl == lv.Price)
                    {
                        foreach (var pos in Positions.FindAll(Label, SymbolName))
                        {
                            Print("[EXIT] swept level {0:F2} broke — premise dead, closing {1} (net {2:F2})", lv.Price, pos.TradeType, pos.NetProfit);
                            ClosePosition(pos);
                        }
                        _posLvl = double.NaN;
                    }
                }
            }
        }

        // TP = nearest LIVE opposite level of the SAME layer
        private double FindTarget(Layer ly, bool bull, double refPrice)
        {
            double tgt = double.NaN;
            foreach (var lv in ly.Levels)
            {
                if (lv.State != 0) continue;
                if (bull && !lv.IsLow && lv.Price > refPrice && (double.IsNaN(tgt) || lv.Price < tgt))
                    tgt = lv.Price;
                if (!bull && lv.IsLow && lv.Price < refPrice && (double.IsNaN(tgt) || lv.Price > tgt))
                    tgt = lv.Price;
            }
            return tgt;
        }

        private void Fire(bool bull, Layer ly, Level swept, double sl, DateTime t)
        {
            if (Positions.FindAll(Label, SymbolName).Length > 0)
            {
                if (Verbose)
                    Print("[SKIP] {0} signal @ {1:yyyy-MM-dd HH:mm} — position already open", bull ? "BUY" : "SELL", t);
                return;
            }

            double entry = bull ? Symbol.Ask : Symbol.Bid;
            double slDist = bull ? entry - sl : sl - entry;
            if (slDist <= 0)
            {
                Print("[SKIP] {0} @ {1:yyyy-MM-dd HH:mm} — SL {2:F2} on wrong side of entry {3:F2}", bull ? "BUY" : "SELL", t, sl, entry);
                return;
            }
            if (MaxSlUsd > 0 && slDist > MaxSlUsd)
            {
                Print("[SKIP] {0} @ {1:yyyy-MM-dd HH:mm} — SL distance {2:F2} > max {3:F2}", bull ? "BUY" : "SELL", t, slDist, MaxSlUsd);
                return;
            }

            double tgt = FindTarget(ly, bull, entry);
            if (double.IsNaN(tgt))
            {
                Print("[SKIP] {0} @ {1:yyyy-MM-dd HH:mm} — no live {2} target on the other side", bull ? "BUY" : "SELL", t, ly.Name);
                return;
            }
            double tpDist = bull ? tgt - entry : entry - tgt;
            if (tpDist <= 0)
            {
                Print("[SKIP] {0} @ {1:yyyy-MM-dd HH:mm} — target {2:F2} on wrong side of entry {3:F2}", bull ? "BUY" : "SELL", t, tgt, entry);
                return;
            }

            double slPips = slDist / Symbol.PipSize;
            double tpPips = tpDist / Symbol.PipSize;
            double volume = Symbol.NormalizeVolumeInUnits(Symbol.QuantityToVolumeInUnits(QuantityLots), RoundingMode.Down);
            var type = bull ? TradeType.Buy : TradeType.Sell;
            var res = ExecuteMarketOrder(type, SymbolName, volume, Label, slPips, tpPips);
            if (res.IsSuccessful)
            {
                _posLvl = swept.Price;
                Print("[ENTRY] {0} @ {1:F2} SL {2:F2} TP {3:F2} (swept {4} {5} {6:F2}) t={7:yyyy-MM-dd HH:mm}",
                    bull ? "BUY" : "SELL", entry, sl, tgt, ly.Name, swept.IsLow ? "low" : "high", swept.Price, t);
                if (Draw)
                {
                    string en = string.Format("sig-{0}", t.Ticks);
                    Color c = bull ? ColUp : ColDn;
                    Chart.DrawIcon(en + "-ic", bull ? ChartIconType.UpTriangle : ChartIconType.DownTriangle, t, entry, c);
                    Chart.DrawText(en + "-txt", bull ? "BUY" : "SELL", t, entry, c);
                    Chart.DrawText(en + "-tptxt", string.Format("TP {0:F2}", tgt), t, tgt, c);
                    Chart.DrawText(en + "-sltxt", string.Format("SL {0:F2}", sl), t, sl, Color.Gray);
                    _tpName = en + "-tp";
                    _slName = en + "-sl";
                    _tpPrice = tgt;
                    _slPrice = sl;
                    _tpStart = t;
                    _tpCol = c;
                    Chart.DrawTrendLine(_tpName, _tpStart, _tpPrice, t, _tpPrice, _tpCol, 2, LineStyle.Dots);
                    Chart.DrawTrendLine(_slName, _tpStart, _slPrice, t, _slPrice, Color.Gray, 1, LineStyle.Dots);
                }
            }
            else
                Print("[ERROR] order failed: {0}", res.Error);
        }
    }
}
