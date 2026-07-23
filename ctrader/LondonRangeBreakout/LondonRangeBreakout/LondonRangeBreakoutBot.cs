// ─── LONDON PRE-FIX RANGE BREAKOUT BOT ──────────────────────────────────────
// cTrader twin of tradingview/LondonRangeBreakout.pine:
//
//   Step 1  Mark the high and low of the 09:30–10:30 London pre-fix range
//           (start/end configurable; the bot runs in Europe/London time via
//           the TimeZone attribute, so the window is exact on any broker).
//   Step 2  After the window, wait for a COMPLETED 15-minute candle to
//           CLOSE outside the range.
//   Step 3  Enter in the breakout direction:
//               stop   inside the range (midpoint by default, or the
//                      opposite side)
//               target 1.5 × the range width from entry (multiplier param)
//
//   • One trade per day (param), optional min/max range filter in pips,
//     open position closed at the day rollover (param).
//   • ATTACH TO THE M15 CHART — decisions on completed bars only
//     (Bars.Count - 2); daily state resets on the bar-date rollover.
//   • Distances converted via Symbol.PipSize; chart drawings show the
//     range box and the trade's TP/SL lines.

using System;
using cAlgo.API;
using cAlgo.API.Internals;

namespace cAlgo.Robots
{
    [Robot(AccessRights = AccessRights.None, TimeZone = TimeZones.GMTStandardTime)]
    public class LondonRangeBreakoutBot : Robot
    {
        private const string Label = "LdnRange";

        [Parameter("Range start hour", Group = "Session", DefaultValue = 9, MinValue = 0, MaxValue = 23)]
        public int StartHour { get; set; }

        [Parameter("Range start minute", Group = "Session", DefaultValue = 30, MinValue = 0, MaxValue = 59)]
        public int StartMin { get; set; }

        [Parameter("Range end hour", Group = "Session", DefaultValue = 10, MinValue = 0, MaxValue = 23)]
        public int EndHour { get; set; }

        [Parameter("Range end minute", Group = "Session", DefaultValue = 30, MinValue = 0, MaxValue = 59)]
        public int EndMin { get; set; }

        [Parameter("Target (x range width)", Group = "Trade", DefaultValue = 1.5, MinValue = 0.1, Step = 0.1)]
        public double TargetMult { get; set; }

        [Parameter("Stop at range midpoint (off = opposite side)", Group = "Trade", DefaultValue = true)]
        public bool StopMid { get; set; }

        [Parameter("One trade per day", Group = "Trade", DefaultValue = true)]
        public bool OneTrade { get; set; }

        [Parameter("Close open position at day end", Group = "Trade", DefaultValue = true)]
        public bool CloseAtDayEnd { get; set; }

        [Parameter("Quantity (lots)", Group = "Risk", DefaultValue = 0.01, MinValue = 0.01, Step = 0.01)]
        public double QuantityLots { get; set; }

        [Parameter("Min range (pips, 0 = off)", Group = "Risk", DefaultValue = 0, MinValue = 0)]
        public double MinRangePips { get; set; }

        [Parameter("Max range (pips, 0 = off)", Group = "Risk", DefaultValue = 0, MinValue = 0)]
        public double MaxRangePips { get; set; }

        [Parameter("Verbose log", Group = "Risk", DefaultValue = true)]
        public bool Verbose { get; set; }

        [Parameter("Draw on chart", Group = "Risk", DefaultValue = true)]
        public bool Draw { get; set; }

        private static readonly Color ColRange = Color.FromArgb(60, 148, 163, 184);
        private static readonly Color ColUp = Color.FromArgb(255, 74, 222, 128);
        private static readonly Color ColDn = Color.FromArgb(255, 251, 113, 133);

        private DateTime _day = DateTime.MinValue.Date;
        private double _rHi = double.NaN, _rLo = double.NaN;
        private DateTime _rStart;
        private bool _traded;
        private string _boxKey;

        protected override void OnStart()
        {
            if (Bars.TimeFrame != TimeFrame.Minute15)
                Print("[WARN] attach to the M15 chart — breakout closes are meant to be 15-minute closes (current TF: {0})", Bars.TimeFrame);
            Print("[START] LondonRangeBreakout | window {0:D2}:{1:D2}-{2:D2}:{3:D2} London | tgt x{4} stopMid={5} oneTrade={6} lots={7}",
                StartHour, StartMin, EndHour, EndMin, TargetMult, StopMid, OneTrade, QuantityLots);
        }

        protected override void OnBar()
        {
            int cb = Bars.Count - 2;               // completed bar
            if (cb < 1) return;
            DateTime t = Bars.OpenTimes[cb];

            // day rollover: reset state, optionally flatten
            if (t.Date != _day)
            {
                _day = t.Date;
                _rHi = double.NaN;
                _rLo = double.NaN;
                _traded = false;
                _boxKey = string.Format("ldnrange-{0:yyyyMMdd}", t);
                if (CloseAtDayEnd)
                    foreach (var pos in Positions.FindAll(Label, SymbolName))
                    {
                        Print("[EXIT] day end — closing {0} (net {1:F2})", pos.TradeType, pos.NetProfit);
                        ClosePosition(pos);
                    }
            }

            int mins = t.Hour * 60 + t.Minute;
            int sMin = StartHour * 60 + StartMin;
            int eMin = EndHour * 60 + EndMin;

            // ── Step 1: build the range from completed bars inside the window
            if (mins >= sMin && mins < eMin)
            {
                if (double.IsNaN(_rHi))
                {
                    _rHi = Bars.HighPrices[cb];
                    _rLo = Bars.LowPrices[cb];
                    _rStart = t;
                }
                else
                {
                    _rHi = Math.Max(_rHi, Bars.HighPrices[cb]);
                    _rLo = Math.Min(_rLo, Bars.LowPrices[cb]);
                }
                if (Draw)
                {
                    var rc = Chart.DrawRectangle(_boxKey, _rStart, _rHi, t.AddMinutes(15), _rLo, ColRange);
                    rc.IsFilled = true;
                }
                return;
            }

            if (double.IsNaN(_rHi) || mins < eMin)
                return;

            // extend the day's range box
            if (Draw)
            {
                var rc = Chart.DrawRectangle(_boxKey, _rStart, _rHi, t.AddMinutes(15), _rLo, ColRange);
                rc.IsFilled = true;
            }

            // ── Step 2: first completed 15m close outside the range ─────────
            if (_traded && OneTrade) return;
            if (Positions.FindAll(Label, SymbolName).Length > 0) return;

            double close = Bars.ClosePrices[cb];
            double rng = _rHi - _rLo;
            if (MinRangePips > 0 && rng < MinRangePips * Symbol.PipSize)
            {
                if (Verbose && !_traded) Print("[SKIP] {0:yyyy-MM-dd} range {1:F1} pips < min {2}", t, rng / Symbol.PipSize, MinRangePips);
                _traded = true;
                return;
            }
            if (MaxRangePips > 0 && rng > MaxRangePips * Symbol.PipSize)
            {
                if (Verbose && !_traded) Print("[SKIP] {0:yyyy-MM-dd} range {1:F1} pips > max {2}", t, rng / Symbol.PipSize, MaxRangePips);
                _traded = true;
                return;
            }

            int dir = close > _rHi ? 1 : close < _rLo ? -1 : 0;
            if (dir == 0) return;

            // ── Step 3: breakout entry, stop inside the range, 1.5x target ──
            _traded = true;
            bool bull = dir == 1;
            double entry = bull ? Symbol.Ask : Symbol.Bid;
            double sl = StopMid ? (_rHi + _rLo) / 2.0 : (bull ? _rLo : _rHi);
            double tp = bull ? entry + TargetMult * rng : entry - TargetMult * rng;
            double slDist = bull ? entry - sl : sl - entry;
            double tpDist = bull ? tp - entry : entry - tp;
            if (slDist <= 0 || tpDist <= 0)
            {
                Print("[SKIP] {0} — degenerate SL/TP (entry {1:F5} sl {2:F5} tp {3:F5})", bull ? "LONG" : "SHORT", entry, sl, tp);
                return;
            }

            double volume = Symbol.NormalizeVolumeInUnits(Symbol.QuantityToVolumeInUnits(QuantityLots), RoundingMode.Down);
            var res = ExecuteMarketOrder(bull ? TradeType.Buy : TradeType.Sell, SymbolName, volume, Label,
                slDist / Symbol.PipSize, tpDist / Symbol.PipSize);
            if (res.IsSuccessful)
            {
                Print("[ENTRY] {0} @ {1:F5} SL {2:F5} TP {3:F5} (range {4:F5}-{5:F5}, {6:F1} pips) t={7:yyyy-MM-dd HH:mm}",
                    bull ? "LONG" : "SHORT", entry, sl, tp, _rLo, _rHi, rng / Symbol.PipSize, t);
                if (Draw)
                {
                    string en = string.Format("ldnsig-{0:yyyyMMddHHmm}", t);
                    Color c = bull ? ColUp : ColDn;
                    Chart.DrawIcon(en + "-ic", bull ? ChartIconType.UpTriangle : ChartIconType.DownTriangle, t, entry, c);
                    Chart.DrawTrendLine(en + "-tp", t, tp, t.AddHours(6), tp, ColUp, 2, LineStyle.Dots);
                    Chart.DrawTrendLine(en + "-sl", t, sl, t.AddHours(6), sl, ColDn, 1, LineStyle.Dots);
                }
            }
            else
                Print("[ERROR] order failed: {0}", res.Error);
        }
    }
}
