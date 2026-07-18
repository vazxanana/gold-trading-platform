// ─── STRUCTURE BASICS BOT ───────────────────────────────────────────────────
// cTrader twin of tradingview/StructureBasics.pine v0.15 — same engine,
// same trades:
//
//   • Two structure layers (H4 + Daily) built from fractal pivots
//     (strength 3). Every confirmed swing high/low is a level.
//   • A level taken by an H1 WICK only is a liquidity Sweep; a level the
//     layer candle CLOSES through is a BOS. A sweep later closed through
//     upgrades to BOS.
//   • On a BOS the bot marks the H1 order block: the last opposite
//     CONFIRMED H1 candle strictly before the H1 bar that first crossed
//     the level (bull OB = last red candle, box wick-low → body-top;
//     bear OB mirrored). Sweeps only stash the candidate; it materialises
//     if the sweep upgrades.
//   • Retest arming: price must first trade a FULL H1 bar clear of the box
//     on the breakout side; only the return into the box arms it. The
//     breakout impulse brushing the box never arms.
//   • OB flips: an H1 close through the far side flips the zone's polarity
//     (broken demand → supply and mirror); the flipped zone re-arms by the
//     same leave-then-return rule. One flip per box; a second close-through
//     kills it.
//   • Entry: while an OB is armed, an M5 change of character — an M5 close
//     breaking the latest strength-2 M5 swing high (longs) / low (shorts),
//     each swing consumed by its break — fires the trade.
//   • H4 trend filter: trend = direction of the last H4 BOS. Buys only in
//     an up trend, sells only in a down trend.
//   • TP = nearest LIVE (untaken, unswept) H4 level beyond price.
//     SL = far edge of the OB that produced the trade ± buffer (USD).
//     Position closes early if the H4 trend flips against it.
//
// Mechanics: decisions on COMPLETED bars only. The M5 series is the master
// clock; H1/H4/D1 state advances only up to each processed bar's open time
// (mirrors Pine request.security f()[1] + lookahead_on visibility). Orders
// are only executed for the newest completed M5 bar — historical preload
// just builds state. All price distances are USD, converted via
// Symbol.PipSize.

using System;
using System.Collections.Generic;
using System.Linq;
using cAlgo.API;
using cAlgo.API.Internals;

namespace cAlgo.Robots
{
    [Robot(AccessRights = AccessRights.None)]
    public class StructureBasicsBot : Robot
    {
        private const string Label = "StructBasics";

        // ── Signal ──────────────────────────────────────────────────────────
        [Parameter("Swing strength (bars each side)", Group = "Signal", DefaultValue = 3, MinValue = 2)]
        public int SwingStrength { get; set; }

        [Parameter("Order blocks on BOS only (skip sweeps)", Group = "Signal", DefaultValue = true)]
        public bool ObBosOnly { get; set; }

        [Parameter("Signals only with the H4 trend", Group = "Signal", DefaultValue = true)]
        public bool TrendFilter { get; set; }

        [Parameter("Trade H1 OB flips", Group = "Signal", DefaultValue = true)]
        public bool TradeObFlips { get; set; }

        [Parameter("Max levels kept per layer", Group = "Signal", DefaultValue = 8, MinValue = 2)]
        public int MaxKeep { get; set; }

        [Parameter("Max order blocks kept per layer", Group = "Signal", DefaultValue = 12, MinValue = 2)]
        public int MaxObs { get; set; }

        // ── Risk ────────────────────────────────────────────────────────────
        [Parameter("Quantity (lots)", Group = "Risk", DefaultValue = 0.01, MinValue = 0.01, Step = 0.01)]
        public double QuantityLots { get; set; }

        [Parameter("SL buffer beyond OB edge (USD)", Group = "Risk", DefaultValue = 0.5, MinValue = 0)]
        public double SlBufferUsd { get; set; }

        [Parameter("Max SL distance (USD, 0 = off)", Group = "Risk", DefaultValue = 0, MinValue = 0)]
        public double MaxSlUsd { get; set; }

        [Parameter("Close position on H4 trend flip", Group = "Risk", DefaultValue = true)]
        public bool CloseOnTrendFlip { get; set; }

        [Parameter("Verbose log", Group = "Risk", DefaultValue = true)]
        public bool Verbose { get; set; }

        [Parameter("Draw on chart (levels, BOS, OBs, TP)", Group = "Risk", DefaultValue = true)]
        public bool Draw { get; set; }

        // ── chart colors (same palette as the Pine script) ──────────────────
        private static readonly Color ColUp = Color.FromArgb(255, 74, 222, 128);      // #4ADE80
        private static readonly Color ColDn = Color.FromArgb(255, 251, 113, 133);     // #FB7185
        private static readonly Color ColUpFill = Color.FromArgb(45, 74, 222, 128);
        private static readonly Color ColDnFill = Color.FromArgb(45, 251, 113, 133);
        private static readonly Color ColUpDim = Color.FromArgb(18, 74, 222, 128);
        private static readonly Color ColDnDim = Color.FromArgb(18, 251, 113, 133);
        private static readonly Color ColBos = Color.FromArgb(255, 96, 165, 250);     // #60A5FA
        private static readonly Color ColSweep = Color.FromArgb(255, 251, 191, 36);   // #FBBF24

        // ── state ───────────────────────────────────────────────────────────
        private Bars _m5, _h1, _h4, _d1;
        private int _m5Done, _h1Done;               // last processed index per series

        private sealed class Layer
        {
            public Bars Bars;
            public string Name;
            public int Done = -1;                   // last layer bar folded into state
            public double LastClose = double.NaN;   // last VISIBLE layer close
            public DateTime LastPivotLowT = DateTime.MinValue;
            public DateTime LastPivotHighT = DateTime.MinValue;
            public List<Level> Levels = new List<Level>();
            public List<OrderBlock> Obs = new List<OrderBlock>();
        }

        private sealed class Level
        {
            public double Price;
            public bool IsLow;
            public int State;                       // 0 live, 2 done, 3 swept
            public DateTime PivotT;
            public string Key;                      // chart object name base
            // OB candidate stashed at a sweep (drawn only on upgrade)
            public bool HasPend;
            public double PendTop, PendBot;
            public DateTime PendT;
        }

        private sealed class OrderBlock
        {
            public double Top, Bot;
            public bool Bull;
            public int State;                       // 0 live, 1 frozen (mitigated/dead)
            public bool Armed, Left, Flipped;
            public DateTime CandleT;
            public string Key;                      // chart object name base
        }

        // active TP line of the open trade
        private string _tpName;
        private double _tpPrice;
        private DateTime _tpStart;
        private Color _tpCol;

        private Layer _a, _b;
        private int _trend;                          // 1 up, -1 down (last H4 BOS)

        // H1 opposite-candle memory (strictly before the crossing bar)
        private double _redTop = double.NaN, _redLo = double.NaN;
        private DateTime _redT = DateTime.MinValue;
        private double _grnBot = double.NaN, _grnHi = double.NaN;
        private DateTime _grnT = DateTime.MinValue;

        // rolling M5 structure (swing strength 2)
        private readonly List<double> _m5H = new List<double>();
        private readonly List<double> _m5L = new List<double>();
        private double _swingHi = double.NaN, _swingLo = double.NaN;

        protected override void OnStart()
        {
            _m5 = MarketData.GetBars(TimeFrame.Minute5);
            _h1 = MarketData.GetBars(TimeFrame.Hour);
            _h4 = MarketData.GetBars(TimeFrame.Hour4);
            _d1 = MarketData.GetBars(TimeFrame.Daily);
            _a = new Layer { Bars = _h4, Name = "H4" };
            _b = new Layer { Bars = _d1, Name = "Daily" };
            _m5Done = -1;
            _h1Done = -1;
            Print("[START] StructureBasics | swS={0} bosOnly={1} trendFilter={2} obFlips={3} lots={4} slBuf={5} maxSl={6} flipClose={7}",
                SwingStrength, ObBosOnly, TrendFilter, TradeObFlips, QuantityLots, SlBufferUsd, MaxSlUsd, CloseOnTrendFlip);
        }

        protected override void OnBar()
        {
            ProcessNewBars();
        }

        private void ProcessNewBars()
        {
            // completed M5 bars only (Count-1 is forming)
            int lastComplete = _m5.Count - 2;
            for (int i = _m5Done + 1; i <= lastComplete; i++)
            {
                DateTime m5Open = _m5.OpenTimes[i];

                // advance the H1 clock: every H1 bar fully closed before this
                // M5 bar opened
                while (_h1Done + 2 < _h1.Count && _h1.OpenTimes[_h1Done + 2] <= m5Open)
                {
                    int h = _h1Done + 1;
                    DateTime h1Open = _h1.OpenTimes[h];
                    AdvanceLayer(_a, h1Open, true);
                    AdvanceLayer(_b, h1Open, false);
                    ProcessH1Bar(h);
                    _h1Done = h;
                }

                bool liveBar = i == lastComplete;
                ProcessM5Bar(i, liveBar);
                _m5Done = i;
            }
        }

        // fold layer bars that became visible before 'upTo' into the state:
        // update the visible layer close and confirm new pivots as levels
        private void AdvanceLayer(Layer ly, DateTime upTo, bool isH4)
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

        private void ProcessH1Bar(int h)
        {
            double hi = _h1.HighPrices[h], lo = _h1.LowPrices[h], cl = _h1.ClosePrices[h];
            double op = _h1.OpenPrices[h];
            DateTime t = _h1.OpenTimes[h];

            RunLevels(_a, hi, lo, t, true);
            RunLevels(_b, hi, lo, t, false);

            RunObLifecycle(_a, hi, lo, cl, t);
            RunObLifecycle(_b, hi, lo, cl, t);

            if (CloseOnTrendFlip)
                CloseAgainstTrend();

            // extend the open trade's dotted TP line to the current bar
            if (Draw && _tpName != null)
            {
                if (Positions.FindAll(Label, SymbolName).Length > 0)
                    Chart.DrawTrendLine(_tpName, _tpStart, _tpPrice, t, _tpPrice, _tpCol, 2, LineStyle.Dots);
                else
                    _tpName = null;   // trade over — line stays frozen where it ended
            }

            // refresh the opposite-candle memory LAST, so lookups above see
            // the last opposite H1 candle strictly BEFORE the crossing bar
            if (cl < op)
            {
                _redTop = Math.Max(op, cl);
                _redLo = lo;
                _redT = t;
            }
            else if (cl > op)
            {
                _grnBot = Math.Min(op, cl);
                _grnHi = hi;
                _grnT = t;
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

        private void RunLevels(Layer ly, double hi, double lo, DateTime t, bool drivesTrend)
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
                    bool obBull = !lv.IsLow;
                    double obTop = obBull ? _redTop : _grnHi;
                    double obBot = obBull ? _redLo : _grnBot;
                    DateTime obT = obBull ? _redT : _grnT;
                    bool haveOb = obBull ? !double.IsNaN(_redTop) : !double.IsNaN(_grnHi);

                    DrawLevel(ly, lv, t);   // freeze the line at the break bar
                    if (closeThrough)
                    {
                        if (drivesTrend)
                            _trend = lv.IsLow ? -1 : 1;
                        if (haveOb)
                            AddOb(ly, obBull, obTop, obBot, obT, t);
                        lv.State = 2;
                        MarkBreak(lv, t, true);
                        if (Verbose)
                            Print("[BOS] {0} {1} {2:F2} @ {3:yyyy-MM-dd HH:mm}", ly.Name, lv.IsLow ? "low" : "high", lv.Price, t);
                    }
                    else
                    {
                        if (haveOb)
                        {
                            if (ObBosOnly)
                            {
                                lv.HasPend = true;
                                lv.PendTop = obTop;
                                lv.PendBot = obBot;
                                lv.PendT = obT;
                            }
                            else
                                AddOb(ly, obBull, obTop, obBot, obT, t);
                        }
                        lv.State = 3;
                        MarkBreak(lv, t, false);
                        if (Verbose)
                            Print("[SWEEP] {0} {1} {2:F2} @ {3:yyyy-MM-dd HH:mm}", ly.Name, lv.IsLow ? "low" : "high", lv.Price, t);
                    }
                }
                else if (lv.State == 3 && closeThrough)
                {
                    // sweep upgrades to BOS on the layer close-through
                    if (drivesTrend)
                        _trend = lv.IsLow ? -1 : 1;
                    if (ObBosOnly && lv.HasPend)
                        AddOb(ly, !lv.IsLow, lv.PendTop, lv.PendBot, lv.PendT, t);
                    lv.State = 2;
                    MarkBreak(lv, t, true);   // sweep label upgrades to BOS
                    if (Verbose)
                        Print("[BOS] {0} {1} {2:F2} (sweep upgrade) @ {3:yyyy-MM-dd HH:mm}", ly.Name, lv.IsLow ? "low" : "high", lv.Price, t);
                }
            }
        }

        private void AddOb(Layer ly, bool bull, double top, double bot, DateTime candleT, DateTime t)
        {
            if (ly.Obs.Any(o => o.CandleT == candleT))
                return;
            var ob = new OrderBlock { Top = top, Bot = bot, Bull = bull, State = 0, CandleT = candleT, Key = string.Format("ob-{0}-{1}", ly.Name, candleT.Ticks) };
            ly.Obs.Add(ob);
            while (ly.Obs.Count > MaxObs)
                ly.Obs.RemoveAt(0);
            DrawOb(ob, t);
            if (Verbose)
                Print("[OB] {0} {1} H1 OB {2:F2}-{3:F2} (candle {4:MM-dd HH:mm}) @ {5:yyyy-MM-dd HH:mm}",
                    ly.Name, bull ? "bull" : "bear", bot, top, candleT, t);
        }

        private void DrawOb(OrderBlock ob, DateTime rightT)
        {
            if (!Draw) return;
            Color fill = ob.State == 1 ? (ob.Bull ? ColUpDim : ColDnDim) : (ob.Bull ? ColUpFill : ColDnFill);
            var rc = Chart.DrawRectangle(ob.Key, ob.CandleT, ob.Top, rightT, ob.Bot, fill);
            rc.IsFilled = true;
            Chart.DrawText(ob.Key + "-cap", ob.Flipped ? "H1 OB Flip" : "H1 OB", ob.CandleT, ob.Top, ob.Bull ? ColUp : ColDn);
        }

        private void RunObLifecycle(Layer ly, double hi, double lo, double cl, DateTime t)
        {
            foreach (var ob in ly.Obs)
            {
                if (ob.State == 0)
                {
                    bool viol = ob.Bull ? cl < ob.Bot : cl > ob.Top;
                    if (viol)
                    {
                        if (TradeObFlips && !ob.Flipped)
                        {
                            // broken demand flips to supply (and mirror)
                            ob.Bull = !ob.Bull;
                            ob.Flipped = true;
                            ob.Left = false;
                            DrawOb(ob, t);
                            if (Verbose)
                                Print("[FLIP] {0} OB {1:F2}-{2:F2} now {3} @ {4:yyyy-MM-dd HH:mm}",
                                    ly.Name, ob.Bot, ob.Top, ob.Bull ? "bull" : "bear", t);
                        }
                        else
                        {
                            ob.State = 1;
                            ob.Armed = false;
                            DrawOb(ob, t);
                        }
                    }
                    else
                    {
                        if (!ob.Left && (ob.Bull ? lo > ob.Top : hi < ob.Bot))
                            ob.Left = true;
                        bool mit = ob.Left && (ob.Bull ? lo <= ob.Top : hi >= ob.Bot);
                        if (mit)
                        {
                            ob.State = 1;
                            ob.Armed = true;
                            DrawOb(ob, t);
                            if (Verbose)
                                Print("[ARM] {0} {1} OB {2:F2}-{3:F2} retested @ {4:yyyy-MM-dd HH:mm}",
                                    ly.Name, ob.Bull ? "bull" : "bear", ob.Bot, ob.Top, t);
                        }
                        else
                            DrawOb(ob, t);   // still live — extend the box
                    }
                }
                else if (ob.State == 1 && ob.Armed)
                {
                    bool inval = ob.Bull ? cl < ob.Bot : cl > ob.Top;
                    if (inval)
                        ob.Armed = false;
                }
            }
        }

        private void ProcessM5Bar(int i, bool liveBar)
        {
            _m5H.Add(_m5.HighPrices[i]);
            _m5L.Add(_m5.LowPrices[i]);
            int sz = _m5H.Count;
            if (sz >= 5)
            {
                double pv = _m5H[sz - 3];
                if (pv >= _m5H[sz - 5] && pv >= _m5H[sz - 4] && pv > _m5H[sz - 2] && pv > _m5H[sz - 1])
                    _swingHi = pv;
                double pl = _m5L[sz - 3];
                if (pl <= _m5L[sz - 5] && pl <= _m5L[sz - 4] && pl < _m5L[sz - 2] && pl < _m5L[sz - 1])
                    _swingLo = pl;
            }
            if (sz > 400)
            {
                _m5H.RemoveAt(0);
                _m5L.RemoveAt(0);
            }

            double cc = _m5.ClosePrices[i];
            bool chUp = false, chDn = false;
            if (!double.IsNaN(_swingHi) && cc > _swingHi)
            {
                chUp = true;
                _swingHi = double.NaN;      // consume: one break = one ChoCh
            }
            if (!double.IsNaN(_swingLo) && cc < _swingLo)
            {
                chDn = true;
                _swingLo = double.NaN;
            }
            if (!chUp && !chDn)
                return;

            Hunt(_a, chUp, chDn, liveBar, _m5.OpenTimes[i]);
            Hunt(_b, chUp, chDn, liveBar, _m5.OpenTimes[i]);
        }

        private void Hunt(Layer ly, bool chUp, bool chDn, bool liveBar, DateTime t)
        {
            foreach (var ob in ly.Obs)
            {
                if (ob.State != 1 || !ob.Armed)
                    continue;
                if (ob.Bull && chUp && (!TrendFilter || _trend == 1))
                {
                    ob.Armed = false;
                    if (liveBar)
                        Fire(true, ob, t);
                    else if (Verbose)
                        Print("[SIG-HIST] BUY (preload) @ {0:yyyy-MM-dd HH:mm}", t);
                }
                else if (!ob.Bull && chDn && (!TrendFilter || _trend == -1))
                {
                    ob.Armed = false;
                    if (liveBar)
                        Fire(false, ob, t);
                    else if (Verbose)
                        Print("[SIG-HIST] SELL (preload) @ {0:yyyy-MM-dd HH:mm}", t);
                }
            }
        }

        // TP = nearest LIVE H4 (layer A) level beyond price
        private double FindTarget(bool bull, double refPrice)
        {
            double tgt = double.NaN;
            foreach (var lv in _a.Levels)
            {
                if (lv.State != 0) continue;
                if (bull && !lv.IsLow && lv.Price > refPrice && (double.IsNaN(tgt) || lv.Price < tgt))
                    tgt = lv.Price;
                if (!bull && lv.IsLow && lv.Price < refPrice && (double.IsNaN(tgt) || lv.Price > tgt))
                    tgt = lv.Price;
            }
            return tgt;
        }

        private void Fire(bool bull, OrderBlock ob, DateTime t)
        {
            if (Positions.FindAll(Label, SymbolName).Length > 0)
            {
                if (Verbose)
                    Print("[SKIP] {0} signal @ {1:yyyy-MM-dd HH:mm} — position already open", bull ? "BUY" : "SELL", t);
                return;
            }

            double entry = bull ? Symbol.Ask : Symbol.Bid;
            double sl = bull ? ob.Bot - SlBufferUsd : ob.Top + SlBufferUsd;
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

            double tgt = FindTarget(bull, entry);
            double slPips = slDist / Symbol.PipSize;
            double? tpPips = null;
            if (!double.IsNaN(tgt))
            {
                double tpDist = bull ? tgt - entry : entry - tgt;
                if (tpDist > 0)
                    tpPips = tpDist / Symbol.PipSize;
            }

            double volume = Symbol.NormalizeVolumeInUnits(Symbol.QuantityToVolumeInUnits(QuantityLots), RoundingMode.Down);
            var type = bull ? TradeType.Buy : TradeType.Sell;
            var res = ExecuteMarketOrder(type, SymbolName, volume, Label, slPips, tpPips);
            if (res.IsSuccessful)
            {
                Print("[ENTRY] {0} @ {1:F2} SL {2:F2} TP {3} ({4} OB {5:F2}-{6:F2}{7}) t={8:yyyy-MM-dd HH:mm}",
                    bull ? "BUY" : "SELL", entry, sl, double.IsNaN(tgt) ? "none" : tgt.ToString("F2"),
                    ob.Bull ? "bull" : "bear", ob.Bot, ob.Top, ob.Flipped ? " flip" : "", t);
                if (Draw)
                {
                    string en = string.Format("sig-{0}", t.Ticks);
                    Color c = bull ? ColUp : ColDn;
                    Chart.DrawIcon(en + "-ic", bull ? ChartIconType.UpTriangle : ChartIconType.DownTriangle, t, entry, c);
                    Chart.DrawText(en + "-txt", bull ? "BUY" : "SELL", t, entry, c);
                    if (!double.IsNaN(tgt))
                    {
                        Chart.DrawText(en + "-tptxt", string.Format("TP {0:F2}", tgt), t, tgt, c);
                        _tpName = en + "-tp";
                        _tpPrice = tgt;
                        _tpStart = t;
                        _tpCol = c;
                        Chart.DrawTrendLine(_tpName, _tpStart, _tpPrice, t, _tpPrice, _tpCol, 2, LineStyle.Dots);
                    }
                }
            }
            else
                Print("[ERROR] order failed: {0}", res.Error);
        }

        private void CloseAgainstTrend()
        {
            foreach (var pos in Positions.FindAll(Label, SymbolName))
            {
                bool against = (pos.TradeType == TradeType.Buy && _trend == -1) ||
                               (pos.TradeType == TradeType.Sell && _trend == 1);
                if (against)
                {
                    Print("[EXIT] H4 flipped against {0} — closing (net {1:F2})", pos.TradeType, pos.NetProfit);
                    ClosePosition(pos);
                }
            }
        }
    }
}
