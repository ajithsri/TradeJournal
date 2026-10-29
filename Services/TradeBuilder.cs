using System.Security.Cryptography;
using System.Text;
using TradeJournal.Models;

namespace TradeJournal.Services;

/// <summary>
/// Turns raw fills into journal trades:
/// 1. Each contract (conid) is tracked as a position; a leg runs from flat to flat again.
/// 2. Option legs on the same underlying opened together (same order or within the grouping window)
///    are combined into one trade, e.g. a vertical spread or an iron condor.
/// </summary>
public static class TradeBuilder
{
    public static TimeSpan GroupWindow { get; set; } = TimeSpan.FromSeconds(90);

    private sealed record Fill(Execution Ex, decimal Qty, decimal Proceeds, decimal Commission);

    private sealed class LegState
    {
        public LegState(Execution first) => First = first;
        public Execution First { get; }
        public List<Fill> Fills { get; } = new();
        public decimal Position { get; private set; }
        public decimal MaxAbs { get; private set; }
        public bool Closed { get; set; }
        public bool Incomplete { get; set; }

        public void Add(Execution ex, decimal qty)
        {
            var fraction = ex.Quantity == 0 ? 1m : qty / ex.Quantity;
            Fills.Add(new Fill(ex, qty, ex.Proceeds * fraction, ex.Commission * fraction));
            Position += qty;
            MaxAbs = Math.Max(MaxAbs, Math.Abs(Position));
        }
    }

    public static List<Trade> Build(IEnumerable<Execution> executions)
    {
        var ordered = executions
            .OrderBy(e => e.TradeTime)
            .ThenBy(e => e.Id)
            .ThenBy(e => e.ExecKey, StringComparer.Ordinal)
            .ToList();

        var open = new Dictionary<string, LegState>();
        var done = new List<LegState>();

        foreach (var ex in ordered)
        {
            var remaining = ex.Quantity;
            while (remaining != 0)
            {
                if (!open.TryGetValue(ex.Conid, out var st))
                {
                    if (IsCloseOnly(ex.OpenClose))
                    {
                        // Closing fill without a known opening: the position was opened before the synced range.
                        var orphan = new LegState(ex) { Incomplete = true, Closed = true };
                        orphan.Add(ex, remaining);
                        done.Add(orphan);
                        break;
                    }
                    st = new LegState(ex);
                    open[ex.Conid] = st;
                }

                if (st.Position == 0 || Math.Sign(st.Position) == Math.Sign(remaining))
                {
                    st.Add(ex, remaining);
                    remaining = 0;
                }
                else
                {
                    var closeQty = Math.Abs(remaining) <= Math.Abs(st.Position) ? remaining : -st.Position;
                    st.Add(ex, closeQty);
                    remaining -= closeQty;
                    if (st.Position == 0)
                    {
                        st.Closed = true;
                        done.Add(st);
                        open.Remove(ex.Conid);
                    }
                }
            }
        }
        done.AddRange(open.Values);

        var legs = done.Select(ToLeg).OrderBy(l => l.Leg.OpenTime).ThenBy(l => l.Leg.Conid).ToList();
        return Group(legs).Select(ToTrade).OrderByDescending(t => t.OpenTime).ToList();
    }

    // ---------- legs ----------

    private sealed record LegInfo(TradeLeg Leg, string Underlying, string Currency, string FirstExecKey, HashSet<string> OpenOrderIds);

    private static LegInfo ToLeg(LegState st)
    {
        var first = st.First;
        var firstSign = Math.Sign(st.Fills[0].Qty);
        var sideSign = st.Incomplete ? -firstSign : firstSign;

        var opens = st.Fills.Where(f => Math.Sign(f.Qty) == sideSign).ToList();
        var closes = st.Fills.Where(f => Math.Sign(f.Qty) != sideSign).ToList();

        decimal Avg(List<Fill> fills) =>
            fills.Count == 0 ? 0 : fills.Sum(f => Math.Abs(f.Qty) * f.Ex.Price) / fills.Sum(f => Math.Abs(f.Qty));

        var avgOpen = Avg(opens);
        var commission = st.Fills.Sum(f => f.Commission);
        decimal net;
        if (st.Closed)
            net = st.Fills.Sum(f => f.Proceeds) + commission;
        else
            net = closes.Sum(f => Math.Abs(f.Qty) * (f.Ex.Price - avgOpen) * first.Multiplier * sideSign) + commission;

        var leg = new TradeLeg
        {
            Conid = first.Conid,
            Symbol = first.Symbol,
            Description = first.Description,
            AssetCategory = first.AssetCategory,
            PutCall = first.PutCall,
            Strike = first.Strike,
            Expiry = first.Expiry,
            Multiplier = first.Multiplier,
            Side = sideSign > 0 ? "Long" : "Short",
            Quantity = Math.Max(st.MaxAbs, Math.Abs(st.Fills[0].Qty)),
            AvgOpenPrice = Math.Round(avgOpen, 4),
            AvgClosePrice = closes.Count > 0 ? Math.Round(Avg(closes), 4) : null,
            OpenTime = st.Fills[0].Ex.TradeTime,
            CloseTime = st.Closed ? st.Fills[^1].Ex.TradeTime : null,
            OpenCash = Math.Round(opens.Sum(f => f.Proceeds), 2),
            NetPnl = Math.Round(net, 2),
            Commission = Math.Round(commission, 2),
            CloseReason = st.Closed ? CloseReasonOf(closes.Count > 0 ? closes[^1].Ex : st.Fills[^1].Ex) : "",
            IncompleteHistory = st.Incomplete,
            ExecKeys = string.Join(",", st.Fills.Select(f => f.Ex.ExecKey).Distinct()),
        };

        var orderIds = opens.Select(f => f.Ex.OrderId).Where(o => !string.IsNullOrEmpty(o) && o != "0").ToHashSet();
        return new LegInfo(leg, first.UnderlyingSymbol, first.Currency, first.ExecKey, orderIds);
    }

    private static bool IsCloseOnly(string openClose)
    {
        if (string.IsNullOrWhiteSpace(openClose)) return false;
        var parts = openClose.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Contains("C") && !parts.Contains("O");
    }

    private static string CloseReasonOf(Execution ex)
    {
        var codes = ex.Notes.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (codes.Contains("Ep")) return "Expired";
        if (codes.Contains("A")) return "Assigned";
        if (codes.Contains("Ex")) return "Exercised";
        if (ex.AssetCategory == "OPT" && ex.Price == 0 && ex.Expiry.HasValue && ex.Expiry.Value.Date == ex.TradeTime.Date)
            return "Expired";
        return "Closed";
    }

    // ---------- grouping ----------

    private static List<List<LegInfo>> Group(List<LegInfo> legs)
    {
        var groups = new List<List<LegInfo>>();
        foreach (var leg in legs)
        {
            List<LegInfo>? target = null;
            if (leg.Leg.AssetCategory == "OPT")
            {
                target = groups.LastOrDefault(g =>
                    g[0].Leg.AssetCategory == "OPT" &&
                    g[0].Underlying == leg.Underlying &&
                    g[0].Leg.IncompleteHistory == leg.Leg.IncompleteHistory &&
                    g.All(x => x.Leg.Conid != leg.Leg.Conid) &&
                    (g.Any(x => x.OpenOrderIds.Overlaps(leg.OpenOrderIds)) ||
                     (leg.Leg.OpenTime - g.Min(x => x.Leg.OpenTime)).Duration() <= GroupWindow));
            }
            if (target is null) groups.Add(new List<LegInfo> { leg });
            else target.Add(leg);
        }
        return groups;
    }

    private static Trade ToTrade(List<LegInfo> group)
    {
        var legs = group.Select(g => g.Leg).ToList();
        var closed = legs.All(l => l.CloseTime.HasValue);
        var net = legs.Sum(l => l.NetPnl);
        var commission = legs.Sum(l => l.Commission);
        var entryCash = legs.Sum(l => l.OpenCash);
        var openTime = legs.Min(l => l.OpenTime);
        var reasons = legs.Select(l => l.CloseReason).Where(r => r.Length > 0).Distinct().ToList();
        var minExpiry = legs.Where(l => l.Expiry.HasValue).Select(l => l.Expiry!.Value).DefaultIfEmpty().Min();

        var strategy = DetectStrategy(legs);
        string direction = legs.Count == 1 ? legs[0].Side : entryCash >= 0 ? "Credit" : "Debit";

        var keySource = string.Join("|", group.Select(g => g.FirstExecKey).OrderBy(k => k, StringComparer.Ordinal));
        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(keySource)))[..16];

        return new Trade
        {
            TradeKey = "TK" + hash,
            Underlying = group[0].Underlying,
            AssetCategory = legs[0].AssetCategory,
            Strategy = strategy,
            Direction = direction,
            Status = closed ? "Closed" : "Open",
            OpenTime = openTime,
            CloseTime = closed ? legs.Max(l => l.CloseTime) : null,
            Quantity = legs.Max(l => l.Quantity),
            EntryCash = Math.Round(entryCash, 2),
            NetPnl = Math.Round(net, 2),
            Commission = Math.Round(commission, 2),
            GrossPnl = Math.Round(net - commission, 2),
            DteAtOpen = minExpiry == default ? null : (int)(minExpiry.Date - openTime.Date).TotalDays,
            CloseReason = !closed ? "" : reasons.Count == 1 ? reasons[0] : "Mixed",
            IncompleteHistory = legs.Any(l => l.IncompleteHistory),
            Currency = group[0].Currency,
            Legs = legs.OrderBy(l => l.Expiry).ThenBy(l => l.PutCall).ThenBy(l => l.Strike).ToList(),
        };
    }

    public static string DetectStrategy(List<TradeLeg> legs)
    {
        if (legs.Count == 1)
        {
            var l = legs[0];
            if (l.AssetCategory == "STK") return $"{l.Side} Stock";
            return $"{l.Side} {(l.PutCall == "P" ? "Put" : "Call")}";
        }
        if (legs.Any(l => l.AssetCategory != "OPT")) return $"Custom ({legs.Count} legs)";

        var sameExpiry = legs.Select(l => l.Expiry).Distinct().Count() == 1;
        var calls = legs.Where(l => l.PutCall == "C").OrderBy(l => l.Strike).ToList();
        var puts = legs.Where(l => l.PutCall == "P").OrderBy(l => l.Strike).ToList();

        if (legs.Count == 2)
        {
            var (a, b) = (legs[0], legs[1]);
            var oppositeSides = a.Side != b.Side;
            var sameType = a.PutCall == b.PutCall;

            if (sameType && oppositeSides && sameExpiry)
            {
                var longLeg = legs.First(l => l.Side == "Long");
                var shortLeg = legs.First(l => l.Side == "Short");
                if (a.PutCall == "C") return longLeg.Strike < shortLeg.Strike ? "Bull Call Spread" : "Bear Call Spread";
                return longLeg.Strike > shortLeg.Strike ? "Bear Put Spread" : "Bull Put Spread";
            }
            if (sameType && oppositeSides && !sameExpiry)
                return a.Strike == b.Strike ? "Calendar Spread" : "Diagonal Spread";
            if (!sameType && !oppositeSides && sameExpiry)
                return $"{a.Side} {(a.Strike == b.Strike ? "Straddle" : "Strangle")}";
            if (!sameType && oppositeSides && sameExpiry)
                return "Risk Reversal";
        }

        if (legs.Count == 3 && sameExpiry && (calls.Count == 3 || puts.Count == 3))
        {
            var s = calls.Count == 3 ? calls : puts;
            if (s[0].Side == s[2].Side && s[1].Side != s[0].Side)
                return $"{s[0].Side} {(s[0].PutCall == "C" ? "Call" : "Put")} Butterfly";
        }

        if (legs.Count == 4 && sameExpiry && calls.Count == 2 && puts.Count == 2 &&
            calls[0].Side != calls[1].Side && puts[0].Side != puts[1].Side)
        {
            var shortCall = calls.First(l => l.Side == "Short");
            var longCall = calls.First(l => l.Side == "Long");
            var shortPut = puts.First(l => l.Side == "Short");
            var longPut = puts.First(l => l.Side == "Long");
            var shortsInside = shortCall.Strike < longCall.Strike && shortPut.Strike > longPut.Strike;
            if (!shortsInside) return "Reverse Iron Condor";
            return shortCall.Strike == shortPut.Strike ? "Iron Butterfly" : "Iron Condor";
        }

        if (legs.Count == 4 && sameExpiry && (calls.Count == 4 || puts.Count == 4))
            return $"{(calls.Count == 4 ? "Call" : "Put")} Condor";

        return $"Custom ({legs.Count} legs)";
    }
}
