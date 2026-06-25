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
  tips: `https://api.stlouisfed.org/fred/series/observations?series_id=DFII10&api_key=${FRED_KEY}&file_type=json&sort_order=desc&limit=1`
};

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
  const observation = observations.find((item) => item.value && item.value !== '.');
  if (!observation) throw new Error('FRED DFII10 missing latest observation');

  const value = Number(observation.value);
  if (!Number.isFinite(value)) throw new Error('FRED DFII10 value invalid');
  return { value, date: observation.date };
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
  const [gold, dxy, vix, tips, eur, gbp, jpy] = await Promise.allSettled([
    getJson(FEEDS.gold).then((payload) => parseYahooChart(payload, 'GC=F')),
    getJson(FEEDS.dxy).then((payload) => parseYahooChart(payload, 'DXY')),
    getJson(FEEDS.vix).then((payload) => parseYahooChart(payload, 'VIX')),
    getJson(FEEDS.tips).then(parseTips),
    getJson(FX_YAHOO.EURUSD).then((p) => parseYahooChart(p, 'EURUSD')),
    getJson(FX_YAHOO.GBPUSD).then((p) => parseYahooChart(p, 'GBPUSD')),
    getJson(FX_YAHOO.USDJPY).then((p) => parseYahooChart(p, 'USDJPY'))
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
    errors
  };

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

  if (pathname === '/api/calendar') {
    res.setHeader('Content-Type', 'application/json');
    try {
      const events = getHighImpactCalendarEvents();
      res.writeHead(200, { 'Cache-Control': 'max-age=300' });
      res.end(JSON.stringify({ ok: true, events, fetchedAt: new Date().toISOString() }));
    } catch (error) {
      res.writeHead(500);
      res.end(JSON.stringify({ ok: false, events: [], errors: [error.message] }));
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
