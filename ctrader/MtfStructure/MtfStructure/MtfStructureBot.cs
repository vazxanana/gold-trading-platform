using cAlgo.API;
using cAlgo.API.Indicators;
using cAlgo.API.Internals;

namespace cAlgo.Robots
{
    public enum TradeDirectionMode
    {
        Buy,
        Sell,
        Both
    }

    public enum EntryTimeframeMode
    {
        M15,
        M5
    }

    /// <summary>
    /// Multi-timeframe market-structure bot (Daily / H4 / M15 / M5), implementing the
    /// fractal-cascade idea: structure change on a lower timeframe is the earliest
    /// evidence that the timeframe above it is turning.
    ///
    ///   - Daily structure (HH/HL vs LH/LL) defines the TREND to trade with.
    ///   - An H4 CHoCH against the Daily trend = "the Daily pullback has started";
    ///     H4 trend opposite to Daily = pullback in progress. We only hunt entries here.
    ///   - An M15 CHoCH back TOWARD the Daily trend = "the H4 pullback is ending" -
    ///     the continuation signal (EntryTimeframe = M15).
    ///   - An M5 CHoCH toward the Daily trend while M15 is still in pullback = the
    ///     earliest version of the same signal (EntryTimeframe = M5): more trades,
    ///     earlier entries, more false starts. A/B both.
    ///
    /// Entry: market order in the Daily direction when the cascade lines up.
    /// SL: structural - beyond the entry timeframe's pullback swing, + ATR buffer.
    /// TP: RR multiple, or 0 = exit on the entry timeframe's opposite CHoCH.
    ///
    /// Every timeframe uses the same mechanical structure engine as MarketStructureBot:
    /// fractal swings confirmed SwingStrength bars late (no look-ahead), trend from the
    /// last two swings of each kind, CHoCH = close through the swing against trend.
    /// All series read COMPLETED bars only.
    ///
    /// ATTACH TO M5 (asserted) - it drives all four trackers.
    /// Evidence note: on gold every shorting variant this repo has tested failed;
    /// A/B TradeDirection=Buy against Both before trusting the short side.
    /// </summary>
    [Robot(AccessRights = AccessRights.None)]
    public class MtfStructureBot : Robot
    {
        [Parameter("Trade Direction", Group = "Signal", DefaultValue = TradeDirectionMode.Both)]
        public TradeDirectionMode TradeDirection { get; set; }

        [Parameter("Entry Timeframe", Group = "Signal", DefaultValue = EntryTimeframeMode.M15)]
        public EntryTimeframeMode EntryTf { get; set; }

        [Parameter("Swing Strength (bars each side)", Group = "Signal", DefaultValue = 3, MinValue = 1, MaxValue = 10)]
        public int SwingStrength { get; set; }

        // Strict: H4 trend must be OPPOSITE the Daily trend (a real pullback).
        // Relaxed (false): H4 merely not aligned (neutral counts) also qualifies.
        [Parameter("Require Strict H4 Pullback", Group = "Signal", DefaultValue = true)]
        public bool StrictH4Pullback { get; set; }

        [Parameter("Risk % per Trade", Group = "Risk", DefaultValue = 0.5, MinValue = 0.05, Step = 0.05)]
        public double RiskPercent { get; set; }

        [Parameter("SL Buffer (ATR fraction, entry TF)", Group = "Risk", DefaultValue = 0.25, MinValue = 0, Step = 0.05)]
        public double SlBufferAtr { get; set; }

        [Parameter("Max SL (USD)", Group = "Risk", DefaultValue = 30.0, MinValue = 1.0)]
        public double MaxSlUsd { get; set; }

        [Parameter("TP (R multiple, 0 = exit on opposite CHoCH)", Group = "Risk", DefaultValue = 2.0, MinValue = 0, Step = 0.25)]
        public double RRRatio { get; set; }

        [Parameter("Daily Trade Limit", Group = "Risk", DefaultValue = 3, MinValue = 1)]
        public int DailyTradeLimit { get; set; }

        [Parameter("Max Entry Spread (USD)", Group = "Risk", DefaultValue = 0.60, MinValue = 0.01, Step = 0.05)]
        public double MaxEntrySpreadUsd { get; set; }

        [Parameter("Debug: Log Structure Events", Group = "Debug", DefaultValue = true)]
        public bool DebugStructure { get; set; }

        private const string Label = "MtfStructure";

        // ------------------------------------------------------------------ tracker --

        /// <summary>
        /// Self-contained structure engine for one timeframe: fractal swings (confirmed
        /// SwingStrength bars late), HH/HL-LH/LL trend, and close-through-swing breaks.
        /// Processes each completed bar exactly once; indexes are stable because
        /// history is loaded before tracking starts and only new bars append.
        /// </summary>
        private sealed class StructureTracker
        {
            public readonly Bars Series;
            public readonly string Name;
            private readonly int _ss;

            public double LastHigh = double.NaN, PrevHigh = double.NaN;
            public double LastLow = double.NaN, PrevLow = double.NaN;
            public bool HighConsumed = true, LowConsumed = true;
            public int Trend;                 // +1 up, -1 down, 0 neutral
            public int PendingBreak;          // +1 bullish / -1 bearish break this update, 0 none
            public bool PendingBreakIsChoch;

            private int _nextPivot;
            private int _nextBreak;

            public StructureTracker(Bars series, string name, int swingStrength)
            {
                Series = series;
                Name = name;
                _ss = swingStrength;
                _nextPivot = _ss;                       // first index that can be a pivot
                _nextBreak = 2 * _ss + 1;
            }

            /// <summary>Consume any newly completed bars. Returns log lines when events occur.</summary>
            public void Update(System.Collections.Generic.List<string> log)
            {
                int lastCompleted = Series.Count - 2;

                // confirm pivots whose right side is fully completed
                while (_nextPivot + _ss <= lastCompleted)
                {
                    ConfirmPivot(_nextPivot, log);
                    _nextPivot++;
                }

                // detect breaks on each completed bar exactly once
                while (_nextBreak <= lastCompleted)
                {
                    DetectBreak(_nextBreak, log);
                    _nextBreak++;
                }
            }

            private void ConfirmPivot(int p, System.Collections.Generic.List<string> log)
            {
                bool isHigh = true, isLow = true;
                for (int k = 1; k <= _ss && (isHigh || isLow); k++)
                {
                    if (Series.HighPrices[p] <= Series.HighPrices[p - k] || Series.HighPrices[p] <= Series.HighPrices[p + k])
                        isHigh = false;
                    if (Series.LowPrices[p] >= Series.LowPrices[p - k] || Series.LowPrices[p] >= Series.LowPrices[p + k])
                        isLow = false;
                }
                if (isHigh)
                {
                    PrevHigh = LastHigh; LastHigh = Series.HighPrices[p]; HighConsumed = false;
                }
                if (isLow)
                {
                    PrevLow = LastLow; LastLow = Series.LowPrices[p]; LowConsumed = false;
                }
                if ((isHigh || isLow) && !double.IsNaN(PrevHigh) && !double.IsNaN(PrevLow))
                {
                    bool hh = LastHigh > PrevHigh, hl = LastLow > PrevLow;
                    bool lh = LastHigh < PrevHigh, ll = LastLow < PrevLow;
                    int t = hh && hl ? 1 : lh && ll ? -1 : Trend;
                    if (t != Trend)
                    {
                        Trend = t;
                        log?.Add(string.Format("[{0}] trend -> {1} (swing sequence)", Name, t > 0 ? "UP" : "DOWN"));
                    }
                }
            }

            private void DetectBreak(int i, System.Collections.Generic.List<string> log)
            {
                double close = Series.ClosePrices[i];
                if (!double.IsNaN(LastHigh) && !HighConsumed && close > LastHigh)
                {
                    HighConsumed = true;
                    PendingBreak = 1;
                    PendingBreakIsChoch = Trend == -1;
                    if (PendingBreakIsChoch) Trend = 1;
                    log?.Add(string.Format("[{0}] {1:yyyy-MM-dd HH:mm} close {2} > swing high {3}: {4}",
                        Name, Series.OpenTimes[i], close, LastHigh, PendingBreakIsChoch ? "CHoCH UP" : "BOS up"));
                }
                if (!double.IsNaN(LastLow) && !LowConsumed && close < LastLow)
                {
                    LowConsumed = true;
                    PendingBreak = -1;
                    PendingBreakIsChoch = Trend == 1;
                    if (PendingBreakIsChoch) Trend = -1;
                    log?.Add(string.Format("[{0}] {1:yyyy-MM-dd HH:mm} close {2} < swing low {3}: {4}",
                        Name, Series.OpenTimes[i], close, LastLow, PendingBreakIsChoch ? "CHoCH DOWN" : "BOS down"));
                }
            }

            /// <summary>Returns the pending break event (+1/-1 with CHoCH flag) and clears it.</summary>
            public int TakeBreak(out bool wasChoch)
            {
                int b = PendingBreak;
                wasChoch = PendingBreakIsChoch;
                PendingBreak = 0;
                PendingBreakIsChoch = false;
                return b;
            }
        }

        // ------------------------------------------------------------------- state --

        private StructureTracker _daily, _h4, _m15, _m5;
        private AverageTrueRange _atrM15, _atrM5;
        private readonly System.Collections.Generic.List<string> _log = new System.Collections.Generic.List<string>();
        private System.DateTime _tradeCountDate = System.DateTime.MinValue;
        private int _tradesToday;

        protected override void OnStart()
        {
            if (TimeFrame != TimeFrame.Minute5)
            {
                Print("FATAL: attach to an M5 chart (it drives all four trackers). Current: {0}. Stopping.", TimeFrame);
                Stop();
                return;
            }

            var daily = MarketData.GetBars(TimeFrame.Daily);
            var h4 = MarketData.GetBars(TimeFrame.Hour4);
            var m15 = MarketData.GetBars(TimeFrame.Minute15);
            EnsureHistory(daily, 200); EnsureHistory(h4, 400); EnsureHistory(m15, 600);

            _daily = new StructureTracker(daily, "D1", SwingStrength);
            _h4 = new StructureTracker(h4, "H4", SwingStrength);
            _m15 = new StructureTracker(m15, "M15", SwingStrength);
            _m5 = new StructureTracker(Bars, "M5", SwingStrength);

            _atrM15 = Indicators.AverageTrueRange(m15, 14, MovingAverageType.Simple);
            _atrM5 = Indicators.AverageTrueRange(14, MovingAverageType.Simple);

            Print("Started on {0} M5. Cascade D1>H4>{1} entries, swing strength {2}, strictH4Pullback={3}, direction={4}, RR={5}.",
                SymbolName, EntryTf, SwingStrength, StrictH4Pullback, TradeDirection, RRRatio);
        }

        private static void EnsureHistory(Bars bars, int target)
        {
            for (int guard = 0; bars.Count < target && guard < 30; guard++)
                if (bars.LoadMoreHistory() <= 0)
                    break;
        }

        protected override void OnBar()
        {
            _log.Clear();
            _daily.Update(DebugStructure ? _log : null);
            _h4.Update(DebugStructure ? _log : null);
            _m15.Update(DebugStructure ? _log : null);
            _m5.Update(DebugStructure ? _log : null);
            foreach (var line in _log)
                Print(line);

            // consume entry-TF break events every bar (they must not go stale),
            // and use the M15 event stream when M15 is the entry TF.
            bool m15Choch, m5Choch;
            int m15Break = _m15.TakeBreak(out m15Choch);
            int m5Break = _m5.TakeBreak(out m5Choch);

            ManageStructureExit(m15Break, m15Choch, m5Break, m5Choch);

            int dailyTrend = _daily.Trend;
            if (dailyTrend == 0)
                return;

            // the cascade: H4 must be pulling back against the Daily trend
            bool h4Pullback = StrictH4Pullback ? _h4.Trend == -dailyTrend : _h4.Trend != dailyTrend;
            if (!h4Pullback)
                return;

            // entry trigger: lower-TF CHoCH flipping back TOWARD the Daily trend
            bool trigger;
            if (EntryTf == EntryTimeframeMode.M15)
                trigger = m15Choch && m15Break == dailyTrend;
            else
                trigger = m5Choch && m5Break == dailyTrend && _m15.Trend == -dailyTrend; // M15 still in pullback

            if (!trigger)
                return;

            var tradeType = dailyTrend > 0 ? TradeType.Buy : TradeType.Sell;
            Enter(tradeType);
        }

        /// <summary>RRRatio = 0 mode: opposite CHoCH on the entry TF closes the position.</summary>
        private void ManageStructureExit(int m15Break, bool m15Choch, int m5Break, bool m5Choch)
        {
            if (RRRatio > 0)
                return;
            int exitBreak = EntryTf == EntryTimeframeMode.M15 ? m15Break : m5Break;
            bool exitChoch = EntryTf == EntryTimeframeMode.M15 ? m15Choch : m5Choch;
            if (exitBreak == 0 || !exitChoch)
                return;
            var against = exitBreak > 0 ? TradeType.Sell : TradeType.Buy;
            foreach (var pos in Positions.FindAll(Label, SymbolName, against))
            {
                var r = ClosePosition(pos);
                if (r.IsSuccessful)
                    Print("[STRUCT-EXIT] closed {0} #{1} on opposite {2} CHoCH: net {3}.", against, pos.Id, EntryTf, pos.NetProfit);
            }
        }

        private void Enter(TradeType tradeType)
        {
            if (TradeDirection != TradeDirectionMode.Both
                && (tradeType == TradeType.Buy) != (TradeDirection == TradeDirectionMode.Buy))
                return;

            var nowOpen = Bars.OpenTimes[Bars.Count - 1]; // fixed at open; OHLC never read
            if (nowOpen.Date != _tradeCountDate)
            {
                _tradeCountDate = nowOpen.Date;
                _tradesToday = 0;
            }
            if (_tradesToday >= DailyTradeLimit)
                return;
            if (Positions.Find(Label, SymbolName, tradeType) != null)
                return;
            if (Symbol.Spread > MaxEntrySpreadUsd)
            {
                Print("[SKIP] {0:yyyy-MM-dd HH:mm} spread ${1:F2} too wide.", nowOpen, Symbol.Spread);
                return;
            }

            // structural stop: beyond the entry TF's pullback swing + ATR buffer
            var entryTracker = EntryTf == EntryTimeframeMode.M15 ? _m15 : _m5;
            double atr = EntryTf == EntryTimeframeMode.M15
                ? _atrM15.Result[entryTracker.Series.Count - 2]
                : _atrM5.Result[Bars.Count - 2];
            double swing = tradeType == TradeType.Buy ? entryTracker.LastLow : entryTracker.LastHigh;
            if (double.IsNaN(swing))
                return;
            double slLevel = tradeType == TradeType.Buy ? swing - SlBufferAtr * atr : swing + SlBufferAtr * atr;
            double refPrice = tradeType == TradeType.Buy ? Symbol.Ask : Symbol.Bid;
            double slUsd = tradeType == TradeType.Buy ? refPrice - slLevel : slLevel - refPrice;
            if (slUsd <= 0)
            {
                Print("[SKIP] {0:yyyy-MM-dd HH:mm} structural stop on wrong side (price {1}, SL {2}).", nowOpen, refPrice, slLevel);
                return;
            }
            if (slUsd > MaxSlUsd)
            {
                Print("[SKIP] {0:yyyy-MM-dd HH:mm} structural SL ${1:F2} exceeds cap ${2}.", nowOpen, slUsd, MaxSlUsd);
                return;
            }

            double slPips = slUsd / Symbol.PipSize;
            double? tpPips = RRRatio > 0 ? slPips * RRRatio : (double?)null;
            double riskMoney = Account.Balance * RiskPercent / 100.0;
            double units = Symbol.NormalizeVolumeInUnits(riskMoney / (slPips * Symbol.PipValue), RoundingMode.Down);
            if (units < Symbol.VolumeInUnitsMin)
            {
                Print("[SKIP] {0:yyyy-MM-dd HH:mm} size below minimum (SL {1:F0} pips).", nowOpen, slPips);
                return;
            }
            if (units > Symbol.VolumeInUnitsMax)
                units = Symbol.VolumeInUnitsMax;

            var result = ExecuteMarketOrder(tradeType, SymbolName, units, Label, slPips, tpPips);
            if (result.IsSuccessful)
            {
                _tradesToday++;
                Print("[ENTRY] {0:yyyy-MM-dd HH:mm} {1} #{2} at {3} - D1 {4}, H4 pullback, {5} CHoCH continuation. {6} units, SL ${7:F2}, TP {8}. Trade {9}/{10}.",
                    nowOpen, tradeType, result.Position.Id, result.Position.EntryPrice,
                    _daily.Trend > 0 ? "UP" : "DOWN", EntryTf, units, slUsd,
                    tpPips.HasValue ? tpPips.Value.ToString("F0") + "p" : "structure exit",
                    _tradesToday, DailyTradeLimit);
            }
            else
                Print("[ENTRY] {0:yyyy-MM-dd HH:mm} FAILED: {1}", nowOpen, result.Error);
        }

        protected override void OnStop()
        {
            Print("Stopped.");
        }
    }
}
