namespace TradeJournal.Models;

/// <summary>One fill as reported by the IBKR Flex statement (Trades section, Execution level).</summary>
public class Execution
{
    public int Id { get; set; }
    /// <summary>Stable unique key built from tradeID / ibExecID.</summary>
    public string ExecKey { get; set; } = "";
    public string AccountId { get; set; } = "";
    public string Currency { get; set; } = "USD";
    public string AssetCategory { get; set; } = "";   // STK or OPT
    public string Symbol { get; set; } = "";
    public string Description { get; set; } = "";
    public string Conid { get; set; } = "";
    public string UnderlyingSymbol { get; set; } = "";
    public string? PutCall { get; set; }              // C or P
    public decimal? Strike { get; set; }
    public DateTime? Expiry { get; set; }
    public decimal Multiplier { get; set; } = 1;
    public DateTime TradeTime { get; set; }
    /// <summary>Signed: positive = buy, negative = sell.</summary>
    public decimal Quantity { get; set; }
    public decimal Price { get; set; }
    public decimal Proceeds { get; set; }
    /// <summary>Usually negative (a cost).</summary>
    public decimal Commission { get; set; }
    public string OpenClose { get; set; } = "";        // O, C, C;O ...
    public string Notes { get; set; } = "";            // IBKR codes: A (assigned), Ep (expired), Ex (exercised) ...
    public string OrderId { get; set; } = "";
    public string TransactionType { get; set; } = "";
    public DateTime ImportedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>A round-trip position (single stock, single option or a multi-leg option strategy). Rebuilt on every sync.</summary>
public class Trade
{
    public int Id { get; set; }
    /// <summary>Stable key derived from the opening fills, used to attach journal entries.</summary>
    public string TradeKey { get; set; } = "";
    public string Underlying { get; set; } = "";
    public string AssetCategory { get; set; } = "";   // STK, OPT
    public string Strategy { get; set; } = "";
    public string Direction { get; set; } = "";       // Long / Short / Debit / Credit
    public string Status { get; set; } = "Open";      // Open / Closed
    public DateTime OpenTime { get; set; }
    public DateTime? CloseTime { get; set; }
    public decimal Quantity { get; set; }             // max contracts / shares of the largest leg
    public decimal EntryCash { get; set; }            // net cash at entry (negative = debit paid)
    public decimal GrossPnl { get; set; }
    public decimal Commission { get; set; }
    public decimal NetPnl { get; set; }
    public int? DteAtOpen { get; set; }
    public string CloseReason { get; set; } = "";     // Closed / Expired / Assigned / Exercised / Mixed
    public bool IncompleteHistory { get; set; }       // opening fills are older than the synced range
    public string Currency { get; set; } = "USD";
    public List<TradeLeg> Legs { get; set; } = new();
}

public class TradeLeg
{
    public int Id { get; set; }
    public int TradeId { get; set; }
    public string Conid { get; set; } = "";
    public string Symbol { get; set; } = "";
    public string Description { get; set; } = "";
    public string AssetCategory { get; set; } = "";
    public string? PutCall { get; set; }
    public decimal? Strike { get; set; }
    public DateTime? Expiry { get; set; }
    public decimal Multiplier { get; set; } = 1;
    public string Side { get; set; } = "";            // Long / Short
    public decimal Quantity { get; set; }             // max absolute position
    public decimal AvgOpenPrice { get; set; }
    public decimal? AvgClosePrice { get; set; }
    public DateTime OpenTime { get; set; }
    public DateTime? CloseTime { get; set; }
    public decimal OpenCash { get; set; }
    public decimal NetPnl { get; set; }
    public decimal Commission { get; set; }
    public string CloseReason { get; set; } = "";
    public bool IncompleteHistory { get; set; }
    public string ExecKeys { get; set; } = "";        // comma separated
}

/// <summary>Your notes for a trade. Keyed by TradeKey so it survives re-syncs.</summary>
public class TradeNote
{
    public string TradeKey { get; set; } = "";
    public List<string> SetupTags { get; set; } = new();
    public List<string> MistakeTags { get; set; } = new();
    public List<string> EmotionTags { get; set; } = new();
    public int? StressBefore { get; set; }     // 1-10
    public int? StressAfter { get; set; }      // 1-10
    public int? Confidence { get; set; }       // 1-10
    public int? ExecutionRating { get; set; }  // 1-5
    public bool? FollowedPlan { get; set; }
    public decimal? PlannedRisk { get; set; }  // $ you were willing to lose -> R multiple
    public string EntryReason { get; set; } = "";
    public string ExitReason { get; set; } = "";
    public string WentWell { get; set; } = "";
    public string WentWrong { get; set; } = "";
    public string Lesson { get; set; } = "";
    public string Notes { get; set; } = "";
    public List<string> Screenshots { get; set; } = new();
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public class DailyNote
{
    /// <summary>yyyy-MM-dd</summary>
    public string Date { get; set; } = "";
    public int? Mood { get; set; }       // 1-10
    public int? Stress { get; set; }     // 1-10
    public int? Focus { get; set; }      // 1-10
    public decimal? SleepHours { get; set; }
    public string Plan { get; set; } = "";
    public string MarketNotes { get; set; } = "";
    public string Review { get; set; } = "";
    public string Lessons { get; set; } = "";
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public class AppSetting
{
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
}

public class SyncLog
{
    public int Id { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
    public string Source { get; set; } = "";  // Flex / File
    public string Range { get; set; } = "";
    public string Status { get; set; } = "";  // Running / Success / Failed
    public int ExecutionsFound { get; set; }
    public int ExecutionsAdded { get; set; }
    public string Message { get; set; } = "";
}
