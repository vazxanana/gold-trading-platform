using System;
using System.Collections.Generic;
using System.Linq;
using cAlgo.API;
using cAlgo.API.Internals;

namespace cAlgo.Robots
{
    /// <summary>
    /// Research tool, not a trading bot: walks your broker's real XAUUSD history and prints
    /// the statistics behind documented gold market structure, so hypotheses can be ranked
    /// on YOUR data instead of trusted from a blog post. Places no orders; runs entirely in
    /// OnStart and then stops itself.
    ///
    /// Usage: backtest it on XAUUSD (any timeframe/period - it loads its own H1/D1/M15
    /// history) and read the log. Every table shows count, mean, win% and a t-statistic;
    /// |t| >= 2 is roughly "unlikely to be luck", anything less is noise.
    /// </summary>
    [Robot(AccessRights = AccessRights.None)]
    public class GoldEdgeResearchBot : Robot
    {
        [Parameter("Target H1 bars", DefaultValue = 30000, MinValue = 1000)]
        public int TargetH1 { get; set; }

        [Parameter("Target D1 bars", DefaultValue = 2500, MinValue = 200)]
        public int TargetD1 { get; set; }

        [Parameter("Target M15 bars", DefaultValue = 40000, MinValue = 2000)]
        public int TargetM15 { get; set; }

        protected override void OnStart()
        {
            var h1 = LoadDeep(TimeFrame.Hour, TargetH1);
            var d1 = LoadDeep(TimeFrame.Daily, TargetD1);
            var m15 = LoadDeep(TimeFrame.Minute15, TargetM15);

            Print("=== GOLD EDGE RESEARCH on {0}: H1 {1} bars ({2:yyyy-MM-dd}..), D1 {3}, M15 {4} ===",
                SymbolName, h1.Count, h1.Count > 0 ? h1.OpenTimes[0] : default, d1.Count, m15.Count);

            HourOfDay(h1);
            SessionBuckets(h1);
            Weekday(d1);
            DailyMomentum(d1);
            TrendRegime(d1);
            BigMoveReversion(d1);
            EngulfingFollowThrough(m15);

            Print("=== END RESEARCH (|t| >= 2 is interesting; also demand it makes economic sense) ===");
            Stop();
        }

        // Same definitions as EngulfingLogic in GoldEngulfingConfluenceBot.cs (spec, exact):
        //   bullish: close > prior_open AND open <= prior_close AND close > open
        //   bearish: close < prior_open AND open >= prior_close AND close < open
        private static bool IsBullishEg(double priorOpen, double priorClose, double open, double close)
        {
            return close > priorOpen && open <= priorClose && close > open;
        }

        private static bool IsBearishEg(double priorOpen, double priorClose, double open, double close)
        {
            return close < priorOpen && open >= priorClose && close < open;
        }

        private Bars LoadDeep(TimeFrame tf, int target)
        {
            var bars = MarketData.GetBars(tf);
            for (int guard = 0; bars.Count < target && guard < 60; guard++)
                if (bars.LoadMoreHistory() <= 0)
                    break;
            return bars;
        }

        // ---- stats helpers -------------------------------------------------------------

        private sealed class Stat
        {
            private readonly List<double> _xs = new List<double>();
            public void Add(double x) { _xs.Add(x); }
            public int N { get { return _xs.Count; } }
            public double Mean { get { return _xs.Count == 0 ? 0 : _xs.Average(); } }
            public double WinPct { get { return _xs.Count == 0 ? 0 : 100.0 * _xs.Count(x => x > 0) / _xs.Count; } }
            public double T
            {
                get
                {
                    if (_xs.Count < 3) return 0;
                    double m = Mean;
                    double sd = Math.Sqrt(_xs.Sum(x => (x - m) * (x - m)) / (_xs.Count - 1));
                    return sd == 0 ? 0 : m / (sd / Math.Sqrt(_xs.Count));
                }
            }
        }

        private void PrintRow(string label, Stat s)
        {
            Print("{0,-28} n={1,6}  mean={2,7:F2} bp  win={3,5:F1}%  t={4,5:F1}", label, s.N, s.Mean, s.WinPct, s.T);
        }

        // Log return of completed bar i in basis points (close-to-close).
        private static double RetBp(Bars b, int i)
        {
            return 10000.0 * Math.Log(b.ClosePrices[i] / b.ClosePrices[i - 1]);
        }

        // ---- hypotheses ----------------------------------------------------------------

        /// Documented: gold has historically drifted UP in Asian hours and DOWN into the
        /// London PM fix window (the "London fix" intraday seasonality literature).
        private void HourOfDay(Bars h1)
        {
            Print("--- 1. Hour-of-day (UTC, close-to-close of that hour's bar) ---");
            var byHour = new Stat[24];
            for (int h = 0; h < 24; h++) byHour[h] = new Stat();
            for (int i = 1; i <= h1.Count - 2; i++)
                byHour[h1.OpenTimes[i].Hour].Add(RetBp(h1, i));
            for (int h = 0; h < 24; h++)
                if (byHour[h].N > 30)
                    PrintRow("  hour " + h.ToString("00"), byHour[h]);
        }

        private void SessionBuckets(Bars h1)
        {
            Print("--- 2. Session buckets (UTC) ---");
            var names = new[] { "Asia 00-07", "London 07-12", "NY overlap 12-16", "NY late 16-21", "Close 21-24" };
            var lo = new[] { 0, 7, 12, 16, 21 };
            var hi = new[] { 7, 12, 16, 21, 24 };
            for (int k = 0; k < names.Length; k++)
            {
                var s = new Stat();
                double acc = 0; DateTime day = DateTime.MinValue; bool has = false;
                for (int i = 1; i <= h1.Count - 2; i++)
                {
                    var t = h1.OpenTimes[i];
                    if (t.Hour < lo[k] || t.Hour >= hi[k]) continue;
                    if (t.Date != day) { if (has) s.Add(acc); day = t.Date; acc = 0; has = true; }
                    acc += RetBp(h1, i);
                }
                if (has) s.Add(acc);
                PrintRow("  " + names[k], s);
            }
        }

        private void Weekday(Bars d1)
        {
            Print("--- 3. Day-of-week (daily close-to-close) ---");
            foreach (var dow in new[] { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday })
            {
                var s = new Stat();
                for (int i = 1; i <= d1.Count - 2; i++)
                    if (d1.OpenTimes[i].DayOfWeek == dow)
                        s.Add(RetBp(d1, i));
                PrintRow("  " + dow, s);
            }
        }

        /// Documented: medium-term momentum/trend-following has worked on gold for decades;
        /// short-term daily follow-through is usually much weaker.
        private void DailyMomentum(Bars d1)
        {
            Print("--- 4. Daily momentum / follow-through ---");
            Stat afterUp = new Stat(), afterDown = new Stat(), after3Up = new Stat(), after3Down = new Stat();
            for (int i = 4; i <= d1.Count - 2; i++)
            {
                double next = RetBp(d1, i);
                if (RetBp(d1, i - 1) > 0) afterUp.Add(next); else afterDown.Add(next);
                if (RetBp(d1, i - 1) > 0 && RetBp(d1, i - 2) > 0 && RetBp(d1, i - 3) > 0) after3Up.Add(next);
                if (RetBp(d1, i - 1) < 0 && RetBp(d1, i - 2) < 0 && RetBp(d1, i - 3) < 0) after3Down.Add(next);
            }
            PrintRow("  next day after UP day", afterUp);
            PrintRow("  next day after DOWN day", afterDown);
            PrintRow("  after 3 straight UP", after3Up);
            PrintRow("  after 3 straight DOWN", after3Down);
        }

        private void TrendRegime(Bars d1)
        {
            Print("--- 5. Trend regime (daily close vs SMA) ---");
            foreach (int len in new[] { 50, 200 })
            {
                Stat above = new Stat(), below = new Stat();
                double sum = 0;
                var q = new Queue<double>();
                for (int i = 1; i <= d1.Count - 2; i++)
                {
                    q.Enqueue(d1.ClosePrices[i - 1]); sum += d1.ClosePrices[i - 1];
                    if (q.Count > len) sum -= q.Dequeue();
                    if (q.Count < len) continue;
                    if (d1.ClosePrices[i - 1] >= sum / len) above.Add(RetBp(d1, i)); else below.Add(RetBp(d1, i));
                }
                PrintRow("  above SMA" + len, above);
                PrintRow("  below SMA" + len, below);
            }
        }

        private void BigMoveReversion(Bars d1)
        {
            Print("--- 6. After a big daily move (|ret| > 150 bp) ---");
            Stat afterBigUp = new Stat(), afterBigDown = new Stat();
            for (int i = 2; i <= d1.Count - 2; i++)
            {
                double prev = RetBp(d1, i - 1);
                if (prev > 150) afterBigUp.Add(RetBp(d1, i));
                else if (prev < -150) afterBigDown.Add(RetBp(d1, i));
            }
            PrintRow("  next day after +150bp day", afterBigUp);
            PrintRow("  next day after -150bp day", afterBigDown);
        }

        /// Your strategy's core assumption, measured directly: does an M15 engulfing predict
        /// the next hour? Spec definition vs strict (prior candle opposite color).
        private void EngulfingFollowThrough(Bars m15)
        {
            Print("--- 7. M15 engulfing follow-through (return over next 4 M15 bars, bp) ---");
            Stat specBull = new Stat(), specBear = new Stat(), strictBull = new Stat(), strictBear = new Stat();
            for (int i = 1; i <= m15.Count - 6; i++)
            {
                double fwd = 10000.0 * Math.Log(m15.ClosePrices[i + 4] / m15.ClosePrices[i]);
                bool bull = IsBullishEg(m15.OpenPrices[i - 1], m15.ClosePrices[i - 1], m15.OpenPrices[i], m15.ClosePrices[i]);
                bool bear = IsBearishEg(m15.OpenPrices[i - 1], m15.ClosePrices[i - 1], m15.OpenPrices[i], m15.ClosePrices[i]);
                bool priorRed = m15.ClosePrices[i - 1] < m15.OpenPrices[i - 1];
                bool priorGreen = m15.ClosePrices[i - 1] > m15.OpenPrices[i - 1];
                if (bull) { specBull.Add(fwd); if (priorRed) strictBull.Add(fwd); }
                if (bear) { specBear.Add(-fwd); if (priorGreen) strictBear.Add(-fwd); }
            }
            PrintRow("  spec bull EG (long)", specBull);
            PrintRow("  strict bull EG (long)", strictBull);
            PrintRow("  spec bear EG (short)", specBear);
            PrintRow("  strict bear EG (short)", strictBear);
            Print("  (bear rows are signed as a SHORT: positive mean = pattern worked)");
        }
    }
}
