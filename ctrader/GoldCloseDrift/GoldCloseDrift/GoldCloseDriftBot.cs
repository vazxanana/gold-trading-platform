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

        [Parameter("Sizing Mode", Group = "Risk", DefaultValue = SizingMode.RiskPercent)]
        public SizingMode Sizing { get; set; }

        // Risk per trade as % of balance, measured against the emergency SL.
        [Parameter("Risk % (RiskPercent mode)", Group = "Risk", DefaultValue = 0.5, MinValue = 0.05, Step = 0.05)]
        public double RiskPercent { get; set; }

        [Parameter("Lot Size (FixedLots mode)", Group = "Risk", DefaultValue = 0.1, MinValue = 0.01, Step = 0.01)]
        public double LotSize { get; set; }

        // Disaster cap, not a managed stop: wide on purpose so normal noise never hits it.
        [Parameter("Emergency SL (pips)", Group = "Risk", DefaultValue = 300, MinValue = 50)]
        public double EmergencySlPips { get; set; }

        // Stop opening new trades for the rest of the week after losing this % of the
        // balance the week started with.
        [Parameter("Weekly Loss Cap (%)", Group = "Risk", DefaultValue = 3.0, MinValue = 0.5, Step = 0.5)]
        public double MaxWeeklyLossPercent { get; set; }

        [Parameter("Entry Hour (UTC)", Group = "Timing", DefaultValue = 21, MinValue = 0, MaxValue = 23)]
        public int EntryHourUtc { get; set; }

        [Parameter("Exit Hour (UTC)", Group = "Timing", DefaultValue = 0, MinValue = 0, MaxValue = 23)]
        public int ExitHourUtc { get; set; }

        // Failsafe if the exit-hour bar never prints (holiday, missing bars).
        [Parameter("Max Hold (hours)", Group = "Timing", DefaultValue = 30, MinValue = 2)]
        public int MaxHoldHours { get; set; }

        // Rollover-spread guard: skip the entry when the live spread exceeds this.
        [Parameter("Max Entry Spread (pips)", Group = "Filters", DefaultValue = 6, MinValue = 1)]
        public double MaxEntrySpreadPips { get; set; }

        [Parameter("Only After Down Day", Group = "Filters", DefaultValue = false)]
        public bool OnlyAfterDownDay { get; set; }

        [Parameter("Skip Friday Entry", Group = "Filters", DefaultValue = true)]
        public bool SkipFridayEntry { get; set; }

        // Thursday was positive in all six research windows; 1.0 = off.
        [Parameter("Thursday Size Multiplier", Group = "Filters", DefaultValue = 1.0, MinValue = 0.5, MaxValue = 3.0, Step = 0.25)]
        public double SizeMultiplierThursday { get; set; }

        private const string Label = "GoldCloseDrift";

        private Bars _daily;                       // cached once in OnStart
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

            Print("Started on {0} H1 ({1} account). Entry {2}:00 -> exit {3}:00 UTC. Sizing={4} ({5}), " +
                  "spread guard {6} pips, weekly cap {7}%, downDayFilter={8}.",
                SymbolName, Account.IsLive ? "LIVE" : "demo", EntryHourUtc, ExitHourUtc,
                Sizing, Sizing == SizingMode.RiskPercent ? RiskPercent + "%" : LotSize + " lots",
                MaxEntrySpreadPips, MaxWeeklyLossPercent, OnlyAfterDownDay);
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
            if (nowOpen.Hour != EntryHourUtc)
                return;
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
            double spreadPips = Symbol.Spread / Symbol.PipSize;
            if (spreadPips > MaxEntrySpreadPips)
            {
                Print("[SKIP] {0:yyyy-MM-dd HH:mm} spread {1:F1} pips > {2} (rollover widening).",
                    nowOpen, spreadPips, MaxEntrySpreadPips);
                return;
            }

            if (OnlyAfterDownDay)
            {
                int d = _daily.Count - 2; // last COMPLETED daily bar; close-to-close definition
                if (d < 1 || _daily.ClosePrices[d] >= _daily.ClosePrices[d - 1])
                    return;
            }

            double volumeUnits = ComputeVolume(nowOpen);
            if (volumeUnits < Symbol.VolumeInUnitsMin)
            {
                Print("[SKIP] {0:yyyy-MM-dd HH:mm} computed volume {1} below symbol minimum {2}.",
                    nowOpen, volumeUnits, Symbol.VolumeInUnitsMin);
                return;
            }

            var result = ExecuteMarketOrder(TradeType.Buy, SymbolName, volumeUnits, Label, EmergencySlPips, null);
            if (result.IsSuccessful)
                Print("[ENTRY] {0:yyyy-MM-dd HH:mm} long #{1} at {2}, {3} units, SL {4} pips, spread {5:F1}p, exit {6}:00 UTC.",
                    nowOpen, result.Position.Id, result.Position.EntryPrice, volumeUnits, EmergencySlPips, spreadPips, ExitHourUtc);
            else
                Print("[ENTRY] {0:yyyy-MM-dd HH:mm} FAILED: {1}", nowOpen, result.Error);
        }

        private double ComputeVolume(System.DateTime nowOpen)
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
                raw = riskMoney / (EmergencySlPips * Symbol.PipValue);
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
