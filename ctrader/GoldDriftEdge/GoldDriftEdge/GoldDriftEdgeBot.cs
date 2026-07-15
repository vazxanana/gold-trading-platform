using cAlgo.API;
using cAlgo.API.Internals;

namespace cAlgo.Robots
{
    /// <summary>
    /// Test bot for the two edges that survived a 16-year, two-era out-of-time validation
    /// on real XAUUSD data (GoldEdgeResearch runs on 2010-2012 and 2024-2026 history):
    ///
    ///   1. Close/reopen drift: gold's upward drift concentrates in the 21-24 UTC window
    ///      (positive in all four IS/OOS windows across both eras).
    ///   2. Buy-after-down-day mean reversion: next-day return after a down close is
    ///      positive in all four windows (t >= 2 in three of them).
    ///
    /// One long entry per day at EntryHourUtc, flat again at ExitHourUtc:
    ///   - ExitHourUtc = 0,  OnlyAfterDownDay = false -> pure close-window drift (21->24)
    ///   - ExitHourUtc = 21, OnlyAfterDownDay = true  -> down-day mean reversion (24h hold)
    /// Long-only by design: shorts were unsupported in every window of both eras.
    ///
    /// ATTACHES TO H1 (asserted). The emergency SL is disaster protection, not a tuned
    /// stop. Overnight holds pay swap - backtest with your broker's real swap-long rate
    /// and check it does not eat the edge (~2-13bp/day gross).
    /// </summary>
    [Robot(AccessRights = AccessRights.None)]
    public class GoldDriftEdgeBot : Robot
    {
        [Parameter("Lot Size", Group = "Risk", DefaultValue = 0.1, MinValue = 0.01, Step = 0.01)]
        public double LotSize { get; set; }

        // Wide on purpose: it caps disaster, it does not manage the trade.
        [Parameter("Emergency SL (pips)", Group = "Risk", DefaultValue = 300, MinValue = 50)]
        public double EmergencySlPips { get; set; }

        // Set to your broker's daily rollover hour (usually 21 or 22 UTC) so the
        // down-day check uses the day that just closed.
        [Parameter("Entry Hour (UTC)", Group = "Timing", DefaultValue = 21, MinValue = 0, MaxValue = 23)]
        public int EntryHourUtc { get; set; }

        [Parameter("Exit Hour (UTC)", Group = "Timing", DefaultValue = 0, MinValue = 0, MaxValue = 23)]
        public int ExitHourUtc { get; set; }

        [Parameter("Only After Down Day", Group = "Filter", DefaultValue = false)]
        public bool OnlyAfterDownDay { get; set; }

        // When the exit lands on the next calendar day, a Friday entry would carry the
        // position (and its risk) across the weekend close - skipped by default.
        [Parameter("Skip Friday Entry", Group = "Filter", DefaultValue = true)]
        public bool SkipFridayEntry { get; set; }

        private const string Label = "GoldDriftEdge";

        private Bars _daily; // cached once in OnStart

        protected override void OnStart()
        {
            if (TimeFrame != TimeFrame.Hour)
            {
                Print("FATAL: attach to an H1 chart (timing is hour-based). Current: {0}. Stopping.", TimeFrame);
                Stop();
                return;
            }

            _daily = MarketData.GetBars(TimeFrame.Daily);
            Print("Started on {0} H1. Entry {1}:00 UTC -> exit {2}:00 UTC, onlyAfterDownDay={3}, skipFriday={4}.",
                SymbolName, EntryHourUtc, ExitHourUtc, OnlyAfterDownDay, SkipFridayEntry);
        }

        protected override void OnBar()
        {
            // The just-opened bar's OPEN TIME is fixed the moment the bar opens - reading
            // it is not look-ahead. Its OHLC is never read. Historical reads stay at
            // Count - 2 as everywhere else.
            var nowOpen = Bars.OpenTimes[Bars.Count - 1];

            var pos = Positions.Find(Label, SymbolName, TradeType.Buy);

            if (pos != null && nowOpen.Hour == ExitHourUtc)
            {
                var closed = ClosePosition(pos);
                if (closed.IsSuccessful)
                    Print("[EXIT] {0:yyyy-MM-dd HH:mm} closed #{1}: net {2}.", nowOpen, pos.Id, pos.NetProfit);
                else
                    Print("[EXIT] {0:yyyy-MM-dd HH:mm} FAILED to close #{1}: {2}", nowOpen, pos.Id, closed.Error);
                pos = null;
            }

            if (nowOpen.Hour != EntryHourUtc || pos != null)
                return;

            bool exitNextDay = ExitHourUtc <= EntryHourUtc;
            if (SkipFridayEntry && exitNextDay && nowOpen.DayOfWeek == System.DayOfWeek.Friday)
            {
                Print("[SKIP] {0:yyyy-MM-dd HH:mm} Friday entry would hold over the weekend.", nowOpen);
                return;
            }

            if (OnlyAfterDownDay)
            {
                int d = _daily.Count - 2; // last COMPLETED daily bar
                if (d < 1)
                    return;
                // Down day = close-to-close, matching the research definition.
                if (_daily.ClosePrices[d] >= _daily.ClosePrices[d - 1])
                {
                    Print("[SKIP] {0:yyyy-MM-dd HH:mm} prior day not down ({1} -> {2}).",
                        nowOpen, _daily.ClosePrices[d - 1], _daily.ClosePrices[d]);
                    return;
                }
            }

            double volumeUnits = Symbol.NormalizeVolumeInUnits(Symbol.QuantityToVolumeInUnits(LotSize), RoundingMode.Down);
            if (volumeUnits < Symbol.VolumeInUnitsMin)
            {
                Print("[ENTRY] REJECTED: LotSize {0} below symbol minimum.", LotSize);
                return;
            }

            var result = ExecuteMarketOrder(TradeType.Buy, SymbolName, volumeUnits, Label, EmergencySlPips, null);
            if (result.IsSuccessful)
                Print("[ENTRY] {0:yyyy-MM-dd HH:mm} long #{1} at {2}, emergency SL {3} pips, exit at {4}:00 UTC.",
                    nowOpen, result.Position.Id, result.Position.EntryPrice, EmergencySlPips, ExitHourUtc);
            else
                Print("[ENTRY] {0:yyyy-MM-dd HH:mm} FAILED: {1}", nowOpen, result.Error);
        }

        protected override void OnStop()
        {
            Print("Stopped.");
        }
    }
}
