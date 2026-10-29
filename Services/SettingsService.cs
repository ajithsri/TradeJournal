using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using TradeJournal.Data;
using TradeJournal.Models;

namespace TradeJournal.Services;

public record TagLists(List<string> Setup, List<string> Mistake, List<string> Emotion);

public record SettingsView(bool HasToken, string TokenHint, string QueryId, int DefaultDays, bool UseDateRange, TagLists Tags);

public record SettingsUpdate(string? Token, string? QueryId, int? DefaultDays, bool? UseDateRange, TagLists? Tags);

public class SettingsService(JournalDbContext db, IDataProtectionProvider dp)
{
    private const string TokenKey = "flex.token";
    private const string QueryKey = "flex.queryId";
    private const string DaysKey = "sync.defaultDays";
    private const string RangeKey = "sync.useDateRange";
    private const string TagsKey = "tags";

    private readonly IDataProtector _protector = dp.CreateProtector("TradeJournal.FlexToken");

    public static readonly TagLists DefaultTags = new(
        Setup: new() { "Trend continuation", "Breakout", "Pullback to support", "Earnings play", "Premium selling", "Mean reversion", "Gap fill", "Hedge" },
        Mistake: new() { "Entered too early", "Chased the move", "Oversized position", "Ignored my stop", "Held past exit plan", "Closed winner too early", "No clear plan", "Traded out of boredom", "Revenge trade", "Ignored IV / earnings date" },
        Emotion: new() { "Calm", "Confident", "Focused", "Anxious", "Fearful", "Greedy", "Impatient", "Frustrated", "FOMO", "Tired", "Overconfident" });

    public async Task<SettingsView> GetAsync()
    {
        var all = await db.Settings.AsNoTracking().ToDictionaryAsync(s => s.Key, s => s.Value);
        var token = Unprotect(all.GetValueOrDefault(TokenKey));
        return new SettingsView(
            HasToken: !string.IsNullOrEmpty(token),
            TokenHint: string.IsNullOrEmpty(token) ? "" : "•••• " + token[^Math.Min(4, token.Length)..],
            QueryId: all.GetValueOrDefault(QueryKey) ?? "",
            DefaultDays: int.TryParse(all.GetValueOrDefault(DaysKey), out var d) ? d : 30,
            UseDateRange: !bool.TryParse(all.GetValueOrDefault(RangeKey), out var r) || r,
            Tags: Deserialize(all.GetValueOrDefault(TagsKey)) ?? DefaultTags);
    }

    public async Task<(string Token, string QueryId, bool UseDateRange)> GetFlexAsync()
    {
        var all = await db.Settings.AsNoTracking().ToDictionaryAsync(s => s.Key, s => s.Value);
        var token = Unprotect(all.GetValueOrDefault(TokenKey)) ?? "";
        var query = all.GetValueOrDefault(QueryKey) ?? "";
        if (token.Length == 0 || query.Length == 0)
            throw new FlexException("Add your Flex token and Flex Query ID on the Sync page first.");
        return (token, query, !bool.TryParse(all.GetValueOrDefault(RangeKey), out var r) || r);
    }

    public async Task UpdateAsync(SettingsUpdate u)
    {
        if (u.Token is not null) await SetAsync(TokenKey, u.Token.Trim().Length == 0 ? "" : _protector.Protect(u.Token.Trim()));
        if (u.QueryId is not null) await SetAsync(QueryKey, u.QueryId.Trim());
        if (u.DefaultDays is not null) await SetAsync(DaysKey, Math.Clamp(u.DefaultDays.Value, 1, 3650).ToString());
        if (u.UseDateRange is not null) await SetAsync(RangeKey, u.UseDateRange.Value.ToString());
        if (u.Tags is not null)
        {
            static List<string> Clean(List<string>? l) =>
                (l ?? new()).Select(s => s.Trim()).Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            await SetAsync(TagsKey, JsonSerializer.Serialize(new TagLists(Clean(u.Tags.Setup), Clean(u.Tags.Mistake), Clean(u.Tags.Emotion))));
        }
        await db.SaveChangesAsync();
    }

    private async Task SetAsync(string key, string value)
    {
        var row = await db.Settings.FindAsync(key);
        if (row is null) db.Settings.Add(new AppSetting { Key = key, Value = value });
        else row.Value = value;
    }

    private string? Unprotect(string? value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        try { return _protector.Unprotect(value); }
        catch { return null; } // keys were reset; the token has to be entered again
    }

    private static TagLists? Deserialize(string? json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        try { return JsonSerializer.Deserialize<TagLists>(json); }
        catch { return null; }
    }
}
