using System.Xml.Linq;

namespace TradeJournal.Services;

public class FlexException(string message, string? code = null) : Exception(message)
{
    /// <summary>The IBKR error code, when IBKR returned one.</summary>
    public string? Code { get; } = code;
}

/// <summary>
/// Client for the IBKR Flex Web Service (v3).
/// Step 1: SendRequest with token + query id returns a reference code.
/// Step 2: GetStatement with that reference code returns the statement once IBKR has generated it.
/// </summary>
public class FlexClient(HttpClient http, ILogger<FlexClient> log)
{
    public const string BaseUrl = "https://ndcdyn.interactivebrokers.com/AccountManagement/FlexWebService";

    // Codes where waiting and asking again usually works.
    private static readonly HashSet<string> RetryCodes = new() { "1001", "1004", "1005", "1006", "1007", "1008", "1009", "1018", "1019", "1021" };

    private static readonly Dictionary<string, string> FriendlyErrors = new()
    {
        ["1003"] = "IBKR has no statement for this date range yet. The end date may be a day IBKR has not finished processing.",
        ["1010"] = "This is a legacy Flex Query. Create a new Activity Flex Query in the IBKR portal.",
        ["1011"] = "The IBKR account behind this token is inactive.",
        ["1012"] = "The Flex token has expired. Generate a new one under Flex Web Service Configuration in the IBKR portal.",
        ["1013"] = "IBKR rejected the request because of an IP restriction on the token. Remove or update the allowed IP in the Flex Web Service settings.",
        ["1014"] = "The Flex Query ID is not valid. Check the ID shown next to the query in the IBKR portal.",
        ["1015"] = "The Flex token is not valid. Copy it again from the IBKR portal.",
        ["1016"] = "The account in this Flex Query is not valid.",
        ["1020"] = "IBKR reported an invalid request. Check the token and query ID.",
    };

    public async Task<string> FetchStatementAsync(string token, string queryId, DateTime? from, DateTime? to, CancellationToken ct)
    {
        var sendUrl = $"{BaseUrl}/SendRequest?t={Uri.EscapeDataString(token)}&q={Uri.EscapeDataString(queryId)}&v=3";
        if (from.HasValue && to.HasValue)
            sendUrl += $"&fd={from.Value:yyyyMMdd}&td={to.Value:yyyyMMdd}";

        string? reference = null;
        string getUrl = $"{BaseUrl}/GetStatement";

        for (var attempt = 1; attempt <= 5 && reference is null; attempt++)
        {
            var root = XDocument.Parse(await GetAsync(sendUrl, ct)).Root!;
            if (IsSuccess(root))
            {
                reference = root.Element("ReferenceCode")?.Value;
                getUrl = root.Element("Url")?.Value is { Length: > 0 } u ? u : getUrl;
            }
            else
            {
                var (code, message) = Error(root);
                if (!RetryCodes.Contains(code) || attempt == 5) throw new FlexException(Describe(code, message), code);
                log.LogInformation("Flex SendRequest returned {Code}, retrying", code);
                await Task.Delay(TimeSpan.FromSeconds(5 * attempt), ct);
            }
        }
        if (string.IsNullOrEmpty(reference)) throw new FlexException("IBKR did not return a reference code.");

        for (var attempt = 1; attempt <= 30; attempt++)
        {
            await Task.Delay(TimeSpan.FromSeconds(attempt == 1 ? 3 : 5), ct);
            var xml = await GetAsync($"{getUrl}?t={Uri.EscapeDataString(token)}&q={Uri.EscapeDataString(reference)}&v=3", ct);

            XElement root;
            try { root = XDocument.Parse(xml).Root!; }
            catch { throw new FlexException("IBKR returned a statement that is not XML. Set the Flex Query output format to XML."); }

            if (root.Name.LocalName == "FlexQueryResponse") return xml;

            var (code, message) = Error(root);
            if (!RetryCodes.Contains(code)) throw new FlexException(Describe(code, message), code);
            log.LogInformation("Flex statement not ready ({Code}), attempt {Attempt}", code, attempt);
        }
        throw new FlexException("IBKR took too long to generate the statement. Try the sync again in a few minutes.");
    }

    private async Task<string> GetAsync(string url, CancellationToken ct)
    {
        using var resp = await http.GetAsync(url, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new FlexException($"IBKR returned HTTP {(int)resp.StatusCode}.");
        return body;
    }

    private static bool IsSuccess(XElement root) =>
        string.Equals(root.Element("Status")?.Value, "Success", StringComparison.OrdinalIgnoreCase);

    private static (string Code, string Message) Error(XElement root) =>
        (root.Element("ErrorCode")?.Value ?? "", root.Element("ErrorMessage")?.Value ?? "Unknown error");

    private static string Describe(string code, string message) =>
        FriendlyErrors.TryGetValue(code, out var friendly) ? $"{friendly} (IBKR {code})" : $"IBKR error {code}: {message}";
}
