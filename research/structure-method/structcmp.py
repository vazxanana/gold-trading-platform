import pandas as pd, numpy as np

def load_btc():
    df = pd.read_csv('btc_h1.csv', parse_dates=['date']).set_index('date')
    return df[['open','high','low','close']].astype(float)

def load_eur():
    df = pd.read_csv('eurusd_h1.csv')
    df['Time'] = pd.to_datetime(df['Time'], format='%d.%m.%Y %H:%M:%S.%f')
    df = df.set_index('Time')[['Open','High','Low','Close']]
    df.columns = ['open','high','low','close']
    return df.astype(float)

def resample(df, rule):
    out = pd.DataFrame({
        'open': df['open'].resample(rule).first(),
        'high': df['high'].resample(rule).max(),
        'low':  df['low'].resample(rule).min(),
        'close':df['close'].resample(rule).last()}).dropna()
    return out

def pivots(srcH, srcL, k):
    n = len(srcH)
    ph = np.zeros(n, bool); pl = np.zeros(n, bool)
    for i in range(k, n-k):
        wH = np.concatenate([srcH[i-k:i], srcH[i+1:i+k+1]])
        if srcH[i] > wH.max(): ph[i] = True
        wL = np.concatenate([srcL[i-k:i], srcL[i+1:i+k+1]])
        if srcL[i] < wL.min(): pl[i] = True
    return ph, pl

def run_struct(df, k, method):
    H, L, C = df['high'].values, df['low'].values, df['close'].values
    srcH, srcL = (H, L) if method == 'wick' else (C, C)
    n = len(C)
    ph, pl = pivots(srcH, srcL, k)
    trend = np.zeros(n, int); events = []
    lvlH = np.nan; lvlL = np.nan; tr = 0
    for t in range(n):
        j = t - k                       # pivot at j confirmed on bar t
        if j >= 0:
            if ph[j]: lvlH = srcH[j]
            if pl[j]: lvlL = srcL[j]
        if not np.isnan(lvlH) and C[t] > lvlH:
            events.append((t, 1, 'ChoCh' if tr == -1 else 'BOS'))
            tr = 1; lvlH = np.nan
        if not np.isnan(lvlL) and C[t] < lvlL:
            events.append((t, -1, 'ChoCh' if tr == 1 else 'BOS'))
            tr = -1; lvlL = np.nan
        trend[t] = tr
    return trend, events

def metrics(df, trend, events, bpy, cost_bp, N=20):
    C = df['close'].values; n = len(C)
    lr = np.zeros(n); lr[1:] = np.diff(np.log(C))
    pos = np.zeros(n); pos[1:] = trend[:-1]
    turn = np.abs(np.diff(pos, prepend=0.0))
    strat = pos * lr - (cost_bp / 1e4) * turn
    def sharpe(x):
        return float(np.mean(x) / np.std(x) * np.sqrt(bpy)) if np.std(x) > 0 else 0.0
    eq = np.cumsum(strat)
    dd = float(np.max(np.maximum.accumulate(eq) - eq)) * 100
    ret = float(eq[-1]) * 100
    flips = int(np.sum(np.abs(np.diff(trend)) > 0))
    # regime predictiveness
    fwd = np.full(n, np.nan); fwd[:-N] = np.log(C[N:] / C[:-N])
    m = (trend != 0) & ~np.isnan(fwd)
    agree = float(np.mean(np.sign(fwd[m]) == trend[m])) * 100 if m.any() else np.nan
    edge = float(np.mean(trend[m] * fwd[m]) / N * 1e4) if m.any() else np.nan
    # event follow-through (20 bars, in break direction)
    ft, cf = [], []
    for (t, d, kind) in events:
        if t + N < n:
            r = d * np.log(C[t + N] / C[t]) * 1e4
            (cf if kind == 'ChoCh' else ft).append(r)
    h1, h2 = len(strat) // 2, len(strat)
    return dict(sharpe=sharpe(strat), ret=ret, dd=dd, flips_k=flips / n * 1000,
                agree=agree, edge_bp=edge,
                bos_ft=float(np.mean(ft)) if ft else np.nan,
                bos_win=float(np.mean(np.array(ft) > 0)) * 100 if ft else np.nan,
                cho_ft=float(np.mean(cf)) if cf else np.nan,
                cho_win=float(np.mean(np.array(cf) > 0)) * 100 if cf else np.nan,
                sh1=sharpe(strat[:h1]), sh2=sharpe(strat[h1:]), nev=len(events))

btc = load_btc(); eur = load_eur()
sets = {
    'BTC-H1':  (btc, 8760, 2.0), 'BTC-H4': (resample(btc, '4h'), 2190, 2.0),
    'BTC-D1':  (resample(btc, '1D'), 365, 2.0),
    'EUR-H1':  (eur, 6240, 1.0), 'EUR-H4': (resample(eur, '4h'), 1560, 1.0),
}
rows = []
for name, (df, bpy, cost) in sets.items():
    for k in (2, 3, 4, 5):
        for method in ('wick', 'close'):
            tr, ev = run_struct(df, k, method)
            m = metrics(df, tr, ev, bpy, cost)
            rows.append(dict(mkt=name, k=k, method=method, **m))
res = pd.DataFrame(rows)
pd.set_option('display.width', 250); pd.set_option('display.max_columns', 30)
print(res.round(2).to_string(index=False))
res.to_csv('structcmp_results.csv', index=False)

# head-to-head: same market+k, wick vs close
piv = res.pivot_table(index=['mkt', 'k'], columns='method',
                      values=['sharpe', 'ret', 'dd', 'flips_k', 'agree', 'edge_bp', 'bos_win', 'cho_win', 'sh1', 'sh2'])
print()
wins = {}
for metric, better_high in [('sharpe', True), ('agree', True), ('edge_bp', True), ('bos_win', True), ('cho_win', True), ('dd', False), ('flips_k', False)]:
    w = piv[metric]['wick'] > piv[metric]['close'] if better_high else piv[metric]['wick'] < piv[metric]['close']
    wins[metric] = (int(w.sum()), len(w))
print("wick wins (out of 20 market/strength combos):")
for k, (w, tot) in wins.items():
    print(f"  {k:>8}: {w}/{tot}")
print()
print("mean by method:")
print(res.groupby('method')[['sharpe', 'agree', 'edge_bp', 'bos_win', 'cho_win', 'flips_k', 'dd', 'sh1', 'sh2']].mean().round(2).to_string())
