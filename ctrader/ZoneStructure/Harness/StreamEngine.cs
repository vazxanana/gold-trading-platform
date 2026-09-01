// Streaming twin of the Smc batch engine — this is the exact algorithm the
// TradingView Pine indicator implements (Pine is bar-by-bar streaming; the
// bot recomputes in batch). StreamTests below proves the two produce the
// same structure events, swings, zones, and ultimately the same signals, so
// the Pine port is verified against the bot's ground-truth engine before it
// ever reaches TradingView.

using System;
using System.Collections.Generic;
using System.Linq;
using cAlgo.Robots;

internal class StreamEngine
{
    private readonly int _s;

    public class SBar { public double O, H, L, C; }
    public readonly List<SBar> Bars = new List<SBar>();

    private class Lvl { public int Idx; public double Price; public bool Swept; }
    private class SIdm { public int Idx; public double Price; public bool IsHigh; }

    public class Ev { public int I1, I2; public double Price; public bool Up; public string Type; }
    public readonly List<Ev> Events = new List<Ev>();      // full history, all types
    public readonly List<Ev> BarEvents = new List<Ev>();   // events fired on the current bar

    public class Sw { public int Idx; public double Price; public bool IsHigh; public bool Swept; }
    public readonly List<Sw> Swings = new List<Sw>();

    public class Zn
    {
        public int OriginIdx, CreatedBar;
        public double Hi, Lo;                // un-padded order-block bounds
        public bool Bull, Star;
        public bool Invalidated, Left, Touched;
    }
    public readonly List<Zn> ZonesRaw = new List<Zn>();

    public string Trend;
    private Lvl _hi, _lo;                    // same roles as the batch engine
    private SIdm _idm;

    public double? Protected => Trend == "bull" ? _lo?.Price : Trend == "bear" ? _hi?.Price : null;
    public int? ProtectedIdx => Trend == "bull" ? _lo?.Idx : Trend == "bear" ? _hi?.Idx : null;

    public StreamEngine(int swingStrength) { _s = swingStrength; }

    public void OnBar(double o, double h, double l, double c)
    {
        Bars.Add(new SBar { O = o, H = h, L = l, C = c });
        BarEvents.Clear();
        int i = Bars.Count - 1;

        // sweep flags for existing swings (fractal right-side condition
        // guarantees the confirmation-window bars can never sweep their own
        // pivot, so updating before today's fractal insert loses nothing)
        foreach (var sw in Swings)
            if (!sw.Swept && (sw.IsHigh ? h > sw.Price : l < sw.Price)) sw.Swept = true;

        if (i >= _s * 2)
        {
            int j = i - _s;
            var cj = Bars[j];
            bool isH = true, isL = true;
            for (int k = 1; k <= _s; k++)
            {
                if (isH && !(cj.H > Bars[j - k].H && cj.H >= Bars[j + k].H)) isH = false;
                if (isL && !(cj.L < Bars[j - k].L && cj.L <= Bars[j + k].L)) isL = false;
                if (!isH && !isL) break;
            }

            if (isH)
            {
                PushSwing(j, cj.H, true);
                if (Trend == "bull") { if (_hi == null || cj.H > _hi.Price) _hi = new Lvl { Idx = j, Price = cj.H }; }
                else if (Trend == "bear") { if (_hi != null && cj.H < _hi.Price) _idm = new SIdm { Idx = j, Price = cj.H, IsHigh = true }; }
                else _hi = new Lvl { Idx = j, Price = cj.H };
            }
            if (isL)
            {
                PushSwing(j, cj.L, false);
                if (Trend == "bear") { if (_lo == null || cj.L < _lo.Price) _lo = new Lvl { Idx = j, Price = cj.L }; }
                else if (Trend == "bull") { if (_lo != null && cj.L > _lo.Price) _idm = new SIdm { Idx = j, Price = cj.L, IsHigh = false }; }
                else _lo = new Lvl { Idx = j, Price = cj.L };
            }

            var bar = Bars[i];
            if (_hi != null)
            {
                if (bar.C > _hi.Price)
                {
                    Emit(new Ev { I1 = _hi.Idx, I2 = i, Price = _hi.Price, Up = true, Type = Trend == "bull" ? "bos" : "ChoCh" });
                    int org = LegOrigin(_hi.Idx, i, true);
                    AddZone(org, true, Trend != "bull", i);
                    _lo = new Lvl { Idx = org, Price = Bars[org].L };
                    Trend = "bull"; _hi = null; _idm = null;
                }
                else if (bar.H > _hi.Price && !_hi.Swept)
                {
                    Emit(new Ev { I1 = _hi.Idx, I2 = i, Price = _hi.Price, Up = true, Type = "sweep" });
                    _hi.Swept = true;
                }
            }
            if (_lo != null)
            {
                if (bar.C < _lo.Price)
                {
                    Emit(new Ev { I1 = _lo.Idx, I2 = i, Price = _lo.Price, Up = false, Type = Trend == "bear" ? "bos" : "ChoCh" });
                    int org = LegOrigin(_lo.Idx, i, false);
                    AddZone(org, false, Trend != "bear", i);
                    _hi = new Lvl { Idx = org, Price = Bars[org].H };
                    Trend = "bear"; _lo = null; _idm = null;
                }
                else if (bar.L < _lo.Price && !_lo.Swept)
                {
                    Emit(new Ev { I1 = _lo.Idx, I2 = i, Price = _lo.Price, Up = false, Type = "sweep" });
                    _lo.Swept = true;
                }
            }
            if (_idm != null && (_idm.IsHigh ? Bars[i].H > _idm.Price : Bars[i].L < _idm.Price))
            {
                Emit(new Ev { I1 = _idm.Idx, I2 = i, Price = _idm.Price, Up = _idm.IsHigh, Type = "idm" });
                _idm = null;
            }
        }

        // zone lifecycle flags for this bar (zones created this bar already
        // replayed through it inside AddZone)
        foreach (var z in ZonesRaw)
        {
            if (z.Invalidated || z.CreatedBar == i) continue;
            StepZone(z, Bars[i]);
        }
    }

    private void Emit(Ev e) { Events.Add(e); BarEvents.Add(e); }

    private void PushSwing(int idx, double price, bool isHigh)
    {
        var last = Swings.Count > 0 ? Swings[Swings.Count - 1] : null;
        if (last != null && last.IsHigh == isHigh)
        {
            bool newWins = isHigh ? price >= last.Price : price <= last.Price;
            if (!newWins) return;
            Swings.RemoveAt(Swings.Count - 1);
        }
        Swings.Add(new Sw { Idx = idx, Price = price, IsHigh = isHigh });
    }

    private int LegOrigin(int fromIdx, int toIdx, bool wantLow)
    {
        int b = Math.Min(fromIdx + 1, toIdx);
        for (int k = fromIdx + 1; k <= toIdx; k++)
        {
            bool better = wantLow ? Bars[k].L < Bars[b].L : Bars[k].H > Bars[b].H;
            if (better) b = k;
        }
        return b;
    }

    private void AddZone(int oIdx, bool demand, bool star, int nowBar)
    {
        int ob = oIdx;
        for (int k = oIdx; k > oIdx - 3 && k >= 0; k--)
        {
            bool bearish = Bars[k].C < Bars[k].O;
            if (demand ? bearish : !bearish) { ob = k; break; }
        }
        var c = Bars[ob];
        var z = new Zn
        {
            OriginIdx = ob,
            CreatedBar = nowBar,
            Bull = demand,
            Star = star,
            Hi = demand ? Math.Max(c.O, c.C) : c.H,
            Lo = demand ? c.L : Math.Min(c.O, c.C)
        };
        // replay lifecycle from the bar after the origin through the current
        // bar — identical to the batch engine's freshness scan
        for (int k = ob + 1; k <= nowBar && !z.Invalidated; k++) StepZone(z, Bars[k]);
        ZonesRaw.Add(z);
    }

    private static void StepZone(Zn z, SBar c)
    {
        if (z.Bull ? c.C < z.Lo : c.C > z.Hi) { z.Invalidated = true; return; }
        if (!z.Left) { if (z.Bull ? c.L > z.Hi : c.H < z.Lo) z.Left = true; return; }
        if (z.Bull ? c.L <= z.Hi : c.H >= z.Lo) z.Touched = true;
    }

    // Read-time zone pipeline — pad dojis with the CURRENT 14-bar mean TR,
    // dedupe newest-first, cap per side: byte-for-byte the batch pipeline.
    public class ZView { public int OriginAbs; public double Hi, Lo; public bool Bull, Star, Fresh; }

    public List<ZView> ZonesView(int maxPerSide)
    {
        double atr = 0;
        int a0 = Math.Max(1, Bars.Count - 14);
        for (int k = a0; k < Bars.Count; k++)
        {
            atr += Math.Max(Bars[k].H - Bars[k].L,
                Math.Max(Math.Abs(Bars[k].H - Bars[k - 1].C), Math.Abs(Bars[k].L - Bars[k - 1].C)));
        }
        atr /= Math.Max(1, Bars.Count - a0);
        double minH = 0.3 * atr;

        var live = new List<ZView>();
        foreach (var z in ZonesRaw)
        {
            if (z.Invalidated) continue;
            var v = new ZView { OriginAbs = z.OriginIdx, Hi = z.Hi, Lo = z.Lo, Bull = z.Bull, Star = z.Star, Fresh = !z.Touched };
            if (v.Hi - v.Lo < minH) { if (v.Bull) v.Hi = v.Lo + minH; else v.Lo = v.Hi - minH; }
            live.Add(v);
        }

        Func<List<ZView>, List<ZView>> dedupe = arr =>
        {
            var kept = new List<ZView>();
            for (int i = arr.Count - 1; i >= 0; i--)
            {
                var z = arr[i];
                bool clash = kept.Any(k2 =>
                    Math.Min(k2.Hi, z.Hi) - Math.Max(k2.Lo, z.Lo) > 0.5 * Math.Min(k2.Hi - k2.Lo, z.Hi - z.Lo));
                if (!clash) kept.Insert(0, z);
            }
            return kept;
        };
        var bull = dedupe(live.Where(z => z.Bull).ToList());
        var bear = dedupe(live.Where(z => !z.Bull).ToList());
        return bull.Skip(Math.Max(0, bull.Count - maxPerSide))
            .Concat(bear.Skip(Math.Max(0, bear.Count - maxPerSide))).ToList();
    }
}
