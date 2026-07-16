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

    /// <summary>
    /// Market-structure bot: a precise, mechanical implementation of the swing/BOS/CHoCH
    /// framework - no discretion, every concept reduced to a rule.
    ///
    /// 1. SWINGS: fractal pivots with SwingStrength bars on each side. A swing high at
    ///    bar p is CONFIRMED only SwingStrength bars later (this delay is inherent to
    ///    the definition - anyone claiming instant swing detection has look-ahead bias).
    /// 2. TREND: from the last two confirmed swings of each kind -
    ///    uptrend = higher high AND higher low; downtrend = lower high AND lower low;
    ///    otherwise neutral (range).
    /// 3. BOS (break of structure, continuation): in an uptrend, a completed bar CLOSES
    ///    above the last confirmed swing high -> BUY. Mirror for downtrend shorts.
    /// 4. CHoCH (change of character, reversal): a close through the swing AGAINST the
    ///    prevailing trend flips the trend state; optionally tradable (off by default -
    ///    reversal entries are the weakest part of the framework).
    /// 5. STRUCTURAL STOP: longs are stopped below the last confirmed swing low (plus
    ///    an ATR-fraction buffer), not at an arbitrary distance - the one genuinely
    ///    good risk idea in this framework. Position size is computed against that
    ///    structural distance, capped by MaxSlUsd.
    /// 6. STRUCTURE EXIT: an opposite CHoCH closes open positions (a long's premise is
    ///    dead once price closes below the swing low that defined the uptrend).
    ///
    /// Each swing high/low is consumed after triggering once - one entry per broken
    /// level, no re-firing on every subsequent bar above it.
    ///
    /// Evidence notes from this repo's research on gold: daily 20-day breakouts showed
    /// no edge, and shorts failed in every tested window - so treat BOS shorts with
    /// suspicion and A/B TradeDirection=Buy. Intraday structure (M15/H1) is untested
    /// on your data: that is what the backtest is for. Attach to the timeframe whose
    /// structure you want to trade.
    /// </summary>
    [Robot(AccessRights = AccessRights.None)]
    public class MarketStructureBot : Robot
    {
        [Parameter("Trade Direction", Group = "Signal", DefaultValue = TradeDirectionMode.Both)]
        public TradeDirectionMode TradeDirection { get; set; }

        // Bars required on EACH side of a pivot. 3 on M15 gives meaningful swings;
        // higher = fewer, larger structures.
        [Parameter("Swing Strength (bars each side)", Group = "Signal", DefaultValue = 3, MinValue = 1, MaxValue = 10)]
        public int SwingStrength { get; set; }

        [Parameter("Trade BOS (continuation)", Group = "Signal", DefaultValue = true)]
        public bool TradeBos { get; set; }

        [Parameter("Trade CHoCH (reversal)", Group = "Signal", DefaultValue = false)]
        public bool TradeChoch { get; set; }

        [Parameter("Structure Exit (opposite CHoCH closes)", Group = "Signal", DefaultValue = true)]
        public bool StructureExit { get; set; }

        [Parameter("Risk % per Trade", Group = "Risk", DefaultValue = 0.5, MinValue = 0.05, Step = 0.05)]
        public double RiskPercent { get; set; }

        // SL sits this fraction of ATR(14) beyond the structural swing level.
        [Parameter("SL Buffer (ATR fraction)", Group = "Risk", DefaultValue = 0.25, MinValue = 0, Step = 0.05)]
        public double SlBufferAtr { get; set; }

        // Reject trades whose structural stop is further than this (USD price distance).
        [Parameter("Max SL (USD)", Group = "Risk", DefaultValue = 30.0, MinValue = 1.0)]
        public double MaxSlUsd { get; set; }

        [Parameter("TP (R multiple, 0 = structure exit only)", Group = "Risk", DefaultValue = 2.0, MinValue = 0, Step = 0.25)]
        public double RRRatio { get; set; }

        [Parameter("Daily Trade Limit", Group = "Risk", DefaultValue = 4, MinValue = 1)]
        public int DailyTradeLimit { get; set; }

        [Parameter("Max Entry Spread (USD)", Group = "Risk", DefaultValue = 0.60, MinValue = 0.01, Step = 0.05)]
        public double MaxEntrySpreadUsd { get; set; }

        [Parameter("Debug: Log Structure Events", Group = "Debug", DefaultValue = true)]
        public bool DebugStructure { get; set; }

        private const string Label = "MarketStructure";

        private enum Trend { Neutral, Up, Down }

        private AverageTrueRange _atr;

        // last two CONFIRMED swings of each kind (price + pivot bar time)
        private double _lastHigh = double.NaN, _prevHigh = double.NaN;
        private double _lastLow = double.NaN, _prevLow = double.NaN;
        private bool _highConsumed, _lowConsumed; // one entry per broken level
        private Trend _trend = Trend.Neutral;

        private System.DateTime _tradeCountDate = System.DateTime.MinValue;
        private int _tradesToday;

        protected override void OnStart()
        {
            _atr = Indicators.AverageTrueRange(14, MovingAverageType.Simple);
            Print("Started on {0} {1}. SwingStrength={2}, BOS={3}, CHoCH={4}, structureExit={5}, direction={6}, RR={7}.",
                SymbolName, TimeFrame, SwingStrength, TradeBos, TradeChoch, StructureExit, TradeDirection, RRRatio);
        }

        protected override void OnBar()
        {
            int last = Bars.Count - 2;               // last COMPLETED bar
            int pivot = last - SwingStrength;        // bar that can now be CONFIRMED as a swing
            if (pivot < SwingStrength)
                return;

            ConfirmSwings(pivot);
            ProcessBreaks(last);
        }

        /// <summary>Fractal pivot confirmation: strictly higher/lower than SwingStrength bars on each side.</summary>
        private void ConfirmSwings(int p)
        {
            bool isHigh = true, isLow = true;
            for (int k = 1; k <= SwingStrength; k++)
            {
                if (Bars.HighPrices[p] <= Bars.HighPrices[p - k] || Bars.HighPrices[p] <= Bars.HighPrices[p + k])
                    isHigh = false;
                if (Bars.LowPrices[p] >= Bars.LowPrices[p - k] || Bars.LowPrices[p] >= Bars.LowPrices[p + k])
                    isLow = false;
                if (!isHigh && !isLow)
                    return;
            }

            if (isHigh)
            {
                _prevHigh = _lastHigh;
                _lastHigh = Bars.HighPrices[p];
                _highConsumed = false;
                if (DebugStructure)
                    Print("[SWING] {0:yyyy-MM-dd HH:mm} swing HIGH {1} ({2}).", Bars.OpenTimes[p], _lastHigh,
                        double.IsNaN(_prevHigh) ? "first" : _lastHigh > _prevHigh ? "HH" : "LH");
            }
            if (isLow)
            {
                _prevLow = _lastLow;
                _lastLow = Bars.LowPrices[p];
                _lowConsumed = false;
                if (DebugStructure)
                    Print("[SWING] {0:yyyy-MM-dd HH:mm} swing LOW {1} ({2}).", Bars.OpenTimes[p], _lastLow,
                        double.IsNaN(_prevLow) ? "first" : _lastLow > _prevLow ? "HL" : "LL");
            }

            if (isHigh || isLow)
                UpdateTrend();
        }

        private void UpdateTrend()
        {
            if (double.IsNaN(_prevHigh) || double.IsNaN(_prevLow))
                return;
            bool hh = _lastHigh > _prevHigh, hl = _lastLow > _prevLow;
            bool lh = _lastHigh < _prevHigh, ll = _lastLow < _prevLow;
            var newTrend = hh && hl ? Trend.Up : lh && ll ? Trend.Down : _trend;
            if (newTrend != _trend)
            {
                _trend = newTrend;
                if (DebugStructure)
                    Print("[STRUCTURE] trend -> {0} (swings: H {1}->{2}, L {3}->{4}).", _trend, _prevHigh, _lastHigh, _prevLow, _lastLow);
            }
        }

        /// <summary>Close-through-level detection on the last completed bar.</summary>
        private void ProcessBreaks(int last)
        {
            double close = Bars.ClosePrices[last];
            var barTime = Bars.OpenTimes[last];

            // --- bullish break: close above the last confirmed swing high ---
            if (!double.IsNaN(_lastHigh) && !_highConsumed && close > _lastHigh)
            {
                _highConsumed = true;
                bool isBos = _trend == Trend.Up;
                bool isChoch = _trend == Trend.Down;
                if (DebugStructure)
                    Print("[BREAK] {0:yyyy-MM-dd HH:mm} close {1} > swing high {2}: {3}.",
                        barTime, close, _lastHigh, isBos ? "BOS up" : isChoch ? "CHoCH up" : "break (neutral)");

                if (isChoch)
                {
                    _trend = Trend.Up; // structure flipped by definition
                    if (StructureExit)
                        CloseAll(TradeType.Sell, "bullish CHoCH");
                }

                if ((isBos && TradeBos) || (isChoch && TradeChoch))
                    Enter(TradeType.Buy, close, barTime, isBos ? "BOS" : "CHoCH");
            }

            // --- bearish break: close below the last confirmed swing low ---
            if (!double.IsNaN(_lastLow) && !_lowConsumed && close < _lastLow)
            {
                _lowConsumed = true;
                bool isBos = _trend == Trend.Down;
                bool isChoch = _trend == Trend.Up;
                if (DebugStructure)
                    Print("[BREAK] {0:yyyy-MM-dd HH:mm} close {1} < swing low {2}: {3}.",
                        barTime, close, _lastLow, isBos ? "BOS down" : isChoch ? "CHoCH down" : "break (neutral)");

                if (isChoch)
                {
                    _trend = Trend.Down;
                    if (StructureExit)
                        CloseAll(TradeType.Buy, "bearish CHoCH");
                }

                if ((isBos && TradeBos) || (isChoch && TradeChoch))
                    Enter(TradeType.Sell, close, barTime, isBos ? "BOS" : "CHoCH");
            }
        }

        private void Enter(TradeType tradeType, double close, System.DateTime barTime, string kind)
        {
            if (TradeDirection != TradeDirectionMode.Both
                && (tradeType == TradeType.Buy) != (TradeDirection == TradeDirectionMode.Buy))
                return;

            if (barTime.Date != _tradeCountDate)
            {
                _tradeCountDate = barTime.Date;
                _tradesToday = 0;
            }
            if (_tradesToday >= DailyTradeLimit)
                return;

            if (Symbol.Spread > MaxEntrySpreadUsd)
            {
                Print("[SKIP] {0:yyyy-MM-dd HH:mm} spread ${1:F2} too wide.", barTime, Symbol.Spread);
                return;
            }
            if (Positions.Find(Label, SymbolName, tradeType) != null)
                return;

            // structural stop: beyond the opposite swing, plus an ATR-fraction buffer
            double buffer = SlBufferAtr * _atr.Result[Bars.Count - 2];
            double slLevel = tradeType == TradeType.Buy ? _lastLow - buffer : _lastHigh + buffer;
            if (double.IsNaN(slLevel))
                return;
            double slUsd = tradeType == TradeType.Buy ? close - slLevel : slLevel - close;
            if (slUsd <= 0)
            {
                Print("[SKIP] {0:yyyy-MM-dd HH:mm} {1}: structural stop on wrong side (entry {2}, SL {3}).", barTime, kind, close, slLevel);
                return;
            }
            if (slUsd > MaxSlUsd)
            {
                Print("[SKIP] {0:yyyy-MM-dd HH:mm} {1}: structural SL ${2:F2} exceeds cap ${3}.", barTime, kind, slUsd, MaxSlUsd);
                return;
            }

            double slPips = slUsd / Symbol.PipSize;
            double? tpPips = RRRatio > 0 ? slPips * RRRatio : (double?)null;

            double riskMoney = Account.Balance * RiskPercent / 100.0;
            double units = Symbol.NormalizeVolumeInUnits(riskMoney / (slPips * Symbol.PipValue), RoundingMode.Down);
            if (units < Symbol.VolumeInUnitsMin)
            {
                Print("[SKIP] {0:yyyy-MM-dd HH:mm} {1}: size below minimum (SL {2:F0} pips).", barTime, kind, slPips);
                return;
            }
            if (units > Symbol.VolumeInUnitsMax)
                units = Symbol.VolumeInUnitsMax;

            var result = ExecuteMarketOrder(tradeType, SymbolName, units, Label, slPips, tpPips);
            if (result.IsSuccessful)
            {
                _tradesToday++;
                Print("[ENTRY] {0:yyyy-MM-dd HH:mm} {1} {2} #{3} at {4}, {5} units, structural SL ${6:F2} ({7:F0}p), TP {8}. Trade {9}/{10} today.",
                    barTime, kind, tradeType, result.Position.Id, result.Position.EntryPrice, units, slUsd, slPips,
                    tpPips.HasValue ? tpPips.Value.ToString("F0") + "p" : "structure exit", _tradesToday, DailyTradeLimit);
            }
            else
                Print("[ENTRY] {0:yyyy-MM-dd HH:mm} {1} FAILED: {2}", barTime, kind, result.Error);
        }

        private void CloseAll(TradeType tradeType, string reason)
        {
            foreach (var pos in Positions.FindAll(Label, SymbolName, tradeType))
            {
                var r = ClosePosition(pos);
                if (r.IsSuccessful)
                    Print("[STRUCT-EXIT] closed {0} #{1} on {2}: net {3}.", tradeType, pos.Id, reason, pos.NetProfit);
            }
        }

        protected override void OnStop()
        {
            Print("Stopped.");
        }
    }
}
