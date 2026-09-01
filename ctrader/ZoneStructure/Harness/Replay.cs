// Screenshot replay: reconstructs the May-Jul 2026 XAUUSD path from the
// landmarks the user marked on their TradingView charts (highs, lows,
// supply zones, break points and their dates), then runs BOTH the
// bot-mirror simulation (Program.Simulate) and the Pine-mirror simulation
// (EquivTests.SimulateStream) over it and reports every trade with its
// timestamp, so the signals can be checked against the drawn trade paths:
//   A. Jun 18-25 SELL from the 4378-4470 supply -> D low ~4020s
//   B. Jul  7-9  SELL from the 4164-4211 supply -> ~4025
//   C. Jul 10-14 SELL from the 4085-4130 supply -> 3965/3940
// This verifies MECHANICS on a landmark-accurate reconstruction — the
// user's cTrader backtest on real tick data remains the final word.

using System;
using System.Collections.Generic;
using System.Linq;

internal static class Replay
{
    // (day offset from May 20 00:00, price) anchors read off the charts
    static readonly (double day, double px)[] Anchors =
    {
        (0.0, 4260), (2.0, 4060), (4.0, 3938),   // the old D low the charts mark at 3940.31
        (6.5, 4180), (8.0, 4300), (9.5, 4260), (10.5, 4380), (11.0, 4430), (11.6, 4460),
        (12.4, 4450),                       // Jun 1
        (16.0, 4485),                       // Jun 5 high under 4506.27
        (21.0, 4230),                       // Jun 10 low
        (24.0, 4310),                       // Jun 13 pullback
        (28.0, 4425),                       // Jun 17 rally into the 4378-4470 supply
        (28.8, 4462),                       // Jun 17 pm tap of zone extreme
        (29.6, 4390),                       // Jun 18 rollover (trade A trigger area)
        (31.0, 4290),
        (35.0, 4020),                       // Jun 24 into the D low
        (37.0, 4105),                       // Jun 26 bounce
        (39.0, 4019),                       // Jun 28 retest
        (42.0, 4085),                       // Jul 1
        (44.0, 4180),                       // Jul 3 rally into 4164-4211 supply
        (46.0, 4205),                       // Jul 5 zone tap
        (48.0, 4160),                       // Jul 7 chop under the zone
        (48.9, 4130),                       // Jul 8 early breakdown (trade B)
        (49.5, 4080),
        (50.3, 4028),                       // Jul 9 low at 4025.81
        (51.3, 4118),                       // Jul 10 rally into 4085-4130 supply
        (52.3, 4108),                       // Jul 11
        (54.3, 4005),                       // Jul 13 breakdown (trade C)
        (55.3, 3968),                       // Jul 14 low near 3965
        (56.3, 4042),                       // Jul 15 pullback
        (57.5, 3976),                       // Jul 16 close
    };

    static readonly DateTime T0 = new DateTime(2026, 5, 20, 0, 0, 0, DateTimeKind.Utc);

    public static List<Program.Bar> Build()
    {
        int nBars = (int)(Anchors[^1].day * 96);
        var bars = new List<Program.Bar>(nBars);
        var rng = new Random(20260717);
        double prevClose = Anchors[0].px;
        for (int i = 0; i < nBars; i++)
        {
            double day = i / 96.0;
            double baseline = Interp(day);
            // multi-scale texture: M15 chop + intraday swings, deterministic
            double wig = 2.2 * Math.Sin(i / 2.9) + 4.5 * Math.Sin(i / 17.3) + 6.0 * Math.Sin(i / 47.0);
            double close = baseline + wig + (rng.NextDouble() - 0.5) * 2.0;
            double open = prevClose;
            double hiW = 1.0 + 1.4 * Math.Abs(Math.Sin(i / 5.1)) + rng.NextDouble();
            double loW = 1.0 + 1.4 * Math.Abs(Math.Cos(i / 6.3)) + rng.NextDouble();
            bars.Add(new Program.Bar
            {
                O = open,
                C = close,
                H = Math.Max(open, close) + hiW,
                L = Math.Min(open, close) - loW,
            });
            prevClose = close;
        }
        return bars;
    }

    static double Interp(double day)
    {
        for (int a = 1; a < Anchors.Length; a++)
        {
            if (day <= Anchors[a].day)
            {
                double t = (day - Anchors[a - 1].day) / (Anchors[a].day - Anchors[a - 1].day);
                return Anchors[a - 1].px + t * (Anchors[a].px - Anchors[a - 1].px);
            }
        }
        return Anchors[^1].px;
    }

    static DateTime Ts(int barIdx) => T0.AddMinutes(15.0 * barIdx);

    public static void Run()
    {
        var bars = Build();
        Console.WriteLine($"── Screenshot replay: {bars.Count} M15 bars {Ts(0):yyyy-MM-dd} → {Ts(bars.Count - 1):yyyy-MM-dd} ──");
        Console.WriteLine($"   path check: Jun17 {Interp(28.8):F0}  Jun24 {Interp(35):F0}  Jul5 {Interp(46):F0}  Jul9 {Interp(50.3):F0}  Jul14 {Interp(55.3):F0}");

        Program.SkipTrace = new List<string>();
        var batch = Program.Simulate(bars).entries;
        var stream = EquivTests.SimulateStream(bars);

        Console.WriteLine($"\nBOT-mirror entries ({batch.Count}):");
        foreach (var e in batch)
            Console.WriteLine($"  {Ts(e.BarIdx):MMM dd HH:mm}  {(e.Long ? "BUY " : "SELL")} @{e.Px,8:F2}  SL {e.Sl,8:F2}  TP {e.Tp,8:F2}  RR {e.RR:F2} [{e.TrigType}]");

        Console.WriteLine($"\nPINE-mirror entries ({stream.Count}):");
        foreach (var e in stream)
            Console.WriteLine($"  {Ts(e.BarIdx):MMM dd HH:mm}  {(e.Long ? "BUY " : "SELL")} SL {e.Sl,8:F2}  TP {e.Tp,8:F2}");

        // screenshot windows (bar offsets from T0)
        bool W(int loDay, int hiDay, Func<Program.Entry, bool> extra = null) =>
            batch.Any(e => !e.Long && e.BarIdx >= loDay * 96 && e.BarIdx <= hiDay * 96 && (extra == null || extra(e)));

        int fails = 0;
        void Check(bool ok, string what)
        {
            Console.WriteLine((ok ? "  PASS  " : "  MISS  ") + what);
            if (!ok) fails++;
        }
        Console.WriteLine("\nScreenshot-trade checks (bot mirror):");
        Check(W(28, 36), "A: SELL Jun 17-25 from the 4378-4470 supply area");
        Check(W(47, 51), "B: SELL Jul  7-9  from the 4164-4211 supply area");
        Check(W(51, 56), "C: SELL Jul 10-14 from the 4085-4130 supply area");
        Check(batch.Count(e => e.Long) <= 1, "at most one BUY (the legitimate early-July H4 bull ChoCh)");
        int match = batch.Count(a => stream.Any(s => s.BarIdx == a.BarIdx && s.Long == a.Long));
        Check(batch.Count > 0 && match == batch.Count && stream.Count == batch.Count,
            $"Pine mirror takes the SAME trades ({match}/{batch.Count} matched, stream={stream.Count})");
        Console.WriteLine(fails == 0 ? "\nREPLAY: ALL SCREENSHOT TRADES REPRODUCED" : $"\nREPLAY: {fails} check(s) MISSING");
        if (fails > 0)
        {
            Console.WriteLine("\n── Gate trace in missing windows ──");
            foreach (var s in Program.SkipTrace)
            {
                int bar = int.Parse(s.Split('|')[0]);
                double d = bar / 96.0;
                if ((d >= 28 && d <= 36) || (d >= 51 && d <= 56))
                    Console.WriteLine($"  {Ts(bar):MMM dd HH:mm}  {s.Split('|')[1]}");
            }
        }
    }

    // daily funnel dump: what did the zone layer see and why did nothing arm
    static void Funnel(List<Program.Bar> m15raw)
    {
        Console.WriteLine("\n── Funnel probe (daily snapshots) ──");
        cAlgo.Robots.Smc.StructureMap h4Map = null;
        List<cAlgo.Robots.Smc.Candle> h4Candles = null;
        int lastH4 = -1;
        long touchesFreshSide = 0, pocketFails = 0, arms = 0;
        for (int m = 12; m < m15raw.Count - 1; m++)
        {
            int h4Completed = (m + 1) / 16;
            if (h4Completed != lastH4 && h4Completed > 11)
            {
                var all = Program.Aggregate(m15raw, 16, m);
                int from = Math.Max(0, all.Count - 600);
                h4Candles = all.Skip(from).Select((b, i) => new cAlgo.Robots.Smc.Candle { Index = from + i, Open = b.O, High = b.H, Low = b.L, Close = b.C }).ToList();
                h4Map = cAlgo.Robots.Smc.Compute(h4Candles, 3, 4);
                lastH4 = h4Completed;
            }
            if (h4Map == null || h4Map.State == null) continue;
            bool wantLong = h4Map.State.Trend == "bull";
            var bar = m15raw[m];
            foreach (var z in h4Map.Zones)
            {
                if (z.Bull != wantLong) continue;
                bool touched = z.Bull ? bar.L <= z.Hi && bar.C >= z.Lo : bar.H >= z.Lo && bar.C <= z.Hi;
                if (!touched) continue;
                touchesFreshSide++;
                if (!Program.InPocket(h4Map, h4Candles, z)) pocketFails++; else arms++;
            }
            if (m % 96 == 0)
            {
                var st = h4Map.State;
                string zs = string.Join(" | ", h4Map.Zones.Select(z =>
                    $"{(z.Bull ? "D" : "S")} {z.Lo:F0}-{z.Hi:F0}{(Program.InPocket(h4Map, h4Candles, z) ? " ★" : "")}"));
                Console.WriteLine($"  {Ts(m):MMM dd}  px={bar.C,7:F1}  H4={st.Trend,-4} prot={st.ProtectedPrice ?? 0,7:F1}  zones: {zs}");
            }
        }
        Console.WriteLine($"  totals: side-matching touches={touchesFreshSide}  pocketFails={pocketFails}  wouldArm={arms}");
    }
}
