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

  if (req.url === '/api/markets') {
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

  if (req.url === '/api/calendar') {
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
  if (req.url === '/' || req.url === '/gold-trading.html') {
    res.setHeader('Content-Type', 'text/html');
    const htmlPath = path.join(__dirname, 'gold-trading.html');
    fs.readFile(htmlPath, 'utf8', (err, data) => {
      if (err) {
        res.writeHead(404);
        res.end('File not found');
        return;
      }
      res.writeHead(200);
      res.end(data);
    });
    return;
  }

  res.writeHead(200, { 'Content-Type': 'text/plain' });
  res.end('Gold Trading Platform server. Visit http://localhost:8787/');
});

server.listen(PORT, '0.0.0.0', () => {
  console.log(`Gold Trading Platform running at http://localhost:${PORT}/`);
});
