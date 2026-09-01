// Diagnostic reproduction of the 2026 log: a long H4 bear run with
// pullbacks but no trend flip produced ZERO zone armings for months.
// This instruments the arming funnel per zone-touch to find which
// condition starves it: view membership, freshness, touch, or pocket.

using System;
using System.Collections.Generic;
using System.Linq;
using cAlgo.Robots;

internal static class Diag
{
    public static void Run()
    {
        Console.WriteLine("── Down-trend zone-funnel diagnostic ──");
        for (int seed = 1; seed <= 5; seed++) RunSeed(seed);
    }

    // sustained decline with pullback regimes, tuned to avoid H4 flips
    static List<Program.Bar> GenDown(int seed, int n)
    {
        var rng = new Random(seed * 4241);
        var bars = new List<Program.Bar>(n);
        double px = 5500;
        double drift = -0.12;
        int regimeLeft = 400;
        for (int i = 0; i < n; i++)
        {
            if (--regimeLeft <= 0)
            {
                bool pullback = rng.NextDouble() < 0.4;
                drift = pullback ? 0.10 + 0.05 * rng.NextDouble() : -(0.12 + 0.10 * rng.NextDouble());
                regimeLeft = pullback ? 120 + rng.Next(180) : 350 + rng.Next(400);
            }
            double sigma = 1.0 + 1.2 * rng.NextDouble();
            double open = px;
            double close = open + drift + Gauss(rng) * sigma;
            double high = Math.Max(open, close) + Math.Abs(Gauss(rng)) * sigma * 0.6;
            double low = Math.Min(open, close) - Math.Abs(Gauss(rng)) * sigma * 0.6;
            bars.Add(new Program.Bar { O = open, H = high, L = low, C = close });
            px = close;
        }
        return bars;
    }

    static double Gauss(Random r)
    {
        double u1 = 1.0 - r.NextDouble(), u2 = r.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Sin(2.0 * Math.PI * u2);
    }

    static void RunSeed(int seed)
    {
        var m15raw = GenDown(seed, 12000);   // ~4 months of M15
        Smc.StructureMap h4Map = null;
        List<Smc.Candle> h4Candles = null;
        int lastH4 = -1;
        int flips = 0; string lastTrend = null;

        long zoneBarChecks = 0, wrongSide = 0, notFresh = 0, notTouched = 0, pocketFail = 0, armEvents = 0;
        var seenZones = new HashSet<int>();
        var everFreshTouched = new HashSet<int>();
        var armedNow = new HashSet<int>();     // origin abs currently armed (expiry ignored here)

        for (int m = 12; m < m15raw.Count - 1; m++)
        {
            int h4Completed = (m + 1) / 16;
            if (h4Completed != lastH4 && h4Completed > 11)
            {
                var all = Program.Aggregate(m15raw, 16, m);
                int from = Math.Max(0, all.Count - 600);
                h4Candles = all.Skip(from).Select((b, i) => new Smc.Candle { Index = from + i, Open = b.O, High = b.H, Low = b.L, Close = b.C }).ToList();
                h4Map = Smc.Compute(h4Candles, 3, 4);
                lastH4 = h4Completed;
            }
            if (h4Map == null || h4Map.State == null) continue;
            string trend = h4Map.State.Trend;
            if (trend != lastTrend) { flips++; lastTrend = trend; }
            bool wantLong = trend == "bull";

            var bar = m15raw[m];
            foreach (var z in h4Map.Zones)
            {
                zoneBarChecks++;
                seenZones.Add(z.OriginAbs);
                if (z.Bull != wantLong) { wrongSide++; continue; }
                if (!z.Fresh) { notFresh++; continue; }
                bool touched = z.Bull ? bar.L <= z.Hi && bar.C >= z.Lo : bar.H >= z.Lo && bar.C <= z.Hi;
                if (!touched) { notTouched++; continue; }
                everFreshTouched.Add(z.OriginAbs);
                if (!Program.InPocket(h4Map, h4Candles, z)) { pocketFail++; continue; }
                if (armedNow.Add(z.OriginAbs)) armEvents++;
            }
        }

        Console.WriteLine($"seed {seed}: H4 flips={flips,2}  zonesSeen={seenZones.Count,3}  zoneBarChecks={zoneBarChecks,7}");
        Console.WriteLine($"   wrongSide={wrongSide,7}  notFresh={notFresh,7}  notTouched={notTouched,7}  " +
                          $"freshTouchedZones={everFreshTouched.Count,3}  pocketFailChecks={pocketFail,5}  ARMED={armEvents,3}");
    }
}
