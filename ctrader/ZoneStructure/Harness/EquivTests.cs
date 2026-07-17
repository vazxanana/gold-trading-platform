// Equivalence proof: the streaming engine (the algorithm the Pine indicator
// implements) must reproduce the batch Smc engine (what the cBot trades)
// exactly — events, swings, sweep flags, zone views — and a full signal
// simulation driven by the streaming engine must take the same trades as the
// batch-driven simulation when both see the same history.

using System;
using System.Collections.Generic;
using System.Linq;
using cAlgo.Robots;

internal static class EquivTests
{
    public static int Failures;

    static void Check(bool ok, string what)
    {
        if (!ok) { Failures++; Console.WriteLine($"EQUIV FAIL: {what}"); }
    }

    public static void RunAll()
    {
        Console.WriteLine("\n── Equivalence: streaming (Pine) engine vs batch (bot) engine ──");
        for (int seed = 1; seed <= 10; seed++)
        {
            var m15 = Program.GenM15(seed, 9000);
            EngineEquivalence(m15.Select(b => (b.O, b.H, b.L, b.C)).ToList(), $"seed {seed} M15");
            var h4 = Program.Aggregate(m15, 16, m15.Count - 1);
            EngineEquivalence(h4.Select(b => (b.O, b.H, b.L, b.C)).ToList(), $"seed {seed} H4");
        }
        Console.WriteLine("engine-level: " + (Failures == 0 ? "IDENTICAL on 10 seeds × (M15 9000 bars + H4)" : $"{Failures} failure(s)"));

        // Signal-level: batch sim uses the BOT's real windows (M15 recomputed
        // over 400 bars); the stream sim, like Pine, sees full history. That
        // windowing is the single intended semantic difference, so we require
        // signals to exist and the overwhelming majority to agree exactly,
        // and print every disagreement for inspection.
        int totBatch = 0, totStream = 0, totMatch = 0;
        for (int seed = 1; seed <= 10; seed++)
        {
            var m15 = Program.GenM15(seed, 9000);
            var batch = Program.Simulate(m15).entries;
            var stream = SimulateStream(m15);
            totBatch += batch.Count; totStream += stream.Count;
            foreach (var a in batch)
            {
                var b = stream.FirstOrDefault(x => x.BarIdx == a.BarIdx && x.Long == a.Long);
                if (b != null)
                {
                    totMatch++;
                    Check(Math.Abs(a.Sl - b.Sl) < 1e-6 && Math.Abs(a.Tp - b.Tp) < 1e-6,
                        $"seed {seed} bar {a.BarIdx}: SL/TP batch {a.Sl:F2}/{a.Tp:F2} vs stream {b.Sl:F2}/{b.Tp:F2}");
                }
                else
                    Console.WriteLine($"  seed {seed}: batch-only signal bar {a.BarIdx} {(a.Long ? "LONG" : "SHORT")}");
            }
            foreach (var b in stream)
                if (!batch.Any(a => a.BarIdx == b.BarIdx && a.Long == b.Long))
                    Console.WriteLine($"  seed {seed}: stream-only signal bar {b.BarIdx} {(b.Long ? "LONG" : "SHORT")}");
        }
        Console.WriteLine($"signal-level: batch={totBatch} stream={totStream} exact-match={totMatch}");
        Check(totBatch > 0 && totStream > 0, "signal comparison not vacuous");
        Check(totMatch >= (int)(0.75 * Math.Max(totBatch, totStream)),
            $"≥75% signal agreement despite the M15 window difference (got {totMatch}/{Math.Max(totBatch, totStream)})");
    }

    // ── Test A: engine internals must match on any series ──────────────────
    static void EngineEquivalence(List<(double O, double H, double L, double C)> bars, string tag)
    {
        var candles = bars.Select((b, i) => new Smc.Candle { Index = i, Open = b.O, High = b.H, Low = b.L, Close = b.C }).ToList();
        var batch = Smc.Compute(candles, 3, 4);

        var se = new StreamEngine(3);
        foreach (var b in bars) se.OnBar(b.O, b.H, b.L, b.C);

        Check(batch.Events.Count == se.Events.Count, $"{tag}: event count {batch.Events.Count} vs {se.Events.Count}");
        for (int i = 0; i < Math.Min(batch.Events.Count, se.Events.Count); i++)
        {
            var a = batch.Events[i]; var b = se.Events[i];
            Check(a.Type == b.Type && a.Up == b.Up && a.I2 == b.I2 && Math.Abs(a.Price - b.Price) < 1e-9,
                $"{tag}: event {i} {a.Type}/{a.Up}@{a.I2} vs {b.Type}/{b.Up}@{b.I2}");
        }

        string batchTrend = batch.State != null ? batch.State.Trend : null;
        Check(batchTrend == se.Trend, $"{tag}: trend {batchTrend} vs {se.Trend}");
        double? bp = batch.State != null ? batch.State.ProtectedPrice : null;
        Check(NearEq(bp, se.Protected), $"{tag}: protected {bp} vs {se.Protected}");

        Check(batch.Swings.Count == se.Swings.Count, $"{tag}: swing count {batch.Swings.Count} vs {se.Swings.Count}");
        for (int i = 0; i < Math.Min(batch.Swings.Count, se.Swings.Count); i++)
        {
            var a = batch.Swings[i]; var b = se.Swings[i];
            bool batchSwept = false;
            for (int k = a.Idx + 1; k < candles.Count; k++)
                if (a.IsHigh ? candles[k].High > a.Price : candles[k].Low < a.Price) { batchSwept = true; break; }
            Check(a.Idx == b.Idx && a.IsHigh == b.IsHigh && Math.Abs(a.Price - b.Price) < 1e-9 && batchSwept == b.Swept,
                $"{tag}: swing {i} idx {a.Idx}/{a.IsHigh}/swept={batchSwept} vs {b.Idx}/{b.IsHigh}/swept={b.Swept}");
        }

        var sv = se.ZonesView(4);
        Check(batch.Zones.Count == sv.Count, $"{tag}: zone count {batch.Zones.Count} vs {sv.Count}");
        for (int i = 0; i < Math.Min(batch.Zones.Count, sv.Count); i++)
        {
            var a = batch.Zones[i]; var b = sv[i];
            Check(a.OriginAbs == b.OriginAbs && a.Bull == b.Bull && a.Star == b.Star && a.Fresh == b.Fresh
                && Math.Abs(a.Hi - b.Hi) < 1e-9 && Math.Abs(a.Lo - b.Lo) < 1e-9,
                $"{tag}: zone {i} {a.OriginAbs}/{a.Bull}/{a.Fresh} {a.Lo:F2}-{a.Hi:F2} vs {b.OriginAbs}/{b.Bull}/{b.Fresh} {b.Lo:F2}-{b.Hi:F2}");
        }
    }

    static bool NearEq(double? a, double? b) =>
        a.HasValue == b.HasValue && (!a.HasValue || Math.Abs(a.Value - b.Value) < 1e-9);

    // ── Test B: full signal loop driven by streaming engines ────────────────
    // Mirrors Program.Simulate gate-for-gate; only the structure source
    // differs (streaming engines instead of batch recomputes).
    class StreamedEntry { public int BarIdx; public bool Long; public double Sl, Tp; }

    static List<StreamedEntry> SimulateStream(List<Program.Bar> m15raw)
    {
        var entries = new List<StreamedEntry>();
        var armed = new List<(double Hi, double Lo, bool Bull, int OriginAbs, int ArmedAtBar)>();
        var consumed = new HashSet<int>();

        var h4Eng = new StreamEngine(3);
        var d1Eng = new StreamEngine(3);
        var m15Eng = new StreamEngine(3);
        int h4Fed = 0, d1Fed = 0;
        List<StreamEngine.ZView> h4Zones = null;
        string lastTrend = null;
        double posSl = 0, posTp = 0; bool posOpen = false, posLong = false;

        for (int m = 0; m < m15raw.Count - 1; m++)
        {
            var cur = m15raw[m];
            m15Eng.OnBar(cur.O, cur.H, cur.L, cur.C);
            if (m < 3 * 2 + 6) continue;

            if (posOpen)
            {
                if (posLong ? cur.L <= posSl : cur.H >= posSl) posOpen = false;
                else if (posLong ? cur.H >= posTp : cur.L <= posTp) posOpen = false;
            }

            int h4Completed = (m + 1) / 16;
            while (h4Fed < h4Completed)
            {
                int st = h4Fed * 16;
                double hh = double.MinValue, ll = double.MaxValue;
                for (int i = st; i < st + 16; i++) { hh = Math.Max(hh, m15raw[i].H); ll = Math.Min(ll, m15raw[i].L); }
                h4Eng.OnBar(m15raw[st].O, hh, ll, m15raw[st + 15].C);
                h4Fed++;
                h4Zones = h4Eng.ZonesView(4);
            }
            int d1Completed = (m + 1) / 96;
            while (d1Fed < d1Completed)
            {
                int st = d1Fed * 96;
                double hh = double.MinValue, ll = double.MaxValue;
                for (int i = st; i < st + 96; i++) { hh = Math.Max(hh, m15raw[i].H); ll = Math.Min(ll, m15raw[i].L); }
                d1Eng.OnBar(m15raw[st].O, hh, ll, m15raw[st + 95].C);
                d1Fed++;
            }
            if (h4Eng.Bars.Count <= 3 * 2 + 5 || h4Eng.Trend == null) continue;

            string trend = h4Eng.Trend;
            if (trend != lastTrend) { armed.Clear(); consumed.Clear(); lastTrend = trend; }
            bool wantLong = trend == "bull";

            double hi = cur.H, lo = cur.L, close = cur.C;
            for (int i = armed.Count - 1; i >= 0; i--)
            {
                var z = armed[i];
                if (m - z.ArmedAtBar > 96 || (z.Bull ? close < z.Lo : close > z.Hi)) armed.RemoveAt(i);
            }
            if (h4Zones != null)
            {
                foreach (var z in h4Zones)
                {
                    if (z.Bull != wantLong || !z.Fresh || consumed.Contains(z.OriginAbs)) continue;
                    bool touchedZ = z.Bull ? lo <= z.Hi && close >= z.Lo : hi >= z.Lo && close <= z.Hi;
                    if (!touchedZ) continue;
                    int ex = armed.FindIndex(a => a.OriginAbs == z.OriginAbs && a.Bull == z.Bull);
                    if (ex >= 0) { armed[ex] = (armed[ex].Hi, armed[ex].Lo, armed[ex].Bull, armed[ex].OriginAbs, m); continue; }
                    armed.Add((z.Hi, z.Lo, z.Bull, z.OriginAbs, m));
                }
            }

            var trig = m15Eng.BarEvents.LastOrDefault(e => (e.Type == "ChoCh" || e.Type == "bos") && e.Up == wantLong);
            if (trig == null) continue;
            if (!armed.Any(z => z.Bull == wantLong)) continue;
            var zone = armed.Where(z => z.Bull == wantLong).OrderByDescending(z => z.ArmedAtBar).First();
            if (posOpen) continue;

            double entry = close;
            double? m15Prot = (m15Eng.Trend == "bull") == wantLong ? m15Eng.Protected : null;
            double slAnchor = m15Prot.HasValue && (wantLong ? m15Prot.Value < entry : m15Prot.Value > entry)
                ? m15Prot.Value : (wantLong ? zone.Lo : zone.Hi);
            double slUsd = (wantLong ? entry - (slAnchor - 1.5) : (slAnchor + 1.5) - entry);
            if (slUsd < 3.0) slUsd = 3.0;
            if (slUsd > 60.0) continue;

            var targets = new List<double>();
            // batch sim has no D1 map until 12 completed D1 bars — mirror that
            var engines = d1Eng.Bars.Count > 3 * 2 + 5 ? new[] { h4Eng, d1Eng } : new[] { h4Eng };
            foreach (var eng in engines)
                foreach (var sw in eng.Swings)
                {
                    if (sw.IsHigh == !wantLong || sw.Swept) continue;
                    if (wantLong ? sw.Price <= entry : sw.Price >= entry) continue;
                    targets.Add(sw.Price);
                }
            targets = (wantLong ? targets.OrderBy(p => p) : targets.OrderByDescending(p => p)).ToList();

            double tpPrice = 0; bool found = false;
            foreach (var t in targets)
            {
                double tp = wantLong ? t - 0.5 : t + 0.5;
                double reward = wantLong ? tp - entry : entry - tp;
                if (reward <= 0) continue;
                if (reward / slUsd >= 1.0) { tpPrice = tp; found = true; break; }
            }
            if (!found) continue;

            posOpen = true; posLong = wantLong;
            posSl = wantLong ? entry - slUsd : entry + slUsd;
            posTp = tpPrice;
            consumed.Add(zone.OriginAbs);
            armed.RemoveAll(a => a.OriginAbs == zone.OriginAbs && a.Bull == zone.Bull);
            entries.Add(new StreamedEntry { BarIdx = m, Long = wantLong, Sl = posSl, Tp = posTp });
        }
        return entries;
    }
}
