//+------------------------------------------------------------------+
//| GoldCloseDrift.mq5                                                |
//| MT5 port of the cTrader GoldCloseDrift bot.                       |
//|                                                                   |
//| Edge: gold's upward drift concentrates in the close/reopen window |
//| (21:00-24:00 UTC) - validated on broker data across three eras    |
//| (2010-2026) and confirmed by peer-reviewed research ("Overnight   |
//| versus day returns in gold and gold related assets", J. Economics |
//| and Finance 2018). Long-only by design: shorts were unsupported   |
//| in every window of every era tested.                              |
//|                                                                   |
//| Core cycle: BUY at the first H1 bar in the entry window (UTC),    |
//| flat at the exit hour (UTC). One entry per day. The exit is the   |
//| TIME, not a target; the stop is disaster protection only          |
//| (default 3 x ATR(14, D1)).                                        |
//|                                                                   |
//| !!! SERVER TIME vs UTC - READ THIS !!!                            |
//| cTrader ran on UTC. MT5 bar times are BROKER SERVER TIME (often   |
//| UTC+2 in winter / UTC+3 in summer for gold brokers). Set          |
//| InpServerMinusUtcHours to your broker's offset (e.g. 2 or 3) or   |
//| every hour-based rule fires at the wrong time. Check it: compare  |
//| your platform clock to UTC right now. The Strategy Tester uses    |
//| server time too, so the same offset applies in backtests.         |
//| The 3-hour entry WINDOW absorbs the winter/summer DST wobble.     |
//+------------------------------------------------------------------+
#property copyright "gold-trading-platform"
#property version   "1.00"
#property strict

#include <Trade/Trade.mqh>

//--- risk
enum ENUM_SIZING_MODE  { SIZING_RISK_PERCENT = 0, SIZING_FIXED_LOTS = 1 };
enum ENUM_STOP_MODE    { STOP_ATR_MULTIPLE = 0, STOP_FIXED_USD = 1 };

input group             "Risk"
input ENUM_SIZING_MODE  InpSizingMode          = SIZING_RISK_PERCENT; // Sizing mode
input double            InpRiskPercent         = 0.5;    // Risk % of balance (RiskPercent mode)
input double            InpFixedLots           = 0.10;   // Lots (FixedLots mode)
input ENUM_STOP_MODE    InpStopMode            = STOP_ATR_MULTIPLE;   // Stop mode
input double            InpAtrMultiple         = 3.0;    // ATR multiple (AtrMultiple mode)
input int               InpAtrPeriod           = 14;     // ATR period (D1 bars)
input double            InpEmergencySlUsd      = 30.0;   // Emergency SL in USD (FixedUsd mode)
input double            InpMaxWeeklyLossPct    = 3.0;    // Weekly loss cap, % of week-start balance

input group             "Timing (all hours in UTC)"
input int               InpServerMinusUtcHours = 2;      // Broker server time minus UTC (hours)
input int               InpEntryHourUtc        = 21;     // Entry hour (UTC)
input int               InpEntryWindowHours    = 3;      // Entry window length (hours)
input int               InpExitHourUtc         = 0;      // Exit hour (UTC)
input int               InpMaxHoldHours        = 30;     // Failsafe max hold (hours)

input group             "Filters"
input double            InpMaxEntrySpreadUsd   = 0.60;   // Max spread at entry (USD)
input bool              InpOnlyAfterDownDay    = false;  // Only enter after a down day (close-to-close)
input bool              InpSkipFridayEntry     = true;   // Skip Friday entries that hold over the weekend
input double            InpThursdayMultiplier  = 1.0;    // Thursday size multiplier (1.0 = off)

input group             "Plumbing"
input long              InpMagic               = 20260716; // Magic number

CTrade   g_trade;
int      g_atrHandle       = INVALID_HANDLE;
datetime g_lastH1BarTime   = 0;
datetime g_lastEntryDate   = 0;   // UTC date of the last entry (once per day)
datetime g_weekAnchor      = 0;   // Monday (UTC date) of the current week
double   g_weekStartBalance = 0.0;
bool     g_weeklyCapTripped = false;

//+------------------------------------------------------------------+
int OnInit()
{
   g_trade.SetExpertMagicNumber(InpMagic);
   g_atrHandle = iATR(_Symbol, PERIOD_D1, InpAtrPeriod);
   if(g_atrHandle == INVALID_HANDLE)
   {
      Print("FATAL: failed to create ATR(D1) handle.");
      return INIT_FAILED;
   }
   PrintFormat("Started on %s. Entry window %02d:00+%dh UTC -> exit %02d:00 UTC (server-UTC offset %+d h). "
               "Sizing=%s, stop=%s, spread guard $%.2f, weekly cap %.1f%%, downDayFilter=%s.",
               _Symbol, InpEntryHourUtc, InpEntryWindowHours, InpExitHourUtc, InpServerMinusUtcHours,
               InpSizingMode == SIZING_RISK_PERCENT ? "Risk%" : "FixedLots",
               InpStopMode == STOP_ATR_MULTIPLE ? "ATRx" : "FixedUSD",
               InpMaxEntrySpreadUsd, InpMaxWeeklyLossPct, InpOnlyAfterDownDay ? "on" : "off");
   return INIT_SUCCEEDED;
}

//+------------------------------------------------------------------+
void OnDeinit(const int reason)
{
   if(g_atrHandle != INVALID_HANDLE)
      IndicatorRelease(g_atrHandle);
}

//+------------------------------------------------------------------+
//| All decisions happen once per new H1 bar (mirrors cTrader OnBar). |
//+------------------------------------------------------------------+
void OnTick()
{
   datetime h1 = iTime(_Symbol, PERIOD_H1, 0); // open time of the FORMING H1 bar -
   if(h1 == 0 || h1 == g_lastH1BarTime)        // fixed at open, its OHLC is never read
      return;
   g_lastH1BarTime = h1;

   datetime utcBarTime = h1 - InpServerMinusUtcHours * 3600;
   MqlDateTime t;
   TimeToStruct(utcBarTime, t);

   RollWeeklyAnchor(utcBarTime, t);
   ManageExit(t, utcBarTime);
   TryEnter(t, utcBarTime);
}

//+------------------------------------------------------------------+
void RollWeeklyAnchor(const datetime utcBarTime, const MqlDateTime &t)
{
   int daysSinceMonday = (t.day_of_week + 6) % 7;           // Mon=1 in MQL: Sun=0..Sat=6
   datetime monday = DateOfUtc(utcBarTime) - daysSinceMonday * 86400;
   if(monday == g_weekAnchor)
      return;
   g_weekAnchor = monday;
   g_weekStartBalance = AccountInfoDouble(ACCOUNT_BALANCE);
   if(g_weeklyCapTripped)
      PrintFormat("[WEEK] new week: weekly loss cap re-armed. Balance %.2f.", g_weekStartBalance);
   g_weeklyCapTripped = false;
}

//+------------------------------------------------------------------+
void ManageExit(const MqlDateTime &t, const datetime utcBarTime)
{
   if(!SelectOurPosition())
      return;

   bool timeExit = (t.hour == InpExitHourUtc);
   datetime entryTime = (datetime)PositionGetInteger(POSITION_TIME); // server time
   bool failsafe = (TimeCurrent() - entryTime) >= InpMaxHoldHours * 3600;
   if(!timeExit && !failsafe)
      return;

   ulong ticket = (ulong)PositionGetInteger(POSITION_TICKET);
   double net = PositionGetDouble(POSITION_PROFIT) + PositionGetDouble(POSITION_SWAP);
   if(g_trade.PositionClose(ticket))
      PrintFormat("[EXIT%s] closed #%I64u: ~net %.2f.", (failsafe && !timeExit) ? "-FAILSAFE" : "", ticket, net);
   else
      PrintFormat("[EXIT] FAILED to close #%I64u: %d %s", ticket, g_trade.ResultRetcode(), g_trade.ResultRetcodeDescription());
}

//+------------------------------------------------------------------+
void TryEnter(const MqlDateTime &t, const datetime utcBarTime)
{
   int hoursIntoWindow = (t.hour - InpEntryHourUtc + 24) % 24;
   if(hoursIntoWindow >= InpEntryWindowHours)
      return;
   if(DateOfUtc(utcBarTime) == g_lastEntryDate)
      return;                                   // one entry per day
   if(SelectOurPosition())
      return;

   // weekly loss cap
   double bal = AccountInfoDouble(ACCOUNT_BALANCE);
   if(bal < g_weekStartBalance * (1.0 - InpMaxWeeklyLossPct / 100.0))
   {
      if(!g_weeklyCapTripped)
         PrintFormat("[CAP] weekly loss cap hit (balance %.2f). No entries until next week.", bal);
      g_weeklyCapTripped = true;
      return;
   }

   bool exitNextDay = (InpExitHourUtc <= InpEntryHourUtc);
   if(InpSkipFridayEntry && exitNextDay && t.day_of_week == 5) // 5 = Friday
      return;

   // rollover-spread guard (price units = USD on XAUUSD)
   double ask = SymbolInfoDouble(_Symbol, SYMBOL_ASK);
   double bid = SymbolInfoDouble(_Symbol, SYMBOL_BID);
   double spread = ask - bid;
   if(spread > InpMaxEntrySpreadUsd)
   {
      PrintFormat("[SKIP] spread $%.2f > $%.2f (rollover widening).", spread, InpMaxEntrySpreadUsd);
      return;
   }

   // down-day filter: yesterday's D1 close vs the day before (completed bars 1 and 2)
   if(InpOnlyAfterDownDay)
   {
      double c1 = iClose(_Symbol, PERIOD_D1, 1);
      double c2 = iClose(_Symbol, PERIOD_D1, 2);
      if(c1 <= 0 || c2 <= 0 || c1 >= c2)
         return;
   }

   // stop distance in price units (USD)
   double slUsd = (InpStopMode == STOP_FIXED_USD) ? InpEmergencySlUsd : InpAtrMultiple * DailyAtr();
   if(slUsd <= 0)
      return;

   double lots = ComputeLots(slUsd, t.day_of_week == 4);    // 4 = Thursday
   double minLot = SymbolInfoDouble(_Symbol, SYMBOL_VOLUME_MIN);
   if(lots < minLot)
   {
      PrintFormat("[SKIP] computed volume %.2f below minimum %.2f (raise risk %% or equity).", lots, minLot);
      return;
   }

   double slPrice = NormalizeDouble(ask - slUsd, (int)SymbolInfoInteger(_Symbol, SYMBOL_DIGITS));
   if(g_trade.Buy(lots, _Symbol, 0.0, slPrice, 0.0, "GoldCloseDrift"))
   {
      g_lastEntryDate = DateOfUtc(utcBarTime);
      PrintFormat("[ENTRY] long %.2f lots at ~%.2f, SL $%.2f (%s), spread $%.2f, exit %02d:00 UTC.",
                  lots, ask, slUsd, InpStopMode == STOP_ATR_MULTIPLE ? "ATRx" : "FixedUSD", spread, InpExitHourUtc);
   }
   else
      PrintFormat("[ENTRY] FAILED: %d %s", g_trade.ResultRetcode(), g_trade.ResultRetcodeDescription());
}

//+------------------------------------------------------------------+
//| ATR(InpAtrPeriod, D1) of the last COMPLETED daily bar, in USD.    |
//+------------------------------------------------------------------+
double DailyAtr()
{
   double buf[1];
   if(CopyBuffer(g_atrHandle, 0, 1, 1, buf) != 1) // shift 1 = last completed D1 bar
      return 0.0;
   return buf[0];
}

//+------------------------------------------------------------------+
//| Lots so that hitting the SL costs ~InpRiskPercent of balance.     |
//| tick_value/tick_size = account currency per 1.0 price move / lot. |
//+------------------------------------------------------------------+
double ComputeLots(const double slUsd, const bool isThursday)
{
   double lots;
   if(InpSizingMode == SIZING_FIXED_LOTS)
      lots = InpFixedLots;
   else
   {
      double tickValue = SymbolInfoDouble(_Symbol, SYMBOL_TRADE_TICK_VALUE);
      double tickSize  = SymbolInfoDouble(_Symbol, SYMBOL_TRADE_TICK_SIZE);
      if(tickValue <= 0 || tickSize <= 0)
         return 0.0;
      double lossPerLot = slUsd * (tickValue / tickSize);
      double riskMoney  = AccountInfoDouble(ACCOUNT_BALANCE) * InpRiskPercent / 100.0;
      lots = (lossPerLot > 0) ? riskMoney / lossPerLot : 0.0;
   }

   if(isThursday)
      lots *= InpThursdayMultiplier;

   double step   = SymbolInfoDouble(_Symbol, SYMBOL_VOLUME_STEP);
   double maxLot = SymbolInfoDouble(_Symbol, SYMBOL_VOLUME_MAX);
   if(step > 0)
      lots = MathFloor(lots / step) * step;
   return MathMin(lots, maxLot);
}

//+------------------------------------------------------------------+
//| True + position selected if we own a position on this symbol.    |
//+------------------------------------------------------------------+
bool SelectOurPosition()
{
   for(int i = PositionsTotal() - 1; i >= 0; i--)
   {
      string sym = PositionGetSymbol(i); // also selects the position
      if(sym == _Symbol && PositionGetInteger(POSITION_MAGIC) == InpMagic)
         return true;
   }
   return false;
}

//+------------------------------------------------------------------+
//| Midnight-UTC datetime of the given UTC timestamp.                 |
//+------------------------------------------------------------------+
datetime DateOfUtc(const datetime utc)
{
   return utc - (utc % 86400);
}
//+------------------------------------------------------------------+
