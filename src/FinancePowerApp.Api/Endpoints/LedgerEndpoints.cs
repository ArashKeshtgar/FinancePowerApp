using System.Security.Claims;
using FinancePowerApp.Api.Auth;
using FinancePowerApp.Api.Data;
using FinancePowerApp.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace FinancePowerApp.Api.Endpoints;

/// <summary>The books themselves: businesses, parties, the chart of accounts, months, the journal, and the posting maps.</summary>
public static class LedgerEndpoints
{
    public record EntityDto(int EntityId, string Code, string Name, string Kind, string? HstNumber, bool IsDefault, bool IsActive, bool ClaimsInputTaxCredits, string[] Sources);
    public record SaveEntity(string Code, string Name, string Kind, string? HstNumber, bool IsActive);
    public record SourceDto(string Source, string SourceKey, int EntityId);
    public record SaveAccount(string Number, string Name, string Type, string? Subtype, bool IsPostable, bool IsActive, string? GifiCode, string? Description);
    public record SaveParty(string PartyType, string Code, string Name, string? Email, string? Phone, int PaymentTermsDays, bool IsActive);
    public record PeriodDto(int PeriodId, int Year, int Month, string Label, bool IsClosed, DateTime? ClosedDate, int Entries);
    public record LineDto(short LineNumber, int AccountId, string Account, decimal Debit, decimal Credit, int? PartyId, string? Party, string? Department, string? ProviderCode, string? TaxCode, string? Memo);
    public record EntryRow(long Id, string Number, string Entity, DateOnly EntryDate, string Period, string Source, string? Memo, string Status, decimal Total, bool LatePosting, long? DocumentId, long? ReversalOfId);
    public record EntryDetail(EntryRow Entry, LineDto[] Lines, string CreatedBy, DateTime CreatedUtc, string? PostedBy, DateTime? PostedUtc, long? ReversedById);
    public record NewEntry(int EntityId, DateOnly Date, string? Memo, Ledger.ManualLine[] Lines, bool Post);
    public record ReverseRequest(DateOnly? Date, string? Memo);
    public record MapDto(int Id, int? EntityId, string Category, int AccountId, string Account, string? Description);
    public record SaveMap(int? EntityId, string Category, int AccountId, string? Description);
    public record Maps(MapDto[] Categories, object[] Controls, object[] Cash);

    public static void MapLedgerEndpoints(this IEndpointRouteBuilder app)
    {
        var view = app.MapGroup("/api").RequireAuthorization(FinancePermissions.View);
        var post = app.MapGroup("/api").RequireAuthorization(FinancePermissions.Post);
        var admin = app.MapGroup("/api").RequireAuthorization(FinancePermissions.Admin);

        app.MapGet("/api/me", (ClaimsPrincipal user) => new
        {
            name = user.UserName(), permissions = user.FindAll(AuthSetup.PermissionClaim).Select(c => c.Value).ToArray(),
        }).RequireAuthorization();

        // ---- businesses
        view.MapGet("/entities", async (FinanceDbContext db) =>
        {
            var sources = await db.EntitySources.AsNoTracking().ToListAsync();
            return (await db.Entities.AsNoTracking().OrderByDescending(e => e.IsDefault).ThenBy(e => e.Code).ToListAsync())
                .Select(e => new EntityDto(e.EntityId, e.Code, e.Name, e.Kind, e.HstNumber, e.IsDefault, e.IsActive, e.ClaimsInputTaxCredits,
                    sources.Where(s => s.EntityId == e.EntityId).Select(s => $"{s.Source}:{s.SourceKey}").ToArray()));
        });
        admin.MapPost("/entities", async (SaveEntity req, FinanceDbContext db) =>
        {
            if (Check(req) is { } bad) return bad;
            if (await db.Entities.AnyAsync(e => e.Code == req.Code.Trim().ToUpper()))
                return Field("code", "This code is taken.");
            var e = new Entity { Code = req.Code.Trim().ToUpper(), Name = req.Name.Trim(), Kind = req.Kind, HstNumber = Clean(req.HstNumber), IsActive = req.IsActive, CreatedUtc = DateTime.UtcNow };
            db.Entities.Add(e);
            await db.SaveChangesAsync();
            return Results.Ok(e);
        });
        admin.MapPut("/entities/{id:int}", async (int id, SaveEntity req, FinanceDbContext db) =>
        {
            if (Check(req) is { } bad) return bad;
            var e = await db.Entities.SingleOrDefaultAsync(x => x.EntityId == id);
            if (e is null) return Results.NotFound();
            if (await db.Entities.AnyAsync(x => x.Code == req.Code.Trim().ToUpper() && x.EntityId != id))
                return Field("code", "This code is taken.");
            (e.Code, e.Name, e.Kind, e.HstNumber, e.IsActive) = (req.Code.Trim().ToUpper(), req.Name.Trim(), req.Kind, Clean(req.HstNumber), req.IsActive);
            await db.SaveChangesAsync();
            return Results.Ok(e);
        });
        admin.MapPut("/entity-sources", async (SourceDto req, FinanceDbContext db, Ledger ledger) =>
        {
            if (!AuthSetup.Sources.Contains(req.Source) || string.IsNullOrWhiteSpace(req.SourceKey) || req.SourceKey.Length > 60)
                return Field("source", $"One of {string.Join(", ", AuthSetup.Sources)}, with the application's name for the business.");
            if (!await db.Entities.AnyAsync(e => e.EntityId == req.EntityId)) return Field("entityId", "Choose a business.");
            var map = await db.EntitySources.SingleOrDefaultAsync(m => m.Source == req.Source && m.SourceKey == req.SourceKey.Trim());
            if (map is null) db.EntitySources.Add(new EntitySource { Source = req.Source, SourceKey = req.SourceKey.Trim(), EntityId = req.EntityId });
            else map.EntityId = req.EntityId;
            await db.SaveChangesAsync();
            return Results.Ok(new { reposted = await Repost(db, ledger) });
        });

        // ---- parties
        view.MapGet("/parties", async (string? type, FinanceDbContext db) =>
            await db.Parties.AsNoTracking().Where(p => type == null || p.PartyType == type).OrderBy(p => p.PartyType).ThenBy(p => p.Name).ToListAsync());
        admin.MapPost("/parties", async (SaveParty req, FinanceDbContext db) =>
        {
            if (Check(req) is { } bad) return bad;
            var code = req.PartyType == PartyTypes.Patient ? "PATIENT" : req.Code.Trim().ToUpper();
            if (await db.Parties.AnyAsync(p => p.PartyType == req.PartyType && p.Code == code)) return Field("code", "Already exists.");
            var p = new Party { PartyType = req.PartyType, Code = code, Name = req.Name.Trim(), Email = Clean(req.Email), Phone = Clean(req.Phone), PaymentTermsDays = req.PaymentTermsDays, IsActive = req.IsActive };
            db.Parties.Add(p);
            await db.SaveChangesAsync();
            return Results.Ok(p);
        });
        admin.MapPut("/parties/{id:int}", async (int id, SaveParty req, FinanceDbContext db) =>
        {
            if (Check(req) is { } bad) return bad;
            var p = await db.Parties.SingleOrDefaultAsync(x => x.PartyId == id);
            if (p is null) return Results.NotFound();
            (p.Name, p.Email, p.Phone, p.PaymentTermsDays, p.IsActive) = (req.Name.Trim(), Clean(req.Email), Clean(req.Phone), req.PaymentTermsDays, req.IsActive);
            await db.SaveChangesAsync();
            return Results.Ok(p);
        });

        // ---- chart of accounts and tax codes
        view.MapGet("/accounts", async (FinanceDbContext db) => await db.Accounts.AsNoTracking().OrderBy(a => a.Number).ToListAsync());
        view.MapGet("/tax-codes", async (FinanceDbContext db) => await db.TaxCodes.AsNoTracking().OrderBy(t => t.Code).ToListAsync());
        admin.MapPost("/accounts", async (SaveAccount req, FinanceDbContext db) =>
        {
            if (Check(req) is { } bad) return bad;
            if (await db.Accounts.AnyAsync(a => a.Number == req.Number.Trim())) return Field("number", "This number is taken.");
            var a = new Account();
            Apply(a, req);
            db.Accounts.Add(a);
            await db.SaveChangesAsync();
            return Results.Ok(a);
        });
        admin.MapPut("/accounts/{id:int}", async (int id, SaveAccount req, FinanceDbContext db) =>
        {
            if (Check(req) is { } bad) return bad;
            var a = await db.Accounts.SingleOrDefaultAsync(x => x.AccountId == id);
            if (a is null) return Results.NotFound();
            if (await db.Accounts.AnyAsync(x => x.Number == req.Number.Trim() && x.AccountId != id)) return Field("number", "This number is taken.");
            // An account with postings keeps its type: changing it would move history between statements.
            if (a.Type != req.Type && await db.JournalLines.AnyAsync(l => l.AccountId == id))
                return Field("type", "This account has postings; its type can't change. Create a new account instead.");
            Apply(a, req);
            await db.SaveChangesAsync();
            return Results.Ok(a);
        });

        // ---- months (the Power App's AccountingPeriods)
        view.MapGet("/periods", async (FinanceDbContext db) =>
        {
            var counts = await db.JournalEntries.GroupBy(e => e.PeriodId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count);
            return (await db.Periods.AsNoTracking().OrderByDescending(p => p.Year).ThenByDescending(p => p.Month).ToListAsync())
                .Select(p => new PeriodDto(p.PeriodId, p.Year, p.Month, p.Label, p.IsClosed, p.ClosedDate, counts.GetValueOrDefault(p.PeriodId)));
        });
        admin.MapPost("/periods/{id:int}/close", async (int id, Ledger ledger) => { await ledger.ClosePeriodAsync(id); return Results.NoContent(); });
        admin.MapPost("/periods/{id:int}/reopen", async (int id, Ledger ledger) => { await ledger.ReopenPeriodAsync(id); return Results.NoContent(); });
        admin.MapPost("/periods/year/{year:int}", async (int year, FinanceDbContext db, Ledger ledger) =>
        {
            if (year < 2000 || year > 2100) return Field("year", "Check the year.");
            for (var m = 1; m <= 12; m++) await ledger.PeriodOfAsync(new DateOnly(year, m, 1));
            return Results.NoContent();
        });

        // ---- the journal
        view.MapGet("/journal", async (int? entityId, DateOnly? from, DateOnly? to, string? source, string? status, int? accountId, FinanceDbContext db) =>
        {
            var q = db.JournalEntries.AsNoTracking().AsQueryable();
            if (entityId is not null) q = q.Where(e => e.EntityId == entityId);
            if (from is not null) q = q.Where(e => e.EntryDate >= from);
            if (to is not null) q = q.Where(e => e.EntryDate <= to);
            if (!string.IsNullOrEmpty(source)) q = q.Where(e => e.Source == source);
            if (!string.IsNullOrEmpty(status)) q = q.Where(e => e.Status == status);
            if (accountId is not null) q = q.Where(e => e.Lines.Any(l => l.AccountId == accountId));
            return await q.OrderByDescending(e => e.EntryDate).ThenByDescending(e => e.JournalEntryId).Take(300)
                .Select(e => new EntryRow(e.JournalEntryId, e.Number, e.Entity.Code, e.EntryDate,
                    e.Period.Year.ToString() + "-" + (e.Period.Month < 10 ? "0" : "") + e.Period.Month.ToString(),
                    e.Source, e.Memo, e.Status, e.Lines.Sum(l => l.Debit), e.LatePosting, e.DocumentId, e.ReversalOfId))
                .ToListAsync();
        });
        view.MapGet("/journal/{id:long}", async (long id, FinanceDbContext db) =>
        {
            var e = await db.JournalEntries.AsNoTracking().Include(x => x.Entity).Include(x => x.Period)
                .Include(x => x.Lines).ThenInclude(l => l.Account).Include(x => x.Lines).ThenInclude(l => l.Party)
                .SingleOrDefaultAsync(x => x.JournalEntryId == id);
            if (e is null) return Results.NotFound();
            var reversedBy = await db.JournalEntries.Where(x => x.ReversalOfId == id).Select(x => (long?)x.JournalEntryId).SingleOrDefaultAsync();
            return Results.Ok(new EntryDetail(
                new EntryRow(e.JournalEntryId, e.Number, e.Entity.Code, e.EntryDate, e.Period.Label, e.Source, e.Memo, e.Status, e.Lines.Sum(l => l.Debit), e.LatePosting, e.DocumentId, e.ReversalOfId),
                e.Lines.OrderBy(l => l.LineNumber).Select(l => new LineDto(l.LineNumber, l.AccountId, $"{l.Account.Number} {l.Account.Name}", l.Debit, l.Credit,
                    l.PartyId, l.Party?.Name, l.Department, l.ProviderCode, l.TaxCode, l.Memo)).ToArray(),
                e.CreatedBy, e.CreatedUtc, e.PostedBy, e.PostedUtc, reversedBy));
        });
        post.MapPost("/journal", async (NewEntry req, ClaimsPrincipal user, Ledger ledger) =>
        {
            var e = await ledger.CreateManualAsync(req.EntityId, req.Date, req.Memo, req.Lines ?? [], req.Post, user.UserName());
            return Results.Created($"/api/journal/{e.JournalEntryId}", new { id = e.JournalEntryId, e.Number, e.Status });
        });
        post.MapPost("/journal/{id:long}/post", async (long id, ClaimsPrincipal user, Ledger ledger) => { await ledger.PostDraftAsync(id, user.UserName()); return Results.NoContent(); });
        post.MapDelete("/journal/{id:long}", async (long id, Ledger ledger) => { await ledger.DeleteDraftAsync(id); return Results.NoContent(); });
        post.MapPost("/journal/{id:long}/reverse", async (long id, ReverseRequest req, ClaimsPrincipal user, Ledger ledger, TimeProvider clock) =>
        {
            var r = await ledger.ReverseEntryAsync(id, req.Date ?? DateOnly.FromDateTime(clock.GetLocalNow().DateTime), user.UserName(), req.Memo);
            return Results.Ok(new { id = r.JournalEntryId, r.Number });
        });

        // ---- posting maps
        view.MapGet("/maps", async (FinanceDbContext db) =>
        {
            var accounts = await db.Accounts.AsNoTracking().ToDictionaryAsync(a => a.AccountId, a => $"{a.Number} {a.Name}");
            var categories = await db.CategoryMaps.AsNoTracking().OrderBy(m => m.Category).ThenBy(m => m.EntityId).ToListAsync();
            var controls = await db.ControlMaps.AsNoTracking().OrderBy(m => m.Side).ThenBy(m => m.PartyType).ToListAsync();
            var cash = await db.CashMaps.AsNoTracking().OrderBy(m => m.Method).ToListAsync();
            return new Maps(
                categories.Select(m => new MapDto(m.CategoryMapId, m.EntityId, m.Category, m.AccountId, accounts[m.AccountId], m.Description)).ToArray(),
                controls.Select(m => (object)new { m.ControlMapId, m.PartyType, m.Side, m.AccountId, account = accounts[m.AccountId] }).ToArray(),
                cash.Select(m => (object)new { m.Method, m.AccountId, account = accounts[m.AccountId] }).ToArray());
        });
        // Mapping a category (for every business, or one) posts the documents that were waiting for it.
        admin.MapPut("/maps/category", async (SaveMap req, FinanceDbContext db, Ledger ledger) =>
        {
            var category = req.Category?.Trim() ?? "";
            if (category.Length is 0 or > 60) return Field("category", "A category, at most 60 characters.");
            if (!await db.Accounts.AnyAsync(a => a.AccountId == req.AccountId && a.IsPostable && a.IsActive)) return Field("accountId", "Choose an account that takes postings.");
            var map = await db.CategoryMaps.SingleOrDefaultAsync(m => m.Category == category && m.EntityId == req.EntityId);
            if (map is null) db.CategoryMaps.Add(new CategoryMap { Category = category, EntityId = req.EntityId, AccountId = req.AccountId, Description = Clean(req.Description) });
            else (map.AccountId, map.Description) = (req.AccountId, Clean(req.Description) ?? map.Description);
            await db.SaveChangesAsync();
            return Results.Ok(new { reposted = await Repost(db, ledger) });
        });
        admin.MapDelete("/maps/category/{id:int}", async (int id, FinanceDbContext db) =>
        {
            var map = await db.CategoryMaps.SingleOrDefaultAsync(m => m.CategoryMapId == id);
            if (map is null) return Results.NotFound();
            db.CategoryMaps.Remove(map);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });
    }

    /// <summary>Tries again every document that waits for a mapping; returns how many got posted.</summary>
    public static async Task<int> Repost(FinanceDbContext db, Ledger ledger)
    {
        var waiting = await db.Documents.Where(d => d.Status == DocumentStatus.NeedsMapping).OrderBy(d => d.DocumentId).ToListAsync();
        foreach (var d in waiting)
            await ledger.PostDocumentAsync(d);
        return waiting.Count(d => d.Status == DocumentStatus.Posted);
    }

    private static void Apply(Account a, SaveAccount r)
    {
        (a.Number, a.Name, a.Type, a.Subtype, a.IsPostable, a.IsActive, a.GifiCode, a.Description) =
            (r.Number.Trim(), r.Name.Trim(), r.Type, Clean(r.Subtype), r.IsPostable, r.IsActive, Clean(r.GifiCode), Clean(r.Description));
    }

    private static readonly string[] Subtypes = ["Bank", "Cash", "Clearing", "Receivable", "Inventory", "TaxRecoverable", "Payable", "TaxPayable", "RetainedEarnings", "CostOfSales"];

    private static IResult? Check(SaveAccount r) =>
        string.IsNullOrWhiteSpace(r.Number) || r.Number.Trim().Length > 10 || !r.Number.Trim().All(char.IsAsciiDigit) ? Field("number", "Up to 10 digits.")
        : string.IsNullOrWhiteSpace(r.Name) || r.Name.Length > 120 ? Field("name", "A name, at most 120 characters.")
        : !AccountTypes.All.Contains(r.Type) ? Field("type", $"One of {string.Join(", ", AccountTypes.All)}.")
        : Clean(r.Subtype) is { } s && !Subtypes.Contains(s) ? Field("subtype", $"One of {string.Join(", ", Subtypes)}.")
        : Clean(r.GifiCode) is { } g && (g.Length != 4 || !g.All(char.IsAsciiDigit)) ? Field("gifiCode", "Four digits.")
        : null;

    private static IResult? Check(SaveEntity r) =>
        string.IsNullOrWhiteSpace(r.Code) || r.Code.Trim().Length > 20 ? Field("code", "A short code, at most 20 characters.")
        : string.IsNullOrWhiteSpace(r.Name) || r.Name.Length > 120 ? Field("name", "A name, at most 120 characters.")
        : !EntityKinds.All.Contains(r.Kind) ? Field("kind", $"One of {string.Join(", ", EntityKinds.All)}.")
        : Clean(r.HstNumber) is { } h && !System.Text.RegularExpressions.Regex.IsMatch(h, @"^\d{9}RT\d{4}$") ? Field("hstNumber", "The business number and program account, like 123456789RT0001.")
        : null;

    private static IResult? Check(SaveParty r) =>
        !PartyTypes.All.Contains(r.PartyType) ? Field("partyType", $"One of {string.Join(", ", PartyTypes.All)}.")
        : r.PartyType != PartyTypes.Patient && (string.IsNullOrWhiteSpace(r.Code) || r.Code.Length > 40) ? Field("code", "A code, at most 40 characters.")
        : string.IsNullOrWhiteSpace(r.Name) || r.Name.Length > 150 ? Field("name", "A name, at most 150 characters.")
        : r.PaymentTermsDays is < 0 or > 365 ? Field("paymentTermsDays", "0 to 365 days.")
        : null;

    public static IResult Field(string field, string message) => Results.ValidationProblem(new Dictionary<string, string[]> { [field] = [message] });
    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
