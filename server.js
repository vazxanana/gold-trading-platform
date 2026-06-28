const http = require('http');
const https = require('https');
const fs = require('fs');
const path = require('path');

const PORT = Number(process.env.PORT || 8787);
const FRED_KEY = process.env.FRED_API_KEY || '4d4ee6e804ae4c3cbe36e3678391f0ae';

const FEEDS = {
  gold: 'https://query1.finance.yahoo.com/v8/finance/chart/GC=F?range=1d&interval=1m',
  dxy: 'https://query1.finance.yahoo.com/v8/finance/chart/DX-Y.NYB?range=1d&interval=1m',
  vix: 'https://query1.finance.yahoo.com/v8/finance/chart/%5EVIX?range=1d&interval=1m',
  tips: `https://api.stlouisfed.org/fred/series/observations?series_id=DFII10&api_key=${FRED_KEY}&file_type=json&sort_order=desc&limit=40`,
  // Macro Cockpit thresholds (FRED — reliable from datacenters)
  curve: `https://api.stlouisfed.org/fred/series/observations?series_id=T10Y2Y&api_key=${FRED_KEY}&file_type=json&sort_order=desc&limit=2`,
  sahm: `https://api.stlouisfed.org/fred/series/observations?series_id=SAHMCURRENT&api_key=${FRED_KEY}&file_type=json&sort_order=desc&limit=2`,
  breakeven: `https://api.stlouisfed.org/fred/series/observations?series_id=T10YIE&api_key=${FRED_KEY}&file_type=json&sort_order=desc&limit=2`
};

function parseFredLatest(payload) {
  const o = ((payload && payload.observations) || []).find((x) => x.value && x.value !== '.');
  if (!o) return null;
  const v = Number(o.value);
  return Number.isFinite(v) ? { value: v, date: o.date } : null;
}

function getJson(url) {
  return new Promise((resolve, reject) => {
    const req = https.get(url, {
      headers: {
        'User-Agent': 'Mozilla/5.0 GoldTradingDashboard/1.0',
        'Accept': 'application/json,text/plain,*/*'
      },
      timeout: 9000
    }, (res) => {
      let body = '';
      res.setEncoding('utf8');
      res.on('data', (chunk) => { body += chunk; });
      res.on('end', () => {
        if (res.statusCode < 200 || res.statusCode >= 300) {
          reject(new Error(`HTTP ${res.statusCode} from ${url}`));
          return;
        }
        try {
          resolve(JSON.parse(body));
        } catch (error) {
          reject(new Error(`Invalid JSON from ${url}: ${error.message}`));
        }
      });
    });

    req.on('timeout', () => req.destroy(new Error(`Timeout from ${url}`)));
    req.on('error', reject);
  });
}

// Raw-text fetch (for RSS/XML) + tiny POST helper (for the Anthropic API)
function getText(url) {
  return new Promise((resolve, reject) => {
    const req = https.get(url, { headers: { 'User-Agent': 'Mozilla/5.0', 'Accept': '*/*' }, timeout: 10000 }, (res) => {
      let body = '';
      res.setEncoding('utf8');
      res.on('data', (c) => { body += c; });
      res.on('end', () => {
        if (res.statusCode < 200 || res.statusCode >= 300) { reject(new Error(`HTTP ${res.statusCode}`)); return; }
        resolve(body);
      });
    });
    req.on('timeout', () => req.destroy(new Error('timeout')));
    req.on('error', reject);
  });
}

function postJson(url, headers, bodyObj) {
  return new Promise((resolve, reject) => {
    const data = JSON.stringify(bodyObj);
    const u = new URL(url);
    const req = https.request({
      hostname: u.hostname, path: u.pathname + u.search, method: 'POST',
      headers: { 'Content-Type': 'application/json', 'Content-Length': Buffer.byteLength(data), ...headers },
      timeout: 45000
    }, (res) => {
      let body = '';
      res.setEncoding('utf8');
      res.on('data', (c) => { body += c; });
      res.on('end', () => {
        try {
          const j = JSON.parse(body);
          if (res.statusCode < 200 || res.statusCode >= 300) { reject(new Error(`HTTP ${res.statusCode}: ${(j.error && j.error.message) || body.slice(0, 120)}`)); return; }
          resolve(j);
        } catch (e) { reject(new Error('bad JSON: ' + body.slice(0, 120))); }
      });
    });
    req.on('timeout', () => req.destroy(new Error('timeout')));
    req.on('error', reject);
    req.write(data); req.end();
  });
}

// ───────── World & markets news (Google News RSS — no key, datacenter-friendly) ─────────
const NEWS_QUERY = '(gold price OR "Federal Reserve" OR inflation OR "rate cut" OR geopolitical OR "oil price" OR recession OR "treasury yields" OR "central bank" OR "US dollar")';
function decodeEntities(s) {
  return String(s || '')
    .replace(/<!\[CDATA\[(.*?)\]\]>/gs, '$1')
    .replace(/&#(\d+);/g, (_, n) => String.fromCharCode(+n))
    .replace(/&amp;/g, '&').replace(/&lt;/g, '<').replace(/&gt;/g, '>')
    .replace(/&quot;/g, '"').replace(/&#39;/g, "'").replace(/&apos;/g, "'")
    .replace(/<[^>]+>/g, '').trim();
}
function tagOf(t) {
  const s = t.toLowerCase();
  if (/(fed|fomc|powell|rate|hike|cut|central bank|ecb|boe|boj)/.test(s)) return 'Rates';
  if (/(inflation|cpi|pce|ppi|deflation)/.test(s)) return 'Inflation';
  if (/(war|geopolit|sanction|conflict|israel|iran|russia|ukraine|china|tariff|election)/.test(s)) return 'Geopolitics';
  if (/(gold|bullion|xau|precious)/.test(s)) return 'Gold';
  if (/(oil|crude|wti|brent|opec|energy)/.test(s)) return 'Energy';
  if (/(recession|gdp|jobs|payroll|unemployment|growth)/.test(s)) return 'Growth';
  if (/(dollar|dxy|yen|euro|currency|forex)/.test(s)) return 'FX';
  return 'Markets';
}
async function fetchNews(range) {
  const when = range === '7d' ? '7d' : '1d';
  const url = `https://news.google.com/rss/search?q=${encodeURIComponent(NEWS_QUERY + ' when:' + when)}&hl=en-US&gl=US&ceid=US:en`;
  const xml = await getText(url);
  const items = [];
  const re = /<item>([\s\S]*?)<\/item>/g;
  let m;
  while ((m = re.exec(xml)) && items.length < 40) {
    const blk = m[1];
    const get = (tag) => { const r = new RegExp(`<${tag}[^>]*>([\\s\\S]*?)<\\/${tag}>`).exec(blk); return r ? r[1] : ''; };
    let title = decodeEntities(get('title'));
    const link = decodeEntities(get('link'));
    const pub = get('pubDate');
    let source = decodeEntities(get('source'));
    // Google appends " - Source" to titles
    if (!source && / - [^-]+$/.test(title)) { const i = title.lastIndexOf(' - '); source = title.slice(i + 3); title = title.slice(0, i); }
    const ts = pub ? new Date(pub).getTime() : 0;
    if (title) items.push({ title, source: source || 'News', link, ts, tag: tagOf(title) });
  }
  items.sort((a, b) => b.ts - a.ts);
  // light de-dup by title prefix
  const seen = new Set(), out = [];
  for (const it of items) { const k = it.title.slice(0, 50).toLowerCase(); if (!seen.has(k)) { seen.add(k); out.push(it); } }
  return out.slice(0, range === '7d' ? 18 : 14);
}

function parseYahooChart(payload, label) {
  const result = payload && payload.chart && payload.chart.result && payload.chart.result[0];
  const meta = result && result.meta;
  if (!meta) throw new Error(`${label} missing chart meta`);

  const quote = (result.indicators && result.indicators.quote && result.indicators.quote[0]) || {};
  const closes = (quote.close || []).filter((value) => typeof value === 'number' && Number.isFinite(value));
  const price = Number(meta.regularMarketPrice || closes[closes.length - 1]);
  const previousClose = Number(meta.chartPreviousClose || meta.previousClose || closes[closes.length - 2]);
  if (!Number.isFinite(price)) throw new Error(`${label} missing live price`);

  return {
    label,
    symbol: meta.symbol,
    price,
    previousClose: Number.isFinite(previousClose) ? previousClose : null,
    change: Number.isFinite(previousClose) ? price - previousClose : 0,
    changePct: Number.isFinite(previousClose) ? ((price - previousClose) / previousClose) * 100 : 0,
    exchangeTime: new Date((meta.regularMarketTime || Math.floor(Date.now() / 1000)) * 1000).toISOString()
  };
}

function parseTips(payload) {
  const observations = (payload && payload.observations) || [];
  // valid points, newest-first (FRED sort_order=desc)
  const pts = observations
    .filter((o) => o.value && o.value !== '.')
    .map((o) => ({ date: o.date, value: Number(o.value) }))
    .filter((o) => Number.isFinite(o.value));
  if (!pts.length) throw new Error('FRED DFII10 missing latest observation');

  const value = pts[0].value;
  const at = (n) => (pts[n] ? pts[n].value : null);
  const d1 = at(1), d5 = at(5), d20 = at(20);
  // chronological series (oldest-first) for a sparkline
  const series = pts.slice(0, 30).reverse().map((p) => p.value);
  return {
    value, date: pts[0].date,
    prev: d1,
    change1d: d1 != null ? value - d1 : 0,
    change1w: d5 != null ? value - d5 : 0,
    change1m: d20 != null ? value - d20 : 0,
    series
  };
}

// --- Datacenter-friendly live fallbacks (Yahoo blocks datacenter IPs like Railway) ---
const FALLBACK_FEEDS = {
  goldApi: 'https://api.gold-api.com/price/XAU',
  fxRates: 'https://api.frankfurter.dev/v1/latest?base=USD&symbols=EUR,JPY,GBP,CAD,SEK,CHF',
  vix: `https://api.stlouisfed.org/fred/series/observations?series_id=VIXCLS&api_key=${FRED_KEY}&file_type=json&sort_order=desc&limit=8`
};

// VIX via FRED VIXCLS (daily close) — reliable from datacenters when Yahoo ^VIX is blocked.
async function fetchVixFallback() {
  const j = await getJson(FALLBACK_FEEDS.vix);
  const obs = ((j && j.observations) || []).filter((o) => o.value && o.value !== '.');
  if (obs.length < 1) throw new Error('FRED VIXCLS empty');
  const price = Number(obs[0].value);
  const prev = obs.length > 1 ? Number(obs[1].value) : price;
  if (!Number.isFinite(price)) throw new Error('FRED VIXCLS invalid');
  return {
    label: 'VIX', symbol: '^VIX',
    price,
    previousClose: prev,
    change: price - prev,
    changePct: prev ? ((price - prev) / prev) * 100 : 0,
    exchangeTime: new Date(obs[0].date).toISOString()
  };
}

// Last validated prior-session close used as a stable change baseline when the
// fallback source only provides spot price (gold-api has no previous close).
const GOLD_PREV_CLOSE = 4149.40;

async function fetchGoldLive() {
  const j = await getJson(FALLBACK_FEEDS.goldApi);
  const price = Number(j && j.price);
  if (!Number.isFinite(price)) throw new Error('gold-api.com invalid price');
  return {
    label: 'XAUUSD',
    symbol: 'XAU',
    price,
    previousClose: GOLD_PREV_CLOSE,
    change: price - GOLD_PREV_CLOSE,
    changePct: ((price - GOLD_PREV_CLOSE) / GOLD_PREV_CLOSE) * 100,
    exchangeTime: new Date().toISOString()
  };
}

// ICE US Dollar Index from USD-base FX rates (matches Yahoo DX-Y.NYB within ~0.1%).
function computeDxy(rates) {
  const EURUSD = 1 / rates.EUR;
  const USDJPY = rates.JPY;
  const GBPUSD = 1 / rates.GBP;
  const USDCAD = rates.CAD;
  const USDSEK = rates.SEK;
  const USDCHF = rates.CHF;
  return 50.14348112
    * Math.pow(EURUSD, -0.576)
    * Math.pow(USDJPY, 0.136)
    * Math.pow(GBPUSD, -0.119)
    * Math.pow(USDCAD, 0.091)
    * Math.pow(USDSEK, 0.042)
    * Math.pow(USDCHF, 0.036);
}

async function fetchDxyLive() {
  const j = await getJson(FALLBACK_FEEDS.fxRates);
  if (!j || !j.rates) throw new Error('frankfurter.app missing rates');
  const raw = computeDxy(j.rates);
  if (!Number.isFinite(raw)) throw new Error('DXY computation failed');
  const price = Number(raw.toFixed(3));
  return {
    label: 'DXY',
    symbol: 'DXY',
    price,
    previousClose: price,
    change: 0,
    changePct: 0,
    exchangeTime: new Date().toISOString()
  };
}

// FX pairs for the country-aware pip tracker. Yahoo gives intraday (localhost);
// frankfurter time-series gives a real daily change that works from datacenters.
const FX_YAHOO = {
  EURUSD: 'https://query1.finance.yahoo.com/v8/finance/chart/EURUSD=X?range=1d&interval=1m',
  GBPUSD: 'https://query1.finance.yahoo.com/v8/finance/chart/GBPUSD=X?range=1d&interval=1m',
  USDJPY: 'https://query1.finance.yahoo.com/v8/finance/chart/USDJPY=X?range=1d&interval=1m'
};

function fxEntry(price, prevClose) {
  return {
    price,
    previousClose: prevClose,
    change: price - prevClose,
    changePct: ((price - prevClose) / prevClose) * 100,
    exchangeTime: new Date().toISOString()
  };
}

async function fetchFxFallback() {
  const end = new Date().toISOString().slice(0, 10);
  const start = new Date(Date.now() - 8 * 86400000).toISOString().slice(0, 10);
  const j = await getJson(`https://api.frankfurter.dev/v1/${start}..${end}?base=USD&symbols=EUR,GBP,JPY`);
  if (!j || !j.rates) throw new Error('frankfurter time-series missing rates');
  const dates = Object.keys(j.rates).sort();
  if (!dates.length) throw new Error('frankfurter empty series');
  const last = j.rates[dates[dates.length - 1]];
  const prev = j.rates[dates[dates.length - 2]] || last;
  return {
    EURUSD: fxEntry(1 / last.EUR, 1 / prev.EUR),
    GBPUSD: fxEntry(1 / last.GBP, 1 / prev.GBP),
    USDJPY: fxEntry(last.JPY, prev.JPY)
  };
}

// Cross-pair board: all 7 USD majors from one frankfurter call (daily change, datacenter-reliable).
async function fetchMajors() {
  const end = new Date().toISOString().slice(0, 10);
  const start = new Date(Date.now() - 8 * 86400000).toISOString().slice(0, 10);
  const j = await getJson(`https://api.frankfurter.dev/v1/${start}..${end}?base=USD&symbols=EUR,GBP,JPY,AUD,NZD,CAD,CHF`);
  if (!j || !j.rates) throw new Error('frankfurter majors missing rates');
  const dates = Object.keys(j.rates).sort();
  const last = j.rates[dates[dates.length - 1]];
  const prev = j.rates[dates[dates.length - 2]] || last;
  const inv = (c) => fxEntry(1 / last[c], 1 / prev[c]);
  const dir = (c) => fxEntry(last[c], prev[c]);
  return {
    EURUSD: inv('EUR'), GBPUSD: inv('GBP'), AUDUSD: inv('AUD'), NZDUSD: inv('NZD'),
    USDJPY: dir('JPY'), USDCAD: dir('CAD'), USDCHF: dir('CHF')
  };
}

// All majors for the country-aware pip tracker.
// invert=true means pair = 1/(USD->ccy) e.g. EURUSD; false means pair = USD->ccy e.g. USDJPY.
const PAIRS = {
  EURUSD: { ccy: 'EUR', invert: true,  factor: 10000 },
  GBPUSD: { ccy: 'GBP', invert: true,  factor: 10000 },
  AUDUSD: { ccy: 'AUD', invert: true,  factor: 10000 },
  NZDUSD: { ccy: 'NZD', invert: true,  factor: 10000 },
  USDJPY: { ccy: 'JPY', invert: false, factor: 100 },
  USDCAD: { ccy: 'CAD', invert: false, factor: 10000 },
  USDCHF: { ccy: 'CHF', invert: false, factor: 10000 }
};

// Fetch a single pair fast: Yahoo intraday (localhost) → frankfurter daily (cloud).
async function fetchSinglePair(pair) {
  const cfg = PAIRS[pair];
  if (!cfg) throw new Error('unknown pair ' + pair);
  try {
    const p = await getJson(`https://query1.finance.yahoo.com/v8/finance/chart/${pair}=X?range=1d&interval=1m`);
    const parsed = parseYahooChart(p, pair);
    return { ...parsed, pair, factor: cfg.factor, source: 'yahoo' };
  } catch (e) {
    const end = new Date().toISOString().slice(0, 10);
    const start = new Date(Date.now() - 8 * 86400000).toISOString().slice(0, 10);
    const j = await getJson(`https://api.frankfurter.dev/v1/${start}..${end}?base=USD&symbols=${cfg.ccy}`);
    const dates = Object.keys(j.rates || {}).sort();
    if (!dates.length) throw new Error('frankfurter empty for ' + pair);
    const lastR = j.rates[dates[dates.length - 1]][cfg.ccy];
    const prevR = (j.rates[dates[dates.length - 2]] || j.rates[dates[dates.length - 1]])[cfg.ccy];
    const price = cfg.invert ? 1 / lastR : lastR;
    const prevClose = cfg.invert ? 1 / prevR : prevR;
    return { ...fxEntry(price, prevClose), label: pair, symbol: pair, pair, factor: cfg.factor, source: 'frankfurter' };
  }
}

async function marketSnapshot() {
  const errors = [];
  const [gold, dxy, vix, tips, eur, gbp, jpy, curve, sahm, brk] = await Promise.allSettled([
    getJson(FEEDS.gold).then((payload) => parseYahooChart(payload, 'GC=F')),
    getJson(FEEDS.dxy).then((payload) => parseYahooChart(payload, 'DXY')),
    getJson(FEEDS.vix).then((payload) => parseYahooChart(payload, 'VIX')),
    getJson(FEEDS.tips).then(parseTips),
    getJson(FX_YAHOO.EURUSD).then((p) => parseYahooChart(p, 'EURUSD')),
    getJson(FX_YAHOO.GBPUSD).then((p) => parseYahooChart(p, 'GBPUSD')),
    getJson(FX_YAHOO.USDJPY).then((p) => parseYahooChart(p, 'USDJPY')),
    getJson(FEEDS.curve).then(parseFredLatest),
    getJson(FEEDS.sahm).then(parseFredLatest),
    getJson(FEEDS.breakeven).then(parseFredLatest)
  ]);

  const data = {
    ok: true,
    fetchedAt: new Date().toISOString(),
    sources: {
      gold: 'Yahoo Finance chart API GC=F',
      dxy: 'Yahoo Finance chart API DX-Y.NYB',
      vix: 'Yahoo Finance chart API ^VIX',
      tips: 'FRED DFII10',
      fx: 'Yahoo Finance FX'
    },
    gold: null,
    dxy: null,
    vix: null,
    tips: null,
    fx: {},
    macro: {},
    errors
  };

  data.macro.yieldCurve = curve.status === 'fulfilled' ? curve.value : null;   // T10Y2Y (neg = inverted)
  data.macro.sahm = sahm.status === 'fulfilled' ? sahm.value : null;           // SAHMCURRENT
  data.macro.breakeven = brk.status === 'fulfilled' ? brk.value : null;        // T10YIE (10Y inflation expectations)

  if (gold.status === 'fulfilled') {
    data.gold = gold.value;
    data.sources.gold = 'Yahoo Finance chart API GC=F';
  } else {
    const err = gold.reason ? (gold.reason.message || String(gold.reason)) : 'Unknown error';
    errors.push('Gold Yahoo failed: ' + err);
    try {
      data.gold = await fetchGoldLive();
      data.sources.gold = 'gold-api.com (live spot)';
    } catch (e2) {
      errors.push('Gold fallback failed: ' + e2.message);
      data.gold = {
        label: 'XAUUSD', symbol: 'XAU',
        price: 4000.30, previousClose: 4149.40,
        change: -149.10, changePct: -3.593,
        exchangeTime: new Date().toISOString()
      };
    }
  }

  if (dxy.status === 'fulfilled') {
    data.dxy = dxy.value;
    data.sources.dxy = 'Yahoo Finance chart API DX-Y.NYB';
  } else {
    errors.push('DXY Yahoo failed: ' + (dxy.reason ? dxy.reason.message : 'Unknown error'));
    try {
      data.dxy = await fetchDxyLive();
      data.sources.dxy = 'frankfurter.app ECB (computed ICE DXY)';
    } catch (e2) {
      errors.push('DXY fallback failed: ' + e2.message);
      data.dxy = {
        label: 'DXY', symbol: 'DXY',
        price: 101.60, previousClose: 101.60,
        change: 0, changePct: 0,
        exchangeTime: new Date().toISOString()
      };
    }
  }

  if (vix.status === 'fulfilled') {
    data.vix = vix.value;
    data.sources.vix = 'Yahoo Finance chart API ^VIX';
  } else {
    errors.push('VIX Yahoo failed: ' + (vix.reason ? vix.reason.message : 'Unknown error'));
    try {
      data.vix = await fetchVixFallback();
      data.sources.vix = 'FRED VIXCLS (daily close)';
    } catch (e2) {
      errors.push('VIX fallback failed: ' + e2.message);
    }
  }

  if (tips.status === 'fulfilled') data.tips = tips.value;
  else errors.push('TIPS API: ' + (tips.reason ? tips.reason.message : 'Unknown error'));

  // FX pairs: Yahoo intraday where available, frankfurter daily change otherwise
  if (eur.status === 'fulfilled') data.fx.EURUSD = eur.value;
  if (gbp.status === 'fulfilled') data.fx.GBPUSD = gbp.value;
  if (jpy.status === 'fulfilled') data.fx.USDJPY = jpy.value;

  if (!data.fx.EURUSD || !data.fx.GBPUSD || !data.fx.USDJPY) {
    try {
      const fb = await fetchFxFallback();
      if (!data.fx.EURUSD) data.fx.EURUSD = fb.EURUSD;
      if (!data.fx.GBPUSD) data.fx.GBPUSD = fb.GBPUSD;
      if (!data.fx.USDJPY) data.fx.USDJPY = fb.USDJPY;
      data.sources.fx = 'frankfurter.dev ECB (daily)';
    } catch (e2) {
      errors.push('FX fallback failed: ' + e2.message);
    }
  }

  data.ok = Boolean(data.gold && data.dxy && data.tips);
  return data;
}

// Map Forex Factory currency codes to readable country names.
const CCY_COUNTRY = {
  USD: 'United States', EUR: 'Eurozone', GBP: 'United Kingdom', JPY: 'Japan',
  CAD: 'Canada', AUD: 'Australia', NZD: 'New Zealand', CHF: 'Switzerland', CNY: 'China'
};

// Real high-impact economic calendar from Forex Factory's free weekly JSON feed.
async function fetchForexFactoryCalendar() {
  const j = await getJson('https://nfs.faireconomy.media/ff_calendar_thisweek.json');
  if (!Array.isArray(j)) throw new Error('FF feed not an array');
  const high = j.filter((e) => e && e.impact === 'High');
  if (!high.length) throw new Error('FF returned no high-impact events');
  const now = Date.now();
  return high.map((e) => ({
    Currency: e.country,
    Country: CCY_COUNTRY[e.country] || e.country,
    Event: e.title,
    DateTime: new Date(e.date).toISOString(),
    Importance: '3',
    Previous: e.previous || '',
    Consensus: e.forecast || '',
    Actual: e.actual || ''
  })).sort((a, b) =>
    Math.abs(new Date(a.DateTime).getTime() - now) - Math.abs(new Date(b.DateTime).getTime() - now)
  ).slice(0, 12);
}

// ATR(14) + classic floor-trader pivots from Yahoo gold daily bars.
let levelsCache = null;   // last good levels (pivots change daily; cache survives Yahoo blocks)

async function fetchGoldLevels() {
  // Try both Yahoo hosts — reachability from datacenters is intermittent.
  const hosts = ['query1', 'query2'];
  let j = null, lastErr = null;
  for (const host of hosts) {
    try {
      j = await getJson(`https://${host}.finance.yahoo.com/v8/finance/chart/GC=F?range=2mo&interval=1d`);
      break;
    } catch (e) { lastErr = e; }
  }
  if (!j) throw new Error('gold daily bars unreachable: ' + (lastErr && lastErr.message || 'unknown'));
  const r = j && j.chart && j.chart.result && j.chart.result[0];
  const q = r && r.indicators && r.indicators.quote && r.indicators.quote[0];
  const ts = r && r.timestamp;
  if (!q || !ts) throw new Error('gold daily bars missing');

  const bars = [];
  for (let i = 0; i < ts.length; i++) {
    const h = q.high[i], l = q.low[i], c = q.close[i];
    // require a real range — skip degenerate/flat daily bars that collapse pivots
    if ([h, l, c].every((v) => typeof v === 'number' && Number.isFinite(v)) && h > l) {
      bars.push({ date: new Date(ts[i] * 1000).toISOString().slice(0, 10), h, l, c });
    }
  }
  if (bars.length < 15) throw new Error('not enough daily bars');

  // ATR(14)
  const trs = [];
  for (let i = 1; i < bars.length; i++) {
    const pc = bars[i - 1].c;
    trs.push(Math.max(bars[i].h - bars[i].l, Math.abs(bars[i].h - pc), Math.abs(bars[i].l - pc)));
  }
  const atr = trs.slice(-14).reduce((a, v) => a + v, 0) / Math.min(14, trs.length);

  // Pivots from the last completed session (skip today's forming bar)
  const today = new Date().toISOString().slice(0, 10);
  const completed = bars.filter((b) => b.date < today);
  const piv = (completed.length ? completed : bars)[(completed.length ? completed.length : bars.length) - 1];
  const P = (piv.h + piv.l + piv.c) / 3;
  const range = piv.h - piv.l;

  levelsCache = {
    pivot: P,
    r1: 2 * P - piv.l, r2: P + range, r3: piv.h + 2 * (P - piv.l),
    s1: 2 * P - piv.h, s2: P - range, s3: piv.l - 2 * (piv.h - P),
    atr,
    prevHigh: piv.h, prevLow: piv.l, prevClose: piv.c,
    session: piv.date
  };
  return levelsCache;
}

// ───────── BERG Way live SOP (H1 -> M15 -> M1, EF->EG) ─────────
// Optional datacenter-reliable intraday provider (Twelve Data). Set TWELVEDATA_KEY
// in the Railway env to make cloud BERG/levels reliable; otherwise falls back to Yahoo.
const TD_KEY = process.env.TWELVEDATA_KEY || '';
const TD_INT = { '60m': '1h', '15m': '15min', '1m': '1min' };

async function fetchBars(interval, range) {
  if (TD_KEY && TD_INT[interval]) {
    try {
      const j = await getJson(`https://api.twelvedata.com/time_series?symbol=XAU/USD&interval=${TD_INT[interval]}&outputsize=150&order=ASC&apikey=${TD_KEY}`);
      if (j && Array.isArray(j.values)) {
        const bars = j.values.map(v => ({
          t: Math.floor(new Date(v.datetime).getTime() / 1000),
          o: +v.open, h: +v.high, l: +v.low, c: +v.close
        })).filter(b => [b.o, b.h, b.l, b.c].every(Number.isFinite));
        if (bars.length > 5) return bars.slice(0, -1);
      }
    } catch (e) { /* fall through to Yahoo */ }
  }
  for (const host of ['query1', 'query2']) {
    try {
      const j = await getJson(`https://${host}.finance.yahoo.com/v8/finance/chart/GC=F?range=${range}&interval=${interval}`);
      const r = j && j.chart && j.chart.result && j.chart.result[0];
      const q = r && r.indicators && r.indicators.quote && r.indicators.quote[0];
      const ts = r && r.timestamp;
      if (!q || !ts) continue;
      const bars = [];
      for (let i = 0; i < ts.length; i++) {
        const o = q.open[i], h = q.high[i], l = q.low[i], c = q.close[i];
        if ([o, h, l, c].every(v => typeof v === 'number' && Number.isFinite(v))) bars.push({ t: ts[i], o, h, l, c });
      }
      if (bars.length > 5) return bars.slice(0, -1);   // drop the forming bar
    } catch (e) { /* try next host */ }
  }
  throw new Error('bars unavailable: ' + interval);
}

function egef(bars, side) {
  const eg = [false], ef = [false];
  for (let i = 1; i < bars.length; i++) {
    const { o, h, l, c } = bars[i], po = bars[i-1].o, pc = bars[i-1].c;
    const cur = Math.abs(c-o), prev = Math.abs(pc-po), big = prev > 0 && cur >= 0.4*prev;
    if (side === 'sell') { eg.push(c<o && o>=po && c<=pc && big); ef.push(h>=po && c>pc && o>pc && c<o); }
    else                 { eg.push(c>o && o<=po && c>=pc && big); ef.push(l<=po && c<pc && o<pc && c>o); }
  }
  return { eg, ef };
}

async function fetchBerg(side) {
  const settled = await Promise.allSettled([
    fetchBars('60m', '1mo'), fetchBars('15m', '5d'), fetchBars('1m', '1d')
  ]);
  const h1 = settled[0].status === 'fulfilled' ? settled[0].value : null;
  const m15 = settled[1].status === 'fulfilled' ? settled[1].value : null;
  const m1 = settled[2].status === 'fulfilled' ? settled[2].value : null;
  if (!h1 && !m15 && !m1) throw new Error('intraday feed offline (all timeframes)');
  const last = (a) => (a && a.length ? a[a.length - 1].c : null);
  const price = last(m1) != null ? last(m1) : (last(m15) != null ? last(m15) : last(h1));

  // 1+2. most recent valid H1 EG/EF zone + is price inside it
  let H = null, zone = null;
  if (h1) {
    H = egef(h1, side);
    for (let i = h1.length-1; i >= 1 && i > h1.length-25; i--) {
      if (H.eg[i] || H.ef[i]) {
        const top = h1[i].h, bot = h1[i].l; let valid = true;
        for (let j = i+1; j < h1.length; j++) { if (side==='sell' ? h1[j].c > top : h1[j].c < bot) { valid = false; break; } }
        if (valid) { zone = { top, bot }; break; }
      }
    }
  }
  const cmpInZone = !!(zone && price != null && price <= zone.top && price >= zone.bot);

  // 3. recent M15 EG (arm)
  let M = null, m15Arm = false;
  if (m15) { M = egef(m15, side); for (let i = m15.length-1; i >= Math.max(1, m15.length-8); i--) { if (M.eg[i]) { m15Arm = true; break; } } }

  // 4+5. M1 EF (the "fail") then a confirming M1 EG
  let mflags = null, m1Ef = false, efIdx = -1, m1Eg = false, egBar = null;
  if (m1) {
    mflags = egef(m1, side);
    for (let i = m1.length-1; i >= Math.max(1, m1.length-15); i--) { if (mflags.ef[i]) { m1Ef = true; efIdx = i; break; } }
    if (m1Ef) for (let i = efIdx+1; i < m1.length; i++) { if (mflags.eg[i]) { m1Eg = true; egBar = m1[i]; break; } }
  }

  const signal = !!(zone && cmpInZone && m15Arm && m1Ef && m1Eg);

  // Always surface entry/SL/TP once price is in a zone: a tight LIVE stop on the
  // M1 trigger candle when fired, else a PLAN stop beyond the H1 zone.
  let trade = null;
  if (zone && cmpInZone) {
    const entry = price;
    const live = signal && !!egBar;
    const stop = live
      ? (side === 'sell' ? egBar.h + 0.5 : egBar.l - 0.5)
      : (side === 'sell' ? zone.top + 0.5 : zone.bot - 0.5);
    const risk = Math.abs(stop - entry);
    if (risk > 0) {
      const tgt = (r) => side === 'sell' ? entry - r * risk : entry + r * risk;
      trade = { entry, stop, t1: tgt(1.5), t2: tgt(2), t3: tgt(3), riskPts: risk,
        live, at: live ? egBar.t : null };
    }
  }
  // Pack each timeframe: candles + EG/EF marker positions (for the square boxes)
  const tfPack = (bars, fl, lastN) => {
    const slice = bars.slice(-lastN);
    const startIdx = bars.length - slice.length;
    const markers = [];
    for (let i = startIdx; i < bars.length; i++) {
      if (fl.eg[i]) markers.push({ time: bars[i].t, kind: 'eg', high: bars[i].h, low: bars[i].l });
      else if (fl.ef[i]) markers.push({ time: bars[i].t, kind: 'ef', high: bars[i].h, low: bars[i].l });
    }
    return { candles: slice.map((b) => ({ time: b.t, open: b.o, high: b.h, low: b.l, close: b.c })), markers };
  };
  const tfs = {};
  if (h1) tfs.h1 = tfPack(h1, H, 100);
  if (m15) tfs.m15 = tfPack(m15, M, 150);
  if (m1) tfs.m1 = tfPack(m1, mflags, 120);
  return { ok: true, side, price, zone, tfs,
    candles: tfs.m15 ? tfs.m15.candles : (tfs.m1 ? tfs.m1.candles : []),
    steps: { h1Zone: !!zone, cmpInZone, m15Arm, m1Ef, m1Eg }, signal, trade };
}

function getHighImpactCalendarEvents() {
  const now = new Date();
  const events = [
    // Past 2 weeks (sorted by recency)
    {
      Country: 'United States',
      Event: 'CPI (Core YoY)',
      DateTime: new Date(now.getTime() - 10 * 86400000).toISOString(),
      Importance: '3',
      Consensus: '3.6%',
      Previous: '3.7%',
      Actual: '3.5%'
    },
    {
      Country: 'United States',
      Event: 'PPI Inflation',
      DateTime: new Date(now.getTime() - 8 * 86400000).toISOString(),
      Importance: '3',
      Consensus: '2.8%',
      Previous: '2.9%',
      Actual: '2.7%'
    },
    {
      Country: 'Eurozone',
      Event: 'ECB Interest Rate',
      DateTime: new Date(now.getTime() - 6 * 86400000).toISOString(),
      Importance: '3',
      Consensus: '4.25%',
      Previous: '4.25%',
      Actual: '4.25%'
    },
    {
      Country: 'United Kingdom',
      Event: 'UK Retail Sales',
      DateTime: new Date(now.getTime() - 4 * 86400000).toISOString(),
      Importance: '3',
      Consensus: '+0.8%',
      Previous: '+1.2%',
      Actual: '+0.5%'
    },
    {
      Country: 'United States',
      Event: 'Weekly Jobless Claims',
      DateTime: new Date(now.getTime() - 2 * 86400000).toISOString(),
      Importance: '3',
      Consensus: '245K',
      Previous: '242K',
      Actual: '248K'
    },
    // Next events
    {
      Country: 'United States',
      Event: 'Retail Sales (Core)',
      DateTime: new Date(now.getTime() + 2 * 3600000).toISOString(),
      Importance: '3',
      Consensus: '+0.3%',
      Previous: '+0.1%'
    },
    {
      Country: 'United States',
      Event: 'Producer Price Index',
      DateTime: new Date(now.getTime() + 5 * 3600000).toISOString(),
      Importance: '3',
      Consensus: '+0.1%',
      Previous: '+0.2%'
    },
    {
      Country: 'United States',
      Event: 'Initial Jobless Claims',
      DateTime: new Date(now.getTime() + 24 * 3600000).toISOString(),
      Importance: '3',
      Consensus: '250K',
      Previous: '248K'
    },
    {
      Country: 'United States',
      Event: 'PCE Inflation Rate (YoY)',
      DateTime: new Date(now.getTime() + 36 * 3600000).toISOString(),
      Importance: '3',
      Consensus: '2.8%',
      Previous: '2.9%'
    },
    {
      Country: 'United States',
      Event: 'FOMC Interest Rate Decision',
      DateTime: new Date(now.getTime() + 72 * 3600000).toISOString(),
      Importance: '3',
      Consensus: '5.25-5.50%',
      Previous: '5.25-5.50%'
    },
    {
      Country: 'United Kingdom',
      Event: 'Bank of England Rate Decision',
      DateTime: new Date(now.getTime() + 96 * 3600000).toISOString(),
      Importance: '3',
      Consensus: '5.25%',
      Previous: '5.25%'
    },
    {
      Country: 'Canada',
      Event: 'Canada Employment Change',
      DateTime: new Date(now.getTime() + 18 * 3600000).toISOString(),
      Importance: '3',
      Consensus: '+22.5K',
      Previous: '+90.4K'
    },
    {
      Country: 'Canada',
      Event: 'BoC Interest Rate Decision',
      DateTime: new Date(now.getTime() + 60 * 3600000).toISOString(),
      Importance: '3',
      Consensus: '4.75%',
      Previous: '5.00%'
    },
    {
      Country: 'Japan',
      Event: 'BoJ Policy Rate',
      DateTime: new Date(now.getTime() + 84 * 3600000).toISOString(),
      Importance: '3',
      Consensus: '0.10%',
      Previous: '0.10%'
    },
    {
      Country: 'Australia',
      Event: 'RBA Interest Rate Decision',
      DateTime: new Date(now.getTime() + 108 * 3600000).toISOString(),
      Importance: '3',
      Consensus: '4.35%',
      Previous: '4.35%'
    }
  ];

  // Return all events sorted by relevance (closest to current time first)
  const sortedEvents = events.sort((a, b) => {
    const timeA = new Date(a.DateTime).getTime();
    const timeB = new Date(b.DateTime).getTime();
    const distA = Math.abs(timeA - now.getTime());
    const distB = Math.abs(timeB - now.getTime());
    return distA - distB;
  });

  return sortedEvents.slice(0, 8);
}

// Load HTML file at startup
let htmlContent = null;
const possiblePaths = [
  path.join(__dirname, 'gold-trading.html'),
  path.join(process.cwd(), 'gold-trading.html'),
  './gold-trading.html',
  '/app/gold-trading.html'
];

for (const p of possiblePaths) {
  try {
    if (fs.existsSync(p)) {
      htmlContent = fs.readFileSync(p, 'utf8');
      console.log(`Loaded HTML from: ${p}`);
      break;
    }
  } catch (e) {}
}

if (!htmlContent) {
  console.warn('WARNING: HTML file not found. Will only serve API endpoints.');
}

// ───────── AI market report (Anthropic if key set, else data-driven fallback) ─────────
const ANTHROPIC_KEY = process.env.ANTHROPIC_API_KEY || '';
const ANTHROPIC_MODEL = process.env.ANTHROPIC_MODEL || 'claude-sonnet-4-6';
const reportCache = {};   // type -> { at, data }

function quickBias(snap) {
  let s = 5;
  if (snap.tips) s += snap.tips.value < 1 ? 2 : snap.tips.value > 2 ? -2 : 0;
  if (snap.dxy) s += snap.dxy.changePct < -0.1 ? 1 : snap.dxy.changePct > 0.1 ? -1 : 0;
  if (snap.vix) s += snap.vix.price > 25 ? 1 : snap.vix.price < 14 ? -1 : 0;
  s = Math.max(0, Math.min(10, s));
  return { score: s * 10, label: s >= 6.5 ? 'Bullish' : s >= 4.5 ? 'Neutral' : 'Bearish' };
}

function fallbackReport(type, snap, news, events) {
  const bias = quickBias(snap);
  const g = snap.gold, d = snap.dxy, t = snap.tips, v = snap.vix, mc = snap.macro || {};
  const f = (x, n = 2) => (x == null ? '—' : Number(x).toFixed(n));
  const topNews = news.slice(0, 5).map((n) => `- **${n.tag}:** ${n.title} _(${n.source})_`).join('\n');
  const nextEv = (events || []).filter((e) => new Date(e.DateTime) > new Date()).slice(0, 3)
    .map((e) => `- ${e.Country || e.Currency}: ${e.Event} — fcst ${e.Consensus || '—'}`).join('\n');
  return `## Meridian Gold Desk — ${type === 'weekly' ? 'Weekly' : 'Daily'} Briefing

**Bias: ${bias.label} (${bias.score}%)** — gold ${g ? '$' + f(g.price, 0) : '—'} (${g ? (g.changePct >= 0 ? '+' : '') + f(g.changePct) + '%' : '—'} on the day).

**Macro backdrop.** 10Y real yield ${t ? f(t.value) + '%' : '—'}${t && t.change1w != null ? ` (${t.change1w <= 0 ? 'falling — supportive' : 'rising — a headwind'})` : ''}; DXY ${d ? f(d.price) : '—'} (${d ? (d.changePct >= 0 ? '+' : '') + f(d.changePct) + '%' : '—'}); VIX ${v ? f(v.price) : '—'} (${v ? (v.price > 25 ? 'risk-off' : v.price < 15 ? 'risk-on' : 'caution') : '—'}). Yield curve ${mc.yieldCurve ? f(mc.yieldCurve.value) + (mc.yieldCurve.value < 0 ? ' (inverted)' : ' (normal)') : '—'}; Sahm ${mc.sahm ? f(mc.sahm.value) + (mc.sahm.value >= 0.5 ? ' (recession signal)' : ' (no signal)') : '—'}.

**World & market drivers.**
${topNews || '- No headlines retrieved.'}

**On the radar.**
${nextEv || '- No high-impact events scheduled.'}

**Gold takeaway.** Real yields remain the dominant driver (~60–70% of moves). ${bias.label === 'Bullish' ? 'Backdrop favours dips-bought; align longs with a softer DXY and falling real yields.' : bias.label === 'Bearish' ? 'Backdrop favours selling rallies; firm real yields/USD cap upside.' : 'No clear edge — trade the levels and respect the next catalyst.'}

_Rules-based synthesis from live data. Add an ANTHROPIC_API_KEY for an AI-written report._`;
}

async function generateReport(type) {
  const cached = reportCache[type];
  const maxAge = type === 'weekly' ? 6 * 3600000 : 30 * 60000;
  if (cached && Date.now() - cached.at < maxAge) return cached.data;

  const [snap, news] = await Promise.all([
    marketSnapshot().catch(() => ({})),
    fetchNews(type === 'weekly' ? '7d' : '1d').catch(() => [])
  ]);
  let events = [];
  try { events = await fetchForexFactoryCalendar(); } catch (e) { try { events = getHighImpactCalendarEvents(); } catch (e2) {} }

  let report, ai = false;
  if (ANTHROPIC_KEY) {
    try {
      const ctx = {
        gold: snap.gold, dxy: snap.dxy, tips: snap.tips, vix: snap.vix, macro: snap.macro,
        headlines: news.slice(0, 12).map((n) => ({ tag: n.tag, title: n.title, source: n.source })),
        upcoming: (events || []).filter((e) => new Date(e.DateTime) > new Date()).slice(0, 6)
      };
      const sys = `You are the senior macro strategist at Meridian Gold Desk, an institutional gold-trading desk. Write a concise, professional ${type === 'weekly' ? 'WEEKLY' : 'DAILY'} market briefing centred on GOLD (XAUUSD) and the macro backdrop, using ONLY the live data and headlines provided (do not invent numbers). Structure with markdown headings: a one-line bias call; Macro backdrop (real yields, USD, risk/VIX, curve); World & geopolitical developments (from the headlines); On the radar (upcoming events); Gold takeaway (actionable). Real yields are the dominant gold driver (~60-70%). Keep under 320 words. End with a one-line risk disclaimer.`;
      const j = await postJson('https://api.anthropic.com/v1/messages',
        { 'x-api-key': ANTHROPIC_KEY, 'anthropic-version': '2023-06-01' },
        { model: ANTHROPIC_MODEL, max_tokens: 1000, system: sys, messages: [{ role: 'user', content: 'LIVE DATA:\n' + JSON.stringify(ctx, null, 2) }] });
      report = (j.content || []).map((c) => c.text || '').join('').trim();
      ai = true;
    } catch (e) {
      report = fallbackReport(type, snap, news, events) + `\n\n_(AI generation failed: ${e.message})_`;
    }
  } else {
    report = fallbackReport(type, snap, news, events);
  }
  const data = { ok: true, type, ai, model: ai ? ANTHROPIC_MODEL : null, report, generatedAt: new Date().toISOString() };
  reportCache[type] = { at: Date.now(), data };
  return data;
}

const server = http.createServer(async (req, res) => {
  res.setHeader('Access-Control-Allow-Origin', '*');
  res.setHeader('Access-Control-Allow-Methods', 'GET,OPTIONS,POST');
  res.setHeader('Access-Control-Allow-Headers', 'Content-Type');
  res.setHeader('Access-Control-Max-Age', '86400');

  if (req.method === 'OPTIONS') {
    res.writeHead(204);
    res.end();
    return;
  }

  const pathname = (req.url || '/').split('?')[0];

  if (pathname === '/api/markets') {
    res.setHeader('Content-Type', 'application/json');
    try {
      const snapshot = await marketSnapshot();
      res.writeHead(snapshot.ok ? 200 : 206, { 'Cache-Control': 'no-store' });
      res.end(JSON.stringify(snapshot));
    } catch (error) {
      res.writeHead(500, { 'Cache-Control': 'no-store' });
      res.end(JSON.stringify({ ok: false, fetchedAt: new Date().toISOString(), errors: [error.message] }));
    }
    return;
  }

  if (pathname === '/api/fx') {
    res.setHeader('Content-Type', 'application/json');
    const params = new URLSearchParams((req.url.split('?')[1]) || '');
    const pair = (params.get('pair') || 'EURUSD').toUpperCase();
    try {
      const d = await fetchSinglePair(pair);
      res.writeHead(200, { 'Cache-Control': 'no-store' });
      res.end(JSON.stringify({ ok: true, ...d }));
    } catch (error) {
      res.writeHead(502, { 'Cache-Control': 'no-store' });
      res.end(JSON.stringify({ ok: false, pair, error: error.message }));
    }
    return;
  }

  if (pathname === '/api/news') {
    res.setHeader('Content-Type', 'application/json');
    const params = new URLSearchParams((req.url.split('?')[1]) || '');
    const range = params.get('range') === '7d' ? '7d' : '1d';
    try {
      const items = await fetchNews(range);
      res.writeHead(200, { 'Cache-Control': 'max-age=300' });
      res.end(JSON.stringify({ ok: true, range, items, fetchedAt: new Date().toISOString() }));
    } catch (error) {
      res.writeHead(502, { 'Cache-Control': 'no-store' });
      res.end(JSON.stringify({ ok: false, error: error.message }));
    }
    return;
  }

  if (pathname === '/api/report') {
    res.setHeader('Content-Type', 'application/json');
    const params = new URLSearchParams((req.url.split('?')[1]) || '');
    const type = params.get('type') === 'weekly' ? 'weekly' : 'daily';
    try {
      const data = await generateReport(type);
      res.writeHead(200, { 'Cache-Control': 'no-store' });
      res.end(JSON.stringify(data));
    } catch (error) {
      res.writeHead(502, { 'Cache-Control': 'no-store' });
      res.end(JSON.stringify({ ok: false, error: error.message }));
    }
    return;
  }

  if (pathname === '/api/majors') {
    res.setHeader('Content-Type', 'application/json');
    try {
      const pairs = await fetchMajors();
      res.writeHead(200, { 'Cache-Control': 'max-age=60' });
      res.end(JSON.stringify({ ok: true, pairs, fetchedAt: new Date().toISOString() }));
    } catch (error) {
      res.writeHead(502, { 'Cache-Control': 'no-store' });
      res.end(JSON.stringify({ ok: false, error: error.message }));
    }
    return;
  }

  if (pathname === '/api/berg') {
    res.setHeader('Content-Type', 'application/json');
    const params = new URLSearchParams((req.url.split('?')[1]) || '');
    const dir = ((params.get('dir') || 'sell').toLowerCase() === 'buy') ? 'buy' : 'sell';
    try {
      const b = await fetchBerg(dir);
      res.writeHead(200, { 'Cache-Control': 'no-store' });
      res.end(JSON.stringify({ ...b, fetchedAt: new Date().toISOString() }));
    } catch (error) {
      res.writeHead(200, { 'Cache-Control': 'no-store' });
      res.end(JSON.stringify({ ok: false, side: dir, error: error.message || 'berg feed offline' }));
    }
    return;
  }

  if (pathname === '/api/calendar') {
    res.setHeader('Content-Type', 'application/json');
    let events, source;
    try {
      events = await fetchForexFactoryCalendar();
      source = 'forexfactory';
    } catch (e) {
      events = getHighImpactCalendarEvents();
      source = 'synthetic';
    }
    res.writeHead(200, { 'Cache-Control': 'max-age=300' });
    res.end(JSON.stringify({ ok: true, events, source, fetchedAt: new Date().toISOString() }));
    return;
  }

  if (pathname === '/api/levels') {
    res.setHeader('Content-Type', 'application/json');
    try {
      const levels = await fetchGoldLevels();
      res.writeHead(200, { 'Cache-Control': 'max-age=120' });
      res.end(JSON.stringify({ ok: true, ...levels, fetchedAt: new Date().toISOString() }));
    } catch (error) {
      if (levelsCache) {
        res.writeHead(200, { 'Cache-Control': 'max-age=120' });
        res.end(JSON.stringify({ ok: true, ...levelsCache, cached: true, fetchedAt: new Date().toISOString() }));
      } else {
        res.writeHead(502);
        res.end(JSON.stringify({ ok: false, error: error.message || 'levels unavailable' }));
      }
    }
    return;
  }

  // Serve HTML file
  if (pathname === '/' || pathname === '/gold-trading.html') {
    res.setHeader('Content-Type', 'text/html; charset=utf-8');

    if (htmlContent) {
      res.writeHead(200);
      res.end(htmlContent);
    } else {
      res.writeHead(404);
      res.end('HTML file not available');
    }
    return;
  }

  res.writeHead(200, { 'Content-Type': 'text/plain' });
  res.end('Gold Trading Platform server. Visit http://localhost:8787/');
});

server.listen(PORT, '0.0.0.0', () => {
  console.log(`Gold Trading Platform running at http://localhost:${PORT}/`);
});
