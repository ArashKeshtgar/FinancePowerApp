using FinancePowerApp.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace FinancePowerApp.Api.Domain;

/// <summary>
/// Statements from the posted ledger, for one business or all of them added together (no intercompany
/// eliminations: the businesses don't bill each other in this platform).
/// </summary>
public class Reports(FinanceDbContext db)
{
    public record AccountBalance(int AccountId, string Number, string Name, string Type, string? Subtype, decimal Debit, decimal Credit, decimal Balance);
    public record Section(string Name, AccountBalance[] Accounts, decimal Total);
    public record TrialBalance(DateOnly AsOf, AccountBalance[] Accounts, decimal TotalDebit, decimal TotalCredit);
    public record IncomeStatement(DateOnly From, DateOnly To, Section Revenue, Section Expenses, decimal NetIncome);
    public record BalanceSheet(DateOnly AsOf, Section Assets, Section Liabilities, Section Equity, decimal CurrentEarnings, decimal LiabilitiesAndEquity);
    public record HstLine(string Period, string Entity, decimal Sales, decimal TaxCollected, decimal InputTaxCredits, decimal NetTax);
    public record AgingRow(long ItemId, string Entity, string PartyType, string Party, string Source, string Reference, DateOnly ItemDate, DateOnly DueDate,
        decimal Amount, decimal Paid, decimal Adjusted, decimal Balance, int DaysOverdue, string Bucket);
    public record Dashboard(decimal Cash, decimal Receivables, decimal Payables, decimal HstOwing, decimal RevenueThisMonth, decimal ExpensesThisMonth,
        NamedAmount[] RevenueBySource, NamedAmount[] RevenueByEntity, NamedAmount[] ReceivablesByPayer, NamedAmount[] MonthlyNet, int DocumentsWaiting, int DraftEntries);
    public record NamedAmount(string Name, decimal Amount);

    private IQueryable<JournalLine> Posted(int? entityId) =>
        db.JournalLines.Where(l => db.JournalEntries.Any(e => e.JournalEntryId == l.JournalEntryId && e.Status != JournalStatus.Draft
            && (entityId == null || e.EntityId == entityId)));

    private IQueryable<JournalLine> Between(int? entityId, DateOnly? from, DateOnly to) =>
        Posted(entityId).Where(l => db.JournalEntries.Any(e => e.JournalEntryId == l.JournalEntryId && e.EntryDate <= to && (from == null || e.EntryDate >= from)));

    private async Task<AccountBalance[]> Balances(IQueryable<JournalLine> lines)
    {
        var sums = await lines.GroupBy(l => l.AccountId).Select(g => new { g.Key, Debit = g.Sum(x => x.Debit), Credit = g.Sum(x => x.Credit) }).ToListAsync();
        var accounts = await db.Accounts.AsNoTracking().ToDictionaryAsync(a => a.AccountId);
        return sums.Select(s =>
        {
            var a = accounts[s.Key];
            var balance = AccountTypes.DebitNormal(a.Type) ? s.Debit - s.Credit : s.Credit - s.Debit;
            return new AccountBalance(a.AccountId, a.Number, a.Name, a.Type, a.Subtype, s.Debit, s.Credit, balance);
        }).Where(b => b.Debit != 0 || b.Credit != 0).OrderBy(b => b.Number).ToArray();
    }

    private static Section Of(string name, IEnumerable<AccountBalance> accounts)
    {
        var list = accounts.ToArray();
        return new Section(name, list, list.Sum(a => a.Balance));
    }

    public async Task<TrialBalance> TrialBalanceAsync(int? entityId, DateOnly asOf)
    {
        var rows = await Balances(Between(entityId, null, asOf));
        return new TrialBalance(asOf, rows, rows.Sum(r => r.Debit), rows.Sum(r => r.Credit));
    }

    public async Task<IncomeStatement> IncomeStatementAsync(int? entityId, DateOnly from, DateOnly to)
    {
        var rows = await Balances(Between(entityId, from, to));
        var revenue = Of("Revenue", rows.Where(r => r.Type == AccountTypes.Revenue));
        var expenses = Of("Expenses", rows.Where(r => r.Type == AccountTypes.Expense));
        return new IncomeStatement(from, to, revenue, expenses, revenue.Total - expenses.Total);
    }

    /// <summary>Calendar fiscal years: this year's revenue less expenses shows as current earnings in equity.</summary>
    public async Task<BalanceSheet> BalanceSheetAsync(int? entityId, DateOnly asOf)
    {
        var rows = await Balances(Between(entityId, null, asOf));
        var yearStart = new DateOnly(asOf.Year, 1, 1);
        var year = await Balances(Between(entityId, yearStart, asOf));
        var prior = await Balances(Between(entityId, null, yearStart.AddDays(-1)));
        decimal Earnings(AccountBalance[] r) => r.Where(x => x.Type == AccountTypes.Revenue).Sum(x => x.Balance) - r.Where(x => x.Type == AccountTypes.Expense).Sum(x => x.Balance);
        var current = Earnings(year);
        var retainedPrior = Earnings(prior);
        var assets = Of("Assets", rows.Where(r => r.Type == AccountTypes.Asset));
        var liabilities = Of("Liabilities", rows.Where(r => r.Type == AccountTypes.Liability));
        var equityAccounts = rows.Where(r => r.Type == AccountTypes.Equity).ToList();
        if (retainedPrior != 0)
            equityAccounts.Add(new AccountBalance(0, "", "Earnings of prior years (not yet closed)", AccountTypes.Equity, "RetainedEarnings", 0, 0, retainedPrior));
        var equity = Of("Equity", equityAccounts);
        return new BalanceSheet(asOf, assets, liabilities, equity, current, liabilities.Total + equity.Total + current);
    }

    public async Task<HstLine[]> HstAsync(int? entityId, DateOnly start, DateOnly end)
    {
        var lines = await (from l in db.JournalLines
                           join e in db.JournalEntries on l.JournalEntryId equals e.JournalEntryId
                           join a in db.Accounts on l.AccountId equals a.AccountId
                           join en in db.Entities on e.EntityId equals en.EntityId
                           join p in db.Periods on e.PeriodId equals p.PeriodId
                           where e.Status != JournalStatus.Draft && e.EntryDate >= start && e.EntryDate <= end && (entityId == null || e.EntityId == entityId)
                           select new { p.Year, p.Month, en.Code, a.Type, a.Subtype, l.Debit, l.Credit }).ToListAsync();
        return lines.GroupBy(x => (x.Year, x.Month, x.Code)).OrderBy(g => g.Key.Year).ThenBy(g => g.Key.Month).ThenBy(g => g.Key.Code).Select(g =>
        {
            var sales = g.Where(x => x.Type == AccountTypes.Revenue).Sum(x => x.Credit - x.Debit);
            var collected = g.Where(x => x.Subtype == "TaxPayable").Sum(x => x.Credit - x.Debit);
            var itc = g.Where(x => x.Subtype == "TaxRecoverable").Sum(x => x.Debit - x.Credit);
            return new HstLine($"{g.Key.Year:0000}-{g.Key.Month:00}", g.Key.Code, sales, collected, itc, collected - itc);
        }).ToArray();
    }

    public async Task<AgingRow[]> AgingAsync(int? entityId, DateOnly asOf, bool payables)
    {
        var entities = await db.Entities.AsNoTracking().ToDictionaryAsync(e => e.EntityId, e => e.Code);
        static string Bucket(int days) => days <= 0 ? "Current" : days <= 30 ? "1-30" : days <= 60 ? "31-60" : days <= 90 ? "61-90" : "90+";
        if (payables)
        {
            var items = await db.Payables.AsNoTracking().Include(i => i.Party).Where(i => i.Balance != 0 && (entityId == null || i.EntityId == entityId)).ToListAsync();
            return items.Select(i =>
            {
                var days = asOf.DayNumber - i.DueDate.DayNumber;
                return new AgingRow(i.ItemId, entities[i.EntityId], i.Party.PartyType, i.Party.Name, i.Source, i.Reference, i.ItemDate, i.DueDate, i.Amount, i.Paid, 0, i.Balance, days, Bucket(days));
            }).OrderByDescending(r => r.DaysOverdue).ToArray();
        }
        var open = await db.Receivables.AsNoTracking().Include(i => i.Party).Where(i => i.Balance != 0 && (entityId == null || i.EntityId == entityId)).ToListAsync();
        return open.Select(i =>
        {
            var days = asOf.DayNumber - i.DueDate.DayNumber;
            return new AgingRow(i.ItemId, entities[i.EntityId], i.Party.PartyType, i.Party.Name, i.Source, i.Reference, i.ItemDate, i.DueDate, i.Amount, i.Paid, i.Adjusted, i.Balance, days, Bucket(days));
        }).OrderByDescending(r => r.DaysOverdue).ToArray();
    }

    public async Task<Dashboard> DashboardAsync(int? entityId, DateOnly today)
    {
        var monthStart = new DateOnly(today.Year, today.Month, 1);
        var all = await Balances(Between(entityId, null, today));
        var month = await Balances(Between(entityId, monthStart, today));

        var revenueLines = from l in Between(entityId, monthStart, today)
                           join a in db.Accounts on l.AccountId equals a.AccountId
                           join e in db.JournalEntries on l.JournalEntryId equals e.JournalEntryId
                           join en in db.Entities on e.EntityId equals en.EntityId
                           where a.Type == AccountTypes.Revenue
                           select new { e.Source, Entity = en.Name, Amount = l.Credit - l.Debit };
        var bySource = await revenueLines.GroupBy(x => x.Source).Select(g => new NamedAmount(g.Key, g.Sum(x => x.Amount))).ToArrayAsync();
        var byEntity = await revenueLines.GroupBy(x => x.Entity).Select(g => new NamedAmount(g.Key, g.Sum(x => x.Amount))).ToArrayAsync();
        var byPayer = await db.Receivables.Where(i => i.Balance != 0 && (entityId == null || i.EntityId == entityId))
            .GroupBy(i => i.Party.PartyType).Select(g => new NamedAmount(g.Key, g.Sum(i => i.Balance))).ToArrayAsync();

        // Net income for each of the last six months.
        var monthly = new List<NamedAmount>();
        for (var i = 5; i >= 0; i--)
        {
            var start = monthStart.AddMonths(-i);
            var end = start.AddMonths(1).AddDays(-1);
            var rows = await Balances(Between(entityId, start, end));
            monthly.Add(new($"{start:yyyy-MM}", rows.Where(r => r.Type == AccountTypes.Revenue).Sum(r => r.Balance) - rows.Where(r => r.Type == AccountTypes.Expense).Sum(r => r.Balance)));
        }

        return new Dashboard(
            all.Where(a => a.Subtype is "Bank" or "Cash" or "Clearing").Sum(a => a.Balance),
            all.Where(a => a.Subtype == "Receivable").Sum(a => a.Balance),
            all.Where(a => a.Subtype == "Payable").Sum(a => a.Balance),
            all.Where(a => a.Subtype == "TaxPayable").Sum(a => a.Balance) - all.Where(a => a.Subtype == "TaxRecoverable").Sum(a => a.Balance),
            month.Where(a => a.Type == AccountTypes.Revenue).Sum(a => a.Balance),
            month.Where(a => a.Type == AccountTypes.Expense).Sum(a => a.Balance),
            bySource.OrderByDescending(x => x.Amount).ToArray(), byEntity.OrderByDescending(x => x.Amount).ToArray(),
            byPayer.OrderByDescending(x => x.Amount).ToArray(), [.. monthly],
            await db.Documents.CountAsync(d => d.Status == DocumentStatus.NeedsMapping && (entityId == null || d.EntityId == entityId || d.EntityId == null)),
            await db.JournalEntries.CountAsync(e => e.Status == JournalStatus.Draft && (entityId == null || e.EntityId == entityId)));
    }
}
