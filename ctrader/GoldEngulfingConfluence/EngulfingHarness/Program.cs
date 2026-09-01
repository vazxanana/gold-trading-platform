using System;
using cAlgo.Robots;

// Unit-check for EngulfingLogic.cs — the same file compiled into the cBot.
//
// Part 1: boundary cases covering every clause of the spec definition:
//   bullish: close > prior_open AND open <= prior_close AND close > open
//   bearish: close < prior_open AND open >= prior_close AND close < open
// Part 2: a deterministic synthetic XAUUSD-like M5 walk (3 days) with every
//   detection printed the same way the bot prints them, plus density stats.

int failures = 0;

void Check(string name, bool actual, bool expected)
{
    var ok = actual == expected;
    if (!ok) failures++;
    Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name}: got {actual}, expected {expected}");
}

Console.WriteLine("=== Part 1: boundary cases (prior O, prior C, O, C) ===");
Console.WriteLine("-- bullish --");
Check("classic bull EG of bearish prior (100,99 | 98.5,100.5)",
    EngulfingLogic.IsBullish(100, 99, 98.5, 100.5), true);
Check("open == prior_close boundary, '<=' admits it (100,99 | 99,100.01)",
    EngulfingLogic.IsBullish(100, 99, 99, 100.01), true);
Check("close == prior_open exactly -> strict '>' rejects (100,99 | 98.5,100)",
    EngulfingLogic.IsBullish(100, 99, 98.5, 100), false);
Check("gap up: open > prior_close -> rejected (100,99 | 99.2,101)",
    EngulfingLogic.IsBullish(100, 99, 99.2, 101), false);
Check("bearish body cannot be bullish EG (100,99 | 99,98)",
    EngulfingLogic.IsBullish(100, 99, 99, 98), false);
Check("prior candle bullish, still EG per spec (99,100 | 99.5,100.5)",
    EngulfingLogic.IsBullish(99, 100, 99.5, 100.5), true);
Check("doji current (open==close) rejected (100,99 | 99,99)",
    EngulfingLogic.IsBullish(100, 99, 99, 99), false);

Console.WriteLine("-- bearish (mirror) --");
Check("classic bear EG of bullish prior (99,100 | 100.5,98.5)",
    EngulfingLogic.IsBearish(99, 100, 100.5, 98.5), true);
Check("open == prior_close boundary, '>=' admits it (99,100 | 100,98.99)",
    EngulfingLogic.IsBearish(99, 100, 100, 98.99), true);
Check("close == prior_open exactly -> strict '<' rejects (99,100 | 100.5,99)",
    EngulfingLogic.IsBearish(99, 100, 100.5, 99), false);
Check("gap down: open < prior_close -> rejected (99,100 | 99.8,98)",
    EngulfingLogic.IsBearish(99, 100, 99.8, 98), false);
Check("bullish body cannot be bearish EG (99,100 | 100,101)",
    EngulfingLogic.IsBearish(99, 100, 100, 101), false);
Check("prior candle bearish, still EG per spec (100,99 | 99.5,98.5)",
    EngulfingLogic.IsBearish(100, 99, 99.5, 98.5), true);
Check("a bar can never be both bull and bear EG (contradictory clauses)",
    EngulfingLogic.IsBullish(100, 99, 98.5, 100.5) && EngulfingLogic.IsBearish(100, 99, 98.5, 100.5), false);

Console.WriteLine();
Console.WriteLine("=== Part 2: synthetic XAUUSD-like M5 walk, 3 days (864 bars), seed=42 ===");

// Deterministic LCG so the run is reproducible.
ulong state = 42;
double NextUnit()
{
    state = state * 6364136223846793005UL + 1442695040888963407UL;
    return (state >> 11) / (double)(1UL << 53);
}

var t0 = new DateTime(2026, 7, 10, 0, 0, 0, DateTimeKind.Utc);
const int n = 864;
var open = new double[n];
var close = new double[n];
double price = 2400.0;
for (int i = 0; i < n; i++)
{
    open[i] = price;
    // ~0.6 USD typical M5 body for gold, fat-tailed now and then.
    double body = (NextUnit() - 0.5) * 1.2;
    if (NextUnit() < 0.05) body *= 4;
    close[i] = Math.Round(price + body, 2);
    price = close[i];
}

int bulls = 0, bears = 0;
for (int i = 1; i < n; i++)
{
    bool bull = EngulfingLogic.IsBullish(open[i - 1], close[i - 1], open[i], close[i]);
    bool bear = EngulfingLogic.IsBearish(open[i - 1], close[i - 1], open[i], close[i]);
    if (!bull && !bear) continue;
    if (bull) bulls++; else bears++;
    if (bulls + bears <= 12 || i > n - 40)  // head + tail sample so output stays readable
        Console.WriteLine($"  [EG-SCAN M5] {(bull ? "BULL" : "BEAR")} {t0.AddMinutes(5 * i):yyyy-MM-dd HH:mm} " +
                          $"O={open[i]:F2} C={close[i]:F2} (prior O={open[i - 1]:F2} C={close[i - 1]:F2})");
}
Console.WriteLine($"  ... ({bulls + bears} total detections, sample above)");
Console.WriteLine($"  Scanned {n - 1} bars: {bulls} bullish EG, {bears} bearish EG " +
                  $"({100.0 * (bulls + bears) / (n - 1):F1}% of bars)");

Console.WriteLine();
Console.WriteLine(failures == 0 ? "ALL CHECKS PASSED" : $"{failures} CHECK(S) FAILED");
return failures == 0 ? 0 : 1;
