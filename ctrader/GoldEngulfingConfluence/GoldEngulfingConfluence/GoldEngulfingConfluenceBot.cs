using cAlgo.API;
using cAlgo.API.Internals;

namespace cAlgo.Robots
{
    public enum TradeDirectionMode
    {
        Buy,
        Sell,
        Both
    }

    /// <summary>
    /// XAUUSD multi-timeframe engulfing confluence bot.
    ///
    /// Attaches to M5. Setup (bullish; bearish is the exact mirror):
    ///   1. M15 bullish engulfing -> its low/high becomes "the M15 zone" (expires after ZoneExpiryBars M15 bars).
    ///   2. M5 bullish engulfing whose CLOSE is inside the M15 zone -> record M5 EG low/high
    ///      (expires after ZoneExpiryBars M5 bars).
    ///   3. On M1: (a) a bar's low taps/breaks the M5 EG low, (b) then an M1 BEARISH engulfing forms,
    ///      (c) then an M1 bar closes above that bearish EG's high -> BUY.
    ///   SL = M5 EG low - SlBufferPips, TP = entry + risk * RRRatio.
    ///
    /// All bar reads use index Count - 2 (last COMPLETED bar) - never the forming bar.
    /// </summary>
    [Robot(AccessRights = AccessRights.None)]
    public class GoldEngulfingConfluenceBot : Robot
    {
        [Parameter("Lot Size", Group = "Risk", DefaultValue = 0.1, MinValue = 0.01, Step = 0.01)]
        public double LotSize { get; set; }

        [Parameter("SL Buffer (pips)", Group = "Risk", DefaultValue = 50, MinValue = 0)]
        public double SlBufferPips { get; set; }

        [Parameter("RR Ratio", Group = "Risk", DefaultValue = 2.5, MinValue = 0.1, Step = 0.1)]
        public double RRRatio { get; set; }

        [Parameter("Max SL (pips)", Group = "Risk", DefaultValue = 200, MinValue = 1)]
        public double MaxSlPips { get; set; }

        [Parameter("Use Session Filter", Group = "Session", DefaultValue = true)]
        public bool UseSessionFilter { get; set; }

        [Parameter("Session Start Hour (UTC)", Group = "Session", DefaultValue = 12, MinValue = 0, MaxValue = 23)]
        public int SessionStartHourUtc { get; set; }

        [Parameter("Session End Hour (UTC)", Group = "Session", DefaultValue = 15, MinValue = 0, MaxValue = 23)]
        public int SessionEndHourUtc { get; set; }

        [Parameter("Daily Trade Limit", Group = "Limits", DefaultValue = 5, MinValue = 1)]
        public int DailyTradeLimit { get; set; }

        // A zone/EG expires after this many bars OF ITS OWN TIMEFRAME:
        // the M15 zone after N M15 bars (N=20 -> 5h), the M5 EG after N M5 bars (N=20 -> 100min).
        [Parameter("Zone Expiry (bars)", Group = "Limits", DefaultValue = 20, MinValue = 1)]
        public int ZoneExpiryBars { get; set; }

        [Parameter("Trade Direction", Group = "Limits", DefaultValue = TradeDirectionMode.Both)]
        public TradeDirectionMode TradeDirection { get; set; }

        // Step 2 diagnostic: on start, scan recent completed bars on M15/M5/M1 and print every
        // engulfing detection so the definition can be eyeballed against the chart.
        [Parameter("Debug: Log EG Scan On Start", Group = "Debug", DefaultValue = true)]
        public bool DebugScanOnStart { get; set; }

        private const string Label = "GoldEngulfingConfluence";

        // Cached in OnStart per hard rule #4 - never call MarketData.GetBars in OnBar.
        private Bars _m15Bars;
        private Bars _m1Bars;

        protected override void OnStart()
        {
            // Hard rule #1: this bot's state machine is driven by the M5 chart it is attached to.
            if (TimeFrame != TimeFrame.Minute5)
            {
                Print("FATAL: bot must be attached to an M5 chart, but chart timeframe is {0}. Stopping.", TimeFrame);
                Stop();
                return;
            }

            _m15Bars = MarketData.GetBars(TimeFrame.Minute15);
            _m1Bars = MarketData.GetBars(TimeFrame.Minute);

            Print("Started on {0} M5. M15 bars: {1}, M1 bars: {2}. Direction={3}, ZoneExpiry={4} bars (per own TF).",
                SymbolName, _m15Bars.Count, _m1Bars.Count, TradeDirection, ZoneExpiryBars);

            if (DebugScanOnStart)
            {
                // Roughly 3 days of M15, 1 day of M5, 6 hours of M1.
                LogEngulfingScan(_m15Bars, "M15", 288);
                LogEngulfingScan(Bars, "M5", 288);
                LogEngulfingScan(_m1Bars, "M1", 360);
            }
        }

        protected override void OnBar()
        {
            // Steps 3-6 build the state machine here.
        }

        /// <summary>
        /// True if the completed bar at <paramref name="index"/> is a bullish engulfing.
        /// Callers must pass an index of a COMPLETED bar (at most series.Count - 2 when
        /// called while a bar is forming).
        /// </summary>
        private static bool IsBullishEngulfing(Bars series, int index)
        {
            if (index < 1 || index >= series.Count)
                return false;
            return EngulfingLogic.IsBullish(
                series.OpenPrices[index - 1], series.ClosePrices[index - 1],
                series.OpenPrices[index], series.ClosePrices[index]);
        }

        private static bool IsBearishEngulfing(Bars series, int index)
        {
            if (index < 1 || index >= series.Count)
                return false;
            return EngulfingLogic.IsBearish(
                series.OpenPrices[index - 1], series.ClosePrices[index - 1],
                series.OpenPrices[index], series.ClosePrices[index]);
        }

        /// <summary>
        /// Step 2 diagnostic: print every engulfing among the last <paramref name="lookback"/>
        /// COMPLETED bars. The newest index scanned is Count - 2 - the bar at Count - 1 is
        /// still forming and is never read (hard rule #2).
        /// </summary>
        private void LogEngulfingScan(Bars series, string tag, int lookback)
        {
            int last = series.Count - 2;
            int first = last - lookback + 1;
            if (first < 1)
                first = 1;
            if (last < 1)
            {
                Print("[EG-SCAN {0}] not enough history ({1} bars).", tag, series.Count);
                return;
            }

            int bullCount = 0, bearCount = 0;
            for (int i = first; i <= last; i++)
            {
                bool bull = IsBullishEngulfing(series, i);
                bool bear = IsBearishEngulfing(series, i);
                if (!bull && !bear)
                    continue;

                if (bull) bullCount++;
                if (bear) bearCount++;
                Print("[EG-SCAN {0}] {1} {2:yyyy-MM-dd HH:mm} O={3} H={4} L={5} C={6} (prior O={7} C={8})",
                    tag, bull ? "BULL" : "BEAR", series.OpenTimes[i],
                    series.OpenPrices[i], series.HighPrices[i], series.LowPrices[i], series.ClosePrices[i],
                    series.OpenPrices[i - 1], series.ClosePrices[i - 1]);
            }

            Print("[EG-SCAN {0}] scanned {1} completed bars ({2:yyyy-MM-dd HH:mm} .. {3:yyyy-MM-dd HH:mm}): {4} bullish, {5} bearish.",
                tag, last - first + 1, series.OpenTimes[first], series.OpenTimes[last], bullCount, bearCount);
        }

        protected override void OnStop()
        {
            Print("Stopped.");
        }
    }
}
