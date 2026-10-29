using TradeJournal.Services;

// Self-contained checks for the parsing, grouping and stats logic. Run: dotnet run --project tests/CoreTests
var xml = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "../../../../../samples/sample-flex.xml"));
var execs = FlexParser.ParseTrades(xml);
var trades = TradeBuilder.Build(execs);
int failures = 0;
void Check(bool ok, string what) { Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {what}"); if (!ok) failures++; }

foreach (var t in trades.OrderBy(t => t.OpenTime))
    Console.WriteLine($"  {t.OpenTime:MM-dd HH:mm} {t.Underlying,-5} {t.Strategy,-20} {t.Direction,-6} {t.Status,-6} net={t.NetPnl,9} comm={t.Commission,6} dte={t.DteAtOpen} close={t.CloseReason} incomplete={t.IncompleteHistory} legs={t.Legs.Count} key={t.TradeKey}");

Check(execs.Count == 24, $"parsed 24 executions (got {execs.Count}), ORDER row skipped");
var aapl = trades.Single(t => t.Underlying == "AAPL");
Check(aapl.NetPnl == 398m && aapl.Strategy == "Long Stock", "AAPL long stock net 398");
var spy = trades.Single(t => t.Underlying == "SPY");
Check(spy.Strategy == "Bull Put Spread" && spy.Direction == "Credit" && spy.NetPnl == 114.8m && spy.DteAtOpen == 39, "SPY bull put spread credit, net 114.80, 39 DTE");
var qqq = trades.Single(t => t.Underlying == "QQQ");
Check(qqq.Strategy == "Iron Condor" && qqq.CloseReason == "Expired" && qqq.NetPnl == 157.4m && qqq.Legs.Count == 4, "QQQ iron condor expired, net 157.40");
var amdPut = trades.Single(t => t.Underlying == "AMD" && t.AssetCategory == "OPT");
Check(amdPut.CloseReason == "Assigned" && amdPut.NetPnl == 249.35m, "AMD short put assigned, net 249.35");
var amdStock = trades.Single(t => t.Underlying == "AMD" && t.AssetCategory == "STK");
Check(amdStock.Status == "Open", "AMD assigned shares are an open trade");
var nvda = trades.Single(t => t.Underlying == "NVDA");
Check(nvda.NetPnl == -423.9m && nvda.Strategy == "Long Call", "NVDA long call loss -423.90");
var tsla = trades.Single(t => t.Underlying == "TSLA");
Check(tsla.IncompleteHistory, "TSLA orphan close flagged as incomplete");
var msft = trades.Where(t => t.Underlying == "MSFT").OrderBy(t => t.OpenTime).ToList();
Check(msft.Count == 2 && msft[0].NetPnl == 498m && msft[1].NetPnl == -502m && msft[1].Strategy == "Short Stock", "MSFT flip split into long +498 and short -502");

// keys are stable across rebuilds
var again = TradeBuilder.Build(FlexParser.ParseTrades(xml));
Check(again.Select(t => t.TradeKey).OrderBy(k => k).SequenceEqual(trades.Select(t => t.TradeKey).OrderBy(k => k)), "trade keys are stable across rebuilds");
Check(trades.Select(t => t.TradeKey).Distinct().Count() == trades.Count, "trade keys are unique");

var dash = StatsService.Dashboard(trades.Select(t => new TradeWithNote(t, null)).ToList());
var s = dash.Summary;
Console.WriteLine($"  summary: trades={s.Trades} net={s.NetPnl} win%={s.WinRate} pf={s.ProfitFactor} dd={s.MaxDrawdown} open={s.OpenTrades} incomplete={s.IncompleteTrades}");
Check(s.Trades == 7, "7 countable closed trades");
Check(s.NetPnl == 398m + 114.8m + 157.4m + 249.35m - 423.9m + 498m - 502m, "net P&L sums correctly");
var reports = StatsService.Reports(trades.Select(t => new TradeWithNote(t, null)).ToList());
Check(reports.Count == 13 && reports.All(r => r.Buckets.Sum(b => b.Trades) >= 7), "reports produced");

Check(TradeBuilder.DetectStrategy(new() {
    new() { AssetCategory = "OPT", PutCall = "C", Strike = 100, Side = "Long", Expiry = new DateTime(2026,1,1) },
    new() { AssetCategory = "OPT", PutCall = "C", Strike = 100, Side = "Short", Expiry = new DateTime(2026,2,1) } }) == "Calendar Spread", "calendar detected");
Check(TradeBuilder.DetectStrategy(new() {
    new() { AssetCategory = "OPT", PutCall = "C", Strike = 100, Side = "Short", Expiry = new DateTime(2026,1,1) },
    new() { AssetCategory = "OPT", PutCall = "P", Strike = 100, Side = "Short", Expiry = new DateTime(2026,1,1) } }) == "Short Straddle", "short straddle detected");

Console.WriteLine(failures == 0 ? "ALL PASSED" : $"{failures} FAILED");
return failures == 0 ? 0 : 1;
