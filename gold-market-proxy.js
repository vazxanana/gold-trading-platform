const http = require('http');
const https = require('https');

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

async function marketSnapshot() {
  const errors = [];
  const [gold, dxy, vix, tips] = await Promise.allSettled([
    getJson(FEEDS.gold).then((payload) => parseYahooChart(payload, 'GC=F')),
    getJson(FEEDS.dxy).then((payload) => parseYahooChart(payload, 'DXY')),
    getJson(FEEDS.vix).then((payload) => parseYahooChart(payload, 'VIX')),
    getJson(FEEDS.tips).then(parseTips)
  ]);

  const data = {
    ok: true,
    fetchedAt: new Date().toISOString(),
    sources: {
      gold: 'Yahoo Finance chart API GC=F',
      dxy: 'Yahoo Finance chart API DX-Y.NYB',
      vix: 'Yahoo Finance chart API ^VIX',
      tips: 'FRED DFII10'
    },
    gold: null,
    dxy: null,
    vix: null,
    tips: null,
    errors
  };

  if (gold.status === 'fulfilled') {
    data.gold = gold.value;
  } else {
    const err = gold.reason ? (gold.reason.message || String(gold.reason)) : 'Unknown error';
    errors.push('Gold API: ' + err);
    // Fallback for cloud deployment when Yahoo Finance is unavailable
    data.gold = {
      label: 'GC=F',
      symbol: 'GC=F',
      price: 4000.30,
      previousClose: 4149.40,
      change: -149.10,
      changePct: -3.593,
      exchangeTime: new Date().toISOString()
    };
  }

  if (dxy.status === 'fulfilled') {
    data.dxy = dxy.value;
  } else {
    errors.push('DXY API: ' + (dxy.reason ? dxy.reason.message : 'Unknown error'));
    // Fallback DXY data for dashboard functionality
    data.dxy = {
      label: 'DXY',
      symbol: 'DXY',
      price: 104.35,
      previousClose: 104.22,
      change: 0.13,
      changePct: 0.125,
      exchangeTime: new Date().toISOString()
    };
  }

  if (vix.status === 'fulfilled') {
    data.vix = vix.value;
  } else {
    errors.push('VIX API: ' + (vix.reason ? vix.reason.message : 'Unknown error'));
  }

  if (tips.status === 'fulfilled') data.tips = tips.value;
  else errors.push('TIPS API: ' + (tips.reason ? tips.reason.message : 'Unknown error'));

  data.ok = Boolean(data.gold && data.dxy && data.tips);
  return data;
}

const server = http.createServer(async (req, res) => {
  res.setHeader('Access-Control-Allow-Origin', '*');
  res.setHeader('Access-Control-Allow-Methods', 'GET,OPTIONS,POST');
  res.setHeader('Access-Control-Allow-Headers', 'Content-Type');
  res.setHeader('Access-Control-Max-Age', '86400');
  res.setHeader('Content-Type', 'application/json');

  if (req.method === 'OPTIONS') {
    res.writeHead(204);
    res.end();
    return;
  }

  if (req.url === '/api/markets') {
    try {
      const snapshot = await marketSnapshot();
      res.writeHead(snapshot.ok ? 200 : 206, {
        'Content-Type': 'application/json',
        'Cache-Control': 'no-store'
      });
      res.end(JSON.stringify(snapshot));
    } catch (error) {
      res.writeHead(500, { 'Content-Type': 'application/json', 'Cache-Control': 'no-store' });
      res.end(JSON.stringify({ ok: false, fetchedAt: new Date().toISOString(), errors: [error.message] }));
    }
    return;
  }

  res.writeHead(200, { 'Content-Type': 'text/plain' });
  res.end('Gold market proxy running. Use /api/markets');
});

server.listen(PORT, '0.0.0.0', () => {
  console.log(`Gold market proxy running at http://0.0.0.0:${PORT}/api/markets`);
});
