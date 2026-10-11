using FinancePowerApp.Api.Auth;
using FinancePowerApp.Api.Data;
using FinancePowerApp.Api.Domain;
using FinancePowerApp.Api.Endpoints;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<FinanceDbContext>(o => o.UseSqlServer(builder.Configuration.GetConnectionString("Finance")));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<Ledger>();
builder.Services.AddScoped<Reports>();
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();
builder.AddFinanceAuth();

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();
// A rule of the books refused the request (unbalanced entry, closed month…): tell the person why.
app.Use(async (ctx, next) =>
{
    try { await next(); }
    catch (Ledger.LedgerException ex) when (!ctx.Response.HasStarted)
    {
        ctx.Response.StatusCode = StatusCodes.Status409Conflict;
        await ctx.Response.WriteAsJsonAsync(new { title = "Not allowed by the books", detail = ex.Message, status = 409 });
    }
});
if (app.Environment.IsDevelopment())
    app.MapOpenApi();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/healthz", () => "ok").AllowAnonymous();
app.MapLedgerEndpoints();
app.MapReportEndpoints();
app.MapIntegrationEndpoints();

app.Run();

public partial class Program;
