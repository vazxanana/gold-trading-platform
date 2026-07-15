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

        // Step 5 mode: log fully-formed signals but place no orders.
        [Parameter("Debug: Dry Run (no orders)", Group = "Debug", DefaultValue = false)]
        public bool DryRun { get; set; }

        private const string Label = "GoldEngulfingConfluence";

        // Cached in OnStart per hard rule #4 - never call MarketData.GetBars in OnBar.
        private Bars _m15Bars;
        private Bars _m1Bars;

        /// <summary>
        /// A price zone anchored to the bar (of its own timeframe) that created it.
        /// Expires after ZoneExpiryBars bars of that same timeframe.
        /// </summary>
        private sealed class Zone
        {
            public double Low;
            public double High;
            public int BarIndex;          // index in its own Bars series
            public System.DateTime BarTime;
        }

        // M15 zones, one slot per direction; a newer M15 engulfing replaces the old zone.
        private Zone _m15BullZone;
        private Zone _m15BearZone;

        // Open time of the last M15 completed bar we evaluated, so each M15 bar is processed once.
        private System.DateTime _lastM15Evaluated = System.DateTime.MinValue;

        // M5 engulfing confirmations (step 4): set when an M5 engulfing closes inside the
        // matching M15 zone; expire after ZoneExpiryBars M5 bars.
        private Zone _m5BullEg;
        private Zone _m5BearEg;

        /// <summary>
        /// Step 5: per-direction M1 trigger state. For buys: price taps the M5 EG low,
        /// then an M1 bearish engulfing forms (its HIGH is the trigger), then an M1 close
        /// above that high signals entry. Sells are the exact mirror.
        /// </summary>
        private sealed class M1State
        {
            public bool Tapped;
            public bool HasTrigger;
            public double TriggerLevel;
            public System.DateTime TriggerTime;

            public void Reset()
            {
                Tapped = false;
                HasTrigger = false;
                TriggerLevel = 0;
                TriggerTime = System.DateTime.MinValue;
            }
        }

        private readonly M1State _m1Buy = new M1State();
        private readonly M1State _m1Sell = new M1State();

        // Open time of the last completed M1 bar we evaluated (process each M1 bar once).
        private System.DateTime _lastM1Evaluated = System.DateTime.MinValue;

        // Step 6: daily trade counter, keyed to the signal bar's DATE (hard rule #6:
        // reset on date change).
        private System.DateTime _tradeCountDate = System.DateTime.MinValue;
        private int _tradesToday;

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

            // React to every completed M1 bar without polling: cAlgo raises BarOpened when a
            // new bar opens, at which point Count - 2 is the just-completed bar.
            _m1Bars.BarOpened += OnM1BarOpened;

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
            // Called when a new M5 bar OPENS; Bars.Count - 2 is the just-completed M5 bar.
            ProcessM15Zones();
            ProcessM5Confluence();
        }

        /// <summary>
        /// Step 4: an M5 engulfing whose CLOSE falls inside the matching M15 zone becomes
        /// the M5 EG reference (its low/high recorded). The M15 zone stays active - it is
        /// not consumed - so a later M5 engulfing can re-confirm and replace the reference.
        /// </summary>
        private void ProcessM5Confluence()
        {
            int last = Bars.Count - 2; // just-completed M5 bar (hard rule #2)
            if (last < 1)
                return;

            // Expiry: the M5 EG reference lives ZoneExpiryBars M5 bars.
            if (_m5BullEg != null && last - _m5BullEg.BarIndex >= ZoneExpiryBars)
            {
                Print("[M5-EG] BULL EG from {0:yyyy-MM-dd HH:mm} [{1}..{2}] EXPIRED after {3} M5 bars.",
                    _m5BullEg.BarTime, _m5BullEg.Low, _m5BullEg.High, ZoneExpiryBars);
                _m5BullEg = null;
                _m1Buy.Reset();
            }
            if (_m5BearEg != null && last - _m5BearEg.BarIndex >= ZoneExpiryBars)
            {
                Print("[M5-EG] BEAR EG from {0:yyyy-MM-dd HH:mm} [{1}..{2}] EXPIRED after {3} M5 bars.",
                    _m5BearEg.BarTime, _m5BearEg.Low, _m5BearEg.High, ZoneExpiryBars);
                _m5BearEg = null;
                _m1Sell.Reset();
            }

            double lastClose = Bars.ClosePrices[last];

            if (_m15BullZone != null && DirectionAllows(TradeType.Buy)
                && IsBullishEngulfing(Bars, last)
                && lastClose >= _m15BullZone.Low && lastClose <= _m15BullZone.High)
            {
                if (_m5BullEg != null)
                {
                    Print("[M5-EG] BULL EG from {0:yyyy-MM-dd HH:mm} replaced by newer confirmation.", _m5BullEg.BarTime);
                    _m1Buy.Reset();
                }
                _m5BullEg = new Zone
                {
                    Low = Bars.LowPrices[last],
                    High = Bars.HighPrices[last],
                    BarIndex = last,
                    BarTime = Bars.OpenTimes[last]
                };
                Print("[M5-EG] BULL confluence {0:yyyy-MM-dd HH:mm}: M5 EG close {1} inside M15 zone [{2}..{3}] -> EG ref [{4}..{5}], valid {6} M5 bars.",
                    _m5BullEg.BarTime, lastClose, _m15BullZone.Low, _m15BullZone.High, _m5BullEg.Low, _m5BullEg.High, ZoneExpiryBars);
            }

            if (_m15BearZone != null && DirectionAllows(TradeType.Sell)
                && IsBearishEngulfing(Bars, last)
                && lastClose >= _m15BearZone.Low && lastClose <= _m15BearZone.High)
            {
                if (_m5BearEg != null)
                {
                    Print("[M5-EG] BEAR EG from {0:yyyy-MM-dd HH:mm} replaced by newer confirmation.", _m5BearEg.BarTime);
                    _m1Sell.Reset();
                }
                _m5BearEg = new Zone
                {
                    Low = Bars.LowPrices[last],
                    High = Bars.HighPrices[last],
                    BarIndex = last,
                    BarTime = Bars.OpenTimes[last]
                };
                Print("[M5-EG] BEAR confluence {0:yyyy-MM-dd HH:mm}: M5 EG close {1} inside M15 zone [{2}..{3}] -> EG ref [{4}..{5}], valid {6} M5 bars.",
                    _m5BearEg.BarTime, lastClose, _m15BearZone.Low, _m15BearZone.High, _m5BearEg.Low, _m5BearEg.High, ZoneExpiryBars);
            }
        }

        private bool DirectionAllows(TradeType tradeType)
        {
            return TradeDirection == TradeDirectionMode.Both
                || (TradeDirection == TradeDirectionMode.Buy && tradeType == TradeType.Buy)
                || (TradeDirection == TradeDirectionMode.Sell && tradeType == TradeType.Sell);
        }

        /// <summary>
        /// Step 3: whenever a new M15 bar has completed, expire stale zones, then look for a
        /// fresh M15 engulfing and record its low/high as the zone for that direction.
        /// </summary>
        private void ProcessM15Zones()
        {
            int last = _m15Bars.Count - 2; // last COMPLETED M15 bar (hard rule #2)
            if (last < 1)
                return;

            var lastOpenTime = _m15Bars.OpenTimes[last];
            if (lastOpenTime == _lastM15Evaluated)
                return; // no new completed M15 bar since the previous M5 OnBar
            _lastM15Evaluated = lastOpenTime;

            // Expiry: a zone lives ZoneExpiryBars M15 bars after the bar that created it.
            if (_m15BullZone != null && last - _m15BullZone.BarIndex >= ZoneExpiryBars)
            {
                Print("[M15-ZONE] BULL zone from {0:yyyy-MM-dd HH:mm} [{1}..{2}] EXPIRED after {3} M15 bars.",
                    _m15BullZone.BarTime, _m15BullZone.Low, _m15BullZone.High, ZoneExpiryBars);
                _m15BullZone = null;
            }
            if (_m15BearZone != null && last - _m15BearZone.BarIndex >= ZoneExpiryBars)
            {
                Print("[M15-ZONE] BEAR zone from {0:yyyy-MM-dd HH:mm} [{1}..{2}] EXPIRED after {3} M15 bars.",
                    _m15BearZone.BarTime, _m15BearZone.Low, _m15BearZone.High, ZoneExpiryBars);
                _m15BearZone = null;
            }

            // Creation: the completed M15 bar's low/high becomes "the M15 zone".
            if (DirectionAllows(TradeType.Buy) && IsBullishEngulfing(_m15Bars, last))
            {
                if (_m15BullZone != null)
                    Print("[M15-ZONE] BULL zone from {0:yyyy-MM-dd HH:mm} replaced by newer engulfing.", _m15BullZone.BarTime);
                _m15BullZone = new Zone
                {
                    Low = _m15Bars.LowPrices[last],
                    High = _m15Bars.HighPrices[last],
                    BarIndex = last,
                    BarTime = lastOpenTime
                };
                Print("[M15-ZONE] BULL engulfing {0:yyyy-MM-dd HH:mm} -> zone [{1}..{2}], valid {3} M15 bars.",
                    lastOpenTime, _m15BullZone.Low, _m15BullZone.High, ZoneExpiryBars);
            }

            if (DirectionAllows(TradeType.Sell) && IsBearishEngulfing(_m15Bars, last))
            {
                if (_m15BearZone != null)
                    Print("[M15-ZONE] BEAR zone from {0:yyyy-MM-dd HH:mm} replaced by newer engulfing.", _m15BearZone.BarTime);
                _m15BearZone = new Zone
                {
                    Low = _m15Bars.LowPrices[last],
                    High = _m15Bars.HighPrices[last],
                    BarIndex = last,
                    BarTime = lastOpenTime
                };
                Print("[M15-ZONE] BEAR engulfing {0:yyyy-MM-dd HH:mm} -> zone [{1}..{2}], valid {3} M15 bars.",
                    lastOpenTime, _m15BearZone.Low, _m15BearZone.High, ZoneExpiryBars);
            }
        }

        private void OnM1BarOpened(BarOpenedEventArgs args)
        {
            int last = _m1Bars.Count - 2; // just-completed M1 bar (hard rule #2)
            if (last < 1)
                return;

            var lastOpenTime = _m1Bars.OpenTimes[last];
            if (lastOpenTime == _lastM1Evaluated)
                return;
            _lastM1Evaluated = lastOpenTime;

            if (_m5BullEg != null)
                ProcessM1ForBuy(last, lastOpenTime);
            if (_m5BearEg != null)
                ProcessM1ForSell(last, lastOpenTime);
        }

        /// <summary>
        /// Hard rule #3: session checks use the BAR's open-time hour, never Server.Time.
        /// Window is [start, end) and wraps midnight when start > end.
        /// </summary>
        private bool IsInSession(System.DateTime barOpenTime)
        {
            if (!UseSessionFilter)
                return true;
            int hour = barOpenTime.Hour;
            return SessionStartHourUtc <= SessionEndHourUtc
                ? hour >= SessionStartHourUtc && hour < SessionEndHourUtc
                : hour >= SessionStartHourUtc || hour < SessionEndHourUtc;
        }

        /// <summary>
        /// Step 5 (bullish): (a) M1 low taps/breaks the M5 EG low, (b) then an M1 bearish
        /// engulfing forms - its high is the trigger (a newer bearish EG updates the
        /// trigger), (c) then an M1 bar closes above the trigger -> BUY signal.
        /// The tap bar may itself be the bearish EG; the breaking close must be a later bar.
        /// </summary>
        private void ProcessM1ForBuy(int last, System.DateTime barTime)
        {
            var st = _m1Buy;

            if (!st.Tapped && _m1Bars.LowPrices[last] <= _m5BullEg.Low)
            {
                st.Tapped = true;
                Print("[M1 BUY] {0:yyyy-MM-dd HH:mm} low {1} tapped M5 EG low {2}.",
                    barTime, _m1Bars.LowPrices[last], _m5BullEg.Low);
            }

            if (!st.Tapped)
                return;

            // (c) before (b) on the same bar is impossible: the trigger comes from a prior bar.
            if (st.HasTrigger && _m1Bars.ClosePrices[last] > st.TriggerLevel && barTime > st.TriggerTime)
            {
                RaiseSignal(TradeType.Buy, last, barTime);
                return;
            }

            if (IsBearishEngulfing(_m1Bars, last))
            {
                if (st.HasTrigger)
                    Print("[M1 BUY] newer bearish EG replaces trigger {0} from {1:HH:mm}.", st.TriggerLevel, st.TriggerTime);
                st.HasTrigger = true;
                st.TriggerLevel = _m1Bars.HighPrices[last];
                st.TriggerTime = barTime;
                Print("[M1 BUY] {0:yyyy-MM-dd HH:mm} bearish EG after tap -> trigger = close above {1}.",
                    barTime, st.TriggerLevel);
            }
        }

        /// <summary>Step 5 (bearish) - exact mirror of <see cref="ProcessM1ForBuy"/>.</summary>
        private void ProcessM1ForSell(int last, System.DateTime barTime)
        {
            var st = _m1Sell;

            if (!st.Tapped && _m1Bars.HighPrices[last] >= _m5BearEg.High)
            {
                st.Tapped = true;
                Print("[M1 SELL] {0:yyyy-MM-dd HH:mm} high {1} tapped M5 EG high {2}.",
                    barTime, _m1Bars.HighPrices[last], _m5BearEg.High);
            }

            if (!st.Tapped)
                return;

            if (st.HasTrigger && _m1Bars.ClosePrices[last] < st.TriggerLevel && barTime > st.TriggerTime)
            {
                RaiseSignal(TradeType.Sell, last, barTime);
                return;
            }

            if (IsBullishEngulfing(_m1Bars, last))
            {
                if (st.HasTrigger)
                    Print("[M1 SELL] newer bullish EG replaces trigger {0} from {1:HH:mm}.", st.TriggerLevel, st.TriggerTime);
                st.HasTrigger = true;
                st.TriggerLevel = _m1Bars.LowPrices[last];
                st.TriggerTime = barTime;
                Print("[M1 SELL] {0:yyyy-MM-dd HH:mm} bullish EG after tap -> trigger = close below {1}.",
                    barTime, st.TriggerLevel);
            }
        }

        /// <summary>
        /// Steps 5+6: validate the fully-formed signal (session, SL cap, daily limit) and
        /// execute it - or just log it when DryRun is on. The M5 EG that produced the
        /// signal is consumed either way (one signal per confirmation).
        /// </summary>
        private void RaiseSignal(TradeType tradeType, int m1Index, System.DateTime barTime)
        {
            bool isBuy = tradeType == TradeType.Buy;
            var eg = isBuy ? _m5BullEg : _m5BearEg;

            // Consume the confirmation and reset the M1 chain regardless of filters below,
            // so a filtered-out signal doesn't fire again on the next M1 bar.
            if (isBuy) { _m5BullEg = null; _m1Buy.Reset(); }
            else { _m5BearEg = null; _m1Sell.Reset(); }

            if (!IsInSession(barTime))
            {
                Print("[SIGNAL {0}] {1:yyyy-MM-dd HH:mm} SKIPPED: outside session {2}:00-{3}:00 (bar hour {4}).",
                    tradeType, barTime, SessionStartHourUtc, SessionEndHourUtc, barTime.Hour);
                return;
            }

            // Entry approximates the breaking M1 close; live fill would be at market.
            double entry = _m1Bars.ClosePrices[m1Index];
            double slPrice = isBuy
                ? eg.Low - SlBufferPips * Symbol.PipSize
                : eg.High + SlBufferPips * Symbol.PipSize;
            double slPips = (isBuy ? entry - slPrice : slPrice - entry) / Symbol.PipSize;

            // Hard rule #5: cap the stop distance.
            if (slPips > MaxSlPips)
            {
                Print("[SIGNAL {0}] {1:yyyy-MM-dd HH:mm} REJECTED: SL {2:F1} pips exceeds MaxSlPips {3}.",
                    tradeType, barTime, slPips, MaxSlPips);
                return;
            }
            if (slPips <= 0)
            {
                Print("[SIGNAL {0}] {1:yyyy-MM-dd HH:mm} REJECTED: non-positive SL distance ({2:F1} pips).",
                    tradeType, barTime, slPips);
                return;
            }

            double risk = slPips * Symbol.PipSize;
            double tpPrice = isBuy ? entry + risk * RRRatio : entry - risk * RRRatio;
            double tpPips = slPips * RRRatio;

            // Hard rule #6: counter keyed to the signal bar's date, reset when it changes.
            if (barTime.Date != _tradeCountDate)
            {
                if (_tradeCountDate != System.DateTime.MinValue)
                    Print("[LIMIT] new day {0:yyyy-MM-dd}: daily trade counter reset (was {1}).", barTime.Date, _tradesToday);
                _tradeCountDate = barTime.Date;
                _tradesToday = 0;
            }
            if (_tradesToday >= DailyTradeLimit)
            {
                Print("[SIGNAL {0}] {1:yyyy-MM-dd HH:mm} SKIPPED: daily trade limit {2} reached.",
                    tradeType, barTime, DailyTradeLimit);
                return;
            }

            if (DryRun)
            {
                Print("[SIGNAL {0}] {1:yyyy-MM-dd HH:mm} entry~{2} SL={3} ({4:F1} pips) TP={5} (RR 1:{6}). M5 EG [{7}..{8}] from {9:HH:mm}. (dry run - no order)",
                    tradeType, barTime, entry, slPrice, slPips, tpPrice, RRRatio, eg.Low, eg.High, eg.BarTime);
                return;
            }

            // LotSize (lots) -> units, clamped to the symbol's tradable volume constraints.
            double volumeUnits = Symbol.NormalizeVolumeInUnits(Symbol.QuantityToVolumeInUnits(LotSize), RoundingMode.Down);
            if (volumeUnits < Symbol.VolumeInUnitsMin)
            {
                Print("[SIGNAL {0}] {1:yyyy-MM-dd HH:mm} REJECTED: LotSize {2} -> {3} units is below symbol minimum {4}.",
                    tradeType, barTime, LotSize, volumeUnits, Symbol.VolumeInUnitsMin);
                return;
            }

            // SL/TP are PIP DISTANCES in this overload (verified against cAlgo.API.xml),
            // applied relative to the actual fill price.
            var result = ExecuteMarketOrder(tradeType, SymbolName, volumeUnits, Label, slPips, tpPips);
            if (!result.IsSuccessful)
            {
                Print("[ORDER {0}] {1:yyyy-MM-dd HH:mm} FAILED: {2}", tradeType, barTime, result.Error);
                return;
            }

            _tradesToday++;
            var pos = result.Position;
            Print("[ORDER {0}] {1:yyyy-MM-dd HH:mm} filled #{2}: entry {3} SL {4} TP {5}, {6} units. Trade {7}/{8} today.",
                tradeType, barTime, pos.Id, pos.EntryPrice, pos.StopLoss, pos.TakeProfit, pos.VolumeInUnits, _tradesToday, DailyTradeLimit);
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
            if (_m1Bars != null)
                _m1Bars.BarOpened -= OnM1BarOpened;
            Print("Stopped.");
        }
    }

    /// <summary>
    /// Pure engulfing predicates - no cAlgo dependency so a console harness can link
    /// this file and unit-check the exact code the bot ships with.
    ///
    /// Definition (spec, used exactly, both directions):
    ///   bullish: close > prior_open AND open <= prior_close AND close > open
    ///   bearish: close < prior_open AND open >= prior_close AND close < open
    /// </summary>
    public static class EngulfingLogic
    {
        public static bool IsBullish(double priorOpen, double priorClose, double open, double close)
        {
            return close > priorOpen && open <= priorClose && close > open;
        }

        public static bool IsBearish(double priorOpen, double priorClose, double open, double close)
        {
            return close < priorOpen && open >= priorClose && close < open;
        }
    }
}
