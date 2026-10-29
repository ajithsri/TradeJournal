using System.Globalization;
using System.Xml.Linq;
using TradeJournal.Models;

namespace TradeJournal.Services;

/// <summary>Reads &lt;Trade&gt; rows from an IBKR Flex Query statement (XML format).</summary>
public static class FlexParser
{
    private static readonly HashSet<string> SupportedAssets = new(StringComparer.OrdinalIgnoreCase) { "STK", "OPT" };

    private static readonly string[] DateTimeFormats =
    {
        "yyyyMMdd;HHmmss", "yyyyMMdd HHmmss", "yyyyMMdd;HH:mm:ss", "yyyy-MM-dd;HH:mm:ss", "yyyy-MM-dd, HH:mm:ss",
        "yyyy-MM-dd HH:mm:ss", "yyyy-MM-ddTHH:mm:ss", "MM/dd/yyyy;HH:mm:ss", "MM/dd/yyyy HH:mm:ss",
        "yyyyMMdd", "yyyy-MM-dd", "MM/dd/yyyy"
    };

    public static List<Execution> ParseTrades(string xml)
    {
        var doc = XDocument.Parse(xml);
        var result = new List<Execution>();

        foreach (var el in doc.Descendants("Trade"))
        {
            var level = A(el, "levelOfDetail");
            if (level.Length > 0 && !level.Equals("EXECUTION", StringComparison.OrdinalIgnoreCase))
                continue; // skip ORDER / SYMBOL_SUMMARY / CLOSED_LOT rows

            var asset = A(el, "assetCategory");
            if (!SupportedAssets.Contains(asset)) continue;

            var qty = Dec(A(el, "quantity")) ?? 0m;
            var buySell = A(el, "buySell");
            if (buySell.StartsWith("SELL", StringComparison.OrdinalIgnoreCase) && qty > 0) qty = -qty;
            if (buySell.StartsWith("BUY", StringComparison.OrdinalIgnoreCase) && qty < 0) qty = -qty;
            if (qty == 0) continue;

            var multiplier = Dec(A(el, "multiplier")) ?? (asset.Equals("OPT", StringComparison.OrdinalIgnoreCase) ? 100m : 1m);
            if (multiplier == 0) multiplier = 1;
            var price = Dec(A(el, "tradePrice")) ?? 0m;
            var proceeds = Dec(A(el, "proceeds")) ?? -qty * price * multiplier;
            var commission = Dec(A(el, "ibCommission")) ?? Dec(A(el, "commission")) ?? 0m;
            if (commission > 0) commission = -commission;

            var time = ParseTime(A(el, "dateTime"))
                       ?? Combine(A(el, "tradeDate"), A(el, "tradeTime"))
                       ?? ParseTime(A(el, "reportDate"));
            if (time is null) continue;

            var symbol = A(el, "symbol");
            var underlying = A(el, "underlyingSymbol");
            if (underlying.Length == 0) underlying = asset.Equals("STK", StringComparison.OrdinalIgnoreCase) ? symbol : symbol.Split(' ')[0];

            var conid = A(el, "conid");
            if (conid.Length == 0) conid = symbol;

            var exec = new Execution
            {
                AccountId = A(el, "accountId"),
                Currency = A(el, "currency") is { Length: > 0 } c ? c : "USD",
                AssetCategory = asset.ToUpperInvariant(),
                Symbol = symbol,
                Description = A(el, "description"),
                Conid = conid,
                UnderlyingSymbol = underlying,
                PutCall = A(el, "putCall") is { Length: > 0 } pc ? pc.Substring(0, 1).ToUpperInvariant() : null,
                Strike = Dec(A(el, "strike")),
                Expiry = ParseTime(A(el, "expiry"))?.Date,
                Multiplier = multiplier,
                TradeTime = time.Value,
                Quantity = qty,
                Price = price,
                Proceeds = proceeds,
                Commission = commission,
                OpenClose = A(el, "openCloseIndicator"),
                Notes = A(el, "notes"),
                OrderId = A(el, "ibOrderID") is { Length: > 0 } oid ? oid : A(el, "orderID"),
                TransactionType = A(el, "transactionType"),
            };
            exec.ExecKey = BuildKey(el, exec);
            result.Add(exec);
        }

        // Same fill can appear twice when a statement covers several accounts/sections; keep the first.
        return result.GroupBy(e => e.ExecKey).Select(g => g.First()).ToList();
    }

    /// <summary>Returns the IBKR error text if the document is an error response instead of a statement.</summary>
    public static string? TryGetError(string xml)
    {
        try
        {
            var doc = XDocument.Parse(xml);
            if (doc.Root?.Name.LocalName != "FlexStatementResponse") return null;
            var status = doc.Root.Element("Status")?.Value;
            if (string.Equals(status, "Success", StringComparison.OrdinalIgnoreCase)) return null;
            return $"{doc.Root.Element("ErrorCode")?.Value} {doc.Root.Element("ErrorMessage")?.Value}".Trim();
        }
        catch
        {
            return "The response is not valid XML. Check that the Flex Query output format is XML.";
        }
    }

    private static string BuildKey(XElement el, Execution e)
    {
        var tradeId = A(el, "tradeID");
        if (tradeId.Length > 0 && tradeId != "0") return "T:" + tradeId;
        var execId = A(el, "ibExecID");
        if (execId.Length > 0) return "E:" + execId;
        return FormattableString.Invariant($"X:{e.Conid}:{e.TradeTime:yyyyMMddHHmmss}:{e.Quantity}:{e.Price}:{e.Notes}");
    }

    private static string A(XElement el, string name) => el.Attribute(name)?.Value?.Trim() ?? "";

    private static decimal? Dec(string s) =>
        decimal.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;

    public static DateTime? ParseTime(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        return DateTime.TryParseExact(s.Trim(), DateTimeFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d
            : null;
    }

    private static DateTime? Combine(string date, string time)
    {
        var d = ParseTime(date);
        if (d is null) return null;
        if (TimeSpan.TryParseExact(time, new[] { "hhmmss", @"hh\:mm\:ss" }, CultureInfo.InvariantCulture, out var t))
            return d.Value.Date + t;
        return d;
    }
}
