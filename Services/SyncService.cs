using Microsoft.EntityFrameworkCore;
using TradeJournal.Data;
using TradeJournal.Models;

namespace TradeJournal.Services;

public class SyncService(JournalDbContext db, FlexClient flex, SettingsService settings, ILogger<SyncService> log)
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>Fetches fills from IBKR for the range, stores new ones and rebuilds trades.</summary>
    public async Task<SyncLog> SyncFromFlexAsync(DateTime from, DateTime to, CancellationToken ct)
    {
        if (!await Gate.WaitAsync(0, ct)) throw new FlexException("A sync is already running.");
        var entry = new SyncLog { StartedAt = DateTime.Now, Source = "IBKR Flex", Status = "Running" };
        try
        {
            var (token, queryId, useRange) = await settings.GetFlexAsync();
            var requestedTo = to.Date;
            if (useRange)
            {
                // IBKR dates its statements in New York time. A local "today" can be a day IBKR has not reached yet.
                to = new[] { to.Date, LatestStatementDay(DateTime.UtcNow) }.Min();
                from = new[] { from.Date, to }.Min();
            }
            entry.Range = useRange ? $"{from:yyyy-MM-dd} to {to:yyyy-MM-dd}" : "Period set in the Flex Query";
            db.SyncLogs.Add(entry);
            await db.SaveChangesAsync(ct);

            var fetched = new List<Execution>();
            if (useRange)
            {
                // IBKR allows at most 365 days per statement.
                var chunkStart = from;
                while (chunkStart <= to)
                {
                    var chunkEnd = new[] { chunkStart.AddDays(364), to }.Min();
                    var isLast = chunkEnd == to;
                    string xml;
                    for (var stepBack = 0; ; stepBack++)
                    {
                        try
                        {
                            xml = await flex.FetchStatementAsync(token, queryId, chunkStart, chunkEnd, ct);
                            break;
                        }
                        catch (FlexException ex) when (ex.Code == "1003" && isLast && stepBack < 3 && chunkEnd > chunkStart)
                        {
                            // The newest day is not processed yet: end the range one business day earlier and ask again.
                            chunkEnd = PreviousBusinessDay(chunkEnd);
                            to = chunkEnd;
                            log.LogInformation("IBKR 1003 for end date, retrying with {End:yyyy-MM-dd}", chunkEnd);
                            await Task.Delay(TimeSpan.FromSeconds(3), ct);
                        }
                        catch (FlexException ex) when (ex.Code == "1003")
                        {
                            throw new FlexException(
                                $"IBKR has no statement for {chunkStart:yyyy-MM-dd} to {chunkEnd:yyyy-MM-dd} (IBKR 1003). " +
                                "Try an earlier end date, or turn off \"Send the date range to IBKR\" to use the period saved in the Flex Query.", "1003");
                        }
                    }
                    fetched.AddRange(FlexParser.ParseTrades(xml));
                    chunkStart = chunkEnd.AddDays(1);
                    if (chunkStart <= to) await Task.Delay(TimeSpan.FromSeconds(3), ct); // stay under IBKR rate limits
                }
                entry.Range = $"{from:yyyy-MM-dd} to {to:yyyy-MM-dd}";
            }
            else
            {
                fetched.AddRange(FlexParser.ParseTrades(await flex.FetchStatementAsync(token, queryId, null, null, ct)));
            }

            entry.ExecutionsFound = fetched.Count;
            entry.ExecutionsAdded = await ImportAsync(fetched, ct);
            entry.Status = "Success";
            entry.Message = $"{entry.ExecutionsAdded} new fills added, {entry.ExecutionsFound - entry.ExecutionsAdded} already stored.";
            if (useRange && to < requestedTo)
                entry.Message += $" Synced up to {to:ddd d MMM}; IBKR has no statement for later days yet. Trades from those days appear once IBKR finishes its end-of-day processing.";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Flex sync failed");
            entry.Status = "Failed";
            entry.Message = ex is FlexException or System.Xml.XmlException ? ex.Message : "Sync failed: " + ex.Message;
            if (entry.Id == 0) db.SyncLogs.Add(entry);
        }
        finally
        {
            entry.FinishedAt = DateTime.Now;
            await db.SaveChangesAsync(CancellationToken.None);
            Gate.Release();
        }
        return entry;
    }

    private static readonly TimeZoneInfo NewYork = FindNewYork();

    /// <summary>The newest day IBKR can have a statement for: today in New York, or the Friday before on a weekend.</summary>
    public static DateTime LatestStatementDay(DateTime utcNow)
    {
        var day = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utcNow, DateTimeKind.Utc), NewYork).Date;
        while (day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) day = day.AddDays(-1);
        return day;
    }

    public static DateTime PreviousBusinessDay(DateTime day)
    {
        do day = day.AddDays(-1);
        while (day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday);
        return day;
    }

    private static TimeZoneInfo FindNewYork()
    {
        foreach (var id in new[] { "America/New_York", "Eastern Standard Time" })
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException) { }
        }
        return TimeZoneInfo.CreateCustomTimeZone("New York (fixed)", TimeSpan.FromHours(-5), "New York", "New York");
    }

    /// <summary>Imports a Flex XML statement downloaded manually from the IBKR portal.</summary>
    public async Task<SyncLog> ImportXmlAsync(string xml, string fileName, CancellationToken ct)
    {
        var entry = new SyncLog { StartedAt = DateTime.Now, Source = "File", Range = fileName };
        try
        {
            var error = FlexParser.TryGetError(xml);
            if (error is not null) throw new FlexException(error);
            var parsed = FlexParser.ParseTrades(xml);
            entry.ExecutionsFound = parsed.Count;
            entry.ExecutionsAdded = await ImportAsync(parsed, ct);
            entry.Status = "Success";
            entry.Message = parsed.Count == 0
                ? "No stock or option fills found. Check that the Flex Query includes the Trades section at Execution level."
                : $"{entry.ExecutionsAdded} new fills added, {entry.ExecutionsFound - entry.ExecutionsAdded} already stored.";
        }
        catch (Exception ex) when (ex is FlexException or System.Xml.XmlException)
        {
            entry.Status = "Failed";
            entry.Message = ex is System.Xml.XmlException ? "The file is not valid Flex XML." : ex.Message;
        }
        entry.FinishedAt = DateTime.Now;
        db.SyncLogs.Add(entry);
        await db.SaveChangesAsync(ct);
        return entry;
    }

    private async Task<int> ImportAsync(List<Execution> executions, CancellationToken ct)
    {
        var existing = (await db.Executions.Select(e => e.ExecKey).ToListAsync(ct)).ToHashSet();
        var fresh = executions.Where(e => existing.Add(e.ExecKey)).ToList();
        db.Executions.AddRange(fresh);
        await db.SaveChangesAsync(ct);
        await RebuildTradesAsync(ct);
        return fresh.Count;
    }

    /// <summary>Recomputes all trades from stored fills. Journal notes are keyed by TradeKey and are not touched.</summary>
    public async Task RebuildTradesAsync(CancellationToken ct)
    {
        var all = await db.Executions.AsNoTracking().ToListAsync(ct);
        var trades = TradeBuilder.Build(all);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.TradeLegs.ExecuteDeleteAsync(ct);
        await db.Trades.ExecuteDeleteAsync(ct);
        db.Trades.AddRange(trades);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        db.ChangeTracker.Clear();
    }
}
