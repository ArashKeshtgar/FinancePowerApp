using System.Text;
using FinancePowerApp.Api.Auth;
using FinancePowerApp.Api.Domain;

namespace FinancePowerApp.Api.Endpoints;

/// <summary>Statements and lists for the accountant; each can also be downloaded as CSV for a spreadsheet or the accountant's own software.</summary>
public static class ReportEndpoints
{
    public static void MapReportEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/reports").RequireAuthorization(FinancePermissions.View).WithTags("Reports");
        DateOnly Today(TimeProvider clock) => DateOnly.FromDateTime(clock.GetLocalNow().DateTime);

        api.MapGet("/dashboard", (int? entityId, Reports r, TimeProvider clock) => r.DashboardAsync(entityId, Today(clock)));

        api.MapGet("/trial-balance", async (int? entityId, DateOnly? asOf, string? format, Reports r, TimeProvider clock) =>
        {
            var tb = await r.TrialBalanceAsync(entityId, asOf ?? Today(clock));
            return format == "csv"
                ? Csv("trial-balance.csv", ["Account", "Name", "Type", "Debit", "Credit"], tb.Accounts.Select(a => new object[] { a.Number, a.Name, a.Type, a.Debit, a.Credit }))
                : Results.Ok(tb);
        });
        api.MapGet("/income-statement", async (int? entityId, DateOnly? from, DateOnly? to, string? format, Reports r, TimeProvider clock) =>
        {
            var end = to ?? Today(clock);
            var s = await r.IncomeStatementAsync(entityId, from ?? new DateOnly(end.Year, 1, 1), end);
            return format == "csv"
                ? Csv("income-statement.csv", ["Section", "Account", "Name", "Amount"],
                    s.Revenue.Accounts.Select(a => new object[] { "Revenue", a.Number, a.Name, a.Balance })
                        .Concat(s.Expenses.Accounts.Select(a => new object[] { "Expenses", a.Number, a.Name, a.Balance }))
                        .Append(["Net income", "", "", s.NetIncome]))
                : Results.Ok(s);
        });
        api.MapGet("/balance-sheet", async (int? entityId, DateOnly? asOf, Reports r, TimeProvider clock) => Results.Ok(await r.BalanceSheetAsync(entityId, asOf ?? Today(clock))));
        api.MapGet("/hst", async (int? entityId, DateOnly? from, DateOnly? to, string? format, Reports r, TimeProvider clock) =>
        {
            var end = to ?? Today(clock);
            var rows = await r.HstAsync(entityId, from ?? new DateOnly(end.Year, 1, 1), end);
            return format == "csv"
                ? Csv("hst.csv", ["Period", "Business", "Line 101 sales", "Line 103 tax collected", "Line 106 ITCs", "Line 109 net tax"],
                    rows.Select(x => new object[] { x.Period, x.Entity, x.Sales, x.TaxCollected, x.InputTaxCredits, x.NetTax }))
                : Results.Ok(rows);
        });
        api.MapGet("/aging", async (int? entityId, DateOnly? asOf, bool? payables, string? format, Reports r, TimeProvider clock) =>
        {
            var rows = await r.AgingAsync(entityId, asOf ?? Today(clock), payables == true);
            return format == "csv"
                ? Csv(payables == true ? "payables.csv" : "receivables.csv", ["Business", "Party", "Reference", "Date", "Due", "Amount", "Balance", "Days overdue", "Bucket"],
                    rows.Select(x => new object[] { x.Entity, x.Party, x.Reference, x.ItemDate, x.DueDate, x.Amount, x.Balance, x.DaysOverdue, x.Bucket }))
                : Results.Ok(rows);
        });
    }

    private static IResult Csv(string name, string[] header, IEnumerable<object[]> rows)
    {
        static string Cell(object v) => v switch
        {
            decimal d => d.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture),
            DateOnly d => d.ToString("yyyy-MM-dd"),
            _ => $"\"{v?.ToString()?.Replace("\"", "\"\"")}\"",
        };
        var sb = new StringBuilder().AppendLine(string.Join(',', header.Select(h => $"\"{h}\"")));
        foreach (var r in rows) sb.AppendLine(string.Join(',', r.Select(Cell)));
        return Results.File(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray(), "text/csv", name);
    }
}
