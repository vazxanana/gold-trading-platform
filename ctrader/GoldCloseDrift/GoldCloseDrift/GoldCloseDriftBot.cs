using cAlgo.API;
using cAlgo.API.Internals;

namespace cAlgo.Robots
{
    /// <summary>
    /// FINAL production bot for the one edge that survived three-era validation on real
    /// XAUUSD data (2010-2012, 2022-2023, 2024-2026 - positive in all six IS/OOS windows):
    /// gold's upward drift concentrates in the close/reopen window. Long-only by design;
    /// shorts were unsupported in every window of every era.
    ///
    /// Core cycle: BUY at EntryHourUtc (default 21:00 UTC), flat at ExitHourUtc (default
    /// 00:00 UTC). One position, one entry per day. The exit is the TIME, not a target;
    /// the SL is disaster protection only.
    ///
    /// Optional research-backed filters:
    ///   - OnlyAfterDownDay: adds the down-day mean-reversion condition (strong in the
    ///     2010-12 and 2024-26 eras, flat in the 2022-23 chop - regime dependent).
    ///   - SizeMultiplierThursday: Thursday was positive in all six windows; leave at 1.0
    ///     unless you want to express it.
    ///
    /// The protections are what make this tradable, not decoration:
    ///   - MaxEntrySpreadPips: the entry hour is exactly when rollover spread blows out;
    ///     entering into a 15-pip spread erases days of edge. Skips wide-spread entries.
    ///   - Weekly loss cap: stops opening new trades for the rest of the week after the
    ///     account draws down MaxWeeklyLossPercent from the week's starting balance.
    ///   - Risk-based sizing (default): volume from RiskPercent of balance against the
    ///     emergency SL distance, so a disaster costs a known fraction of the account.
    ///
    /// ATTACHES TO H1 (asserted). Expect the edge to be small (~2-10bp/night gross);
    /// verify your broker's swap-long and real rollover spread on DEMO before live.
    /// </summary>
    [Robot(AccessRights = AccessRights.None)]
    public class GoldCloseDriftBot : Robot
    {
        public enum SizingMode
        {
            RiskPercent,
            FixedLots
        }

        public enum StopMode
        {
            AtrMultiple,
            FixedUsd
        }

        [Parameter("Sizing Mode", Group = "Risk", DefaultValue = SizingMode.RiskPercent)]
        public SizingMode Sizing { get; set; }

        // AtrMultiple adapts the disaster stop to current volatility (wide in wild weeks,
        // tighter in quiet ones) - avoids noise stop-outs without changing the strategy.
        [Parameter("Stop Mode", Group = "Risk", DefaultValue = StopMode.AtrMultiple)]
        public StopMode StopSizing { get; set; }

        [Parameter("ATR Multiple (AtrMultiple mode)", Group = "Risk", DefaultValue = 3.0, MinValue = 1.0, Step = 0.25)]
        public double AtrMultiple { get; set; }

        [Parameter("ATR Period (daily bars)", Group = "Risk", DefaultValue = 14, MinValue = 5)]
        public int AtrPeriod { get; set; }

        // Risk per trade as % of balance, measured against the emergency SL.
        [Parameter("Risk % (RiskPercent mode)", Group = "Risk", DefaultValue = 0.5, MinValue = 0.05, Step = 0.05)]
        public double RiskPercent { get; set; }

        [Parameter("Lot Size (FixedLots mode)", Group = "Risk", DefaultValue = 0.1, MinValue = 0.01, Step = 0.01)]
        public double LotSize { get; set; }

        // Disaster cap, not a managed stop: wide on purpose so normal noise never hits it.
        // Denominated in USD price distance because brokers disagree on what a XAUUSD
        // "pip" is ($0.10 vs $0.01); converted via Symbol.PipSize at runtime.
        [Parameter("Emergency SL (USD)", Group = "Risk", DefaultValue = 30.0, MinValue = 5.0)]
        public double EmergencySlUsd { get; set; }

        // Stop opening new trades for the rest of the week after losing this % of the
        // balance the week started with.
        [Parameter("Weekly Loss Cap (%)", Group = "Risk", DefaultValue = 3.0, MinValue = 0.5, Step = 0.5)]
        public double MaxWeeklyLossPercent { get; set; }

        // Entry WINDOW, not a single hour: in US summer time the market halt moves and the
        // 21:00 UTC H1 bar does not exist, so a fixed hour silently skips 8 months a year.
        // The bot enters on the FIRST bar whose hour falls in [EntryHourUtc, +WindowHours),
        // once per day: 21:00 in winter, 22:00 (the reopen) in summer.
        [Parameter("Entry Hour (UTC)", Group = "Timing", DefaultValue = 21, MinValue = 0, MaxValue = 23)]
        public int EntryHourUtc { get; set; }

        [Parameter("Entry Window (hours)", Group = "Timing", DefaultValue = 3, MinValue = 1, MaxValue = 6)]
        public int EntryWindowHours { get; set; }

        [Parameter("Exit Hour (UTC)", Group = "Timing", DefaultValue = 0, MinValue = 0, MaxValue = 23)]
        public int ExitHourUtc { get; set; }

        // Failsafe if the exit-hour bar never prints (holiday, missing bars).
        [Parameter("Max Hold (hours)", Group = "Timing", DefaultValue = 30, MinValue = 2)]
        public int MaxHoldHours { get; set; }

        // Rollover-spread guard: skip the entry when the live spread exceeds this (USD).
        [Parameter("Max Entry Spread (USD)", Group = "Filters", DefaultValue = 0.60, MinValue = 0.05, Step = 0.05)]
        public double MaxEntrySpreadUsd { get; set; }

        [Parameter("Only After Down Day", Group = "Filters", DefaultValue = false)]
        public bool OnlyAfterDownDay { get; set; }

        [Parameter("Skip Friday Entry", Group = "Filters", DefaultValue = true)]
        public bool SkipFridayEntry { get; set; }

        // Thursday was positive in all six research windows; 1.0 = off.
        [Parameter("Thursday Size Multiplier", Group = "Filters", DefaultValue = 1.0, MinValue = 0.5, MaxValue = 3.0, Step = 0.25)]
        public double SizeMultiplierThursday { get; set; }

        private const string Label = "GoldCloseDrift";

        private Bars _daily;                       // cached once in OnStart
        private System.DateTime _lastEntryDate = System.DateTime.MinValue;
        private System.DateTime _weekAnchor = System.DateTime.MinValue;
        private double _weekStartBalance;
        private bool _weeklyCapTripped;

        protected override void OnStart()
        {
            if (TimeFrame != TimeFrame.Hour)
            {
                Print("FATAL: attach to an H1 chart (timing is hour-based). Current: {0}. Stopping.", TimeFrame);
                Stop();
                return;
            }

            _daily = MarketData.GetBars(TimeFrame.Daily);

            Print("Started on {0} H1 ({1} account). Entry window {2}:00+{3}h -> exit {4}:00 UTC. Sizing={5} ({6}), " +
                  "SL ${7} = {8:F0} broker pips (PipSize {9}), spread guard ${10}, weekly cap {11}%, downDayFilter={12}.",
                SymbolName, Account.IsLive ? "LIVE" : "demo", EntryHourUtc, EntryWindowHours, ExitHourUtc,
                Sizing, Sizing == SizingMode.RiskPercent ? RiskPercent + "%" : LotSize + " lots",
                EmergencySlUsd, EmergencySlUsd / Symbol.PipSize, Symbol.PipSize, MaxEntrySpreadUsd,
                MaxWeeklyLossPercent, OnlyAfterDownDay);
        }

        protected override void OnBar()
        {
            // The just-opened bar's OPEN TIME is fixed the moment it opens - reading it is
            // not look-ahead. Its OHLC is never read; historical reads stay at Count - 2.
            var nowOpen = Bars.OpenTimes[Bars.Count - 1];

            RollWeeklyAnchor(nowOpen);
            ManageExit(nowOpen);
            TryEnter(nowOpen);
        }

        private void RollWeeklyAnchor(System.DateTime nowOpen)
        {
            // Week anchor = the Monday of nowOpen's week.
            int daysSinceMonday = ((int)nowOpen.DayOfWeek + 6) % 7;
            var monday = nowOpen.Date.AddDays(-daysSinceMonday);
            if (monday == _weekAnchor)
                return;
            _weekAnchor = monday;
            _weekStartBalance = Account.Balance;
            if (_weeklyCapTripped)
                Print("[WEEK] new week {0:yyyy-MM-dd}: weekly loss cap re-armed. Balance {1}.", monday, Account.Balance);
            _weeklyCapTripped = false;
        }

        private void ManageExit(System.DateTime nowOpen)
        {
            var pos = Positions.Find(Label, SymbolName, TradeType.Buy);
            if (pos == null)
                return;

            bool timeExit = nowOpen.Hour == ExitHourUtc;
            bool failsafe = (nowOpen - pos.EntryTime).TotalHours >= MaxHoldHours;
            if (!timeExit && !failsafe)
                return;

            var result = ClosePosition(pos);
            if (result.IsSuccessful)
                Print("[EXIT{0}] {1:yyyy-MM-dd HH:mm} closed #{2}: net {3} ({4} pips).",
                    failsafe && !timeExit ? "-FAILSAFE" : "", nowOpen, pos.Id, pos.NetProfit, pos.Pips);
            else
                Print("[EXIT] {0:yyyy-MM-dd HH:mm} FAILED to close #{1}: {2}", nowOpen, pos.Id, result.Error);
        }

        private void TryEnter(System.DateTime nowOpen)
        {
            int hoursIntoWindow = (nowOpen.Hour - EntryHourUtc + 24) % 24;
            if (hoursIntoWindow >= EntryWindowHours)
                return;
            if (_lastEntryDate == nowOpen.Date)
                return; // one entry per day, even when the window's first bar is missing
            if (Positions.Find(Label, SymbolName, TradeType.Buy) != null)
                return;

            // Weekly loss cap: no new risk for the rest of the week once tripped.
            if (Account.Balance < _weekStartBalance * (1 - MaxWeeklyLossPercent / 100.0))
            {
                if (!_weeklyCapTripped)
                    Print("[CAP] {0:yyyy-MM-dd} weekly loss cap hit (balance {1} < {2:F2}). No entries until next week.",
                        nowOpen, Account.Balance, _weekStartBalance * (1 - MaxWeeklyLossPercent / 100.0));
                _weeklyCapTripped = true;
                return;
            }

            bool exitNextDay = ExitHourUtc <= EntryHourUtc;
            if (SkipFridayEntry && exitNextDay && nowOpen.DayOfWeek == System.DayOfWeek.Friday)
                return;

            // Rollover-spread guard - the make-or-break filter for this edge.
            // Symbol.Spread is already in price units (USD for XAUUSD).
            if (Symbol.Spread > MaxEntrySpreadUsd)
            {
                Print("[SKIP] {0:yyyy-MM-dd HH:mm} spread ${1:F2} > ${2:F2} (rollover widening).",
                    nowOpen, Symbol.Spread, MaxEntrySpreadUsd);
                return;
            }

            if (OnlyAfterDownDay)
            {
                int d = _daily.Count - 2; // last COMPLETED daily bar; close-to-close definition
                if (d < 1 || _daily.ClosePrices[d] >= _daily.ClosePrices[d - 1])
                    return;
            }

            double slUsd = StopSizing == StopMode.FixedUsd ? EmergencySlUsd : AtrMultiple * DailyAtr();
            if (slUsd <= 0)
                return;

            double volumeUnits = ComputeVolume(nowOpen, slUsd);
            if (volumeUnits < Symbol.VolumeInUnitsMin)
            {
                Print("[SKIP] {0:yyyy-MM-dd HH:mm} computed volume {1} below symbol minimum {2}.",
                    nowOpen, volumeUnits, Symbol.VolumeInUnitsMin);
                return;
            }

            double slPips = slUsd / Symbol.PipSize;
            var result = ExecuteMarketOrder(TradeType.Buy, SymbolName, volumeUnits, Label, slPips, null);
            if (result.IsSuccessful)
            {
                _lastEntryDate = nowOpen.Date;
                Print("[ENTRY] {0:yyyy-MM-dd HH:mm} long #{1} at {2}, {3} units, SL ${4:F2} ({5:F0} pips, {6}), spread ${7:F2}, exit {8}:00 UTC.",
                    nowOpen, result.Position.Id, result.Position.EntryPrice, volumeUnits, slUsd, slPips, StopSizing, Symbol.Spread, ExitHourUtc);
            }
            else
                Print("[ENTRY] {0:yyyy-MM-dd HH:mm} FAILED: {1}", nowOpen, result.Error);
        }

        /// Average True Range over the last AtrPeriod COMPLETED daily bars, in USD.
        private double DailyAtr()
        {
            int last = _daily.Count - 2;
            if (last < AtrPeriod)
                return 0;
            double sum = 0;
            for (int i = last - AtrPeriod + 1; i <= last; i++)
            {
                double tr = System.Math.Max(_daily.HighPrices[i] - _daily.LowPrices[i],
                            System.Math.Max(System.Math.Abs(_daily.HighPrices[i] - _daily.ClosePrices[i - 1]),
                                            System.Math.Abs(_daily.LowPrices[i] - _daily.ClosePrices[i - 1])));
                sum += tr;
            }
            return sum / AtrPeriod;
        }

        private double ComputeVolume(System.DateTime nowOpen, double slUsd)
        {
            double raw;
            if (Sizing == SizingMode.FixedLots)
            {
                raw = Symbol.QuantityToVolumeInUnits(LotSize);
            }
            else
            {
                // PipValue is per unit of volume: units = risk money / (SL pips * pip value).
                double riskMoney = Account.Balance * RiskPercent / 100.0;
                raw = riskMoney / ((slUsd / Symbol.PipSize) * Symbol.PipValue);
            }

            if (nowOpen.DayOfWeek == System.DayOfWeek.Thursday)
                raw *= SizeMultiplierThursday;

            double normalized = Symbol.NormalizeVolumeInUnits(raw, RoundingMode.Down);
            return System.Math.Min(normalized, Symbol.VolumeInUnitsMax);
        }

        protected override void OnStop()
        {
            Print("Stopped.");
        }
    }
}
