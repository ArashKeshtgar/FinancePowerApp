using Microsoft.EntityFrameworkCore;

namespace FinancePowerApp.Api.Data;

/// <summary>Maps the tables built by database/*.sql. No migrations: the SQL scripts own the schema.</summary>
public class FinanceDbContext(DbContextOptions<FinanceDbContext> options) : DbContext(options)
{
    public DbSet<Entity> Entities => Set<Entity>();
    public DbSet<EntitySource> EntitySources => Set<EntitySource>();
    public DbSet<Party> Parties => Set<Party>();
    public DbSet<TaxCode> TaxCodes => Set<TaxCode>();
    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<Period> Periods => Set<Period>();
    public DbSet<JournalEntry> JournalEntries => Set<JournalEntry>();
    public DbSet<JournalLine> JournalLines => Set<JournalLine>();
    public DbSet<CategoryMap> CategoryMaps => Set<CategoryMap>();
    public DbSet<ControlMap> ControlMaps => Set<ControlMap>();
    public DbSet<CashMap> CashMaps => Set<CashMap>();
    public DbSet<ArItem> Receivables => Set<ArItem>();
    public DbSet<ApItem> Payables => Set<ApItem>();
    public DbSet<Document> Documents => Set<Document>();
    public DbSet<Expense> Expenses => Set<Expense>();

    protected override void ConfigureConventions(ModelConfigurationBuilder b)
    {
        b.Properties<decimal>().HavePrecision(14, 2);
        b.Properties<decimal?>().HavePrecision(14, 2);
    }

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Entity>(e => { e.ToTable("Entity", "org"); e.Ignore(x => x.ClaimsInputTaxCredits); });
        b.Entity<EntitySource>(e => { e.ToTable("EntitySource", "org"); e.HasKey(x => new { x.Source, x.SourceKey }); });
        b.Entity<Party>().ToTable("Party", "org");
        b.Entity<TaxCode>(e => { e.ToTable("TaxCode", "tax"); e.HasKey(x => x.Code); e.Property(x => x.RatePercent).HasPrecision(5, 2); });
        b.Entity<Account>().ToTable("Account", "gl");
        b.Entity<Period>(e =>
        {
            e.ToTable("AccountingPeriods", "dbo");
            e.Ignore(x => x.Start); e.Ignore(x => x.End); e.Ignore(x => x.Label);
        });
        b.Entity<JournalEntry>(e =>
        {
            e.ToTable("JournalEntry", "gl");
            e.HasMany(x => x.Lines).WithOne().HasForeignKey(x => x.JournalEntryId);
        });
        b.Entity<JournalLine>().ToTable("JournalLine", "gl");
        b.Entity<CategoryMap>().ToTable("CategoryMap", "gl");
        b.Entity<ControlMap>().ToTable("ControlMap", "gl");
        b.Entity<CashMap>(e => { e.ToTable("CashMap", "gl"); e.HasKey(x => x.Method); });
        b.Entity<ArItem>(e =>
        {
            e.ToTable("Item", "ar"); e.HasKey(x => x.ItemId);
            e.Property(x => x.Balance).HasComputedColumnSql("Amount - Paid - Adjusted", stored: true);
        });
        b.Entity<ApItem>(e =>
        {
            e.ToTable("Item", "ap"); e.HasKey(x => x.ItemId);
            e.Property(x => x.Balance).HasComputedColumnSql("Amount - Paid", stored: true);
        });
        b.Entity<Document>().ToTable("Document", "integ");
        b.Entity<Expense>(e => { e.ToTable("Expenses", "dbo"); e.Property(x => x.Amount).HasPrecision(12, 2); e.Property(x => x.TaxAmount).HasPrecision(12, 2); });
    }

    public async Task<int> NextJournalNumberAsync() =>
        (await Database.SqlQueryRaw<int>("SELECT NEXT VALUE FOR gl.JournalNumber AS Value").ToListAsync()).Single();
}
