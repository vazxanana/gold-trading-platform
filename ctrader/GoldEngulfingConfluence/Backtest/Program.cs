using System;
using System.Collections.Generic;
using System.Linq;
using cAlgo.Robots;

// Monte-Carlo backtest of the GoldEngulfingConfluence state machine, ported 1:1 from
// GoldEngulfingConfluenceBot.cs (the engulfing predicate itself is NOT ported - the bot's
// EngulfingLogic class is compiled in via the linked source file).
//
// Data: synthetic XAUUSD-calibrated M1 bars (GBM + GARCH-lite vol clustering + UTC-hour
// vol seasonality + weekend gaps). Bars are BID prices; buys fill at ask (= bid + spread).
// This validates MECHANICS and cost drag, and shows the strategy's behavior on no-edge
// data. It cannot prove an edge on real gold - run cTrader's backtester for that.

const double RRRatio = 2.5;
const int Seeds = 15;
const int TradingDays = 90;

var configs = new (string Name, bool StrictEg, bool SessionFilter)[]
{
    ("spec EG,   session 12-15", false, true),
    ("strict EG, session 12-15", true,  true),
    ("spec EG,   no session",    false, false),
    ("strict EG, no session",    true,  false),
};

Console.WriteLine($"Monte-Carlo backtest: {Seeds} seeds x {TradingDays} weekdays of synthetic XAUUSD M1, " +
                  $"{configs.Length} configs. Spread {Simulator.SpreadPips} pips, SL buffer {Simulator.SlBufferPips}, " +
                  $"RR 1:{RRRatio}, MaxSL {Simulator.MaxSlPips}, daily limit {Simulator.DailyTradeLimit}, zone expiry {Simulator.ZoneExpiryBars} bars.");
Console.WriteLine();

foreach (var cfg in configs)
{
    var perSeed = new List<SeedResult>();
    var funnel = new Funnel();
    for (int seed = 1; seed <= Seeds; seed++)
        perSeed.Add(Simulator.Run(SyntheticGold.Generate(seed, TradingDays), cfg.StrictEg, cfg.SessionFilter, funnel));
    Report(cfg.Name, perSeed, funnel);
}

void Report(string name, List<SeedResult> rs, Funnel f)
{
    var all = rs.SelectMany(r => r.TradesR).ToList();
    int n = all.Count;
    Console.WriteLine($"=== {name} ===");
    Console.WriteLine($"  funnel: M15 zones {f.M15Zones}, M5 confirmations {f.M5Confirms}, taps {f.Taps}, " +
                      $"M1 triggers {f.Triggers}, signals {f.Signals}");
    Console.WriteLine($"  filtered: session {f.SkipSession}, SL cap/invalid {f.SkipSlCap}, daily limit {f.SkipDaily}; " +
                      $"SL+TP same bar (worst-case SL taken) {f.Ambiguous}, open at end {f.OpenAtEnd}");
    if (n == 0) { Console.WriteLine("  NO TRADES\n"); return; }

    int wins = all.Count(r => r > 0);
    double grossWin = all.Where(r => r > 0).Sum();
    double grossLoss = -all.Where(r => r <= 0).Sum();
    var totals = rs.Select(r => r.TradesR.Sum()).OrderBy(x => x).ToList();
    double worstDd = 0;
    foreach (var r in rs)
    {
        double eq = 0, peak = 0;
        foreach (var t in r.TradesR) { eq += t; peak = Math.Max(peak, eq); worstDd = Math.Min(worstDd, eq - peak); }
    }
    double avgSl = rs.SelectMany(r => r.SlPips).DefaultIfEmpty(0).Average();

    Console.WriteLine($"  trades: {n} ({(double)n / (Seeds * TradingDays):F2}/day), win rate {100.0 * wins / n:F1}% " +
                      $"(breakeven {100 / (1 + RRRatio):F1}%), avg SL {avgSl:F0} pips (1R ~ ${avgSl * 1:F0} at 0.1 lot)");
    Console.WriteLine($"  expectancy {all.Average():+0.000;-0.000}R/trade, profit factor {(grossLoss > 0 ? grossWin / grossLoss : double.PositiveInfinity):F2}, " +
                      $"total {all.Sum():+0.0;-0.0}R across all seeds");
    Console.WriteLine($"  per-seed total R: median {totals[totals.Count / 2]:+0.0;-0.0}, p25 {totals[(int)(totals.Count * 0.25)]:+0.0;-0.0}, " +
                      $"p75 {totals[(int)(totals.Count * 0.75)]:+0.0;-0.0}; profitable seeds {totals.Count(t => t > 0)}/{Seeds}; " +
                      $"worst drawdown {worstDd:F1}R");
    Console.WriteLine();
}

public sealed class Funnel
{
    public int M15Zones, M5Confirms, Taps, Triggers, Signals, SkipSession, SkipSlCap, SkipDaily, Ambiguous, OpenAtEnd;
}

public sealed class SeedResult
{
    public List<double> TradesR = new();
    public List<double> SlPips = new();
}

public static class SyntheticGold
{
    // M1 BID bars: GBM, GARCH-lite vol clustering, UTC-hour vol seasonality, weekend gaps.
    public static Series Generate(int seed, int weekdays)
    {
        var rng = new Random(seed * 7919);
        double[] hourVol = new double[24];
        for (int h = 0; h < 24; h++)
            hourVol[h] = h < 6 ? 0.55 : h < 7 ? 0.8 : h < 12 ? 1.15 : h < 16 ? 1.7 : h < 20 ? 1.1 : 0.7;

        int n = weekdays * 1440;
        var s = new Series(n);
        double price = 3300;                        // ~mid-2026 gold
        double baseSigma = 0.010 / Math.Sqrt(1440); // ~1.0% daily vol
        double vol = 1.0;
        var time = new DateTime(2026, 3, 2, 0, 0, 0, DateTimeKind.Utc); // a Monday

        double Gauss()
        {
            double u1 = 1 - rng.NextDouble(), u2 = rng.NextDouble();
            return Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
        }

        for (int i = 0; i < n; i++)
        {
            if (time.DayOfWeek == DayOfWeek.Saturday) { time = time.AddDays(2); price *= 1 + Gauss() * 0.0015; }

            double z = Gauss();
            if (rng.NextDouble() < 0.02) z *= 3.5;
            vol = 0.94 * vol + 0.06 * Math.Min(3.0, Math.Abs(z));
            double sigma = baseSigma * hourVol[time.Hour] * (0.6 + 0.8 * vol);

            double open = price;
            double close = price * Math.Exp(sigma * z);
            double hi = Math.Max(open, close) + Math.Abs(Gauss()) * sigma * price * 0.6;
            double lo = Math.Min(open, close) - Math.Abs(Gauss()) * sigma * price * 0.6;
            s.Add(time, open, hi, lo, close);
            price = close;
            time = time.AddMinutes(1);
        }
        return s;
    }
}

public sealed class Series
{
    public readonly List<DateTime> T; public readonly List<double> O, H, L, C;
    public Series(int cap = 16) { T = new(cap); O = new(cap); H = new(cap); L = new(cap); C = new(cap); }
    public int Count => T.Count;
    public void Add(DateTime t, double o, double h, double l, double c) { T.Add(t); O.Add(o); H.Add(h); L.Add(l); C.Add(c); }
    public void Extend(double h, double l, double c) { H[^1] = Math.Max(H[^1], h); L[^1] = Math.Min(L[^1], l); C[^1] = c; }
}

public static class Simulator
{
    public const double PipSize = 0.1;   // XAUUSD pip in cTrader
    public const double SpreadPips = 2.5;
    public const double SlBufferPips = 50;
    public const double MaxSlPips = 200;
    public const double RRRatio = 2.5;
    public const int SessionStartHourUtc = 12, SessionEndHourUtc = 15;
    public const int DailyTradeLimit = 5;
    public const int ZoneExpiryBars = 20;

    sealed class Zone { public double Low, High; public int BarIndex; }
    sealed class Trade { public bool IsBuy; public double Entry, Sl, Tp, SlPips; }

    // Spec definition comes from the bot's own EngulfingLogic (linked source, not a port);
    // strict variant adds the classic opposite-color-prior requirement.
    static bool BullEg(Series s, int i, bool strict) =>
        i >= 1 && EngulfingLogic.IsBullish(s.O[i - 1], s.C[i - 1], s.O[i], s.C[i]) && (!strict || s.C[i - 1] < s.O[i - 1]);
    static bool BearEg(Series s, int i, bool strict) =>
        i >= 1 && EngulfingLogic.IsBearish(s.O[i - 1], s.C[i - 1], s.O[i], s.C[i]) && (!strict || s.C[i - 1] > s.O[i - 1]);

    public static SeedResult Run(Series m1, bool strictEg, bool sessionFilter, Funnel f)
    {
        double spread = SpreadPips * PipSize;
        var res = new SeedResult();
        var m5 = new Series(m1.Count / 5 + 2);
        var m15 = new Series(m1.Count / 15 + 2);

        Zone m15Bull = null, m15Bear = null, m5BullEg = null, m5BearEg = null;
        bool buyTapped = false, sellTapped = false, buyTrig = false, sellTrig = false;
        double buyTrigLevel = 0, sellTrigLevel = 0;
        int buyTrigBar = -1, sellTrigBar = -1;
        DateTime lastM15Eval = DateTime.MinValue;
        DateTime tradeCountDate = DateTime.MinValue; int tradesToday = 0;
        var open = new List<Trade>();
        var pendings = new List<Trade>(); // signals fire on M1 close; market orders fill at next bar open

        for (int i = 0; i < m1.Count; i++)
        {
            var bt = m1.T[i];

            // ---- fill pending market orders at this bar's open ----
            foreach (var tr in pendings)
            {
                double fill = tr.IsBuy ? m1.O[i] + spread : m1.O[i];
                double dist = tr.SlPips * PipSize;
                tr.Entry = fill;
                tr.Sl = tr.IsBuy ? fill - dist : fill + dist;
                tr.Tp = tr.IsBuy ? fill + dist * RRRatio : fill - dist * RRRatio;
                open.Add(tr);
            }
            pendings.Clear();

            // ---- resolve open positions on this bar (worst case: SL before TP) ----
            for (int p = open.Count - 1; p >= 0; p--)
            {
                var tr = open[p];
                bool slHit, tpHit;
                if (tr.IsBuy) { slHit = m1.L[i] <= tr.Sl; tpHit = m1.H[i] >= tr.Tp; }
                else { slHit = m1.H[i] + spread >= tr.Sl; tpHit = m1.L[i] + spread <= tr.Tp; }
                if (slHit && tpHit) f.Ambiguous++;
                if (slHit) { res.TradesR.Add(-1); res.SlPips.Add(tr.SlPips); open.RemoveAt(p); }
                else if (tpHit) { res.TradesR.Add(RRRatio); res.SlPips.Add(tr.SlPips); open.RemoveAt(p); }
            }

            // ---- aggregate M5/M15 ----
            bool newM5 = bt.Minute % 5 == 0, newM15 = bt.Minute % 15 == 0;
            if (newM5) m5.Add(bt, m1.O[i], m1.H[i], m1.L[i], m1.C[i]); else m5.Extend(m1.H[i], m1.L[i], m1.C[i]);
            if (newM15) m15.Add(bt, m1.O[i], m1.H[i], m1.L[i], m1.C[i]); else m15.Extend(m1.H[i], m1.L[i], m1.C[i]);

            // ============ OnBar (M5 open; completed index = Count-2), mirrors the bot ============
            if (newM5)
            {
                int m15Last = m15.Count - 2;
                if (m15Last >= 1 && m15.T[m15Last] != lastM15Eval)
                {
                    lastM15Eval = m15.T[m15Last];
                    if (m15Bull != null && m15Last - m15Bull.BarIndex >= ZoneExpiryBars) m15Bull = null;
                    if (m15Bear != null && m15Last - m15Bear.BarIndex >= ZoneExpiryBars) m15Bear = null;
                    if (BullEg(m15, m15Last, strictEg)) { m15Bull = new Zone { Low = m15.L[m15Last], High = m15.H[m15Last], BarIndex = m15Last }; f.M15Zones++; }
                    if (BearEg(m15, m15Last, strictEg)) { m15Bear = new Zone { Low = m15.L[m15Last], High = m15.H[m15Last], BarIndex = m15Last }; f.M15Zones++; }
                }

                int m5Last = m5.Count - 2;
                if (m5Last >= 1)
                {
                    if (m5BullEg != null && m5Last - m5BullEg.BarIndex >= ZoneExpiryBars) { m5BullEg = null; buyTapped = buyTrig = false; }
                    if (m5BearEg != null && m5Last - m5BearEg.BarIndex >= ZoneExpiryBars) { m5BearEg = null; sellTapped = sellTrig = false; }
                    double cl = m5.C[m5Last];
                    if (m15Bull != null && BullEg(m5, m5Last, strictEg) && cl >= m15Bull.Low && cl <= m15Bull.High)
                    { m5BullEg = new Zone { Low = m5.L[m5Last], High = m5.H[m5Last], BarIndex = m5Last }; buyTapped = buyTrig = false; f.M5Confirms++; }
                    if (m15Bear != null && BearEg(m5, m5Last, strictEg) && cl >= m15Bear.Low && cl <= m15Bear.High)
                    { m5BearEg = new Zone { Low = m5.L[m5Last], High = m5.H[m5Last], BarIndex = m5Last }; sellTapped = sellTrig = false; f.M5Confirms++; }
                }
            }

            // ============ M1 BarOpened (completed index = i-1), mirrors the bot ============
            int last = i - 1;
            if (last < 1) continue;

            if (m5BullEg != null)
            {
                if (!buyTapped && m1.L[last] <= m5BullEg.Low) { buyTapped = true; f.Taps++; }
                if (buyTapped)
                {
                    if (buyTrig && m1.C[last] > buyTrigLevel && last > buyTrigBar)
                    {
                        var eg = m5BullEg; m5BullEg = null; buyTapped = buyTrig = false;
                        Signal(true, eg, last);
                    }
                    else if (BearEg(m1, last, strictEg)) { buyTrig = true; buyTrigLevel = m1.H[last]; buyTrigBar = last; f.Triggers++; }
                }
            }
            if (m5BearEg != null)
            {
                if (!sellTapped && m1.H[last] >= m5BearEg.High) { sellTapped = true; f.Taps++; }
                if (sellTapped)
                {
                    if (sellTrig && m1.C[last] < sellTrigLevel && last > sellTrigBar)
                    {
                        var eg = m5BearEg; m5BearEg = null; sellTapped = sellTrig = false;
                        Signal(false, eg, last);
                    }
                    else if (BullEg(m1, last, strictEg)) { sellTrig = true; sellTrigLevel = m1.L[last]; sellTrigBar = last; f.Triggers++; }
                }
            }

            continue;

            void Signal(bool isBuy, Zone eg, int sigBar)
            {
                f.Signals++;
                var sigTime = m1.T[sigBar];
                if (sessionFilter && (sigTime.Hour < SessionStartHourUtc || sigTime.Hour >= SessionEndHourUtc)) { f.SkipSession++; return; }
                double entry = m1.C[sigBar];
                double slPrice = isBuy ? eg.Low - SlBufferPips * PipSize : eg.High + SlBufferPips * PipSize;
                double slPips = (isBuy ? entry - slPrice : slPrice - entry) / PipSize;
                if (slPips > MaxSlPips || slPips <= 0) { f.SkipSlCap++; return; }
                if (sigTime.Date != tradeCountDate) { tradeCountDate = sigTime.Date; tradesToday = 0; }
                if (tradesToday >= DailyTradeLimit) { f.SkipDaily++; return; }
                tradesToday++;
                pendings.Add(new Trade { IsBuy = isBuy, SlPips = slPips });
            }
        }

        f.OpenAtEnd += open.Count + pendings.Count;
        return res;
    }
}
