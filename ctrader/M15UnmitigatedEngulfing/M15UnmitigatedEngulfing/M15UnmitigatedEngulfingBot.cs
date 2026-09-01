// ─── M15 UNMITIGATED ENGULFING BOT ──────────────────────────────────────────
// Trades M15 engulfing zones while they are still UNMITIGATED, with a fixed
// 1:1 bracket (10 pip TP / 10 pip SL by default):
//
//   1. On every COMPLETED M15 bar, look for an engulfing candle
//      (house rule, same as GoldEngulfingConfluence:
//         bull  close > priorOpen && open <= priorClose && close > open
//         bear  close < priorOpen && open >= priorClose && close < open
//       plus, by default, the prior candle must be the opposite colour).
//   2. The engulfing candle becomes a ZONE:
//         bull (demand) → [candle low, body top]
//         bear (supply) → [body bottom, candle high]
//      (switch ZoneFullRange on to use the candle's whole low→high instead).
//   3. The zone is UNMITIGATED until price trades back into it. The FIRST
//      touch is the entry: buy a demand tap, sell a supply tap — checked on
//      tick so the fill is at the zone edge, not a bar late (matters when
//      the whole trade is 10 pips wide).
//   4. Bracket: TakeProfitPips / StopLossPips, both 10 by default = 1:1 RR.
//   5. A tapped zone is mitigated for good — one trade per zone, ever. A
//      zone whose far side is CLOSED through is dead (never traded), and
//      zones expire after MaxZoneAgeBars M15 bars (0 = never).
//
// Mechanics: zones are built only from completed bars (Bars.Count - 2), so
// there is no look-ahead; only the tap check runs on live price. Attach to
// the M15 chart. Distances are pips — Symbol.PipSize on XAUUSD/Raw is $0.01,
// so 10 pips = $0.10: OnStart prints the real price distance next to the
// live spread and warns when the bracket is too tight to survive costs.

using System;
using System.Collections.Generic;
using cAlgo.API;
using cAlgo.API.Internals;

namespace cAlgo.Robots
{
    public enum EgDirectionMode { Buy, Sell, Both }

    [Robot(AccessRights = AccessRights.None)]
    public class M15UnmitigatedEngulfingBot : Robot
    {
        private const string Label = "M15EgUnmit";

        // ── Signal ──────────────────────────────────────────────────────────
        [Parameter("Trade direction", Group = "Signal", DefaultValue = EgDirectionMode.Both)]
        public EgDirectionMode Direction { get; set; }

        [Parameter("Prior candle must be opposite colour", Group = "Signal", DefaultValue = true)]
        public bool RequireOppositePrior { get; set; }

        [Parameter("Zone = full candle range (off = wick to body)", Group = "Signal", DefaultValue = false)]
        public bool ZoneFullRange { get; set; }

        [Parameter("Min engulfing body (pips, 0 = off)", Group = "Signal", DefaultValue = 0, MinValue = 0)]
        public double MinBodyPips { get; set; }

        [Parameter("Max zone age (M15 bars, 0 = never)", Group = "Signal", DefaultValue = 0, MinValue = 0)]
        public int MaxZoneAgeBars { get; set; }

        [Parameter("Max zones kept per side", Group = "Signal", DefaultValue = 12, MinValue = 1)]
        public int MaxZones { get; set; }

        // ── Trade ───────────────────────────────────────────────────────────
        [Parameter("Take profit (pips)", Group = "Trade", DefaultValue = 10, MinValue = 0.1, Step = 0.1)]
        public double TakeProfitPips { get; set; }

        [Parameter("Stop loss (pips)", Group = "Trade", DefaultValue = 10, MinValue = 0.1, Step = 0.1)]
        public double StopLossPips { get; set; }

        [Parameter("Quantity (lots)", Group = "Trade", DefaultValue = 0.01, MinValue = 0.01, Step = 0.01)]
        public double QuantityLots { get; set; }

        [Parameter("Max spread (pips, 0 = off)", Group = "Trade", DefaultValue = 0, MinValue = 0)]
        public double MaxSpreadPips { get; set; }

        [Parameter("Max trades per day (0 = off)", Group = "Trade", DefaultValue = 0, MinValue = 0)]
        public int MaxTradesPerDay { get; set; }

        [Parameter("Verbose log", Group = "Trade", DefaultValue = true)]
        public bool Verbose { get; set; }

        [Parameter("Draw on chart", Group = "Trade", DefaultValue = true)]
        public bool Draw { get; set; }

        private static readonly Color ColUp = Color.FromArgb(255, 74, 222, 128);
        private static readonly Color ColDn = Color.FromArgb(255, 251, 113, 133);
        private static readonly Color ColUpFill = Color.FromArgb(45, 74, 222, 128);
        private static readonly Color ColDnFill = Color.FromArgb(45, 251, 113, 133);
        private static readonly Color ColUpDim = Color.FromArgb(16, 74, 222, 128);
        private static readonly Color ColDnDim = Color.FromArgb(16, 251, 113, 133);

        private sealed class Zone
        {
            public double Top, Bot;
            public bool Bull;
            public int Bar;                 // index of the engulfing bar
            public DateTime Time;
            public bool Mitigated;          // tapped once — never unmitigated again
            public bool Dead;               // closed through, or expired
            public string Key;
        }

        private readonly List<Zone> _zones = new List<Zone>();
        private int _lastBar = -1;
        private DateTime _day = DateTime.MinValue.Date;
        private int _dayTrades;

        protected override void OnStart()
        {
            if (Bars.TimeFrame != TimeFrame.Minute15)
                Print("[WARN] attach to the M15 chart — engulfing zones are meant to be M15 (current TF: {0})", Bars.TimeFrame);

            double slDist = StopLossPips * Symbol.PipSize;
            double tpDist = TakeProfitPips * Symbol.PipSize;
            double spread = Symbol.Spread;
            Print("[START] M15UnmitigatedEngulfing | dir={0} TP={1}p ({2}) SL={3}p ({4}) RR={5:F2} lots={6} zoneFull={7} strictEG={8}",
                Direction, TakeProfitPips, tpDist.ToString("F5"), StopLossPips, slDist.ToString("F5"),
                StopLossPips > 0 ? TakeProfitPips / StopLossPips : 0, QuantityLots, ZoneFullRange, RequireOppositePrior);
            Print("[START] pip size {0} · live spread {1:F5} ({2:F1} pips)", Symbol.PipSize, spread, spread / Symbol.PipSize);
            if (spread > 0 && slDist < 3 * spread)
                Print("[WARN] the {0}-pip bracket is only {1:F1}x the current spread — costs will dominate this edge. " +
                      "On XAUUSD one pip is $0.01, so 10 pips is a $0.10 target; consider 100+ pips ($1.00) there.",
                    StopLossPips, slDist / spread);
        }

        protected override void OnBar()
        {
            int cb = Bars.Count - 2;                 // completed bar
            if (cb < 1 || cb == _lastBar) return;
            _lastBar = cb;

            DateTime t = Bars.OpenTimes[cb];
            if (t.Date != _day)
            {
                _day = t.Date;
                _dayTrades = 0;
            }

            // ── zone housekeeping against this completed bar ────────────────
            double close = Bars.ClosePrices[cb];
            foreach (var z in _zones)
            {
                if (z.Dead) continue;
                if (z.Bull ? close < z.Bot : close > z.Top)
                {
                    z.Dead = true;
                    if (Verbose)
                        Print("[ZONE-DEAD] {0} zone {1:F5}-{2:F5} closed through @ {3:yyyy-MM-dd HH:mm}",
                            z.Bull ? "demand" : "supply", z.Bot, z.Top, t);
                }
                else if (MaxZoneAgeBars > 0 && cb - z.Bar > MaxZoneAgeBars)
                {
                    z.Dead = true;
                    if (Verbose)
                        Print("[ZONE-EXPIRED] {0} zone {1:F5}-{2:F5} after {3} bars", z.Bull ? "demand" : "supply", z.Bot, z.Top, MaxZoneAgeBars);
                }
            }

            // ── new engulfing zone from the completed bar ───────────────────
            double o = Bars.OpenPrices[cb], c = Bars.ClosePrices[cb];
            double h = Bars.HighPrices[cb], l = Bars.LowPrices[cb];
            double po = Bars.OpenPrices[cb - 1], pc = Bars.ClosePrices[cb - 1];

            bool bull = c > po && o <= pc && c > o && (!RequireOppositePrior || pc < po);
            bool bear = c < po && o >= pc && c < o && (!RequireOppositePrior || pc > po);

            if (bull || bear)
            {
                double body = Math.Abs(c - o);
                if (MinBodyPips > 0 && body < MinBodyPips * Symbol.PipSize)
                {
                    if (Verbose)
                        Print("[SKIP-EG] {0} engulfing body {1:F1} pips < min {2} @ {3:yyyy-MM-dd HH:mm}",
                            bull ? "bull" : "bear", body / Symbol.PipSize, MinBodyPips, t);
                }
                else if ((bull && Direction != EgDirectionMode.Sell) || (bear && Direction != EgDirectionMode.Buy))
                {
                    var z = new Zone
                    {
                        Bull = bull,
                        Top = bull ? (ZoneFullRange ? h : Math.Max(o, c)) : h,
                        Bot = bull ? l : (ZoneFullRange ? l : Math.Min(o, c)),
                        Bar = cb,
                        Time = t,
                        Key = string.Format("eg-{0:yyyyMMddHHmm}-{1}", t, bull ? "b" : "s")
                    };
                    _zones.Add(z);
                    if (Verbose)
                        Print("[ZONE] {0} M15 engulfing zone {1:F5}-{2:F5} ({3:F1} pips) @ {4:yyyy-MM-dd HH:mm}",
                            bull ? "DEMAND" : "SUPPLY", z.Bot, z.Top, (z.Top - z.Bot) / Symbol.PipSize, t);
                    DrawZone(z, t);
                }
            }

            // prune: drop dead/mitigated zones once the list outgrows the cap
            while (_zones.Count > 2 * MaxZones)
            {
                int rm = 0;
                for (int i = 0; i < _zones.Count; i++)
                    if (_zones[i].Dead || _zones[i].Mitigated) { rm = i; break; }
                if (Draw) Chart.RemoveObject(_zones[rm].Key);
                _zones.RemoveAt(rm);
            }

            // extend live zones to the current bar
            if (Draw)
                foreach (var z in _zones)
                    if (!z.Dead)
                        DrawZone(z, t.AddMinutes(15));
        }

        protected override void OnTick()
        {
            if (_zones.Count == 0) return;
            if (Positions.FindAll(Label, SymbolName).Length > 0) return;

            double bid = Symbol.Bid, ask = Symbol.Ask;

            foreach (var z in _zones)
            {
                if (z.Dead || z.Mitigated) continue;

                // first touch back into the zone
                bool tapped = z.Bull ? bid <= z.Top && bid >= z.Bot : ask >= z.Bot && ask <= z.Top;
                if (!tapped) continue;

                z.Mitigated = true;                    // one trade per zone, ever
                DrawZone(z, Server.Time);
                if (Verbose)
                    Print("[TAP] {0} zone {1:F5}-{2:F5} mitigated @ {3:F5}", z.Bull ? "demand" : "supply", z.Bot, z.Top, z.Bull ? bid : ask);
                Fire(z);
                return;                                 // one entry per tick at most
            }
        }

        private void Fire(Zone z)
        {
            if (MaxTradesPerDay > 0 && _dayTrades >= MaxTradesPerDay)
            {
                if (Verbose) Print("[SKIP] daily cap {0} reached", MaxTradesPerDay);
                return;
            }
            double spreadPips = Symbol.Spread / Symbol.PipSize;
            if (MaxSpreadPips > 0 && spreadPips > MaxSpreadPips)
            {
                Print("[SKIP] spread {0:F1} pips > max {1}", spreadPips, MaxSpreadPips);
                return;
            }

            var type = z.Bull ? TradeType.Buy : TradeType.Sell;
            double entry = z.Bull ? Symbol.Ask : Symbol.Bid;
            double volume = Symbol.NormalizeVolumeInUnits(Symbol.QuantityToVolumeInUnits(QuantityLots), RoundingMode.Down);
            if (volume <= 0)
            {
                Print("[ERROR] volume for {0} lots normalises to 0 on {1}", QuantityLots, SymbolName);
                return;
            }

            var res = ExecuteMarketOrder(type, SymbolName, volume, Label, StopLossPips, TakeProfitPips);
            if (res.IsSuccessful)
            {
                _dayTrades++;
                double sl = z.Bull ? entry - StopLossPips * Symbol.PipSize : entry + StopLossPips * Symbol.PipSize;
                double tp = z.Bull ? entry + TakeProfitPips * Symbol.PipSize : entry - TakeProfitPips * Symbol.PipSize;
                Print("[ENTRY] {0} @ {1:F5} SL {2:F5} TP {3:F5} | zone {4:F5}-{5:F5} from {6:yyyy-MM-dd HH:mm} | spread {7:F1}p",
                    type, entry, sl, tp, z.Bot, z.Top, z.Time, spreadPips);
                if (Draw)
                {
                    string en = string.Format("egsig-{0:yyyyMMddHHmmss}", Server.Time);
                    Color col = z.Bull ? ColUp : ColDn;
                    Chart.DrawIcon(en + "-ic", z.Bull ? ChartIconType.UpTriangle : ChartIconType.DownTriangle, Server.Time, entry, col);
                    Chart.DrawTrendLine(en + "-tp", Server.Time, tp, Server.Time.AddHours(4), tp, ColUp, 2, LineStyle.Dots);
                    Chart.DrawTrendLine(en + "-sl", Server.Time, sl, Server.Time.AddHours(4), sl, ColDn, 1, LineStyle.Dots);
                }
            }
            else
                Print("[ERROR] order failed: {0}", res.Error);
        }

        private void DrawZone(Zone z, DateTime right)
        {
            if (!Draw) return;
            Color fill = z.Mitigated || z.Dead
                ? (z.Bull ? ColUpDim : ColDnDim)
                : (z.Bull ? ColUpFill : ColDnFill);
            var rc = Chart.DrawRectangle(z.Key, z.Time, z.Top, right, z.Bot, fill);
            rc.IsFilled = true;
        }
    }
}
