using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using TradeJournal.Models;

namespace TradeJournal.Data;

public class JournalDbContext(DbContextOptions<JournalDbContext> options) : DbContext(options)
{
    public DbSet<Execution> Executions => Set<Execution>();
    public DbSet<Trade> Trades => Set<Trade>();
    public DbSet<TradeLeg> TradeLegs => Set<TradeLeg>();
    public DbSet<TradeNote> TradeNotes => Set<TradeNote>();
    public DbSet<DailyNote> DailyNotes => Set<DailyNote>();
    public DbSet<AppSetting> Settings => Set<AppSetting>();
    public DbSet<SyncLog> SyncLogs => Set<SyncLog>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Execution>().HasIndex(e => e.ExecKey).IsUnique();
        b.Entity<Execution>().HasIndex(e => e.TradeTime);

        b.Entity<Trade>().HasIndex(t => t.TradeKey).IsUnique();
        b.Entity<Trade>()
            .HasMany(t => t.Legs)
            .WithOne()
            .HasForeignKey(l => l.TradeId)
            .OnDelete(DeleteBehavior.Cascade);

        var listConverter = new ValueConverter<List<string>, string>(
            v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
            v => JsonSerializer.Deserialize<List<string>>(v, (JsonSerializerOptions?)null) ?? new List<string>());
        var listComparer = new ValueComparer<List<string>>(
            (a, c) => a!.SequenceEqual(c!),
            v => v.Aggregate(0, (h, s) => HashCode.Combine(h, s.GetHashCode())),
            v => v.ToList());

        var note = b.Entity<TradeNote>();
        note.HasKey(n => n.TradeKey);
        note.Property(n => n.SetupTags).HasConversion(listConverter, listComparer);
        note.Property(n => n.MistakeTags).HasConversion(listConverter, listComparer);
        note.Property(n => n.EmotionTags).HasConversion(listConverter, listComparer);
        note.Property(n => n.Screenshots).HasConversion(listConverter, listComparer);

        b.Entity<DailyNote>().HasKey(d => d.Date);
        b.Entity<AppSetting>().HasKey(s => s.Key);
    }
}
