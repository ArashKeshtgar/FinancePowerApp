using System.Text.Json;
using FinancePowerApp.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace FinancePowerApp.Api.Domain;

/// <summary>
/// Everything that writes to the ledger: documents from the applications, manual entries, reversals, the Power
/// App's approved expenses, and closing months. Each operation is one transaction.
/// </summary>
public class Ledger(FinanceDbContext db, TimeProvider clock)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public class LedgerException(string message) : Exception(message);

    // ------------------------------------------------------------ documents from the applications

    /// <summary>Stores the document (once per source and external id) and posts it if everything it names has an account.</summary>
    public async Task<(Document Document, bool Duplicate)> ReceiveAsync(string source, FinancialDocument d)
    {
        var existing = await db.Documents.SingleOrDefaultAsync(x => x.Source == source && x.ExternalId == d.ExternalId);
        if (existing is not null)
            return (existing, true);

        var doc = new Document
        {
            Source = source, ExternalId = d.ExternalId, SourceKey = d.SourceKey, Kind = d.Kind, PartyType = d.Party?.Type,
            PartyCode = d.Party?.Code, DocumentDate = d.Date, Reference = d.Reference,
            Total = d.Lines?.Sum(l => l.Amount + (l.Tax ?? 0)) ?? 0,
            PayloadJson = JsonSerializer.Serialize(d, Json), Status = DocumentStatus.Received, ReceivedUtc = Now,
        };
        db.Documents.Add(doc);
        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            // Sent twice at the same moment: the other request stored it.
            db.ChangeTracker.Clear();
            var stored = await db.Documents.SingleOrDefaultAsync(x => x.Source == source && x.ExternalId == d.ExternalId);
            if (stored is null) throw;
            return (stored, true);
        }
        await PostDocumentAsync(doc);
        return (doc, false);
    }

    /// <summary>Posts a stored document. When a word has no account it waits as NeedsMapping, to be posted again once mapped.</summary>
    public async Task PostDocumentAsync(Document doc)
    {
        if (doc.Status is DocumentStatus.Posted or DocumentStatus.Reversed)
            return;
        var d = JsonSerializer.Deserialize<FinancialDocument>(doc.PayloadJson, Json)!;
        var entity = await ResolveEntityAsync(doc.Source, d.SourceKey);
        if (entity is null)
        {
            await Wait(doc, $"No business is mapped to {doc.Source} \"{d.SourceKey}\". Add it under Businesses → Sources.");
            return;
        }
        doc.EntityId = entity.EntityId;

        if (d.Kind == DocumentKinds.Reversal)
        {
            await ReverseDocumentAsync(doc, d, entity);
            return;
        }

        var maps = await MapsAsync(entity.EntityId);
        var plan = PostingEngine.Plan(d, entity, maps);
        if (!plan.Complete)
        {
            await Wait(doc, string.Join("; ", plan.Missing));
            return;
        }
        if (plan.Debits != plan.Credits)
            throw new LedgerException($"Document {doc.DocumentId} does not balance ({plan.Debits} ≠ {plan.Credits}).");

        await InTransaction(async () =>
        {
            var party = d.Party is null ? null : await PartyAsync(d.Party);
            var (period, late) = await PeriodForAsync(d.Date);
            var entry = await NewEntryAsync(entity, period, late ? period.Start : d.Date, doc.Source,
                Memo(d, late), doc.Source);
            entry.DocumentId = doc.DocumentId;
            entry.LatePosting = late;
            short n = 1;
            foreach (var l in plan.Lines)
                entry.Lines.Add(new JournalLine
                {
                    LineNumber = n++, AccountId = l.AccountId, Debit = l.Debit, Credit = l.Credit, PartyId = l.WithParty ? party?.PartyId : null,
                    Department = l.Department, ProviderCode = l.ProviderCode, TaxCode = l.TaxCode, Memo = Short(l.Memo, 200),
                });
            Post(entry, doc.Source);
            db.JournalEntries.Add(entry);
            await db.SaveChangesAsync();

            if (party is not null)
                await ApplyEffectsAsync(d.Kind, entity, party, doc, d, plan.Effects);

            doc.Status = DocumentStatus.Posted;
            doc.Error = null;
            doc.JournalEntryId = entry.JournalEntryId;
            doc.PostedUtc = Now;
            await db.SaveChangesAsync();
        });
    }

    private async Task ApplyEffectsAsync(string kind, Entity entity, Party party, Document doc, FinancialDocument d, List<ItemEffect> effects)
    {
        var payable = kind is DocumentKinds.Bill or DocumentKinds.Payment;
        foreach (var e in effects)
        {
            if (payable)
            {
                var item = await db.Payables.SingleOrDefaultAsync(i => i.EntityId == entity.EntityId && i.Source == doc.Source && i.Reference == e.Reference);
                if (e.Raised > 0)
                {
                    if (item is null)
                        db.Payables.Add(new ApItem
                        {
                            EntityId = entity.EntityId, PartyId = party.PartyId, Source = doc.Source, Reference = e.Reference, DocumentId = doc.DocumentId,
                            ItemDate = d.Date, DueDate = d.DueDate ?? d.Date.AddDays(party.PaymentTermsDays), Amount = e.Raised,
                        });
                    else item.Amount += e.Raised;
                }
                if (item is not null) item.Paid += e.Paid;
            }
            else
            {
                var item = await db.Receivables.SingleOrDefaultAsync(i => i.EntityId == entity.EntityId && i.Source == doc.Source && i.Reference == e.Reference);
                if (e.Raised > 0)
                {
                    if (item is null)
                        db.Receivables.Add(new ArItem
                        {
                            EntityId = entity.EntityId, PartyId = party.PartyId, Source = doc.Source, Reference = e.Reference, DocumentId = doc.DocumentId,
                            ItemDate = d.Date, DueDate = d.DueDate ?? d.Date.AddDays(party.PaymentTermsDays), Amount = e.Raised,
                        });
                    else item.Amount += e.Raised;
                }
                if (item is not null)
                {
                    item.Paid += e.Paid;
                    item.Adjusted += e.Adjusted;
                }
            }
        }
        await db.SaveChangesAsync();
    }

    /// <summary>Undoes an earlier document: the mirror of its entry, and its effect on open items taken back.</summary>
    private async Task ReverseDocumentAsync(Document doc, FinancialDocument d, Entity entity)
    {
        var original = await db.Documents.SingleOrDefaultAsync(x => x.Source == doc.Source && x.ExternalId == d.Reverses);
        if (original is null || original.Status != DocumentStatus.Posted || original.JournalEntryId is null)
        {
            await Wait(doc, original is null ? $"Nothing to reverse: {d.Reverses} was never received." : $"{d.Reverses} is not posted ({original.Status}).");
            return;
        }
        await InTransaction(async () =>
        {
            var reversal = await ReverseEntryAsync(original.JournalEntryId.Value, d.Date, doc.Source, d.Memo ?? $"Reverses {original.Reference}");
            reversal.DocumentId = doc.DocumentId;
            var od = JsonSerializer.Deserialize<FinancialDocument>(original.PayloadJson, Json)!;
            var maps = await MapsAsync(entity.EntityId);
            var party = od.Party is null ? null : await PartyAsync(od.Party);
            if (party is not null)
            {
                var undo = PostingEngine.Plan(od, entity, maps).Effects
                    .Select(e => new ItemEffect(e.Reference, Raised: 0, Paid: -e.Paid, Adjusted: e.Raised > 0 ? e.Raised : -e.Adjusted)).ToList();
                // An invoice or bill taken back: its whole amount is adjusted away (payables have no adjustment column, so it is "paid").
                if (od.Kind is DocumentKinds.Bill)
                    undo = undo.Select(u => u with { Paid = u.Adjusted, Adjusted = 0 }).ToList();
                await ApplyEffectsAsync(od.Kind, entity, party, original, od, undo);
            }
            original.Status = DocumentStatus.Reversed;
            doc.Status = DocumentStatus.Posted;
            doc.Error = null;
            doc.JournalEntryId = reversal.JournalEntryId;
            doc.PostedUtc = Now;
            await db.SaveChangesAsync();
        });
    }

    private async Task Wait(Document doc, string why)
    {
        doc.Status = DocumentStatus.NeedsMapping;
        doc.Error = Short(why, 1000);
        await db.SaveChangesAsync();
    }

    // ------------------------------------------------------------ manual entries

    public record ManualLine(int AccountId, decimal Debit, decimal Credit, int? PartyId, string? Department, string? ProviderCode, string? TaxCode, string? Memo);

    public async Task<JournalEntry> CreateManualAsync(int entityId, DateOnly date, string? memo, IReadOnlyList<ManualLine> lines, bool post, string user)
    {
        var entity = await db.Entities.SingleOrDefaultAsync(e => e.EntityId == entityId && e.IsActive) ?? throw new LedgerException("Choose an active business.");
        await CheckLinesAsync(lines);
        var period = await PeriodOfAsync(date);
        if (period.IsClosed)
            throw new LedgerException($"{period.Label} is closed. Reopen it, or date the entry in an open month.");
        JournalEntry entry = null!;
        await InTransaction(async () =>
        {
            entry = await NewEntryAsync(entity, period, date, JournalSources.Manual, memo, user);
            short n = 1;
            foreach (var l in lines)
                entry.Lines.Add(new JournalLine
                {
                    LineNumber = n++, AccountId = l.AccountId, Debit = l.Debit, Credit = l.Credit, PartyId = l.PartyId,
                    Department = Clean(l.Department), ProviderCode = Clean(l.ProviderCode), TaxCode = Clean(l.TaxCode), Memo = Short(l.Memo, 200),
                });
            if (post) Post(entry, user);
            db.JournalEntries.Add(entry);
            await db.SaveChangesAsync();
        });
        return entry;
    }

    public async Task PostDraftAsync(long id, string user)
    {
        var entry = await db.JournalEntries.Include(e => e.Period).SingleOrDefaultAsync(e => e.JournalEntryId == id) ?? throw new LedgerException("No such entry.");
        if (entry.Status != JournalStatus.Draft)
            throw new LedgerException("Only a draft can be posted.");
        if (entry.Period.IsClosed)
            throw new LedgerException($"{entry.Period.Label} is closed.");
        Post(entry, user);
        await db.SaveChangesAsync();
    }

    public async Task DeleteDraftAsync(long id)
    {
        var entry = await db.JournalEntries.SingleOrDefaultAsync(e => e.JournalEntryId == id) ?? throw new LedgerException("No such entry.");
        if (entry.Status != JournalStatus.Draft)
            throw new LedgerException("A posted entry is never deleted: reverse it.");
        db.JournalEntries.Remove(entry);
        await db.SaveChangesAsync();
    }

    /// <summary>A posted entry is corrected by its mirror image, dated in an open month; both stay in the ledger.</summary>
    public async Task<JournalEntry> ReverseEntryAsync(long id, DateOnly date, string user, string? memo)
    {
        var original = await db.JournalEntries.Include(e => e.Lines).Include(e => e.Entity).SingleOrDefaultAsync(e => e.JournalEntryId == id)
            ?? throw new LedgerException("No such entry.");
        if (original.Status != JournalStatus.Posted)
            throw new LedgerException(original.Status == JournalStatus.Reversed ? "Already reversed." : "Only a posted entry can be reversed.");
        var (period, late) = await PeriodForAsync(date);
        var reversal = await NewEntryAsync(original.Entity, period, late ? period.Start : date, original.Source, memo ?? $"Reverses {original.Number}", user);
        reversal.ReversalOfId = original.JournalEntryId;
        reversal.LatePosting = late;
        foreach (var l in original.Lines.OrderBy(l => l.LineNumber))
            reversal.Lines.Add(new JournalLine
            {
                LineNumber = l.LineNumber, AccountId = l.AccountId, Debit = l.Credit, Credit = l.Debit, PartyId = l.PartyId,
                Department = l.Department, ProviderCode = l.ProviderCode, TaxCode = l.TaxCode, Memo = l.Memo,
            });
        Post(reversal, user);
        original.Status = JournalStatus.Reversed;
        db.JournalEntries.Add(reversal);
        await db.SaveChangesAsync();
        return reversal;
    }

    private async Task CheckLinesAsync(IReadOnlyList<ManualLine> lines)
    {
        if (lines.Count < 2)
            throw new LedgerException("An entry needs at least two lines.");
        if (lines.Any(l => l.Debit < 0 || l.Credit < 0 || (l.Debit > 0 && l.Credit > 0) || l.Debit + l.Credit == 0
                || decimal.Round(l.Debit, 2) != l.Debit || decimal.Round(l.Credit, 2) != l.Credit))
            throw new LedgerException("Each line is either a debit or a credit, in dollars and cents.");
        if (lines.Sum(l => l.Debit) != lines.Sum(l => l.Credit))
            throw new LedgerException($"Debits ({lines.Sum(l => l.Debit):N2}) and credits ({lines.Sum(l => l.Credit):N2}) must be equal.");
        var ids = lines.Select(l => l.AccountId).Distinct().ToList();
        var accounts = await db.Accounts.Where(a => ids.Contains(a.AccountId)).ToListAsync();
        if (accounts.Count != ids.Count || accounts.Any(a => !a.IsActive || !a.IsPostable))
            throw new LedgerException("Every line needs an active account that takes postings.");
        var parties = lines.Where(l => l.PartyId is not null).Select(l => l.PartyId!.Value).Distinct().ToList();
        if (parties.Count > 0 && await db.Parties.CountAsync(p => parties.Contains(p.PartyId)) != parties.Count)
            throw new LedgerException("Unknown party.");
    }

    // ------------------------------------------------------------ the Power App's expenses

    /// <summary>
    /// Approved expenses not yet in the ledger become bills: the expense account of their category, the HST (an input
    /// tax credit where the business can claim one), owed to the vendor. Returns how many were posted.
    /// </summary>
    public async Task<(int Posted, List<string> Skipped)> PostApprovedExpensesAsync(string user)
    {
        var skipped = new List<string>();
        var expenses = await db.Expenses.Where(e => e.Status == "Approved" && e.JournalEntryId == null).OrderBy(e => e.ExpenseId).ToListAsync();
        var fallback = await db.Entities.SingleAsync(e => e.IsDefault);
        var posted = 0;
        foreach (var x in expenses)
        {
            var entity = x.EntityId is { } id ? await db.Entities.SingleAsync(e => e.EntityId == id) : fallback;
            var maps = await MapsAsync(entity.EntityId);
            var category = x.Category is not null && maps.Categories.ContainsKey(x.Category) ? x.Category : "Other";
            var date = x.ExpenseDate ?? DateOnly.FromDateTime(x.CreatedAt ?? Now);
            var d = new FinancialDocument(entity.Code, $"expense:{x.ExpenseId}", DocumentKinds.Bill, date, $"EXP-{x.ExpenseId}",
                new DocumentParty(PartyTypes.Supplier, SupplierCode(x.Vendor), x.Vendor ?? "Unknown vendor"),
                [new DocumentLine(category, x.Amount, x.TaxAmount > 0 ? "HST13" : null, x.TaxAmount > 0 ? x.TaxAmount : null, Memo: Short(x.Description, 200))],
                Memo: $"Expense {x.ExpenseId}: {x.Vendor}");
            var plan = PostingEngine.Plan(d, entity, maps);
            if (!plan.Complete)
            {
                skipped.Add($"Expense {x.ExpenseId}: {string.Join("; ", plan.Missing)}");
                continue;
            }
            Period period;
            bool late;
            if (x.PeriodId is { } pid && await db.Periods.SingleOrDefaultAsync(p => p.PeriodId == pid) is { IsClosed: false } own)
                (period, late) = (own, false);
            else
                (period, late) = await PeriodForAsync(date);

            await InTransaction(async () =>
            {
                var party = await PartyAsync(d.Party!);
                var entry = await NewEntryAsync(entity, period, late ? period.Start : date, JournalSources.Expense, d.Memo, user);
                entry.LatePosting = late;
                short n = 1;
                foreach (var l in plan.Lines)
                    entry.Lines.Add(new JournalLine
                    {
                        LineNumber = n++, AccountId = l.AccountId, Debit = l.Debit, Credit = l.Credit, PartyId = l.WithParty ? party.PartyId : null,
                        TaxCode = l.TaxCode, Memo = Short(l.Memo, 200),
                    });
                Post(entry, user);
                db.JournalEntries.Add(entry);
                await db.SaveChangesAsync();
                db.Payables.Add(new ApItem
                {
                    EntityId = entity.EntityId, PartyId = party.PartyId, Source = JournalSources.Expense, Reference = d.Reference,
                    ItemDate = date, DueDate = date.AddDays(party.PaymentTermsDays), Amount = plan.Credits,
                });
                x.JournalEntryId = entry.JournalEntryId;
                x.PeriodId ??= period.PeriodId;
                x.EntityId ??= entity.EntityId;
                await db.SaveChangesAsync();
            });
            posted++;
        }
        return (posted, skipped);
    }

    private static string SupplierCode(string? vendor)
    {
        var code = new string((vendor ?? "UNKNOWN").ToUpperInvariant().Where(c => char.IsAsciiLetterOrDigit(c) || c == ' ').ToArray()).Trim().Replace(' ', '-');
        return code.Length == 0 ? "UNKNOWN" : code.Length > 40 ? code[..40] : code;
    }

    // ------------------------------------------------------------ months

    public async Task ClosePeriodAsync(int periodId)
    {
        var period = await db.Periods.SingleOrDefaultAsync(p => p.PeriodId == periodId) ?? throw new LedgerException("No such month.");
        if (period.IsClosed)
            throw new LedgerException($"{period.Label} is already closed.");
        var drafts = await db.JournalEntries.CountAsync(e => e.PeriodId == periodId && e.Status == JournalStatus.Draft);
        if (drafts > 0)
            throw new LedgerException($"{period.Label} has {drafts} draft {(drafts == 1 ? "entry" : "entries")}: post or delete them first.");
        var waiting = await db.Documents.CountAsync(d => d.Status == DocumentStatus.NeedsMapping && d.DocumentDate >= period.Start && d.DocumentDate <= period.End);
        if (waiting > 0)
            throw new LedgerException($"{waiting} {(waiting == 1 ? "document dated in this month waits" : "documents dated in this month wait")} for an account mapping.");
        period.IsClosed = true;
        period.ClosedDate = Now;
        await db.SaveChangesAsync();
    }

    public async Task ReopenPeriodAsync(int periodId)
    {
        var period = await db.Periods.SingleOrDefaultAsync(p => p.PeriodId == periodId) ?? throw new LedgerException("No such month.");
        period.IsClosed = false;
        period.ClosedDate = null;
        await db.SaveChangesAsync();
    }

    /// <summary>The month a date falls in; created if the calendar doesn't have it yet.</summary>
    public async Task<Period> PeriodOfAsync(DateOnly date)
    {
        var p = await db.Periods.SingleOrDefaultAsync(x => x.Year == date.Year && x.Month == date.Month);
        if (p is not null)
            return p;
        p = new Period { Year = date.Year, Month = date.Month };
        db.Periods.Add(p);
        await db.SaveChangesAsync();
        return p;
    }

    /// <summary>The date's month, or, when it is closed, the first open month after it (a late posting).</summary>
    public async Task<(Period Period, bool Late)> PeriodForAsync(DateOnly date)
    {
        var p = await PeriodOfAsync(date);
        if (!p.IsClosed)
            return (p, false);
        var next = date;
        for (var i = 0; i < 36; i++)
        {
            next = new DateOnly(next.Year, next.Month, 1).AddMonths(1);
            var candidate = await PeriodOfAsync(next);
            if (!candidate.IsClosed)
                return (candidate, true);
        }
        throw new LedgerException("No open month in the next three years.");
    }

    // ------------------------------------------------------------ helpers

    public async Task<Entity?> ResolveEntityAsync(string source, string sourceKey)
    {
        var map = await db.EntitySources.AsNoTracking().SingleOrDefaultAsync(m => m.Source == source && m.SourceKey == sourceKey);
        if (map is not null)
            return await db.Entities.SingleAsync(e => e.EntityId == map.EntityId);
        // A business can also be named by its own code ("RX").
        return await db.Entities.SingleOrDefaultAsync(e => e.Code == sourceKey && e.IsActive);
    }

    public async Task<PostingMaps> MapsAsync(int entityId)
    {
        var categories = await db.CategoryMaps.AsNoTracking().Where(m => m.EntityId == null || m.EntityId == entityId).ToListAsync();
        var accounts = await db.Accounts.AsNoTracking().Where(a => a.Subtype == "TaxPayable" || a.Subtype == "TaxRecoverable").ToListAsync();
        return new PostingMaps
        {
            // A business's own mapping wins over the default.
            Categories = categories.OrderBy(m => m.EntityId is null ? 0 : 1).GroupBy(m => m.Category).ToDictionary(g => g.Key, g => g.Last().AccountId),
            Controls = await db.ControlMaps.AsNoTracking().ToDictionaryAsync(m => (m.PartyType, m.Side), m => m.AccountId),
            Cash = await db.CashMaps.AsNoTracking().ToDictionaryAsync(m => m.Method, m => m.AccountId),
            TaxCodes = await db.TaxCodes.AsNoTracking().ToDictionaryAsync(t => t.Code),
            TaxPayableAccountId = accounts.First(a => a.Subtype == "TaxPayable").AccountId,
            TaxRecoverableAccountId = accounts.First(a => a.Subtype == "TaxRecoverable").AccountId,
        };
    }

    /// <summary>The party a document names, created the first time. Patients are one party: never named.</summary>
    public async Task<Party> PartyAsync(DocumentParty p)
    {
        var code = p.Type == PartyTypes.Patient ? "PATIENT" : p.Code.Trim().ToUpperInvariant();
        var party = await db.Parties.SingleOrDefaultAsync(x => x.PartyType == p.Type && x.Code == code);
        if (party is not null)
            return party;
        party = new Party
        {
            PartyType = p.Type, Code = code, Name = p.Type == PartyTypes.Patient ? "Patients (private pay)" : Short(p.Name, 150) ?? code,
            PaymentTermsDays = p.Type is PartyTypes.Patient ? 0 : 30,
        };
        db.Parties.Add(party);
        await db.SaveChangesAsync();
        return party;
    }

    private async Task<JournalEntry> NewEntryAsync(Entity entity, Period period, DateOnly date, string source, string? memo, string user) => new()
    {
        EntityId = entity.EntityId, PeriodId = period.PeriodId, EntryDate = date, Source = source, Memo = Short(memo, 300),
        Number = $"JE-{entity.Code}-{await db.NextJournalNumberAsync():000000}", Status = JournalStatus.Draft, CreatedBy = user, CreatedUtc = Now,
    };

    private void Post(JournalEntry entry, string user)
    {
        entry.Status = JournalStatus.Posted;
        entry.PostedBy = user;
        entry.PostedUtc = Now;
    }

    private static string? Memo(FinancialDocument d, bool late) =>
        late ? $"{d.Memo ?? d.Reference} (dated {d.Date:yyyy-MM-dd}, a closed month)" : d.Memo ?? $"{d.Kind} {d.Reference}";

    private async Task InTransaction(Func<Task> work)
    {
        if (db.Database.CurrentTransaction is not null)
        {
            await work();
            return;
        }
        await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync();
            await work();
            await tx.CommitAsync();
        });
    }

    private DateTime Now => clock.GetUtcNow().UtcDateTime;
    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    private static string? Short(string? s, int max) => s is null ? null : s.Length <= max ? s : s[..max];
}
