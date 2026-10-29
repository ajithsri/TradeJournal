using TradeJournal.Models;

namespace TradeJournal.Services;

public record TradeWithNote(Trade Trade, TradeNote? Note);

public record Summary(
    int Trades, int Wins, int Losses, int BreakEven,
    decimal NetPnl, decimal GrossPnl, decimal Commission,
    decimal WinRate, decimal AvgWin, decimal AvgLoss, decimal? ProfitFactor, decimal Expectancy,
    decimal LargestWin, decimal LargestLoss, decimal MaxDrawdown,
    int MaxWinStreak, int MaxLossStreak,
    double AvgHoldHoursWin, double AvgHoldHoursLoss,
    decimal? AvgR, int Journaled, int OpenTrades, int IncompleteTrades);

public record EquityPoint(string Date, decimal Pnl, decimal Cumulative);
public record DayPnl(string Date, decimal Pnl, int Trades, int Wins);
public record Bucket(string Key, int Trades, decimal NetPnl, decimal WinRate, decimal AvgPnl);
public record DashboardData(Summary Summary, List<EquityPoint> Equity, List<DayPnl> Days);
public record ReportGroup(string Name, List<Bucket> Buckets);

/// <summary>Pure calculations over closed trades. Open trades and trades with incomplete history are excluded from P&amp;L figures.</summary>
public static class StatsService
{
    public static DashboardData Dashboard(IReadOnlyList<TradeWithNote> all)
    {
        var closed = Countable(all);

        var wins = closed.Where(t => t.Trade.NetPnl > 0).Select(t => t.Trade).ToList();
        var losses = closed.Where(t => t.Trade.NetPnl < 0).Select(t => t.Trade).ToList();
        var sumWin = wins.Sum(t => t.NetPnl);
        var sumLoss = losses.Sum(t => t.NetPnl);
        var net = closed.Sum(t => t.Trade.NetPnl);
        var decided = wins.Count + losses.Count;

        // drawdown + streaks in close order
        decimal cum = 0, peak = 0, maxDd = 0;
        int ws = 0, ls = 0, maxWs = 0, maxLs = 0;
        foreach (var t in closed.Select(x => x.Trade).OrderBy(t => t.CloseTime))
        {
            cum += t.NetPnl;
            peak = Math.Max(peak, cum);
            maxDd = Math.Max(maxDd, peak - cum);
            if (t.NetPnl > 0) { ws++; ls = 0; }
            else if (t.NetPnl < 0) { ls++; ws = 0; }
            maxWs = Math.Max(maxWs, ws);
            maxLs = Math.Max(maxLs, ls);
        }

        var rValues = closed
            .Where(t => t.Note?.PlannedRisk is > 0)
            .Select(t => t.Trade.NetPnl / t.Note!.PlannedRisk!.Value)
            .ToList();

        var summary = new Summary(
            Trades: closed.Count,
            Wins: wins.Count,
            Losses: losses.Count,
            BreakEven: closed.Count - decided,
            NetPnl: net,
            GrossPnl: closed.Sum(t => t.Trade.GrossPnl),
            Commission: closed.Sum(t => t.Trade.Commission),
            WinRate: decided == 0 ? 0 : Math.Round((decimal)wins.Count / decided * 100, 1),
            AvgWin: wins.Count == 0 ? 0 : Math.Round(sumWin / wins.Count, 2),
            AvgLoss: losses.Count == 0 ? 0 : Math.Round(sumLoss / losses.Count, 2),
            ProfitFactor: sumLoss == 0 ? null : Math.Round(sumWin / Math.Abs(sumLoss), 2),
            Expectancy: closed.Count == 0 ? 0 : Math.Round(net / closed.Count, 2),
            LargestWin: wins.Count == 0 ? 0 : wins.Max(t => t.NetPnl),
            LargestLoss: losses.Count == 0 ? 0 : losses.Min(t => t.NetPnl),
            MaxDrawdown: maxDd,
            MaxWinStreak: maxWs,
            MaxLossStreak: maxLs,
            AvgHoldHoursWin: wins.Count == 0 ? 0 : Math.Round(wins.Average(HoldHours), 1),
            AvgHoldHoursLoss: losses.Count == 0 ? 0 : Math.Round(losses.Average(HoldHours), 1),
            AvgR: rValues.Count == 0 ? null : Math.Round(rValues.Average(), 2),
            Journaled: closed.Count(t => t.Note is not null),
            OpenTrades: all.Count(t => t.Trade.Status == "Open"),
            IncompleteTrades: all.Count(t => t.Trade.IncompleteHistory));

        var days = closed
            .GroupBy(t => t.Trade.CloseTime!.Value.Date)
            .OrderBy(g => g.Key)
            .Select(g => new DayPnl(g.Key.ToString("yyyy-MM-dd"), g.Sum(x => x.Trade.NetPnl), g.Count(), g.Count(x => x.Trade.NetPnl > 0)))
            .ToList();

        decimal running = 0;
        var equity = days.Select(d => { running += d.Pnl; return new EquityPoint(d.Date, d.Pnl, running); }).ToList();

        return new DashboardData(summary, equity, days);
    }

    public static List<ReportGroup> Reports(IReadOnlyList<TradeWithNote> all)
    {
        var closed = Countable(all);
        return new List<ReportGroup>
        {
            Multi("Setup", closed, t => t.Note is null ? new[] { "Not journaled" } : t.Note.SetupTags.Count == 0 ? new[] { "No setup tagged" } : t.Note.SetupTags.ToArray()),
            Multi("Mistakes", closed, t => t.Note is null ? new[] { "Not journaled" } : t.Note.MistakeTags.Count == 0 ? new[] { "No mistakes tagged" } : t.Note.MistakeTags.ToArray()),
            Multi("Emotions", closed, t => t.Note is null ? new[] { "Not journaled" } : t.Note.EmotionTags.Count == 0 ? new[] { "No emotion tagged" } : t.Note.EmotionTags.ToArray()),
            Single("Strategy", closed, t => t.Trade.Strategy),
            Single("Underlying", closed, t => t.Trade.Underlying),
            Single("Days to expiry at entry", closed, t => DteBucket(t.Trade), Ordered("0 DTE", "1-7 days", "8-30 days", "31-60 days", "Over 60 days", "Stock")),
            Single("Stress before entry", closed, t => StressBucket(t.Note?.StressBefore), Ordered("Low (1-3)", "Medium (4-6)", "High (7-10)", "Not rated")),
            Single("Followed plan", closed, t => t.Note?.FollowedPlan switch { true => "Yes", false => "No", _ => "Not answered" }, Ordered("Yes", "No", "Not answered")),
            Single("Day of week", closed, t => t.Trade.OpenTime.DayOfWeek.ToString(), Ordered("Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday")),
            Single("Hour opened", closed, t => t.Trade.OpenTime.ToString("HH:00"), (a, b) => string.CompareOrdinal(a, b)),
            Single("Holding time", closed, t => HoldBucket(HoldHours(t.Trade)), Ordered("Under 1 hour", "1-24 hours", "1-7 days", "1-4 weeks", "Over 4 weeks")),
            Single("How it closed", closed, t => t.Trade.CloseReason),
            Single("Direction", closed, t => t.Trade.Direction),
        };
    }

    private static List<TradeWithNote> Countable(IReadOnlyList<TradeWithNote> all) =>
        all.Where(t => t.Trade.Status == "Closed" && !t.Trade.IncompleteHistory && t.Trade.CloseTime.HasValue).ToList();

    private static double HoldHours(Trade t) => ((t.CloseTime ?? t.OpenTime) - t.OpenTime).TotalHours;

    private static string DteBucket(Trade t)
    {
        if (t.AssetCategory != "OPT" || t.DteAtOpen is null) return "Stock";
        return t.DteAtOpen.Value switch
        {
            <= 0 => "0 DTE",
            <= 7 => "1-7 days",
            <= 30 => "8-30 days",
            <= 60 => "31-60 days",
            _ => "Over 60 days",
        };
    }

    private static string StressBucket(int? s) => s switch
    {
        null => "Not rated",
        <= 3 => "Low (1-3)",
        <= 6 => "Medium (4-6)",
        _ => "High (7-10)",
    };

    private static string HoldBucket(double h) => h switch
    {
        < 1 => "Under 1 hour",
        < 24 => "1-24 hours",
        < 24 * 7 => "1-7 days",
        < 24 * 28 => "1-4 weeks",
        _ => "Over 4 weeks",
    };

    private static Comparison<string> Ordered(params string[] order) =>
        (a, b) => Index(order, a).CompareTo(Index(order, b));

    private static int Index(string[] order, string key)
    {
        var i = Array.IndexOf(order, key);
        return i < 0 ? int.MaxValue : i;
    }

    private static ReportGroup Single(string name, List<TradeWithNote> trades, Func<TradeWithNote, string> key, Comparison<string>? order = null) =>
        Multi(name, trades, t => new[] { key(t) }, order);

    private static ReportGroup Multi(string name, List<TradeWithNote> trades, Func<TradeWithNote, string[]> keys, Comparison<string>? order = null)
    {
        var buckets = trades
            .SelectMany(t => keys(t).Distinct().Select(k => (Key: k, t.Trade)))
            .GroupBy(x => x.Key)
            .Select(g =>
            {
                var list = g.Select(x => x.Trade).ToList();
                var decided = list.Count(t => t.NetPnl != 0);
                var winRate = decided == 0 ? 0 : Math.Round((decimal)list.Count(t => t.NetPnl > 0) / decided * 100, 1);
                return new Bucket(g.Key, list.Count, list.Sum(t => t.NetPnl), winRate, Math.Round(list.Average(t => t.NetPnl), 2));
            })
            .ToList();

        if (order is null) buckets.Sort((a, b) => b.NetPnl.CompareTo(a.NetPnl));
        else buckets.Sort((a, b) => order(a.Key, b.Key));
        return new ReportGroup(name, buckets);
    }
}
