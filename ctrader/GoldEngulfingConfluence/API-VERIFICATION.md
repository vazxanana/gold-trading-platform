# cAlgo.API signature verification

Verified against the real `cAlgo.API.dll` (net6.0) shipped in the official NuGet package
`cTrader.Automate 1.0.17` — extracted `lib/net6.0/cAlgo.API.dll` + `cAlgo.API.xml` and
inspected via the XML doc member list plus `MetadataLoadContext` reflection. Nothing below
is from memory.

## The previous bot's error checklist — resolved

| Guess (wrong) | Real API |
|---|---|
| `Chart.Comment` | Does not exist. `Chart` extends `ChartArea`, which has `ChartStaticText DrawStaticText(string name, string text, VerticalAlignment, HorizontalAlignment, Color)`. |
| `ExecuteMarketOrder` arg confusion | `Robot.ExecuteMarketOrder(TradeType tradeType, string symbolName, double volume, string label, double? stopLossPips, double? takeProfitPips)` — the symbol arg is a **string** (`SymbolName`), volume is **units** (not lots), and SL/TP are **pip distances** (XML doc: "Stop loss in pips" / "Take profit in pips"), not absolute prices. Longer overloads add `string comment`, `bool hasTrailingStop`, `StopTriggerMethod?`. |
| `Positions.Find` | `Find(string label)`, `Find(string label, string symbolName)`, `Find(string label, string symbolName, TradeType tradeType)`. Also `FindAll(...)` same shapes, and `FindById(int)`. All symbol args are strings. |
| `Symbol.QuantityToVolume` | Does not exist. Real: `Symbol.QuantityToVolumeInUnits(double quantity)` — lots in, units out. Related: `Symbol.NormalizeVolumeInUnits(double, RoundingMode)`, `VolumeInUnitsMin/Max/Step`. |
| DataSeries shadowing | Style rule enforced in code: locals never reuse `ClosePrices`/`OpenPrices`/etc. names. |

## Other members verified before use

- `Robot` base (via `cAlgo.API.Internals.Algo`): `TimeFrame`, `Bars`, `Symbol`, `SymbolName`,
  `MarketData`, `Positions`, `Server`, `Time`, `IsBacktesting`, `Print(object)` /
  `Print(string, object[])`, `Stop()`; virtuals `OnStart`, `OnBar`, `OnBarClosed`, `OnTick`,
  `OnStop` (compile-checked), `OnTimer`, `OnException`.
- `TimeFrame` is a class with singleton fields (`Minute`, `Minute5`, `Minute15`, …) and real
  `op_Equality`/`op_Inequality` overloads — `TimeFrame != TimeFrame.Minute5` is safe.
- `MarketData.GetBars(TimeFrame)` / `GetBars(TimeFrame, string symbolName)` → `Bars`.
- `Bars`: `Count`, `OpenPrices`, `HighPrices`, `LowPrices`, `ClosePrices`, `OpenTimes`,
  indexer `this[int]`, `TimeFrame`, events `BarOpened(BarOpenedEventArgs)` / `BarClosed`.
- `RobotAttribute`: settable `AccessRights`, `Name`, `TimeZone`, `DefaultTimeFrame`, …
- `ParameterAttribute`: ctor `()`/`(string name)`; settable `DefaultValue`, `MinValue`,
  `MaxValue`, `Step`, `Group`, `Description`.
- `AccessRights` enum: `None`, `Internet`, `FileSystem`, `Registry`, `FullAccess`.
- `TradeType` enum: `Buy`, `Sell`.
- `TradeResult`: `IsSuccessful`, `Position`, `Error`, `PendingOrder`.
- `Position`: `EntryPrice`, `StopLoss`, `TakeProfit`, `Label`, `SymbolName`, `TradeType`,
  `VolumeInUnits`, `Id`, `Pips`, `NetProfit`.
- `Symbol` (`cAlgo.API.Internals.Symbol`): `PipSize`, `TickSize`, `Digits`, `Ask`, `Bid`,
  `LotSize`, `QuantityToVolumeInUnits`, `NormalizeVolumeInUnits`, `VolumeInUnitsMin/Max/Step`.

## Assumptions (explicit, not verified by a signature)

- Enum-typed `[Parameter]` (for `TradeDirection`) renders as a dropdown — standard cAlgo
  behavior, compile-checked but UI rendering can only be confirmed inside cTrader.
- `Bars.OpenTimes` values are in the platform/server time zone; the `[Robot]` attribute's
  default `TimeZone` is UTC, so `OpenTimes[i].Hour` is treated as UTC for the session filter.
  If your broker/platform setting differs, set the session hours accordingly.
