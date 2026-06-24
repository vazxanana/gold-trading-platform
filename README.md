# Gold Trading Platform

An institutional-grade gold trading dashboard with live market data integration.

## Features

- **Live Market Data** — Real-time spot gold (XAUUSD), DXY index, and 10Y TIPS yield
- **Macro Drivers Analysis** — Fundamental scoring based on real yields, USD strength, and risk sentiment
- **Technical Entry Points** — Calculated support/resistance levels and risk/reward targets
- **Contingency Scenarios** — Missed-move setups and re-entry strategies
- **4-Step Execution Framework** — Macro bias → structure → pattern → entry workflow
- **Institutional Philosophy** — Real interest rate thesis for gold positioning

## Quick Start

### Requirements
- Node.js 14+
- Modern web browser

### Setup

1. Start the market data proxy server:
```bash
node gold-market-proxy.js
```
The server will run on `http://127.0.0.1:8787/api/markets`

2. Open the dashboard:
```bash
# Windows
start gold-trading.html

# macOS
open gold-trading.html

# Linux
xdg-open gold-trading.html
```

Or open `gold-trading.html` directly in your browser.

## API Feeds

The platform aggregates data from:

- **Yahoo Finance** — XAUUSD (spot gold) and DX-Y.NYB (DXY index)
- **FRED (St. Louis Fed)** — DFII10 (10-year TIPS yield)

Updates every 30 seconds via the local proxy server.

## Project Files

- `gold-trading.html` — Dashboard UI with embedded JavaScript
- `gold-market-proxy.js` — Node.js proxy server for API aggregation

## Configuration

### FRED API Key
The proxy uses a default public API key. To use your own:
```bash
export FRED_API_KEY=your_api_key
node gold-market-proxy.js
```

Or set in `.env`:
```
FRED_API_KEY=your_api_key
```

### Port
Default port is 8787. Change via environment variable:
```bash
PORT=9000 node gold-market-proxy.js
```

## Core Thesis

Gold is fundamentally a **real interest rate asset**, not a nominal inflation hedge.

**The three institutional pillars:**
1. **Real Yields** — Negative/falling TIPS yields create powerful bullish tailwinds
2. **Central Bank Flows** — De-dollarization and official sector accumulation
3. **Geopolitical Risk** — Safe-haven flows during elevated tensions

When holding "safe" bonds creates real purchasing power loss, gold becomes the superior asset.

## License

MIT
