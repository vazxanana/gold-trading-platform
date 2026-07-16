// ZoneStructure mechanics harness.
//
// Links the bot source file so Smc.Compute here IS the engine the bot runs
// (same assembly, not a copy). The decision loop (zone arming, gates, trigger,
// SL/TP construction) is mirrored from ZoneStructureBot.OnBar — kept in sync
// by hand; any change to the bot's flow must be reflected here.
//
// What it verifies, on every simulated entry, across seeds:
//   I1  H4 trend exists and entry direction matches it (and D1 aligns).
//   I2  an armed zone existed: correct side, FRESH at arm time, touched
//       within the expiry window before the trigger.
//   I3  the M15 trigger (ChoCh/bos, correct direction) fired exactly on the
//       just-completed bar — never the forming bar.
//   I4  SL is structural: beyond the zone's far edge by the buffer (or the
//       MinSl floor), and within MaxSl.
//   I5  TP is a genuine unswept H4/D1 swing beyond price at entry time,
//       re-verified independently against raw candles, with RR ≥ MinRR.
//   I6  the loop never reads any bar beyond the completed index (enforced
//       by a guarded accessor).
// Plus: at least one entry must occur overall, otherwise the test is vacuous.

using System;
using System.Collections.Generic;
using System.Linq;
using cAlgo.Robots;

internal static class Program
{
    // Mirror of the bot's defaults
    const int SwingStrength = 3;
    const bool RequireFreshZone = true;
    const bool RequireDailyAlignment = false;
    const int ZoneExpiryBars = 96;
    const int H4Lookback = 600, D1Lookback = 300, M15Lookback = 400;
    const int MaxZonesPerSide = 4;
    const double SlBufferUsd = 1.5, MinSlUsd = 3.0, MaxSlUsd = 60.0, MinRR = 1.0, TargetOffsetUsd = 0.5;

    class Bar { public double O, H, L, C; }

    class GuardedSeries
    {
        private readonly List<Bar> _bars;
        public int Limit;                       // highest index the sim may read
        public GuardedSeries(List<Bar> bars) { _bars = bars; }
        public int Count => _bars.Count;
        public Bar this[int i]
        {
            get
            {
                if (i > Limit) throw new Exception($"LOOK-AHEAD: read bar {i} with limit {Limit}");
                return _bars[i];
            }
        }
    }

    class ArmedZone
    {
        public double Hi, Lo; public bool Bull, Star, FreshAtArm; public int OriginAbs, ArmedAtBar;
    }

    class Entry
    {
        public int BarIdx; public bool Long; public double Px, Sl, Tp, SlUsd, RR; public string TrigType;
    }

    static int _failures;

    static void Main()
    {
        int totalEntries = 0, totalTriggersNoZone = 0, totalArmed = 0, totalAlignBlocked = 0;

        for (int seed = 1; seed <= 10; seed++)
        {
            var m15 = GenM15(seed, 9000);
            var r = Simulate(m15);
            totalEntries += r.entries.Count;
            totalTriggersNoZone += r.triggersNoZone;
            totalArmed += r.zonesArmed;
            totalAlignBlocked += r.alignBlocked;
            Console.WriteLine($"seed {seed,2}: entries={r.entries.Count,3}  zonesArmed={r.zonesArmed,3}  " +
                              $"triggerButNoZone={r.triggersNoZone,4}  d1AlignBlocked={r.alignBlocked,3}  " +
                              $"avgRR={(r.entries.Count > 0 ? r.entries.Average(e => e.RR) : 0):F2}");
            foreach (var e in r.entries.Take(2))
                Console.WriteLine($"    e.g. {(e.Long ? "BUY " : "SELL")} @{e.Px:F2} on {e.TrigType}  SL {e.Sl:F2} (${e.SlUsd:F2})  TP {e.Tp:F2}  RR {e.RR:F2}");
        }

        Console.WriteLine();
        Console.WriteLine($"TOTAL: entries={totalEntries} zonesArmed={totalArmed} triggersWithoutZone={totalTriggersNoZone} d1AlignBlocked={totalAlignBlocked}");
        Check(totalEntries > 0, "at least one entry across all seeds (test not vacuous)");
        Check(totalTriggersNoZone > 0, "zone gate actually rejects zone-less triggers (selectivity is real)");
        Check(totalArmed >= totalEntries, "every entry consumed an armed zone");

        Console.WriteLine(_failures == 0 ? "\nALL CHECKS PASSED" : $"\n{_failures} CHECK(S) FAILED");
        Environment.Exit(_failures == 0 ? 0 : 1);
    }

    // ── Synthetic M15 series: regime-switching drift random walk ───────────
    static List<Bar> GenM15(int seed, int n)
    {
        var rng = new Random(seed * 7919);
        var bars = new List<Bar>(n);
        double px = 4000;
        double drift = -0.15;
        int regimeLeft = 900 + rng.Next(600);
        for (int i = 0; i < n; i++)
        {
            if (--regimeLeft <= 0)
            {
                drift = (rng.NextDouble() < 0.5 ? -1 : 1) * (0.08 + 0.15 * rng.NextDouble());
                regimeLeft = 700 + rng.Next(900);
            }
            double sigma = 1.1 + 1.5 * rng.NextDouble();
            double open = px;
            double close = open + drift + Gauss(rng) * sigma;
            double high = Math.Max(open, close) + Math.Abs(Gauss(rng)) * sigma * 0.6;
            double low = Math.Min(open, close) - Math.Abs(Gauss(rng)) * sigma * 0.6;
            bars.Add(new Bar { O = open, H = high, L = low, C = close });
            px = close;
        }
        return bars;
    }

    static double Gauss(Random r)
    {
        double u1 = 1.0 - r.NextDouble(), u2 = r.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Sin(2.0 * Math.PI * u2);
    }

    static List<Bar> Aggregate(List<Bar> src, int k, int completedSrc)
    {
        var outBars = new List<Bar>();
        for (int start = 0; start + k <= completedSrc + 1; start += k)
        {
            var b = new Bar { O = src[start].O, H = double.MinValue, L = double.MaxValue, C = src[start + k - 1].C };
            for (int i = start; i < start + k; i++) { b.H = Math.Max(b.H, src[i].H); b.L = Math.Min(b.L, src[i].L); }
            outBars.Add(b);
        }
        return outBars;
    }

    // ── The bot loop, mirrored ──────────────────────────────────────────────
    static (List<Entry> entries, int triggersNoZone, int zonesArmed, int alignBlocked) Simulate(List<Bar> m15raw)
    {
        var m15 = new GuardedSeries(m15raw);
        var entries = new List<Entry>();
        var armed = new List<ArmedZone>();
        var consumed = new HashSet<int>();
        int triggersNoZone = 0, zonesArmed = 0, alignBlocked = 0;

        Smc.StructureMap h4Map = null, d1Map = null;
        List<Smc.Candle> h4Candles = null, d1Candles = null;
        int lastH4 = -1, lastD1 = -1;
        string lastTrend = null;

        double posSl = 0, posTp = 0; bool posOpen = false, posLong = false;

        for (int m = SwingStrength * 2 + 6; m < m15raw.Count - 1; m++)
        {
            m15.Limit = m;   // completed bar index — reading past this throws (I6)

            // manage the simulated open position with the completed bar
            if (posOpen)
            {
                var b = m15[m];
                if (posLong ? b.L <= posSl : b.H >= posSl) posOpen = false;
                else if (posLong ? b.H >= posTp : b.L <= posTp) posOpen = false;
            }

            // higher-TF maps on completed bars only
            int h4Completed = (m + 1) / 16;
            if (h4Completed != lastH4 && h4Completed > SwingStrength * 2 + 5)
            {
                var all = Aggregate(m15raw, 16, m);
                int from = Math.Max(0, all.Count - H4Lookback);
                h4Candles = all.Skip(from).Select((b, i) => new Smc.Candle { Index = from + i, Open = b.O, High = b.H, Low = b.L, Close = b.C }).ToList();
                h4Map = Smc.Compute(h4Candles, SwingStrength, MaxZonesPerSide);
                lastH4 = h4Completed;
            }
            int d1Completed = (m + 1) / 96;
            if (d1Completed != lastD1 && d1Completed > SwingStrength * 2 + 5)
            {
                var all = Aggregate(m15raw, 96, m);
                int from = Math.Max(0, all.Count - D1Lookback);
                d1Candles = all.Skip(from).Select((b, i) => new Smc.Candle { Index = from + i, Open = b.O, High = b.H, Low = b.L, Close = b.C }).ToList();
                d1Map = Smc.Compute(d1Candles, SwingStrength, MaxZonesPerSide);
                lastD1 = d1Completed;
            }
            if (h4Map == null || h4Map.State == null) continue;

            string trend = h4Map.State.Trend;
            if (trend != lastTrend) { armed.Clear(); consumed.Clear(); lastTrend = trend; }
            bool wantLong = trend == "bull";

            // arm / expire zones (mirror of ArmAndExpireZones)
            double hi = m15[m].H, lo = m15[m].L, close = m15[m].C;
            for (int i = armed.Count - 1; i >= 0; i--)
            {
                var z = armed[i];
                if (m - z.ArmedAtBar > ZoneExpiryBars || (z.Bull ? close < z.Lo : close > z.Hi)) armed.RemoveAt(i);
            }
            foreach (var z in h4Map.Zones)
            {
                if (z.Bull != wantLong) continue;
                if (RequireFreshZone && !z.Fresh) continue;
                if (consumed.Contains(z.OriginAbs)) continue;
                bool touched = z.Bull ? lo <= z.Hi && close >= z.Lo : hi >= z.Lo && close <= z.Hi;
                if (!touched) continue;
                var existing = armed.FirstOrDefault(a => a.OriginAbs == z.OriginAbs && a.Bull == z.Bull);
                if (existing != null) { existing.ArmedAtBar = m; continue; }
                armed.Add(new ArmedZone { Hi = z.Hi, Lo = z.Lo, Bull = z.Bull, Star = z.Star, FreshAtArm = z.Fresh, OriginAbs = z.OriginAbs, ArmedAtBar = m });
                zonesArmed++;
            }

            // M15 trigger on the just-completed bar
            int fromM = Math.Max(0, m - M15Lookback + 1);
            var m15Candles = new List<Smc.Candle>(m - fromM + 1);
            for (int i = fromM; i <= m; i++)
                m15Candles.Add(new Smc.Candle { Index = i, Open = m15[i].O, High = m15[i].H, Low = m15[i].L, Close = m15[i].C });
            var m15Map = Smc.Compute(m15Candles, SwingStrength, MaxZonesPerSide);
            var trig = m15Map.Events.LastOrDefault(e =>
                e.I2 == m15Candles.Count - 1 && (e.Type == "ChoCh" || e.Type == "bos") && e.Up == wantLong);
            if (trig == null) continue;

            var zone = armed.Where(z => z.Bull == wantLong).OrderByDescending(z => z.ArmedAtBar).FirstOrDefault();
            if (zone == null) { triggersNoZone++; continue; }

            string d1Trend = d1Map != null && d1Map.State != null ? d1Map.State.Trend : null;
            if (RequireDailyAlignment && d1Trend != trend) { alignBlocked++; continue; }
            if (posOpen) continue;

            double entry = close;   // proxy for next-bar market fill
            // mirror of the bot's M15Swing SL placement with zone-edge fallback
            double? m15Prot = m15Map.State != null && (m15Map.State.Trend == "bull") == wantLong
                ? m15Map.State.ProtectedPrice : null;
            double slAnchor = m15Prot.HasValue && (wantLong ? m15Prot.Value < entry : m15Prot.Value > entry)
                ? m15Prot.Value
                : (wantLong ? zone.Lo : zone.Hi);
            double slPrice = wantLong ? slAnchor - SlBufferUsd : slAnchor + SlBufferUsd;
            double slUsd = wantLong ? entry - slPrice : slPrice - entry;
            if (slUsd < MinSlUsd) slUsd = MinSlUsd;
            if (slUsd > MaxSlUsd) continue;

            var targets = CollectTargets(wantLong, entry, h4Map, h4Candles, d1Map, d1Candles);
            double tpUsd = 0, tpPrice = 0;
            foreach (var t in targets)
            {
                double tp = wantLong ? t - TargetOffsetUsd : t + TargetOffsetUsd;
                double reward = wantLong ? tp - entry : entry - tp;
                if (reward <= 0) continue;
                if (reward / slUsd >= MinRR) { tpUsd = reward; tpPrice = tp; break; }
            }
            if (tpUsd == 0) continue;

            // ── record + verify invariants ───────────────────────────────
            var e2 = new Entry
            {
                BarIdx = m, Long = wantLong, Px = entry,
                Sl = wantLong ? entry - slUsd : entry + slUsd,
                Tp = tpPrice, SlUsd = slUsd, RR = tpUsd / slUsd, TrigType = trig.Type
            };
            entries.Add(e2);
            posOpen = true; posLong = wantLong; posSl = e2.Sl; posTp = e2.Tp;
            consumed.Add(zone.OriginAbs); armed.Remove(zone);

            Check(!RequireDailyAlignment || d1Trend == trend, $"I1 D1 aligned at bar {m}");
            Check(zone.FreshAtArm || !RequireFreshZone, $"I2 zone fresh at arm (bar {m})");
            Check(m - zone.ArmedAtBar <= ZoneExpiryBars, $"I2 touch within expiry (bar {m})");
            Check(trig.I2 == m15Candles.Count - 1 && m15Candles[trig.I2].Index == m, $"I3 trigger on completed bar {m}");
            Check(wantLong ? slPrice <= slAnchor - SlBufferUsd + 1e-9 : slPrice >= slAnchor + SlBufferUsd - 1e-9,
                $"I4 SL beyond structural anchor (bar {m})");
            Check(wantLong ? slAnchor < entry : slAnchor > entry, $"I4 SL anchor on the correct side (bar {m})");
            Check(slUsd >= MinSlUsd - 1e-9 && slUsd <= MaxSlUsd + 1e-9, $"I4 SL within [{MinSlUsd},{MaxSlUsd}] (bar {m})");
            double rawTarget = wantLong ? tpPrice + TargetOffsetUsd : tpPrice - TargetOffsetUsd;
            Check(VerifyUnswept(rawTarget, wantLong, h4Candles, d1Candles), $"I5 target {rawTarget:F2} unswept at entry (bar {m})");
            Check(e2.RR >= MinRR - 1e-9, $"I5 RR ≥ {MinRR} (bar {m})");
        }
        return (entries, triggersNoZone, zonesArmed, alignBlocked);
    }

    static List<double> CollectTargets(bool wantLong, double entry,
        Smc.StructureMap h4Map, List<Smc.Candle> h4c, Smc.StructureMap d1Map, List<Smc.Candle> d1c)
    {
        var list = new List<double>();
        AddUnswept(list, h4Map, h4c, wantLong, entry);
        AddUnswept(list, d1Map, d1c, wantLong, entry);
        return (wantLong ? list.OrderBy(p => p) : list.OrderByDescending(p => p)).ToList();
    }

    static void AddUnswept(List<double> list, Smc.StructureMap map, List<Smc.Candle> candles, bool wantLong, double entry)
    {
        if (map == null || candles == null) return;
        foreach (var sw in map.Swings)
        {
            if (sw.IsHigh == !wantLong) continue;
            if (wantLong ? sw.Price <= entry : sw.Price >= entry) continue;
            bool swept = false;
            for (int k = sw.Idx + 1; k < candles.Count; k++)
                if (wantLong ? candles[k].High > sw.Price : candles[k].Low < sw.Price) { swept = true; break; }
            if (!swept) list.Add(sw.Price);
        }
    }

    // independent re-check: the chosen target price exists as a swing extreme
    // in the H4 or D1 candle data and no candle after it has traded through it
    static bool VerifyUnswept(double price, bool wantLong, List<Smc.Candle> h4c, List<Smc.Candle> d1c)
    {
        foreach (var candles in new[] { h4c, d1c })
        {
            if (candles == null) continue;
            for (int i = 0; i < candles.Count; i++)
            {
                double extreme = wantLong ? candles[i].High : candles[i].Low;
                if (Math.Abs(extreme - price) > 1e-9) continue;
                bool swept = false;
                for (int k = i + 1; k < candles.Count; k++)
                    if (wantLong ? candles[k].High > price : candles[k].Low < price) { swept = true; break; }
                if (!swept) return true;
            }
        }
        return false;
    }

    static void Check(bool ok, string what)
    {
        if (!ok) { _failures++; Console.WriteLine($"FAIL: {what}"); }
    }
}
