using System.Globalization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using TradeJournal.Data;
using TradeJournal.Models;
using TradeJournal.Services;

var builder = WebApplication.CreateBuilder(args);

var dataDir = Path.Combine(builder.Environment.ContentRootPath, "App_Data");
var shotsDir = Path.Combine(dataDir, "screenshots");
Directory.CreateDirectory(shotsDir);

builder.Services.AddDbContext<JournalDbContext>(o => o.UseSqlite($"Data Source={Path.Combine(dataDir, "journal.db")}"));

var dataProtection = builder.Services.AddDataProtection()
    .SetApplicationName("TradeJournal")
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataDir, "keys")));
if (OperatingSystem.IsWindows()) dataProtection.ProtectKeysWithDpapi();

builder.Services.AddHttpClient<FlexClient>(c =>
{
    c.Timeout = TimeSpan.FromSeconds(60);
    c.DefaultRequestHeaders.UserAgent.ParseAdd("TradeJournal/1.0"); // IBKR rejects requests without a User-Agent
});
builder.Services.AddScoped<SettingsService>();
builder.Services.AddScoped<SyncService>();
builder.Services.Configure<FormOptions>(o => o.MultipartBodyLengthLimit = 100 * 1024 * 1024);

var app = builder.Build();

using (var scope = app.Services.CreateScope())
    scope.ServiceProvider.GetRequiredService<JournalDbContext>().Database.EnsureCreated();

app.UseDefaultFiles();
app.UseStaticFiles();

var api = app.MapGroup("/api");

// ---------- trades ----------

api.MapGet("/trades", async ([AsParameters] TradeFilter f, JournalDbContext db) =>
{
    var list = await Api.LoadTrades(db, f);
    return list.OrderByDescending(x => x.Trade.CloseTime ?? x.Trade.OpenTime).ToList();
});

api.MapGet("/trades/{key}", async (string key, JournalDbContext db) =>
{
    var trade = await db.Trades.AsNoTracking().Include(t => t.Legs).FirstOrDefaultAsync(t => t.TradeKey == key);
    if (trade is null) return Results.NotFound(new { message = "This trade no longer exists. It may have been regrouped after a sync." });
    var note = await db.TradeNotes.AsNoTracking().FirstOrDefaultAsync(n => n.TradeKey == key);
    var execKeys = trade.Legs.SelectMany(l => l.ExecKeys.Split(',', StringSplitOptions.RemoveEmptyEntries)).Distinct().ToList();
    var fills = (await db.Executions.AsNoTracking().Where(e => execKeys.Contains(e.ExecKey)).ToListAsync())
        .OrderBy(e => e.TradeTime).ToList();
    return Results.Ok(new { trade, note, fills });
});

api.MapPut("/trades/{key}/note", async (string key, TradeNote input, JournalDbContext db) =>
{
    if (!await db.Trades.AnyAsync(t => t.TradeKey == key)) return Results.NotFound();
    var note = await db.TradeNotes.FindAsync(key);
    if (note is null)
    {
        note = new TradeNote { TradeKey = key };
        db.TradeNotes.Add(note);
    }
    note.SetupTags = Api.CleanTags(input.SetupTags);
    note.MistakeTags = Api.CleanTags(input.MistakeTags);
    note.EmotionTags = Api.CleanTags(input.EmotionTags);
    note.StressBefore = Api.Clamp(input.StressBefore, 1, 10);
    note.StressAfter = Api.Clamp(input.StressAfter, 1, 10);
    note.Confidence = Api.Clamp(input.Confidence, 1, 10);
    note.ExecutionRating = Api.Clamp(input.ExecutionRating, 1, 5);
    note.FollowedPlan = input.FollowedPlan;
    note.PlannedRisk = input.PlannedRisk is > 0 ? input.PlannedRisk : null;
    note.EntryReason = input.EntryReason ?? "";
    note.ExitReason = input.ExitReason ?? "";
    note.WentWell = input.WentWell ?? "";
    note.WentWrong = input.WentWrong ?? "";
    note.Lesson = input.Lesson ?? "";
    note.Notes = input.Notes ?? "";
    note.UpdatedAt = DateTime.UtcNow;
    await db.SaveChangesAsync();
    return Results.Ok(note);
});

api.MapPost("/trades/{key}/screenshots", async (string key, IFormFile file, JournalDbContext db) =>
{
    var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
    if (!Api.ImageExtensions.Contains(ext)) return Results.BadRequest(new { message = "Use a PNG, JPG, GIF or WebP image." });
    if (file.Length > 15 * 1024 * 1024) return Results.BadRequest(new { message = "Images must be 15 MB or smaller." });
    if (!await db.Trades.AnyAsync(t => t.TradeKey == key)) return Results.NotFound();

    var name = $"{key}-{Guid.NewGuid():N}{ext}";
    await using (var stream = File.Create(Path.Combine(shotsDir, name)))
        await file.CopyToAsync(stream);

    var note = await db.TradeNotes.FindAsync(key);
    if (note is null)
    {
        note = new TradeNote { TradeKey = key };
        db.TradeNotes.Add(note);
    }
    note.Screenshots = note.Screenshots.Append(name).ToList();
    note.UpdatedAt = DateTime.UtcNow;
    await db.SaveChangesAsync();
    return Results.Ok(note.Screenshots);
}).DisableAntiforgery();

api.MapDelete("/trades/{key}/screenshots/{name}", async (string key, string name, JournalDbContext db) =>
{
    var note = await db.TradeNotes.FindAsync(key);
    if (note is null || !note.Screenshots.Contains(name)) return Results.NotFound();
    note.Screenshots = note.Screenshots.Where(s => s != name).ToList();
    await db.SaveChangesAsync();
    var path = Path.Combine(shotsDir, Path.GetFileName(name));
    if (File.Exists(path)) File.Delete(path);
    return Results.Ok(note.Screenshots);
});

api.MapGet("/screenshots/{name}", (string name) =>
{
    var safe = Path.GetFileName(name);
    var path = Path.Combine(shotsDir, safe);
    if (safe != name || !File.Exists(path)) return Results.NotFound();
    var type = Path.GetExtension(safe).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        _ => "image/jpeg",
    };
    return Results.File(path, type);
});

// ---------- dashboard and reports ----------

api.MapGet("/dashboard", async ([AsParameters] TradeFilter f, JournalDbContext db) =>
{
    var list = await Api.LoadTrades(db, f);
    var data = StatsService.Dashboard(list);
    var recent = list.Where(x => x.Trade.Status == "Closed")
        .OrderByDescending(x => x.Trade.CloseTime).Take(8).ToList();
    var open = (await Api.LoadTrades(db, f with { From = null, To = null, Status = "Open" }))
        .OrderByDescending(x => x.Trade.OpenTime).ToList();
    return new { data.Summary, data.Equity, data.Days, recent, open };
});

api.MapGet("/reports", async ([AsParameters] TradeFilter f, JournalDbContext db) =>
    StatsService.Reports(await Api.LoadTrades(db, f)));

api.MapGet("/meta", async (JournalDbContext db, SettingsService settings) =>
{
    var trades = await db.Trades.AsNoTracking().Select(t => new { t.Underlying, t.Strategy }).ToListAsync();
    var notes = await db.TradeNotes.AsNoTracking().ToListAsync();
    var tags = (await settings.GetAsync()).Tags;
    static List<string> Merge(IEnumerable<string> a, IEnumerable<string> b) =>
        a.Concat(b).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    return new
    {
        underlyings = trades.Select(t => t.Underlying).Distinct().Order().ToList(),
        strategies = trades.Select(t => t.Strategy).Distinct().Order().ToList(),
        tags = new TagLists(
            Merge(tags.Setup, notes.SelectMany(n => n.SetupTags)),
            Merge(tags.Mistake, notes.SelectMany(n => n.MistakeTags)),
            Merge(tags.Emotion, notes.SelectMany(n => n.EmotionTags))),
    };
});

// ---------- daily journal ----------

api.MapGet("/daily", async (DateTime? from, DateTime? to, string? asset, JournalDbContext db) =>
{
    var list = await Api.LoadTrades(db, new TradeFilter(from, to, asset, "Closed", null, null, null, null, null, null));
    var days = list.Where(x => !x.Trade.IncompleteHistory)
        .GroupBy(x => x.Trade.CloseTime!.Value.ToString("yyyy-MM-dd"))
        .ToDictionary(g => g.Key, g => new { pnl = g.Sum(x => x.Trade.NetPnl), trades = g.Count(), wins = g.Count(x => x.Trade.NetPnl > 0) });
    var notes = await db.DailyNotes.AsNoTracking().ToListAsync();
    var fromKey = from?.ToString("yyyy-MM-dd") ?? "0000";
    var toKey = to?.ToString("yyyy-MM-dd") ?? "9999";
    var noteMap = notes.Where(n => string.CompareOrdinal(n.Date, fromKey) >= 0 && string.CompareOrdinal(n.Date, toKey) <= 0)
        .ToDictionary(n => n.Date);
    return days.Keys.Union(noteMap.Keys).OrderByDescending(d => d, StringComparer.Ordinal).Select(d =>
    {
        var stats = days.GetValueOrDefault(d);
        var note = noteMap.GetValueOrDefault(d);
        return new
        {
            date = d,
            pnl = stats?.pnl ?? 0m,
            trades = stats?.trades ?? 0,
            wins = stats?.wins ?? 0,
            hasNote = note is not null,
            mood = note?.Mood,
            stress = note?.Stress,
        };
    }).ToList();
});

api.MapGet("/daily/{date}", async (string date, JournalDbContext db) =>
{
    if (!Api.TryDay(date, out var day)) return Results.BadRequest(new { message = "Use the date format yyyy-MM-dd." });
    var note = await db.DailyNotes.AsNoTracking().FirstOrDefaultAsync(n => n.Date == date);
    var trades = (await Api.LoadTrades(db, new TradeFilter(null, null, null, null, null, null, null, null, null, null)))
        .Where(x => x.Trade.OpenTime.Date == day || x.Trade.CloseTime?.Date == day)
        .OrderBy(x => x.Trade.OpenTime).ToList();
    return Results.Ok(new { note, trades });
});

api.MapPut("/daily/{date}", async (string date, DailyNote input, JournalDbContext db) =>
{
    if (!Api.TryDay(date, out _)) return Results.BadRequest(new { message = "Use the date format yyyy-MM-dd." });
    var note = await db.DailyNotes.FindAsync(date);
    if (note is null)
    {
        note = new DailyNote { Date = date };
        db.DailyNotes.Add(note);
    }
    note.Mood = Api.Clamp(input.Mood, 1, 10);
    note.Stress = Api.Clamp(input.Stress, 1, 10);
    note.Focus = Api.Clamp(input.Focus, 1, 10);
    note.SleepHours = input.SleepHours is >= 0 and <= 24 ? input.SleepHours : null;
    note.Plan = input.Plan ?? "";
    note.MarketNotes = input.MarketNotes ?? "";
    note.Review = input.Review ?? "";
    note.Lessons = input.Lessons ?? "";
    note.UpdatedAt = DateTime.UtcNow;
    await db.SaveChangesAsync();
    return Results.Ok(note);
});

// ---------- settings and sync ----------

api.MapGet("/settings", (SettingsService s) => s.GetAsync());

api.MapPut("/settings", async (SettingsUpdate u, SettingsService s) =>
{
    await s.UpdateAsync(u);
    return await s.GetAsync();
});

api.MapPost("/sync", async (SyncRequest req, SyncService sync, SettingsService settings, CancellationToken ct) =>
{
    var cfg = await settings.GetAsync();
    var to = (req.To ?? DateTime.Today).Date;
    var from = (req.From ?? to.AddDays(-(req.Days ?? cfg.DefaultDays) + 1)).Date;
    if (from > to) return Results.BadRequest(new { message = "The start date must be before the end date." });
    return Results.Ok(await sync.SyncFromFlexAsync(from, to, ct));
});

api.MapPost("/import", async (IFormFile file, SyncService sync, CancellationToken ct) =>
{
    using var reader = new StreamReader(file.OpenReadStream());
    var xml = await reader.ReadToEndAsync(ct);
    return Results.Ok(await sync.ImportXmlAsync(xml, file.FileName, ct));
}).DisableAntiforgery();

api.MapPost("/rebuild", async (SyncService sync, CancellationToken ct) =>
{
    await sync.RebuildTradesAsync(ct);
    return Results.Ok(new { message = "Trades rebuilt from stored fills." });
});

api.MapGet("/sync/logs", async (JournalDbContext db) =>
    await db.SyncLogs.AsNoTracking().OrderByDescending(l => l.Id).Take(20).ToListAsync());

app.Run();

// ---------- helpers ----------

public record TradeFilter(
    DateTime? From, DateTime? To, string? Asset, string? Status, string? Result,
    string? Underlying, string? Strategy, string? Tag, string? Journaled, string? Q);

public record SyncRequest(DateTime? From, DateTime? To, int? Days);

static class Api
{
    public static readonly HashSet<string> ImageExtensions = new() { ".png", ".jpg", ".jpeg", ".gif", ".webp" };

    public static async Task<List<TradeWithNote>> LoadTrades(JournalDbContext db, TradeFilter f)
    {
        var trades = await db.Trades.AsNoTracking().Include(t => t.Legs).ToListAsync();
        var notes = await db.TradeNotes.AsNoTracking().ToDictionaryAsync(n => n.TradeKey);
        IEnumerable<TradeWithNote> q = trades.Select(t => new TradeWithNote(t, notes.GetValueOrDefault(t.TradeKey)));

        static DateTime Day(Trade t) => (t.CloseTime ?? t.OpenTime).Date;
        static bool Is(string? a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        if (f.From is { } from) q = q.Where(x => Day(x.Trade) >= from.Date);
        if (f.To is { } to) q = q.Where(x => Day(x.Trade) <= to.Date);
        if (!string.IsNullOrEmpty(f.Asset)) q = q.Where(x => Is(x.Trade.AssetCategory, f.Asset));
        if (!string.IsNullOrEmpty(f.Status)) q = q.Where(x => Is(x.Trade.Status, f.Status));
        if (Is(f.Result, "win")) q = q.Where(x => x.Trade.Status == "Closed" && x.Trade.NetPnl > 0);
        if (Is(f.Result, "loss")) q = q.Where(x => x.Trade.Status == "Closed" && x.Trade.NetPnl < 0);
        if (!string.IsNullOrEmpty(f.Underlying)) q = q.Where(x => Is(x.Trade.Underlying, f.Underlying));
        if (!string.IsNullOrEmpty(f.Strategy)) q = q.Where(x => Is(x.Trade.Strategy, f.Strategy));
        if (!string.IsNullOrEmpty(f.Tag))
            q = q.Where(x => x.Note is not null &&
                             x.Note.SetupTags.Concat(x.Note.MistakeTags).Concat(x.Note.EmotionTags).Any(t => Is(t, f.Tag)));
        if (Is(f.Journaled, "yes")) q = q.Where(x => x.Note is not null);
        if (Is(f.Journaled, "no")) q = q.Where(x => x.Note is null);
        if (!string.IsNullOrWhiteSpace(f.Q))
        {
            var term = f.Q.Trim();
            q = q.Where(x =>
                x.Trade.Underlying.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                x.Trade.Strategy.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                (x.Note is not null && new[] { x.Note.EntryReason, x.Note.ExitReason, x.Note.WentWell, x.Note.WentWrong, x.Note.Lesson, x.Note.Notes }
                    .Any(s => s.Contains(term, StringComparison.OrdinalIgnoreCase))));
        }
        return q.ToList();
    }

    public static List<string> CleanTags(List<string>? tags) =>
        (tags ?? new()).Select(t => t.Trim()).Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    public static int? Clamp(int? v, int min, int max) => v is null ? null : Math.Clamp(v.Value, min, max);

    public static bool TryDay(string s, out DateTime day) =>
        DateTime.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out day);
}
