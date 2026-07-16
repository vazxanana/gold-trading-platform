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
    /// cTrader port of the TradingView "EngulfingCandle" study (© ahmedirshad419,
    /// Pine v4) - upgraded from a signal-drawing indicator to an actual bot with the
    /// exits and risk management the original lacks.
    ///
    /// Signal (kept faithful to the Pine source, on COMPLETED chart-timeframe bars):
    ///   bullish candle: previous bar closed red  AND close >= previous bar's open
    ///   bearish candle: previous bar closed green AND close <= previous bar's open
    ///   BUY  when RSI(len) was at/below oversold  on any of the last 3 bars + bullish candle
    ///   SELL when RSI(len) was at/above overbought on any of the last 3 bars + bearish candle
    /// (The Pine's commented-out high/low engulfing conditions are also available
    /// via RequireFullEngulf.)
    ///
    /// Added beyond the indicator: ATR-based SL, RR-based TP, optional time stop,
    /// risk-percent sizing, direction filter, daily trade limit, spread guard.
    ///
    /// Evidence note from this repo's research (see GoldEdgeResearch/): on gold,
    /// counter-trend SHORTS failed in every window of every era tested, while
    /// dip-buying mean reversion had support. Default direction is Both to stay
    /// faithful to the source - A/B against TradeDirection=Buy and expect Buy-only
    /// to win on XAUUSD. Backtest before trusting either.
    ///
    /// Attach to the timeframe you want signals on (the Pine ran on chart TF).
    /// </summary>
    [Robot(AccessRights = AccessRights.None)]
    public class RsiReversalBot : Robot
    {
        [Parameter("Trade Direction", Group = "Signal", DefaultValue = TradeDirectionMode.Both)]
        public TradeDirectionMode TradeDirection { get; set; }

        [Parameter("RSI Length", Group = "Signal", DefaultValue = 14, MinValue = 2)]
        public int RsiLength { get; set; }

        [Parameter("RSI Overbought", Group = "Signal", DefaultValue = 70, MinValue = 50, MaxValue = 100)]
        public int RsiOverbought { get; set; }

        [Parameter("RSI Oversold", Group = "Signal", DefaultValue = 30, MinValue = 0, MaxValue = 50)]
        public int RsiOversold { get; set; }

        // The Pine checks the RSI extreme on the signal bar or the 2 before it.
        [Parameter("RSI Extreme Lookback (bars)", Group = "Signal", DefaultValue = 3, MinValue = 1)]
        public int RsiLookbackBars { get; set; }

        // The original's stricter (commented-out) variant: current bar must also
        // engulf the previous bar's full range (high/low).
        [Parameter("Require Full Engulf (high/low)", Group = "Signal", DefaultValue = false)]
        public bool RequireFullEngulf { get; set; }

        [Parameter("Risk % per Trade", Group = "Risk", DefaultValue = 0.5, MinValue = 0.05, Step = 0.05)]
        public double RiskPercent { get; set; }

        [Parameter("SL (ATR multiple, chart TF)", Group = "Risk", DefaultValue = 2.0, MinValue = 0.5, Step = 0.25)]
        public double SlAtrMultiple { get; set; }

        [Parameter("ATR Period", Group = "Risk", DefaultValue = 14, MinValue = 5)]
        public int AtrPeriod { get; set; }

        [Parameter("TP (R multiple of SL)", Group = "Risk", DefaultValue = 1.5, MinValue = 0.5, Step = 0.25)]
        public double RRRatio { get; set; }

        // Close a trade that has hit neither SL nor TP within this many chart bars (0=off).
        [Parameter("Time Stop (bars, 0=off)", Group = "Risk", DefaultValue = 0, MinValue = 0)]
        public int TimeStopBars { get; set; }

        [Parameter("Daily Trade Limit", Group = "Risk", DefaultValue = 5, MinValue = 1)]
        public int DailyTradeLimit { get; set; }

        // Spread guard in USD price units (broker pip definitions vary on metals).
        [Parameter("Max Entry Spread (USD)", Group = "Risk", DefaultValue = 0.60, MinValue = 0.01, Step = 0.05)]
        public double MaxEntrySpreadUsd { get; set; }

        private const string Label = "RsiReversal";

        private RelativeStrengthIndex _rsi;
        private AverageTrueRange _atr;
        private System.DateTime _tradeCountDate = System.DateTime.MinValue;
        private int _tradesToday;

        protected override void OnStart()
        {
            _rsi = Indicators.RelativeStrengthIndex(Bars.ClosePrices, RsiLength);
            _atr = Indicators.AverageTrueRange(AtrPeriod, MovingAverageType.Simple);
            Print("Started on {0} {1}. RSI({2}) OB {3}/OS {4}, lookback {5} bars, fullEngulf={6}, direction={7}, SL {8}xATR, TP {9}R.",
                SymbolName, TimeFrame, RsiLength, RsiOverbought, RsiOversold, RsiLookbackBars,
                RequireFullEngulf, TradeDirection, SlAtrMultiple, RRRatio);
        }

        protected override void OnBar()
        {
            ApplyTimeStop();

            int i = Bars.Count - 2; // last COMPLETED bar - the signal bar
            if (i < RsiLength + RsiLookbackBars || i < 1)
                return;

            // --- candle definitions, faithful to the Pine source ---
            double open0 = Bars.OpenPrices[i], close0 = Bars.ClosePrices[i];
            double open1 = Bars.OpenPrices[i - 1], close1 = Bars.ClosePrices[i - 1];
            bool bullishCandle = close0 >= open1 && close1 < open1;
            bool bearishCandle = close0 <= open1 && close1 > open1;
            if (RequireFullEngulf)
            {
                bullishCandle = bullishCandle && Bars.HighPrices[i] >= Bars.HighPrices[i - 1] && Bars.LowPrices[i] <= Bars.LowPrices[i - 1];
                bearishCandle = bearishCandle && Bars.HighPrices[i] > Bars.HighPrices[i - 1] && Bars.LowPrices[i] < Bars.LowPrices[i - 1];
            }

            // --- RSI extreme on the signal bar or the (lookback-1) bars before it ---
            bool wasOversold = false, wasOverbought = false;
            for (int k = 0; k < RsiLookbackBars; k++)
            {
                double v = _rsi.Result[i - k];
                if (v <= RsiOversold) wasOversold = true;
                if (v >= RsiOverbought) wasOverbought = true;
            }

            bool buySignal = wasOversold && bullishCandle
                && (TradeDirection == TradeDirectionMode.Both || TradeDirection == TradeDirectionMode.Buy);
            bool sellSignal = wasOverbought && bearishCandle
                && (TradeDirection == TradeDirectionMode.Both || TradeDirection == TradeDirectionMode.Sell);
            if (!buySignal && !sellSignal)
                return;

            var barTime = Bars.OpenTimes[i];

            // daily limit, keyed to the signal bar's date
            if (barTime.Date != _tradeCountDate)
            {
                _tradeCountDate = barTime.Date;
                _tradesToday = 0;
            }
            if (_tradesToday >= DailyTradeLimit)
                return;

            if (Symbol.Spread > MaxEntrySpreadUsd)
            {
                Print("[SKIP] {0:yyyy-MM-dd HH:mm} spread ${1:F2} > ${2:F2}.", barTime, Symbol.Spread, MaxEntrySpreadUsd);
                return;
            }

            var tradeType = buySignal ? TradeType.Buy : TradeType.Sell;

            // one position per direction at a time
            if (Positions.Find(Label, SymbolName, tradeType) != null)
                return;

            double slPrice = _atr.Result[i] * SlAtrMultiple;      // SL distance in price units
            if (slPrice <= 0)
                return;
            double slPips = slPrice / Symbol.PipSize;
            double tpPips = slPips * RRRatio;

            double riskMoney = Account.Balance * RiskPercent / 100.0;
            double units = Symbol.NormalizeVolumeInUnits(riskMoney / (slPips * Symbol.PipValue), RoundingMode.Down);
            if (units < Symbol.VolumeInUnitsMin)
            {
                Print("[SKIP] {0:yyyy-MM-dd HH:mm} size {1} below minimum (SL {2:F0} pips).", barTime, units, slPips);
                return;
            }
            if (units > Symbol.VolumeInUnitsMax)
                units = Symbol.VolumeInUnitsMax;

            var result = ExecuteMarketOrder(tradeType, SymbolName, units, Label, slPips, tpPips);
            if (result.IsSuccessful)
            {
                _tradesToday++;
                Print("[ENTRY] {0:yyyy-MM-dd HH:mm} {1} #{2} at {3}, {4} units, SL {5:F0}p TP {6:F0}p (RSI extreme + reversal candle). Trade {7}/{8} today.",
                    barTime, tradeType, result.Position.Id, result.Position.EntryPrice, units, slPips, tpPips, _tradesToday, DailyTradeLimit);
            }
            else
                Print("[ENTRY] {0:yyyy-MM-dd HH:mm} {1} FAILED: {2}", barTime, tradeType, result.Error);
        }

        private void ApplyTimeStop()
        {
            if (TimeStopBars <= 0)
                return;
            var nowOpen = Bars.OpenTimes[Bars.Count - 1]; // fixed at open; OHLC never read
            double barMinutes = (Bars.OpenTimes[Bars.Count - 1] - Bars.OpenTimes[Bars.Count - 2]).TotalMinutes;
            foreach (var pos in Positions.FindAll(Label, SymbolName))
            {
                if ((nowOpen - pos.EntryTime).TotalMinutes < TimeStopBars * barMinutes)
                    continue;
                var r = ClosePosition(pos);
                if (r.IsSuccessful)
                    Print("[TIME-STOP] closed #{0} after {1} bars: net {2}.", pos.Id, TimeStopBars, pos.NetProfit);
            }
        }

        protected override void OnStop()
        {
            Print("Stopped.");
        }
    }
}
