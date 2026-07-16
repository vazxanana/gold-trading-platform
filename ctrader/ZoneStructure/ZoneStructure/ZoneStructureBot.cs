// ─── ZONE STRUCTURE BOT ─────────────────────────────────────────────────────
// Trades the top-down SMC playbook from the hand-drawn TradingView charts:
//
//   1. D1 sets the bias        → structure trend from the MarketStructure
//                                engine on Daily bars (optional gate).
//   2. H4 supplies the zone    → price must pull back INTO a FRESH
//                                (unmitigated) H4 supply zone in a bear
//                                trend / demand zone in a bull trend.
//   3. M15 gives the trigger   → while the zone is "armed" (touched and not
//                                invalidated/expired), an M15 ChoCh (or bos)
//                                back in the H4 trend direction fires entry.
//   4. SL beyond the zone      → zone far edge + buffer (structural stop).
//   5. TP at resting liquidity → nearest UNSWEPT H4 or D1 swing low (shorts)
//                                / high (longs) beyond price, front-run by
//                                TargetOffsetUsd. MinRR gate walks to the
//                                next further target if the nearest is too
//                                close.
//
// The structure/zone engine is the exact ComputeStructure() port from
// MarketStructure2.cs (the web platform's structureMap engine) — same
// alternating fractal swings, same bos/ChoCh via trend-extreme + protected
// origin, same order-block zones with freshness/mitigation — so what this
// bot trades is what that indicator draws.
//
// HARD RULES honoured:
//   • must attach to XAUUSD M15 (asserted in OnStart, otherwise Stop()).
//   • decisions on COMPLETED bars only: chart series uses Bars.Count-2 at
//     bar open; cached H4/D1 series exclude their last (forming) element.
//   • MarketData.GetBars() cached once in OnStart.
//   • session/date logic uses bar open times, never Server.Time hours.
//   • daily trade counter resets on bar-date rollover.
//
// All price-distance parameters are in USD (broker pip on Raw Trading XAUUSD
// is $0.01, so pip inputs are a footgun) — converted via Symbol.PipSize.

using System;
using System.Collections.Generic;
using System.Linq;
using cAlgo.API;
using cAlgo.API.Internals;

namespace cAlgo.Robots
{
    public enum TradeDirectionMode { Buy, Sell, Both }
    public enum EntryTriggerMode { ChochOnly, ChochOrBos }

    [Robot(AccessRights = AccessRights.None)]
    public class ZoneStructureBot : Robot
    {
        private const string Label = "ZoneStructure";

        // ── Signal ──────────────────────────────────────────────────────────
        [Parameter("Trade Direction", Group = "Signal", DefaultValue = TradeDirectionMode.Both)]
        public TradeDirectionMode TradeDirection { get; set; }

        [Parameter("Swing strength (bars each side)", Group = "Signal", DefaultValue = 3, MinValue = 2)]
        public int SwingStrength { get; set; }

        [Parameter("Require FRESH (unmitigated) zone", Group = "Signal", DefaultValue = true)]
        public bool RequireFreshZone { get; set; }

        [Parameter("Require D1 trend alignment", Group = "Signal", DefaultValue = true)]
        public bool RequireDailyAlignment { get; set; }

        [Parameter("M15 entry trigger", Group = "Signal", DefaultValue = EntryTriggerMode.ChochOrBos)]
        public EntryTriggerMode EntryTrigger { get; set; }

        [Parameter("Zone armed expiry (M15 bars)", Group = "Signal", DefaultValue = 32, MinValue = 4)]
        public int ZoneExpiryBars { get; set; }

        // ── Structure lookbacks ─────────────────────────────────────────────
        [Parameter("H4 lookback bars", Group = "Structure", DefaultValue = 600, MinValue = 120)]
        public int H4LookbackBars { get; set; }

        [Parameter("D1 lookback bars", Group = "Structure", DefaultValue = 300, MinValue = 60)]
        public int D1LookbackBars { get; set; }

        [Parameter("M15 lookback bars", Group = "Structure", DefaultValue = 400, MinValue = 60)]
        public int M15LookbackBars { get; set; }

        [Parameter("Max zones per side", Group = "Structure", DefaultValue = 4, MinValue = 1)]
        public int MaxZonesPerSide { get; set; }

        // ── Risk ────────────────────────────────────────────────────────────
        [Parameter("Risk % of balance per trade", Group = "Risk", DefaultValue = 0.5, MinValue = 0.01, Step = 0.05)]
        public double RiskPercent { get; set; }

        [Parameter("SL buffer beyond zone (USD)", Group = "Risk", DefaultValue = 1.5, MinValue = 0.0, Step = 0.1)]
        public double SlBufferUsd { get; set; }

        [Parameter("Min SL distance (USD)", Group = "Risk", DefaultValue = 3.0, MinValue = 0.5, Step = 0.5)]
        public double MinSlUsd { get; set; }

        [Parameter("Max SL distance (USD)", Group = "Risk", DefaultValue = 30.0, MinValue = 1.0, Step = 1.0)]
        public double MaxSlUsd { get; set; }

        [Parameter("Min reward:risk", Group = "Risk", DefaultValue = 1.0, MinValue = 0.1, Step = 0.1)]
        public double MinRR { get; set; }

        [Parameter("Target front-run offset (USD)", Group = "Risk", DefaultValue = 0.5, MinValue = 0.0, Step = 0.1)]
        public double TargetOffsetUsd { get; set; }

        [Parameter("Max entry spread (USD)", Group = "Risk", DefaultValue = 0.6, MinValue = 0.0, Step = 0.05)]
        public double MaxEntrySpreadUsd { get; set; }

        [Parameter("Daily trade limit", Group = "Risk", DefaultValue = 3, MinValue = 1)]
        public int DailyTradeLimit { get; set; }

        // ── Debug ───────────────────────────────────────────────────────────
        [Parameter("Debug diagnostics", Group = "Debug", DefaultValue = true)]
        public bool DebugDiagnostics { get; set; }

        // ── State ───────────────────────────────────────────────────────────
        private Bars _h4, _d1;
        private int _lastH4Completed = -1, _lastD1Completed = -1;

        private Smc.StructureMap _h4Map, _d1Map, _m15Map;
        private List<Smc.Candle> _h4Candles, _d1Candles;

        private string _lastH4Trend, _lastD1Trend;

        private class ArmedZone
        {
            public double Hi, Lo;
            public bool Bull, Star;
            public int OriginAbs;          // absolute index into the H4 series (stable across recomputes)
            public DateTime OriginTime;
            public int ArmedAtBar;         // absolute M15 chart index of the latest touch
        }

        private readonly List<ArmedZone> _armed = new List<ArmedZone>();
        private readonly HashSet<int> _consumedZones = new HashSet<int>();  // H4 origin indices already traded

        private DateTime _tradeCountDate = DateTime.MinValue;
        private int _tradesToday;

        protected override void OnStart()
        {
            if (TimeFrame != TimeFrame.Minute15)
            {
                Print("[FATAL] ZoneStructureBot must be attached to the M15 chart (attached: {0}). Stopping.", TimeFrame);
                Stop();
                return;
            }

            _h4 = MarketData.GetBars(TimeFrame.Hour4);
            _d1 = MarketData.GetBars(TimeFrame.Daily);
            EnsureHistory(_h4, H4LookbackBars + 20, "H4");
            EnsureHistory(_d1, D1LookbackBars + 10, "D1");

            Print("[START] ZoneStructure on {0} {1} | dir={2} trigger={3} freshZone={4} d1Align={5}",
                SymbolName, TimeFrame, TradeDirection, EntryTrigger, RequireFreshZone, RequireDailyAlignment);
            Print("[START] risk={0}% slBuffer=${1} minSL=${2} maxSL=${3} minRR={4} tgtOffset=${5} zoneExpiry={6} m15 bars",
                RiskPercent, SlBufferUsd, MinSlUsd, MaxSlUsd, MinRR, TargetOffsetUsd, ZoneExpiryBars);
            Print("[START] pip size = {0} → $1.00 = {1} pips on this symbol", Symbol.PipSize, 1.0 / Symbol.PipSize);
        }

        private void EnsureHistory(Bars bars, int wanted, string name)
        {
            int guard = 0;
            while (bars.Count < wanted && guard++ < 40)
            {
                if (bars.LoadMoreHistory() <= 0) break;
            }
            Print("[START] {0} history: {1} bars (wanted {2})", name, bars.Count, wanted);
        }

        protected override void OnBar()
        {
            int last = Bars.ClosePrices.Count - 2;          // just-completed M15 bar
            if (last < SwingStrength * 2 + 5) return;

            DateTime barDate = Bars.OpenTimes[last + 1].Date;
            if (barDate != _tradeCountDate)
            {
                _tradeCountDate = barDate;
                _tradesToday = 0;
            }

            UpdateHigherTfMaps();
            if (_h4Map == null || _h4Map.State == null) return;

            string trend = _h4Map.State.Trend;              // "bull" | "bear"
            if (trend != _lastH4Trend)
            {
                Print("[TREND] H4 → {0} at {1:yyyy-MM-dd HH:mm} (protected={2:F2} next={3:F2})",
                    trend, Bars.OpenTimes[last], _h4Map.State.ProtectedPrice ?? 0, _h4Map.State.NextPrice ?? 0);
                _armed.Clear();
                _consumedZones.Clear();
                _lastH4Trend = trend;
            }
            string d1Trend = _d1Map != null && _d1Map.State != null ? _d1Map.State.Trend : null;
            if (d1Trend != _lastD1Trend)
            {
                Print("[TREND] D1 → {0} at {1:yyyy-MM-dd}", d1Trend ?? "none", barDate);
                _lastD1Trend = d1Trend;
            }

            bool wantLong = trend == "bull";
            var tradeType = wantLong ? TradeType.Buy : TradeType.Sell;

            ArmAndExpireZones(last, wantLong);

            // ── M15 trigger on the just-completed bar ───────────────────────
            var m15Candles = BuildCandles(Bars, last, M15LookbackBars);
            _m15Map = Smc.Compute(m15Candles, SwingStrength, MaxZonesPerSide);
            var trig = _m15Map.Events.LastOrDefault(e =>
                e.I2 == m15Candles.Count - 1
                && (e.Type == "ChoCh" || (EntryTrigger == EntryTriggerMode.ChochOrBos && e.Type == "bos"))
                && e.Up == wantLong);
            if (trig == null) return;

            var zone = _armed.Where(z => z.Bull == wantLong).OrderByDescending(z => z.ArmedAtBar).FirstOrDefault();
            if (zone == null)
            {
                if (DebugDiagnostics)
                    Print("[GATE] M15 {0} {1} at {2:yyyy-MM-dd HH:mm} but no armed H4 {3} zone — skip",
                        trig.Type, wantLong ? "up" : "down", Bars.OpenTimes[last], wantLong ? "demand" : "supply");
                return;
            }

            // ── Entry gates ─────────────────────────────────────────────────
            if (TradeDirection != TradeDirectionMode.Both
                && (tradeType == TradeType.Buy) != (TradeDirection == TradeDirectionMode.Buy))
            { LogSkip(last, "direction filter blocks " + tradeType); return; }

            if (RequireDailyAlignment && d1Trend != trend)
            { LogSkip(last, string.Format("D1 trend ({0}) not aligned with H4 ({1})", d1Trend ?? "none", trend)); return; }

            if (Positions.Find(Label, SymbolName) != null)
            { LogSkip(last, "position already open"); return; }

            if (_tradesToday >= DailyTradeLimit)
            { LogSkip(last, "daily trade limit reached"); return; }

            if (Symbol.Spread > MaxEntrySpreadUsd)
            { LogSkip(last, string.Format("spread ${0:F2} > max ${1:F2}", Symbol.Spread, MaxEntrySpreadUsd)); return; }

            // ── Structural stop ─────────────────────────────────────────────
            double entry = wantLong ? Symbol.Ask : Symbol.Bid;
            double slPrice = wantLong ? zone.Lo - SlBufferUsd : zone.Hi + SlBufferUsd;
            double slUsd = wantLong ? entry - slPrice : slPrice - entry;
            if (slUsd < MinSlUsd) slUsd = MinSlUsd;
            if (slUsd > MaxSlUsd)
            { LogSkip(last, string.Format("structural SL ${0:F2} > max ${1:F2} (zone {2:F2}-{3:F2})", slUsd, MaxSlUsd, zone.Lo, zone.Hi)); return; }

            // ── Liquidity target: nearest unswept H4/D1 swing beyond price ──
            var targets = CollectUnsweptTargets(wantLong, entry);
            double tpUsd = 0; string tgtDesc = null;
            foreach (var t in targets)
            {
                double tpPrice = wantLong ? t.Price - TargetOffsetUsd : t.Price + TargetOffsetUsd;
                double reward = wantLong ? tpPrice - entry : entry - tpPrice;
                if (reward <= 0) continue;
                if (reward / slUsd >= MinRR)
                {
                    tpUsd = reward;
                    tgtDesc = string.Format("{0} swing {1:F2} @ {2:yyyy-MM-dd HH:mm}", t.Tf, t.Price, t.Time);
                    break;
                }
            }
            if (tgtDesc == null)
            {
                LogSkip(last, string.Format("no unswept target with RR ≥ {0:F1} (SL ${1:F2}, {2} candidates)", MinRR, slUsd, targets.Count));
                return;
            }

            // ── Sizing & order ──────────────────────────────────────────────
            double riskMoney = Account.Balance * RiskPercent / 100.0;
            double slPips = slUsd / Symbol.PipSize;
            double tpPips = tpUsd / Symbol.PipSize;
            double units = Symbol.NormalizeVolumeInUnits(riskMoney / (slPips * Symbol.PipValue), RoundingMode.Down);
            if (units < Symbol.VolumeInUnitsMin)
            { LogSkip(last, string.Format("size {0} below minimum {1}", units, Symbol.VolumeInUnitsMin)); return; }

            var result = ExecuteMarketOrder(tradeType, SymbolName, units, Label, slPips, tpPips);
            if (result.IsSuccessful)
            {
                _tradesToday++;
                _consumedZones.Add(zone.OriginAbs);
                _armed.Remove(zone);
                Print("[ENTER] {0} {1} @ {2:F2} on M15 {3} | zone {4:F2}-{5:F2}{6} (H4 origin {7:yyyy-MM-dd HH:mm}) | SL {8:F2} (${9:F2}) TP → {10} | RR {11:F2} | vol {12}",
                    tradeType, SymbolName, entry, trig.Type,
                    zone.Lo, zone.Hi, zone.Star ? " ★ChoCh-origin" : "", zone.OriginTime,
                    slPrice, slUsd, tgtDesc, tpUsd / slUsd, units);
            }
            else
            {
                Print("[ERROR] order failed: {0}", result.Error);
            }
        }

        // ── Zone arming ─────────────────────────────────────────────────────
        private void ArmAndExpireZones(int last, bool wantLong)
        {
            double hi = Bars.HighPrices[last], lo = Bars.LowPrices[last], close = Bars.ClosePrices[last];

            // expire / invalidate
            for (int i = _armed.Count - 1; i >= 0; i--)
            {
                var z = _armed[i];
                bool expired = last - z.ArmedAtBar > ZoneExpiryBars;
                bool invalidated = z.Bull ? close < z.Lo : close > z.Hi;   // body close through the far side
                if (expired || invalidated)
                {
                    if (DebugDiagnostics)
                        Print("[ZONE] disarm {0} {1:F2}-{2:F2} at {3:yyyy-MM-dd HH:mm} ({4})",
                            z.Bull ? "demand" : "supply", z.Lo, z.Hi, Bars.OpenTimes[last], expired ? "expired" : "invalidated");
                    _armed.RemoveAt(i);
                }
            }

            if (_h4Map == null) return;
            foreach (var z in _h4Map.Zones)
            {
                if (z.Bull != wantLong) continue;                       // only trend-side zones
                if (RequireFreshZone && !z.Fresh) continue;
                if (_consumedZones.Contains(z.OriginAbs)) continue;

                bool touched = z.Bull ? lo <= z.Hi && close >= z.Lo     // dipped into demand, not closed through
                                      : hi >= z.Lo && close <= z.Hi;    // poked into supply, not closed through
                if (!touched) continue;

                var existing = _armed.FirstOrDefault(a => a.OriginAbs == z.OriginAbs && a.Bull == z.Bull);
                if (existing != null) { existing.ArmedAtBar = last; continue; }   // refresh expiry while price sits in it

                _armed.Add(new ArmedZone
                {
                    Hi = z.Hi, Lo = z.Lo, Bull = z.Bull, Star = z.Star,
                    OriginAbs = z.OriginAbs,
                    OriginTime = _h4.OpenTimes[z.OriginAbs],
                    ArmedAtBar = last
                });
                Print("[ZONE] armed {0} {1:F2}-{2:F2}{3} fresh={4} (H4 origin {5:yyyy-MM-dd HH:mm}) touched at {6:yyyy-MM-dd HH:mm}",
                    z.Bull ? "demand" : "supply", z.Lo, z.Hi, z.Star ? " ★" : "", z.Fresh,
                    _h4.OpenTimes[z.OriginAbs], Bars.OpenTimes[last]);
            }
        }

        // ── Higher-timeframe structure maps (completed bars only) ───────────
        private void UpdateHigherTfMaps()
        {
            int h4Completed = _h4.ClosePrices.Count - 1;    // bars 0..Count-2 are completed
            if (h4Completed != _lastH4Completed && h4Completed > SwingStrength * 2 + 5)
            {
                _h4Candles = BuildCandles(_h4, h4Completed - 1, H4LookbackBars);
                _h4Map = Smc.Compute(_h4Candles, SwingStrength, MaxZonesPerSide);
                _lastH4Completed = h4Completed;
            }
            int d1Completed = _d1.ClosePrices.Count - 1;
            if (d1Completed != _lastD1Completed && d1Completed > SwingStrength * 2 + 5)
            {
                _d1Candles = BuildCandles(_d1, d1Completed - 1, D1LookbackBars);
                _d1Map = Smc.Compute(_d1Candles, SwingStrength, MaxZonesPerSide);
                _lastD1Completed = d1Completed;
            }
        }

        private static List<Smc.Candle> BuildCandles(Bars bars, int lastCompleted, int lookback)
        {
            int from = Math.Max(0, lastCompleted - lookback + 1);
            var list = new List<Smc.Candle>(lastCompleted - from + 1);
            for (int i = from; i <= lastCompleted; i++)
            {
                list.Add(new Smc.Candle
                {
                    Index = i,
                    Open = bars.OpenPrices[i],
                    High = bars.HighPrices[i],
                    Low = bars.LowPrices[i],
                    Close = bars.ClosePrices[i]
                });
            }
            return list;
        }

        // ── Unswept liquidity targets, nearest first ────────────────────────
        private struct Target { public double Price; public DateTime Time; public string Tf; }

        private List<Target> CollectUnsweptTargets(bool wantLong, double entry)
        {
            var list = new List<Target>();
            AddUnswept(list, _h4Map, _h4Candles, _h4, "H4", wantLong, entry);
            AddUnswept(list, _d1Map, _d1Candles, _d1, "D1", wantLong, entry);
            // shorts: nearest low below entry = highest candidate; longs: nearest high above = lowest
            return (wantLong ? list.OrderBy(t => t.Price) : list.OrderByDescending(t => t.Price)).ToList();
        }

        private void AddUnswept(List<Target> list, Smc.StructureMap map, List<Smc.Candle> candles,
            Bars bars, string tf, bool wantLong, double entry)
        {
            if (map == null || candles == null) return;
            foreach (var sw in map.Swings)
            {
                if (sw.IsHigh == !wantLong) continue;                    // shorts target lows, longs target highs
                if (wantLong ? sw.Price <= entry : sw.Price >= entry) continue;
                bool swept = false;
                for (int k = sw.Idx + 1; k < candles.Count; k++)
                {
                    if (wantLong ? candles[k].High > sw.Price : candles[k].Low < sw.Price) { swept = true; break; }
                }
                if (swept) continue;
                list.Add(new Target { Price = sw.Price, Time = bars.OpenTimes[candles[sw.Idx].Index], Tf = tf });
            }
        }

        private void LogSkip(int last, string reason)
        {
            Print("[SKIP] {0:yyyy-MM-dd HH:mm} — {1}", Bars.OpenTimes[last], reason);
        }
    }

    // ─── STRUCTURE ENGINE ───────────────────────────────────────────────────
    // Verbatim port of ComputeStructure() from MarketStructure2.cs (itself the
    // structureMap() engine from the GOLD Terminal web platform). Only bot-
    // relevant changes: no drawing, events are NOT display-capped, and each
    // zone carries the ABSOLUTE series index of its origin candle so the bot
    // can identify a zone stably across recomputes.
    internal static class Smc
    {
        public class Candle { public int Index; public double Open, High, Low, Close; }

        public class Swing { public int Idx; public double Price; public bool IsHigh; public string Tag; }

        private class Level { public int Idx; public double Price; public bool Swept; }

        private class Idm { public int Idx; public double Price; public bool IsHigh; }

        public class StructEvent { public int I1, I2; public double Price; public bool Up; public string Type; }

        public class Zone
        {
            public int OriginIdx;       // window-relative
            public int OriginAbs;       // absolute series index (stable)
            public double Hi, Lo;
            public bool Bull, Star, Fresh;
        }

        public class StructureState
        {
            public string Trend;
            public bool Confirmed;
            public double? ProtectedPrice, NextPrice, IdmPrice;
        }

        public class StructureMap
        {
            public List<Swing> Swings = new List<Swing>();
            public List<StructEvent> Events = new List<StructEvent>();
            public List<Zone> Zones = new List<Zone>();
            public StructureState State;
        }

        public static StructureMap Compute(List<Candle> candles, int s, int maxZonesPerSide)
        {
            var map = new StructureMap();
            if (candles.Count < s * 2 + 5) return map;

            var swings = map.Swings;
            var events = map.Events;
            var zonesRaw = new List<Zone>();

            string trend = null;
            Level hiLevel = null;   // bull: ratcheting trend-extreme high · bear: protected origin high
            Level loLevel = null;   // bear: ratcheting trend-extreme low  · bull: protected origin low
            Idm idm = null;         // latest minor pullback swing inside the trend (inducement)

            Action<Swing> pushSwing = (sw) =>
            {
                var lastSw = swings.Count > 0 ? swings[swings.Count - 1] : null;
                if (lastSw != null && lastSw.IsHigh == sw.IsHigh)
                {
                    // alternation rule: consecutive same-side pivots collapse into the extreme
                    bool newWins = sw.IsHigh ? sw.Price >= lastSw.Price : sw.Price <= lastSw.Price;
                    if (!newWins) return;
                    swings.RemoveAt(swings.Count - 1);
                }
                Swing prevSame = null;
                for (int k = swings.Count - 1; k >= 0; k--)
                    if (swings[k].IsHigh == sw.IsHigh) { prevSame = swings[k]; break; }
                sw.Tag = prevSame == null ? (sw.IsHigh ? "H" : "L")
                    : sw.IsHigh ? (sw.Price > prevSame.Price ? "HH" : "LH")
                    : (sw.Price > prevSame.Price ? "HL" : "LL");
                swings.Add(sw);
            };

            // extreme bar of the leg between a broken swing and the break bar
            Func<int, int, bool, int> legOrigin = (fromIdx, toIdx, wantLow) =>
            {
                int b = Math.Min(fromIdx + 1, toIdx);
                for (int k = fromIdx + 1; k <= toIdx; k++)
                {
                    bool better = wantLow ? candles[k].Low < candles[b].Low : candles[k].High > candles[b].High;
                    if (better) b = k;
                }
                return b;
            };

            // supply/demand zone = order block at the origin of a breaking leg
            Action<int, bool, bool> addZone = (oIdx, demand, star) =>
            {
                int ob = oIdx;
                for (int k = oIdx; k > oIdx - 3 && k >= 0; k--)   // last opposite-colour candle
                {
                    bool bearish = candles[k].Close < candles[k].Open;
                    if (demand ? bearish : !bearish) { ob = k; break; }
                }
                var c = candles[ob];
                zonesRaw.Add(new Zone
                {
                    OriginIdx = ob,
                    OriginAbs = c.Index,
                    Bull = demand,
                    Star = star,
                    Hi = demand ? Math.Max(c.Open, c.Close) : c.High,
                    Lo = demand ? c.Low : Math.Min(c.Open, c.Close)
                });
            };

            for (int i = s * 2; i < candles.Count; i++)
            {
                // a fractal centred at j = i-S confirms only after S more candles close
                int j = i - s;
                var c = candles[j];
                bool isH = true, isL = true;
                for (int k = 1; k <= s; k++)
                {
                    if (isH && !(c.High > candles[j - k].High && c.High >= candles[j + k].High)) isH = false;
                    if (isL && !(c.Low < candles[j - k].Low && c.Low <= candles[j + k].Low)) isL = false;
                    if (!isH && !isL) break;
                }

                if (isH)
                {
                    pushSwing(new Swing { Idx = j, Price = c.High, IsHigh = true });
                    if (trend == "bull") { if (hiLevel == null || c.High > hiLevel.Price) hiLevel = new Level { Idx = j, Price = c.High }; }
                    else if (trend == "bear") { if (hiLevel != null && c.High < hiLevel.Price) idm = new Idm { Idx = j, Price = c.High, IsHigh = true }; }
                    else hiLevel = new Level { Idx = j, Price = c.High };
                }
                if (isL)
                {
                    pushSwing(new Swing { Idx = j, Price = c.Low, IsHigh = false });
                    if (trend == "bear") { if (loLevel == null || c.Low < loLevel.Price) loLevel = new Level { Idx = j, Price = c.Low }; }
                    else if (trend == "bull") { if (loLevel != null && c.Low > loLevel.Price) idm = new Idm { Idx = j, Price = c.Low, IsHigh = false }; }
                    else loLevel = new Level { Idx = j, Price = c.Low };
                }

                var bar = candles[i];
                if (hiLevel != null)
                {
                    if (bar.Close > hiLevel.Price)               // body close above the major high
                    {
                        events.Add(new StructEvent { I1 = hiLevel.Idx, I2 = i, Price = hiLevel.Price, Up = true, Type = trend == "bull" ? "bos" : "ChoCh" });
                        int o = legOrigin(hiLevel.Idx, i, true);
                        addZone(o, true, trend != "bull");
                        loLevel = new Level { Idx = o, Price = candles[o].Low };   // protected origin low
                        trend = "bull"; hiLevel = null; idm = null;
                    }
                    else if (bar.High > hiLevel.Price && !hiLevel.Swept)
                    {
                        events.Add(new StructEvent { I1 = hiLevel.Idx, I2 = i, Price = hiLevel.Price, Up = true, Type = "sweep" });
                        hiLevel.Swept = true;                    // buy-side liquidity grab
                    }
                }
                if (loLevel != null)
                {
                    if (bar.Close < loLevel.Price)
                    {
                        events.Add(new StructEvent { I1 = loLevel.Idx, I2 = i, Price = loLevel.Price, Up = false, Type = trend == "bear" ? "bos" : "ChoCh" });
                        int o = legOrigin(loLevel.Idx, i, false);
                        addZone(o, false, trend != "bear");
                        hiLevel = new Level { Idx = o, Price = candles[o].High };  // protected origin high
                        trend = "bear"; loLevel = null; idm = null;
                    }
                    else if (bar.Low < loLevel.Price && !loLevel.Swept)
                    {
                        events.Add(new StructEvent { I1 = loLevel.Idx, I2 = i, Price = loLevel.Price, Up = false, Type = "sweep" });
                        loLevel.Swept = true;                    // sell-side liquidity grab
                    }
                }
                // inducement taken: minor pullback swing swept inside the trend
                if (idm != null)
                {
                    bool taken = idm.IsHigh ? bar.High > idm.Price : bar.Low < idm.Price;
                    if (taken)
                    {
                        events.Add(new StructEvent { I1 = idm.Idx, I2 = i, Price = idm.Price, Up = idm.IsHigh, Type = "idm" });
                        idm = null;
                    }
                }
            }

            var breaks = events.Where(e => e.Type == "bos" || e.Type == "ChoCh").ToList();
            var lastBreak = breaks.Count > 0 ? breaks[breaks.Count - 1] : null;
            Level prot = trend == "bull" ? loLevel : trend == "bear" ? hiLevel : null;
            Level next = trend == "bull" ? hiLevel : trend == "bear" ? loLevel : null;
            map.State = trend != null ? new StructureState
            {
                Trend = trend,
                Confirmed = lastBreak != null && lastBreak.Type == "bos",
                ProtectedPrice = prot != null ? prot.Price : (double?)null,
                NextPrice = next != null ? next.Price : (double?)null,
                IdmPrice = idm != null ? idm.Price : (double?)null
            } : null;

            // zones survive until some later candle CLOSES through the far side;
            // FRESH = price has never traded back in after the impulse first left
            var zonesLive = new List<Zone>();
            foreach (var z in zonesRaw)
            {
                bool left = false, touched = false, invalidated = false;
                for (int k = z.OriginIdx + 1; k < candles.Count; k++)
                {
                    var c = candles[k];
                    if (z.Bull ? c.Close < z.Lo : c.Close > z.Hi) { invalidated = true; break; }
                    if (!left) { if (z.Bull ? c.Low > z.Hi : c.High < z.Lo) left = true; continue; }
                    if (z.Bull ? c.Low <= z.Hi : c.High >= z.Lo) touched = true;
                }
                if (invalidated) continue;
                z.Fresh = !touched;
                zonesLive.Add(z);
            }

            // pad doji order blocks to a usable box (anchored at the wick side)
            double atr = 0;
            int a0 = Math.Max(1, candles.Count - 14);
            for (int k = a0; k < candles.Count; k++)
            {
                atr += Math.Max(candles[k].High - candles[k].Low,
                    Math.Max(Math.Abs(candles[k].High - candles[k - 1].Close),
                             Math.Abs(candles[k].Low - candles[k - 1].Close)));
            }
            atr /= Math.Max(1, candles.Count - a0);
            double minH = 0.3 * atr;
            foreach (var z in zonesLive)
            {
                if (z.Hi - z.Lo < minH) { if (z.Bull) z.Hi = z.Lo + minH; else z.Lo = z.Hi - minH; }
            }

            // same-side zones that overlap collapse into the freshest one
            Func<List<Zone>, List<Zone>> dedupe = (arr) =>
            {
                var kept = new List<Zone>();
                for (int i = arr.Count - 1; i >= 0; i--)
                {
                    var z = arr[i];
                    bool clash = kept.Any(k2 =>
                        Math.Min(k2.Hi, z.Hi) - Math.Max(k2.Lo, z.Lo) > 0.5 * Math.Min(k2.Hi - k2.Lo, z.Hi - z.Lo));
                    if (!clash) kept.Insert(0, z);
                }
                return kept;
            };
            var bullZones = dedupe(zonesLive.Where(z => z.Bull).ToList());
            var bearZones = dedupe(zonesLive.Where(z => !z.Bull).ToList());
            map.Zones = bullZones.Skip(Math.Max(0, bullZones.Count - maxZonesPerSide))
                .Concat(bearZones.Skip(Math.Max(0, bearZones.Count - maxZonesPerSide)))
                .ToList();

            return map;
        }
    }
}
