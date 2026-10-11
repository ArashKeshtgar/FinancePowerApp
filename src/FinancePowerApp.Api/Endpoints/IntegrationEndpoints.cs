using System.Security.Claims;
using FinancePowerApp.Api.Auth;
using FinancePowerApp.Api.Data;
using FinancePowerApp.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace FinancePowerApp.Api.Endpoints;

/// <summary>
/// The inbox: ClinicHub, LabFlow, ClinicReport and PharmaFlow post their financial documents here. Each is kept as
/// it arrived, posted when every word it uses has an account, and otherwise waits for an accountant to map it.
/// </summary>
public static class IntegrationEndpoints
{
    public record DocumentRow(long Id, string Source, string ExternalId, string SourceKey, string? Entity, string Kind, string? PartyType, string? PartyCode,
        DateOnly Date, string Reference, decimal Total, string Status, string? Error, long? JournalEntryId, DateTime ReceivedUtc, DateTime? PostedUtc);

    public static void MapIntegrationEndpoints(this IEndpointRouteBuilder app)
    {
        // Not behind the people's sign-in: the sender is checked here (ClinicHub token, or a development key).
        app.MapPost("/api/integration/documents", async (FinancialDocument[] documents, HttpContext http, FinanceDbContext db, Ledger ledger) =>
        {
            var source = await AuthSetup.DocumentSourceAsync(http);
            if (source is null)
                return Results.Problem("Send a ClinicHub token for the audience \"finance\" with the scope \"finance\".", statusCode: StatusCodes.Status401Unauthorized);
            if (documents.Length is 0 or > 200)
                return LedgerEndpoints.Field("documents", "Send 1 to 200 documents at a time.");
            var taxCodes = await db.TaxCodes.Select(t => t.Code).ToListAsync();
            for (var i = 0; i < documents.Length; i++)
            {
                var errors = DocumentValidation.Validate(documents[i], taxCodes);
                if (errors.Count > 0)
                    return Results.ValidationProblem(errors.ToDictionary(e => $"[{i}].{e.Key}", e => e.Value));
            }
            var results = new List<object>();
            foreach (var d in documents)
            {
                var (doc, duplicate) = await ledger.ReceiveAsync(source, d);
                results.Add(new { externalId = doc.ExternalId, id = doc.DocumentId, status = doc.Status, duplicate, error = doc.Error, journalEntryId = doc.JournalEntryId });
            }
            return Results.Ok(results);
        }).AllowAnonymous().WithTags("Integration");

        var view = app.MapGroup("/api/documents").RequireAuthorization(FinancePermissions.View).WithTags("Integration");
        view.MapGet("", async (string? status, string? source, int? entityId, FinanceDbContext db) =>
        {
            var entities = await db.Entities.AsNoTracking().ToDictionaryAsync(e => e.EntityId, e => e.Code);
            var q = db.Documents.AsNoTracking().AsQueryable();
            if (!string.IsNullOrEmpty(status)) q = q.Where(d => d.Status == status);
            if (!string.IsNullOrEmpty(source)) q = q.Where(d => d.Source == source);
            if (entityId is not null) q = q.Where(d => d.EntityId == entityId);
            var rows = await q.OrderByDescending(d => d.DocumentId).Take(300).ToListAsync();
            return rows.Select(d => new DocumentRow(d.DocumentId, d.Source, d.ExternalId, d.SourceKey, d.EntityId is { } e ? entities[e] : null, d.Kind,
                d.PartyType, d.PartyCode, d.DocumentDate, d.Reference, d.Total, d.Status, d.Error, d.JournalEntryId, d.ReceivedUtc, d.PostedUtc));
        });
        view.MapGet("/{id:long}", async (long id, FinanceDbContext db) =>
            await db.Documents.AsNoTracking().SingleOrDefaultAsync(d => d.DocumentId == id) is { } d ? Results.Ok(d) : Results.NotFound());
        app.MapPost("/api/documents/repost", async (FinanceDbContext db, Ledger ledger) => Results.Ok(new { reposted = await LedgerEndpoints.Repost(db, ledger) }))
            .RequireAuthorization(FinancePermissions.Post).WithTags("Integration");

        // The Power App's approved expenses into the ledger.
        app.MapPost("/api/expenses/post-approved", async (ClaimsPrincipal user, Ledger ledger) =>
        {
            var (posted, skipped) = await ledger.PostApprovedExpensesAsync(user.UserName());
            return Results.Ok(new { posted, skipped });
        }).RequireAuthorization(FinancePermissions.Post).WithTags("Expenses");
        app.MapGet("/api/expenses", async (FinanceDbContext db) =>
            await db.Expenses.AsNoTracking().OrderByDescending(e => e.ExpenseDate).ThenByDescending(e => e.ExpenseId).Take(300)
                .Select(e => new
                {
                    e.ExpenseId, e.Amount, e.Category, e.ExpenseDate, e.Vendor, e.Description, e.Status, e.TaxAmount, e.PeriodId,
                    JournalEntryId = db.ExpensePostings.Where(p => p.ExpenseId == e.ExpenseId).Select(p => (long?)p.JournalEntryId).FirstOrDefault(),
                }).ToListAsync())
            .RequireAuthorization(FinancePermissions.View).WithTags("Expenses");
    }
}
