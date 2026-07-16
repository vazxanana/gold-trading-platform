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

    public enum TargetMode
    {
        M15Swing,
        H4Swing
    }

    /// <summary>
    /// Multi-timeframe market-structure bot v2 (H4 / M15 / M5), revised after the v1
    /// backtest showed the counter-trend cascade lost (PF 0.86): v1 bought while the
    /// H4 was still falling with M15-sized stops - a thesis/stop scale mismatch.
    ///
    /// v2 trades WITH the H4 trend and targets structure:
    ///   - H4 structure (HH/HL vs LH/LL) defines the trend to follow.
    ///   - Entry: an M15 CHoCH back TOWARD the H4 trend (the M15 pullback inside the
    ///     H4 trend is ending). EntryTimeframe=M5 uses the earlier M5 CHoCH while the
    ///     M15 is still in pullback.
    ///   - TP: STRUCTURAL - the last confirmed M15 or H4 swing high (longs) / low
    ///     (shorts), selectable via TargetMode. No fixed R multiple.
    ///   - Quality gate: computed reward/risk to the structural target must be at
    ///     least MinRR, otherwise the trade is skipped (target too close).
    ///   - SL: structural at the entry-TF pullback swing + ATR buffer, USD-capped.
    ///   - Optional Daily alignment filter (D1 trend must match H4) and optional
    ///     structure exit on an opposite entry-TF CHoCH before target.
    ///
    /// Same mechanical structure engine as before: fractal swings confirmed late
    /// (no look-ahead), completed bars only. ATTACH TO M5 (asserted).
    /// Evidence note: gold shorts failed every research window - A/B Buy vs Both.
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

        // Structural take-profit level: last confirmed swing of this timeframe
        // (high for longs, low for shorts). H4Swing = larger targets, fewer hit.
        [Parameter("Target", Group = "Signal", DefaultValue = TargetMode.M15Swing)]
        public TargetMode Target { get; set; }

        // Optional higher-TF alignment: Daily trend must match the H4 trend.
        [Parameter("Require Daily Alignment", Group = "Signal", DefaultValue = false)]
        public bool RequireDailyAlignment { get; set; }

        // Close early if the entry TF fires a CHoCH against the position before TP.
        [Parameter("Structure Exit (opposite CHoCH closes)", Group = "Signal", DefaultValue = true)]
        public bool StructureExit { get; set; }

        [Parameter("Risk % per Trade", Group = "Risk", DefaultValue = 0.5, MinValue = 0.05, Step = 0.05)]
        public double RiskPercent { get; set; }

        [Parameter("SL Buffer (ATR fraction, entry TF)", Group = "Risk", DefaultValue = 0.25, MinValue = 0, Step = 0.05)]
        public double SlBufferAtr { get; set; }

        [Parameter("Max SL (USD)", Group = "Risk", DefaultValue = 30.0, MinValue = 1.0)]
        public double MaxSlUsd { get; set; }

        // Skip trades whose structural target pays less than this multiple of the risk.
        [Parameter("Min Reward/Risk to Target", Group = "Risk", DefaultValue = 1.0, MinValue = 0.25, Step = 0.25)]
        public double MinRR { get; set; }

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

        // cascade diagnostics: why potential triggers were blocked (printed daily)
        private System.DateTime _diagDate = System.DateTime.MinValue;
        private int _diagChochToward, _diagBlockedDailyNeutral, _diagBlockedNoH4Pullback, _diagBlockedDirection;

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

            Print("Started on {0} M5. Follow H4 trend, {1} entries, target {2}, swing strength {3}, dailyAlign={4}, direction={5}, minRR={6}.",
                SymbolName, EntryTf, Target, SwingStrength, RequireDailyAlignment, TradeDirection, MinRR);
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

            if (DebugStructure)
                DailyDiag();

            int h4Trend = _h4.Trend;

            // candidate lower-TF CHoCH event this bar?
            int entryBreak = EntryTf == EntryTimeframeMode.M15 ? m15Break : m5Break;
            bool entryChoch = EntryTf == EntryTimeframeMode.M15 ? m15Choch : m5Choch;
            if (entryBreak == 0 || !entryChoch)
                return;

            if (h4Trend == 0)
            {
                _diagBlockedDailyNeutral++;
                return;
            }
            if (entryBreak != h4Trend)
                return; // CHoCH away from the H4 trend - not our signal
            if (EntryTf == EntryTimeframeMode.M5 && _m15.Trend != -h4Trend)
                return; // M5 mode requires M15 still in pullback

            _diagChochToward++;

            if (RequireDailyAlignment && _daily.Trend != h4Trend)
            {
                _diagBlockedNoH4Pullback++;
                if (DebugStructure)
                    Print("[GATE] {0:yyyy-MM-dd HH:mm} {1} CHoCH toward H4 trend, but D1 trend={2} not aligned.",
                        Bars.OpenTimes[Bars.Count - 1], EntryTf, _daily.Trend);
                return;
            }

            var tradeType = h4Trend > 0 ? TradeType.Buy : TradeType.Sell;
            if (TradeDirection != TradeDirectionMode.Both
                && (tradeType == TradeType.Buy) != (TradeDirection == TradeDirectionMode.Buy))
            {
                _diagBlockedDirection++;
                return;
            }
            Enter(tradeType);
        }

        /// <summary>Once per day: the state of all four trackers plus gate counters.</summary>        /// <summary>Once per day: the state of all four trackers plus gate counters.</summary>
        private void DailyDiag()
        {
            var d = Bars.OpenTimes[Bars.Count - 1].Date;
            if (d == _diagDate)
                return;
            if (_diagDate != System.DateTime.MinValue)
                Print("[DIAG {0:yyyy-MM-dd}] D1={1} H4={2} M15={3} M5={4} | CHoCH-toward-H4: {5}, blocked: h4Neutral {6}, dailyAlign {7}, direction {8}.",
                    _diagDate, T(_daily), T(_h4), T(_m15), T(_m5),
                    _diagChochToward + _diagBlockedDailyNeutral, _diagBlockedDailyNeutral, _diagBlockedNoH4Pullback, _diagBlockedDirection);
            _diagDate = d;
            _diagChochToward = 0; _diagBlockedDailyNeutral = 0; _diagBlockedNoH4Pullback = 0; _diagBlockedDirection = 0;
        }

        private static string T(StructureTracker tr)
        {
            return tr.Trend > 0 ? "UP" : tr.Trend < 0 ? "DOWN" : "flat";
        }

        /// <summary>Optional: opposite CHoCH on the entry TF closes the position early.</summary>
        private void ManageStructureExit(int m15Break, bool m15Choch, int m5Break, bool m5Choch)
        {
            if (!StructureExit)
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

            // structural take-profit: last confirmed swing of the target timeframe
            var targetTracker = Target == TargetMode.H4Swing ? _h4 : _m15;
            double targetLevel = tradeType == TradeType.Buy ? targetTracker.LastHigh : targetTracker.LastLow;
            if (double.IsNaN(targetLevel))
                return;
            double rewardUsd = tradeType == TradeType.Buy ? targetLevel - refPrice : refPrice - targetLevel;
            if (rewardUsd <= 0)
            {
                Print("[SKIP] {0:yyyy-MM-dd HH:mm} target {1} already beyond price {2}.", nowOpen, targetLevel, refPrice);
                return;
            }
            double rr = rewardUsd / slUsd;
            if (rr < MinRR)
            {
                Print("[SKIP] {0:yyyy-MM-dd HH:mm} structural RR {1:F2} below minimum {2:F2} (reward ${3:F2} / risk ${4:F2}).",
                    nowOpen, rr, MinRR, rewardUsd, slUsd);
                return;
            }

            double slPips = slUsd / Symbol.PipSize;
            double? tpPips = rewardUsd / Symbol.PipSize;
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
                Print("[ENTRY] {0:yyyy-MM-dd HH:mm} {1} #{2} at {3} - H4 {4}, {5} CHoCH continuation. {6} units, SL ${7:F2}, target {8} ({9}, RR {10:F2}). Trade {11}/{12}.",
                    nowOpen, tradeType, result.Position.Id, result.Position.EntryPrice,
                    _h4.Trend > 0 ? "UP" : "DOWN", EntryTf, units, slUsd,
                    targetLevel, Target, rr, _tradesToday, DailyTradeLimit);
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
