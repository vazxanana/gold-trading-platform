using System;
using System.Collections.Generic;
using System.Linq;
using cAlgo.API;
using cAlgo.API.Internals;

namespace cAlgo.Robots
{
    /// <summary>
    /// Research tool, not a trading bot: measures a fixed list of economically-motivated
    /// gold hypotheses on YOUR broker's real history and prints honest statistics. Places
    /// no orders; runs entirely in OnStart and then stops itself.
    ///
    /// Every hypothesis is evaluated twice:
    ///   IS  (in-sample)     = first 70% of history  -> where the pattern is "found"
    ///   OOS (out-of-sample) = last 30% of history   -> where it must survive
    /// A pattern is only a candidate edge if BOTH windows agree in sign and the OOS
    /// t-statistic holds up. The final summary ranks hypotheses by min(|t_IS|, |t_OOS|),
    /// zeroed when the windows disagree.
    ///
    /// Multiple-testing warning: ~75 rows are tested, so ~3-4 will show |t| >= 2 by pure
    /// luck. That is exactly why the OOS column exists - demand agreement, not one number.
    ///
    /// Usage: backtest on XAUUSD (any timeframe/period - it loads its own H1/D1/M15
    /// history) and read the log.
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

        [Parameter("Out-of-sample fraction", DefaultValue = 0.3, MinValue = 0.1, MaxValue = 0.5)]
        public double OosFraction { get; set; }

        // For the gold/silver ratio section; section is skipped if the symbol is missing.
        [Parameter("Silver Symbol", DefaultValue = "XAGUSD")]
        public string SilverSymbol { get; set; }

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

        // ---- sample registry: every hypothesis appends (time, return-in-bp) points ------

        private sealed class Track
        {
            public string Section;
            public string Label;
            public readonly List<double> Ret = new List<double>();
        }

        private readonly Dictionary<string, Track> _tracks = new Dictionary<string, Track>();
        private readonly List<string> _order = new List<string>();

        private void Collect(string section, string label, double retBp)
        {
            string key = section + "|" + label;
            Track tr;
            if (!_tracks.TryGetValue(key, out tr))
            {
                tr = new Track { Section = section, Label = label };
                _tracks[key] = tr;
                _order.Add(key);
            }
            tr.Ret.Add(retBp); // appended in chronological order by construction
        }

        private sealed class Split
        {
            public int N; public double Mean, Win, T;
        }

        private static Split Describe(List<double> xs)
        {
            var s = new Split { N = xs.Count };
            if (xs.Count == 0) return s;
            s.Mean = xs.Average();
            s.Win = 100.0 * xs.Count(x => x > 0) / xs.Count;
            if (xs.Count >= 3)
            {
                double m = s.Mean;
                double sd = Math.Sqrt(xs.Sum(x => (x - m) * (x - m)) / (xs.Count - 1));
                s.T = sd == 0 ? 0 : m / (sd / Math.Sqrt(xs.Count));
            }
            return s;
        }

        protected override void OnStart()
        {
            var h1 = LoadDeep(TimeFrame.Hour, TargetH1);
            var d1 = LoadDeep(TimeFrame.Daily, TargetD1);
            var m15 = LoadDeep(TimeFrame.Minute15, TargetM15);

            Print("=== GOLD EDGE RESEARCH on {0}: H1 {1} bars (from {2:yyyy-MM-dd}), D1 {3}, M15 {4}; OOS = last {5:P0} ===",
                SymbolName, h1.Count, h1.Count > 0 ? h1.OpenTimes[0] : default, d1.Count, m15.Count, OosFraction);

            HourOfDay(h1);
            SessionBuckets(h1);
            Weekday(d1);
            DailyFollowThrough(d1);
            TrendRegime(d1);
            Breakouts(d1);
            BigMoveAndGaps(d1);
            EngulfingFollowThrough(m15, DailyTrendMap(d1));
            MonthOfYear(d1);
            VolRegime(d1, m15);
            NfpFriday(h1);
            GoldSilverRatio(d1);

            PrintTables();
            PrintSummary();
            Stop();
        }

        private Bars LoadDeep(TimeFrame tf, int target)
        {
            var bars = MarketData.GetBars(tf);
            for (int guard = 0; bars.Count < target && guard < 60; guard++)
                if (bars.LoadMoreHistory() <= 0)
                    break;
            return bars;
        }

        private static double RetBp(Bars b, int i)
        {
            return 10000.0 * Math.Log(b.ClosePrices[i] / b.ClosePrices[i - 1]);
        }

        // ---- hypotheses ------------------------------------------------------------------

        /// Documented intraday seasonality: gold historically drifts up in Asian hours and
        /// down into the London PM fix (~15:00 UTC).
        private void HourOfDay(Bars h1)
        {
            for (int i = 1; i <= h1.Count - 2; i++)
                Collect("1. Hour-of-day (UTC)", "hour " + h1.OpenTimes[i].Hour.ToString("00"), RetBp(h1, i));
        }

        private void SessionBuckets(Bars h1)
        {
            var names = new[] { "Asia 00-07", "London 07-12", "NY overlap 12-16", "NY late 16-21", "Close 21-24", "PM-fix hour 14-15" };
            var lo = new[] { 0, 7, 12, 16, 21, 14 };
            var hi = new[] { 7, 12, 16, 21, 24, 15 };
            for (int k = 0; k < names.Length; k++)
            {
                double acc = 0; var day = System.DateTime.MinValue; bool has = false;
                for (int i = 1; i <= h1.Count - 2; i++)
                {
                    var t = h1.OpenTimes[i];
                    if (t.Hour < lo[k] || t.Hour >= hi[k]) continue;
                    if (t.Date != day)
                    {
                        if (has) Collect("2. Session buckets (UTC)", names[k], acc);
                        day = t.Date; acc = 0; has = true;
                    }
                    acc += RetBp(h1, i);
                }
                if (has) Collect("2. Session buckets (UTC)", names[k], acc);
            }
        }

        private void Weekday(Bars d1)
        {
            for (int i = 1; i <= d1.Count - 2; i++)
            {
                var dow = d1.OpenTimes[i].DayOfWeek;
                if (dow >= DayOfWeek.Monday && dow <= DayOfWeek.Friday)
                    Collect("3. Day-of-week", dow.ToString(), RetBp(d1, i));
            }
        }

        /// Short-term follow-through is usually weak in gold; medium-term trend is the
        /// robust one - sections 4-6 separate them.
        private void DailyFollowThrough(Bars d1)
        {
            for (int i = 4; i <= d1.Count - 2; i++)
            {
                double next = RetBp(d1, i);
                Collect("4. Daily follow-through", RetBp(d1, i - 1) > 0 ? "after UP day" : "after DOWN day", next);
                if (RetBp(d1, i - 1) > 0 && RetBp(d1, i - 2) > 0 && RetBp(d1, i - 3) > 0)
                    Collect("4. Daily follow-through", "after 3 straight UP", next);
                if (RetBp(d1, i - 1) < 0 && RetBp(d1, i - 2) < 0 && RetBp(d1, i - 3) < 0)
                    Collect("4. Daily follow-through", "after 3 straight DOWN", next);
            }
        }

        private void TrendRegime(Bars d1)
        {
            foreach (int len in new[] { 50, 200 })
            {
                double sum = 0;
                var q = new Queue<double>();
                for (int i = 1; i <= d1.Count - 2; i++)
                {
                    q.Enqueue(d1.ClosePrices[i - 1]); sum += d1.ClosePrices[i - 1];
                    if (q.Count > len) sum -= q.Dequeue();
                    if (q.Count < len) continue;
                    bool above = d1.ClosePrices[i - 1] >= sum / len;
                    Collect("5. Trend regime (daily)", (above ? "above" : "below") + " SMA" + len, RetBp(d1, i));
                }
            }
        }

        /// Donchian-style breakout: day after a new 20-day closing high/low - the classic
        /// trend-following entry, long documented in gold. Short rows signed as shorts.
        private void Breakouts(Bars d1)
        {
            const int len = 20;
            for (int i = len + 2; i <= d1.Count - 2; i++)
            {
                double maxPrior = double.MinValue, minPrior = double.MaxValue;
                for (int k = i - 1 - len; k < i - 1; k++)
                {
                    if (d1.ClosePrices[k] > maxPrior) maxPrior = d1.ClosePrices[k];
                    if (d1.ClosePrices[k] < minPrior) minPrior = d1.ClosePrices[k];
                }
                double prevClose = d1.ClosePrices[i - 1];
                if (prevClose > maxPrior)
                    Collect("6. Breakout (20-day)", "day after new 20d HIGH (long)", RetBp(d1, i));
                else if (prevClose < minPrior)
                    Collect("6. Breakout (20-day)", "day after new 20d LOW (short)", -RetBp(d1, i));
            }
        }

        private void BigMoveAndGaps(Bars d1)
        {
            for (int i = 2; i <= d1.Count - 2; i++)
            {
                double prev = RetBp(d1, i - 1);
                if (prev > 150) Collect("7. Shocks & gaps", "day after +150bp day", RetBp(d1, i));
                else if (prev < -150) Collect("7. Shocks & gaps", "day after -150bp day", RetBp(d1, i));

                // Weekend gap fade: known at Monday open; return measured open -> close,
                // signed so that positive = fading the gap worked.
                if (d1.OpenTimes[i].DayOfWeek == DayOfWeek.Monday)
                {
                    double gap = d1.OpenPrices[i] - d1.ClosePrices[i - 1];
                    if (Math.Abs(gap) > 0)
                    {
                        double oc = 10000.0 * Math.Log(d1.ClosePrices[i] / d1.OpenPrices[i]);
                        Collect("7. Shocks & gaps", "fade weekend gap (Mon O->C)", gap > 0 ? -oc : oc);
                    }
                }
            }
        }

        /// Regime lookup: date -> was yesterday's close above its SMA200? (no look-ahead)
        private Dictionary<System.DateTime, bool> DailyTrendMap(Bars d1)
        {
            var map = new Dictionary<System.DateTime, bool>();
            double sum = 0;
            var q = new Queue<double>();
            for (int i = 1; i < d1.Count; i++)
            {
                q.Enqueue(d1.ClosePrices[i - 1]); sum += d1.ClosePrices[i - 1];
                if (q.Count > 200) sum -= q.Dequeue();
                if (q.Count == 200) map[d1.OpenTimes[i].Date] = d1.ClosePrices[i - 1] >= sum / 200;
            }
            return map;
        }

        /// The strategy's core assumption, measured directly: forward return over the next
        /// 4 M15 bars (1h) after an engulfing - spec vs strict definition, then the strict
        /// version conditioned on session and on daily SMA200 trend alignment.
        /// Bear rows are signed as a SHORT: positive mean = the pattern worked.
        private void EngulfingFollowThrough(Bars m15, Dictionary<System.DateTime, bool> trendUp)
        {
            const string sec = "8. M15 engulfing (fwd 1h, shorts signed)";
            for (int i = 1; i <= m15.Count - 6; i++)
            {
                double fwd = 10000.0 * Math.Log(m15.ClosePrices[i + 4] / m15.ClosePrices[i]);
                var t = m15.OpenTimes[i];
                bool bull = IsBullishEg(m15.OpenPrices[i - 1], m15.ClosePrices[i - 1], m15.OpenPrices[i], m15.ClosePrices[i]);
                bool bear = IsBearishEg(m15.OpenPrices[i - 1], m15.ClosePrices[i - 1], m15.OpenPrices[i], m15.ClosePrices[i]);
                bool priorRed = m15.ClosePrices[i - 1] < m15.OpenPrices[i - 1];
                bool priorGreen = m15.ClosePrices[i - 1] > m15.OpenPrices[i - 1];
                bool inNySession = t.Hour >= 12 && t.Hour < 16;
                bool up;
                bool hasTrend = trendUp.TryGetValue(t.Date, out up);

                if (bull)
                {
                    Collect(sec, "spec bull EG", fwd);
                    if (priorRed)
                    {
                        Collect(sec, "strict bull EG", fwd);
                        if (inNySession) Collect(sec, "strict bull EG in 12-16 UTC", fwd);
                        if (hasTrend && up) Collect(sec, "strict bull EG + SMA200 uptrend", fwd);
                    }
                }
                if (bear)
                {
                    Collect(sec, "spec bear EG", -fwd);
                    if (priorGreen)
                    {
                        Collect(sec, "strict bear EG", -fwd);
                        if (inNySession) Collect(sec, "strict bear EG in 12-16 UTC", -fwd);
                        if (hasTrend && !up) Collect(sec, "strict bear EG + SMA200 downtrend", -fwd);
                    }
                }
            }
        }

        /// Documented physical-demand seasonality: strong Jan and Aug-Sep (Indian festival
        /// and wedding restocking, Chinese New Year buying), weak Mar/Jun.
        private void MonthOfYear(Bars d1)
        {
            for (int i = 1; i <= d1.Count - 2; i++)
                Collect("9. Month-of-year", d1.OpenTimes[i].ToString("MMM"), RetBp(d1, i));
        }

        /// Vol clustering as a FILTER: does the next day - and does the engulfing pattern -
        /// behave differently when ATR14 is above vs below its trailing-100-day median?
        /// Regime uses only completed data (no look-ahead).
        private void VolRegime(Bars d1, Bars m15)
        {
            int n = d1.Count;
            if (n < 140) return;

            // ATR14 as a simple SMA of true range
            var atr = new double[n];
            var trArr = new double[n];
            for (int i = 1; i < n; i++)
                trArr[i] = Math.Max(d1.HighPrices[i] - d1.LowPrices[i],
                           Math.Max(Math.Abs(d1.HighPrices[i] - d1.ClosePrices[i - 1]),
                                    Math.Abs(d1.LowPrices[i] - d1.ClosePrices[i - 1])));
            for (int i = 15; i < n; i++)
            {
                double s = 0;
                for (int k = i - 13; k <= i; k++) s += trArr[k];
                atr[i] = s / 14;
            }

            var highVolByDate = new Dictionary<System.DateTime, bool>();
            for (int i = 130; i <= n - 2; i++)
            {
                // regime known at day i's open: yesterday's ATR vs the 100 before it
                double y = atr[i - 1];
                int below = 0, cnt = 0;
                for (int k = i - 101; k < i - 1; k++)
                {
                    if (atr[k] <= 0) continue;
                    cnt++;
                    if (atr[k] < y) below++;
                }
                if (cnt < 60) continue;
                bool high = below > cnt / 2;
                highVolByDate[d1.OpenTimes[i].Date] = high;
                Collect("10. Vol regime (ATR14 vs trailing median)",
                    high ? "next day in HIGH-vol regime" : "next day in LOW-vol regime", RetBp(d1, i));
            }

            // Condition the strategy's pattern on the regime (strict definition, fwd 1h).
            const string sec = "10. Vol regime (ATR14 vs trailing median)";
            for (int i = 1; i <= m15.Count - 6; i++)
            {
                bool up;
                if (!highVolByDate.TryGetValue(m15.OpenTimes[i].Date, out up)) continue;
                double fwd = 10000.0 * Math.Log(m15.ClosePrices[i + 4] / m15.ClosePrices[i]);
                bool priorRed = m15.ClosePrices[i - 1] < m15.OpenPrices[i - 1];
                bool priorGreen = m15.ClosePrices[i - 1] > m15.OpenPrices[i - 1];
                if (priorRed && IsBullishEg(m15.OpenPrices[i - 1], m15.ClosePrices[i - 1], m15.OpenPrices[i], m15.ClosePrices[i]))
                    Collect(sec, up ? "strict bull EG, HIGH vol" : "strict bull EG, LOW vol", fwd);
                if (priorGreen && IsBearishEg(m15.OpenPrices[i - 1], m15.ClosePrices[i - 1], m15.OpenPrices[i], m15.ClosePrices[i]))
                    Collect(sec, up ? "strict bear EG, HIGH vol (short)" : "strict bear EG, LOW vol (short)", -fwd);
            }
        }

        /// Crude NFP test: first Friday of the month; 8:30 ET release falls in the 12:00 or
        /// 13:00 UTC bar depending on DST, so the event window is 12-14 UTC and the
        /// follow-through window 14-16 UTC, signed by the event move's direction.
        /// No economic calendar involved - schedule exceptions add noise; treat as rough.
        private void NfpFriday(Bars h1)
        {
            var day = System.DateTime.MinValue;
            double evt = 0, post = 0; bool active = false;
            for (int i = 1; i <= h1.Count - 2; i++)
            {
                var t = h1.OpenTimes[i];
                if (t.Date != day)
                {
                    if (active)
                    {
                        Collect("11. NFP Friday (crude, no calendar)", "event window 12-14 UTC", evt);
                        if (Math.Abs(evt) > 0)
                            Collect("11. NFP Friday (crude, no calendar)", "follow event move, 14-16 UTC", Math.Sign(evt) * post);
                    }
                    day = t.Date; evt = 0; post = 0;
                    active = t.DayOfWeek == DayOfWeek.Friday && t.Day <= 7;
                }
                if (!active) continue;
                if (t.Hour >= 12 && t.Hour < 14) evt += RetBp(h1, i);
                if (t.Hour >= 14 && t.Hour < 16) post += RetBp(h1, i);
            }
            if (active)
            {
                Collect("11. NFP Friday (crude, no calendar)", "event window 12-14 UTC", evt);
                if (Math.Abs(evt) > 0)
                    Collect("11. NFP Friday (crude, no calendar)", "follow event move, 14-16 UTC", Math.Sign(evt) * post);
            }
        }

        /// Gold/silver ratio extremes: when the ratio is in the top/bottom 15% of its
        /// trailing 250 days, does GOLD's next-day return differ? (We only trade gold here;
        /// the classic trade is the ratio itself.) Skipped if the silver symbol is missing.
        private void GoldSilverRatio(Bars d1)
        {
            Bars xag = null;
            try
            {
                xag = MarketData.GetBars(TimeFrame.Daily, SilverSymbol);
                for (int guard = 0; xag.Count < TargetD1 && guard < 60; guard++)
                    if (xag.LoadMoreHistory() <= 0)
                        break;
            }
            catch (Exception e)
            {
                Print("12. Gold/silver ratio: SKIPPED ({0} not available: {1})", SilverSymbol, e.Message);
                return;
            }
            if (xag == null || xag.Count < 300)
            {
                Print("12. Gold/silver ratio: SKIPPED (insufficient {0} history)", SilverSymbol);
                return;
            }

            var silverClose = new Dictionary<System.DateTime, double>();
            for (int i = 0; i < xag.Count; i++)
                silverClose[xag.OpenTimes[i].Date] = xag.ClosePrices[i];

            // ratio[i] = ratio at day i's CLOSE; decisions for day i use ratio[i-1]
            var ratio = new double[d1.Count];
            for (int i = 0; i < d1.Count; i++)
            {
                double s;
                ratio[i] = silverClose.TryGetValue(d1.OpenTimes[i].Date, out s) && s > 0
                    ? d1.ClosePrices[i] / s : 0;
            }

            for (int i = 260; i <= d1.Count - 2; i++)
            {
                double y = ratio[i - 1];
                if (y <= 0) continue;
                int below = 0, cnt = 0;
                for (int k = i - 251; k < i - 1; k++)
                {
                    if (ratio[k] <= 0) continue;
                    cnt++;
                    if (ratio[k] < y) below++;
                }
                if (cnt < 200) continue;
                double pct = (double)below / cnt;
                if (pct >= 0.85) Collect("12. Gold/silver ratio (trailing 250d)", "gold next day, ratio > p85", RetBp(d1, i));
                else if (pct <= 0.15) Collect("12. Gold/silver ratio (trailing 250d)", "gold next day, ratio < p15", RetBp(d1, i));
            }
        }

        // ---- reporting -------------------------------------------------------------------

        private KeyValuePair<Split, Split> SplitTrack(Track tr)
        {
            int cut = (int)(tr.Ret.Count * (1 - OosFraction));
            return new KeyValuePair<Split, Split>(
                Describe(tr.Ret.Take(cut).ToList()),
                Describe(tr.Ret.Skip(cut).ToList()));
        }

        private void PrintTables()
        {
            string section = null;
            foreach (var key in _order)
            {
                var tr = _tracks[key];
                if (tr.Ret.Count < 20) continue; // below this there is nothing to say at all
                                                 // (20-59 samples still print but rank as "thin")
                if (tr.Section != section)
                {
                    section = tr.Section;
                    Print("--- {0} ---", section);
                    Print("    {0,-34} | {1,28} | {2,28} |", "hypothesis", "IS  n / mean bp / win / t", "OOS n / mean bp / win / t");
                }
                var s = SplitTrack(tr);
                Print("    {0,-34} | {1,6} {2,7:F2} {3,5:F1}% {4,5:F1} | {5,6} {6,7:F2} {7,5:F1}% {8,5:F1} | {9}",
                    tr.Label, s.Key.N, s.Key.Mean, s.Key.Win, s.Key.T, s.Value.N, s.Value.Mean, s.Value.Win, s.Value.T,
                    Verdict(s.Key, s.Value));
            }
        }

        private static string Verdict(Split a, Split b)
        {
            if (a.N < 20 || b.N < 20) return "thin";
            bool sameSign = Math.Sign(a.Mean) == Math.Sign(b.Mean) && a.Mean != 0;
            double score = sameSign ? Math.Min(Math.Abs(a.T), Math.Abs(b.T)) : 0;
            if (score >= 2) return "** CANDIDATE **";
            if (score >= 1.2) return "weak but consistent";
            return sameSign ? "consistent, insignificant" : "DISAGREES (likely luck)";
        }

        private void PrintSummary()
        {
            Print("=== TOP FINDINGS (ranked by min |t| across IS/OOS, sign must agree) ===");
            var ranked = new List<KeyValuePair<Track, KeyValuePair<Split, Split>>>();
            foreach (var key in _order)
            {
                var tr = _tracks[key];
                if (tr.Ret.Count < 60) continue;
                var s = SplitTrack(tr);
                if (Math.Sign(s.Key.Mean) == Math.Sign(s.Value.Mean) && s.Key.Mean != 0)
                    ranked.Add(new KeyValuePair<Track, KeyValuePair<Split, Split>>(tr, s));
            }
            ranked = ranked
                .OrderByDescending(x => Math.Min(Math.Abs(x.Value.Key.T), Math.Abs(x.Value.Value.T)))
                .Take(5)
                .ToList();

            if (ranked.Count == 0)
            {
                Print("  nothing survived - that is a valid (and common) result.");
                return;
            }
            for (int r = 0; r < ranked.Count; r++)
            {
                var tr = ranked[r].Key;
                var s = ranked[r].Value;
                Print("  {0}. [{1}] {2}: IS {3:+0.0;-0.0}bp (t={4:F1}), OOS {5:+0.0;-0.0}bp (t={6:F1}), n={7}",
                    r + 1, tr.Section, tr.Label, s.Key.Mean, s.Key.T, s.Value.Mean, s.Value.T, tr.Ret.Count);
            }
            Print("  Reminder: ~75 hypotheses tested -> a few |t|>=2 rows are expected by luck alone.");
            Print("  Trust a finding only if it is a ** CANDIDATE **, makes economic sense, and survives");
            Print("  a paper-traded or later-period retest.");
        }
    }
}
