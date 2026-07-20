import pandas as pd, numpy as np
from structcmp import load_btc, load_eur, resample, pivots

def run_events(df, k, method, N=20):
    H, L, C = df['high'].values, df['low'].values, df['close'].values
    srcH, srcL = (H, L) if method == 'wick' else (C, C)
    n = len(C)
    ph, pl = pivots(srcH, srcL, k)
    lvlH = np.nan; lvlL = np.nan
    sweeps, bos = [], []          # (t, fade_dir) / (t, break_dir)
    for t in range(n):
        j = t - k
        if j >= 0:
            if ph[j]: lvlH = srcH[j]
            if pl[j]: lvlL = srcL[j]
        if not np.isnan(lvlH):
            if C[t] > lvlH:
                bos.append((t, 1)); lvlH = np.nan
            elif H[t] > lvlH:
                sweeps.append((t, -1)); lvlH = np.nan   # swept high -> fade short
        if not np.isnan(lvlL):
            if C[t] < lvlL:
                bos.append((t, -1)); lvlL = np.nan
            elif L[t] < lvlL:
                sweeps.append((t, 1)); lvlL = np.nan    # swept low -> fade long
    def stats(evs):
        rs = [d * np.log(C[t + N] / C[t]) * 1e4 for (t, d) in evs if t + N < n]
        if not rs: return (0, np.nan, np.nan, np.nan)
        rs = np.array(rs)
        tstat = rs.mean() / (rs.std() / np.sqrt(len(rs))) if rs.std() > 0 else 0
        return (len(rs), rs.mean(), (rs > 0).mean() * 100, tstat)
    return stats(sweeps), stats(bos)

btc = load_btc(); eur = load_eur()
sets = {'BTC-H1': btc, 'BTC-H4': resample(btc, '4h'), 'BTC-D1': resample(btc, '1D'),
        'EUR-H1': eur, 'EUR-H4': resample(eur, '4h')}
rows = []
for name, df in sets.items():
    for k in (3, 5):
        for method in ('wick', 'close'):
            (sn, smean, swin, st), (bn, bmean, bwin, bt) = run_events(df, k, method)
            rows.append(dict(mkt=name, k=k, method=method,
                             sweep_n=sn, sweep_bp=smean, sweep_win=swin, sweep_t=st,
                             bos_n=bn, bos_bp=bmean, bos_win=bwin, bos_t=bt))
r = pd.DataFrame(rows)
pd.set_option('display.width', 220); pd.set_option('display.max_columns', 30)
print("SWEEP-FADE (enter against the wick-take, 20-bar horizon) and BOS follow-through:")
print(r.round(2).to_string(index=False))
print()
print("mean by method:")
print(r.groupby('method')[['sweep_bp', 'sweep_win', 'sweep_t', 'bos_bp', 'bos_win', 'bos_t']].mean().round(2).to_string())
