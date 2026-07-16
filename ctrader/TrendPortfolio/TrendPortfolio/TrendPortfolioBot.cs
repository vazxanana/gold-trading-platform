using System;
using System.Collections.Generic;
using cAlgo.API;
using cAlgo.API.Internals;

namespace cAlgo.Robots
{
    /// <summary>
    /// Multi-market time-series momentum (trend-following) portfolio, implementing the
    /// documented rule from Hurst, Ooi & Pedersen, "A Century of Evidence on Trend-
    /// Following Investing" (1880-2016, 67 markets):
    ///
    ///   signal per market = equal-weighted SIGN of the past 1-month, 3-month and
    ///   12-month returns (21/63/261 completed daily bars) -> -1..+1 in steps of 1/3;
    ///   each market sized to contribute a constant ex-ante volatility share of equity,
    ///   using its own realized daily vol (63-day) and PipValue for currency conversion.
    ///
    /// Documented expectations (see ctrader/GoldEdgeResearch/QUANT-INDUSTRY.md):
    /// single-market Sharpe ~0.4, diversified portfolio ~0.76 net - i.e. roughly
    /// 0.5-1%/month on average with deep drawdowns and multi-year lean stretches,
    /// and a BELOW-50% win rate by construction (positive skew). This is the honest
    /// industry profile, not a smooth high-win-rate curve.
    ///
    /// Mechanics: attach to a DAILY chart (asserted). Once per day it recomputes
    /// signals on COMPLETED bars only; it trades on a direction flip immediately and
    /// resizes on the first trading day of each month (or when drift exceeds the
    /// threshold). Positions are held for weeks-months and pay swap - verify your
    /// broker's swap rates in a backtest before going live. Markets whose computed
    /// size is below the symbol minimum are skipped with a log line (small accounts:
    /// raise Vol Target or drop expensive markets like XAUUSD).
    /// </summary>
    [Robot(AccessRights = AccessRights.None)]
    public class TrendPortfolioBot : Robot
    {
        [Parameter("Symbols (comma-separated)", Group = "Universe",
            DefaultValue = "XAUUSD,EURUSD,GBPUSD,USDJPY,AUDUSD,USDCHF")]
        public string SymbolList { get; set; }

        // Annualized volatility each market should contribute, as % of equity.
        // 6 markets at 4% each ~ 10% portfolio vol if imperfectly correlated -
        // the paper's risk target.
        [Parameter("Vol Target per Market (%/yr)", Group = "Risk", DefaultValue = 4.0, MinValue = 0.5, Step = 0.5)]
        public double VolTargetPerMarketPct { get; set; }

        // Optional per-position disaster stop as % of entry price (0 = off).
        // The paper uses none - vol scaling is the risk control - but a wide cap
        // bounds broker/black-swan risk.
        [Parameter("Disaster SL (% of price, 0=off)", Group = "Risk", DefaultValue = 10.0, MinValue = 0, Step = 0.5)]
        public double DisasterSlPercent { get; set; }

        [Parameter("Long Only", Group = "Risk", DefaultValue = false)]
        public bool LongOnly { get; set; }

        // Chandelier-style ATR trail: for longs the SL only ever ratchets UP to
        // close - mult*ATR(14, daily); mirrored for shorts. 0 = off (paper spec).
        // Locks large trend wins while preserving the strategy's positive skew.
        [Parameter("Trailing Stop (ATR mult, 0=off)", Group = "Risk", DefaultValue = 0.0, MinValue = 0, Step = 0.5)]
        public double TrailAtrMult { get; set; }

        // Resize outside the monthly rebalance only when the open size drifts this far
        // from target (keeps churn and spread costs down).
        [Parameter("Resize Drift Threshold (%)", Group = "Risk", DefaultValue = 40, MinValue = 10)]
        public int ResizeDriftPercent { get; set; }

        private const string Label = "TrendTSM";
        private const int Look1 = 21, Look3 = 63, Look12 = 261; // trading days
        private const int VolLookback = 63;
        private const int MinBars = 300;

        private sealed class Market
        {
            public Symbol Sym;
            public Bars Daily;
        }

        private readonly List<Market> _markets = new List<Market>();
        private int _lastRebalanceMonth = -1;

        protected override void OnStart()
        {
            if (TimeFrame != TimeFrame.Daily)
            {
                Print("FATAL: attach to a DAILY chart (signals are daily). Current: {0}. Stopping.", TimeFrame);
                Stop();
                return;
            }

            foreach (var raw in SymbolList.Split(','))
            {
                var name = raw.Trim();
                if (name.Length == 0)
                    continue;
                if (!Symbols.Exists(name))
                {
                    Print("[UNIVERSE] '{0}' not offered by this broker - skipped.", name);
                    continue;
                }
                var bars = MarketData.GetBars(TimeFrame.Daily, name);
                for (int guard = 0; bars.Count < MinBars && guard < 30; guard++)
                    if (bars.LoadMoreHistory() <= 0)
                        break;
                if (bars.Count < Look12 + 2)
                {
                    Print("[UNIVERSE] '{0}' has only {1} daily bars (<{2}) - skipped.", name, bars.Count, Look12 + 2);
                    continue;
                }
                _markets.Add(new Market { Sym = Symbols.GetSymbol(name), Daily = bars });
            }

            if (_markets.Count == 0)
            {
                Print("FATAL: no tradable symbols in universe. Stopping.");
                Stop();
                return;
            }

            Print("Started: {0} markets [{1}], vol target {2}%/yr each, {3}, drift resize {4}%.",
                _markets.Count, string.Join(",", _markets.ConvertAll(m => m.Sym.Name)),
                VolTargetPerMarketPct, LongOnly ? "LONG-ONLY" : "long-short", ResizeDriftPercent);
        }

        protected override void OnBar()
        {
            var nowOpen = Bars.OpenTimes[Bars.Count - 1]; // fixed at open; OHLC never read
            bool monthlyRebalance = nowOpen.Month != _lastRebalanceMonth;

            foreach (var m in _markets)
                ProcessMarket(m, monthlyRebalance);

            if (monthlyRebalance)
                _lastRebalanceMonth = nowOpen.Month;
        }

        private void ProcessMarket(Market m, bool monthlyRebalance)
        {
            int last = m.Daily.Count - 2; // last COMPLETED daily bar of THIS market
            if (last < Look12 + 1)
                return;

            // --- signal: equal-weighted sign of 1/3/12-month past returns ---
            double c0 = m.Daily.ClosePrices[last];
            double sig = (Math.Sign(c0 - m.Daily.ClosePrices[last - Look1])
                        + Math.Sign(c0 - m.Daily.ClosePrices[last - Look3])
                        + Math.Sign(c0 - m.Daily.ClosePrices[last - Look12])) / 3.0;
            if (LongOnly && sig < 0)
                sig = 0;

            // --- realized daily vol (63-day stdev of daily log returns), in price units ---
            double sum = 0, sumSq = 0;
            for (int k = last - VolLookback + 1; k <= last; k++)
            {
                double r = Math.Log(m.Daily.ClosePrices[k] / m.Daily.ClosePrices[k - 1]);
                sum += r; sumSq += r * r;
            }
            double mean = sum / VolLookback;
            double dailyVolFrac = Math.Sqrt(Math.Max(1e-12, sumSq / VolLookback - mean * mean));
            double dailyVolPrice = dailyVolFrac * c0;

            // --- target size: units whose daily $ vol = equity * volTarget / sqrt(261) * |sig| ---
            // PipValue converts pips -> account currency per unit, handling quote-ccy conversion.
            double targetDailyDollarVol = Account.Equity * (VolTargetPerMarketPct / 100.0) / Math.Sqrt(261.0) * Math.Abs(sig);
            double dollarVolPerUnit = (dailyVolPrice / m.Sym.PipSize) * m.Sym.PipValue;
            double targetUnits = dollarVolPerUnit > 0 ? targetDailyDollarVol / dollarVolPerUnit : 0;
            targetUnits = m.Sym.NormalizeVolumeInUnits(targetUnits, RoundingMode.Down);
            if (targetUnits > m.Sym.VolumeInUnitsMax)
                targetUnits = m.Sym.VolumeInUnitsMax;

            var wantType = sig > 0 ? TradeType.Buy : TradeType.Sell;
            bool wantFlat = sig == 0 || targetUnits < m.Sym.VolumeInUnitsMin;

            var pos = Positions.Find(Label, m.Sym.Name);

            if (pos != null)
            {
                bool flip = pos.TradeType != wantType;
                double drift = wantFlat ? 1.0 : Math.Abs(pos.VolumeInUnits - targetUnits) / targetUnits;
                bool resize = monthlyRebalance && drift > ResizeDriftPercent / 100.0;

                if (wantFlat || flip || resize)
                {
                    var closed = ClosePosition(pos);
                    if (!closed.IsSuccessful)
                    {
                        Print("[{0}] close FAILED: {1}", m.Sym.Name, closed.Error);
                        return;
                    }
                    Print("[{0}] closed {1} {2} units ({3}): net {4}.",
                        m.Sym.Name, pos.TradeType, pos.VolumeInUnits,
                        wantFlat ? "signal flat/too small" : flip ? "signal flip" : "monthly resize", pos.NetProfit);
                    pos = null;
                }
                else
                {
                    UpdateTrailingStop(m, pos, last);
                    return; // keep riding the trend
                }
            }

            if (wantFlat)
            {
                if (sig != 0 && targetUnits < m.Sym.VolumeInUnitsMin)
                    Print("[{0}] signal {1:+0.00;-0.00} but computed size {2} < min {3} - skipped (raise vol target or equity).",
                        m.Sym.Name, sig, targetUnits, m.Sym.VolumeInUnitsMin);
                return;
            }

            double? slPips = null;
            if (DisasterSlPercent > 0)
                slPips = c0 * DisasterSlPercent / 100.0 / m.Sym.PipSize;

            var result = ExecuteMarketOrder(wantType, m.Sym.Name, targetUnits, Label, slPips, null);
            if (result.IsSuccessful)
                Print("[{0}] {1} {2} units at {3} (signal {4:+0.00;-0.00}, vol {5:F2}%/day, SL {6}).",
                    m.Sym.Name, wantType, targetUnits, result.Position.EntryPrice, sig,
                    dailyVolFrac * 100, slPips.HasValue ? slPips.Value.ToString("F0") + "p" : "none");
            else
                Print("[{0}] {1} FAILED: {2}", m.Sym.Name, wantType, result.Error);
        }

        /// ATR(14) over completed daily bars, in price units.
        private static double DailyAtr(Bars d, int last, int period)
        {
            if (last < period)
                return 0;
            double sum = 0;
            for (int i = last - period + 1; i <= last; i++)
            {
                double tr = Math.Max(d.HighPrices[i] - d.LowPrices[i],
                            Math.Max(Math.Abs(d.HighPrices[i] - d.ClosePrices[i - 1]),
                                     Math.Abs(d.LowPrices[i] - d.ClosePrices[i - 1])));
                sum += tr;
            }
            return sum / period;
        }

        private void UpdateTrailingStop(Market m, Position pos, int last)
        {
            if (TrailAtrMult <= 0)
                return;
            double atr = DailyAtr(m.Daily, last, 14);
            if (atr <= 0)
                return;
            double close = m.Daily.ClosePrices[last];
            // ModifyPosition takes ABSOLUTE prices; ratchet only in the protective direction.
            if (pos.TradeType == TradeType.Buy)
            {
                double candidate = close - TrailAtrMult * atr;
                if (!pos.StopLoss.HasValue || candidate > pos.StopLoss.Value)
                {
                    var r = ModifyPosition(pos, candidate, pos.TakeProfit, null);
                    if (r.IsSuccessful)
                        Print("[{0}] trail SL -> {1}", m.Sym.Name, candidate);
                }
            }
            else
            {
                double candidate = close + TrailAtrMult * atr;
                if (!pos.StopLoss.HasValue || candidate < pos.StopLoss.Value)
                {
                    var r = ModifyPosition(pos, candidate, pos.TakeProfit, null);
                    if (r.IsSuccessful)
                        Print("[{0}] trail SL -> {1}", m.Sym.Name, candidate);
                }
            }
        }

        protected override void OnStop()
        {
            Print("Stopped. Open positions are left running (they are the strategy).");
        }
    }
}
