using FinancePowerApp.Api.Auth;
using FinancePowerApp.Api.Data;
using FinancePowerApp.Api.Domain;
using FinancePowerApp.Api.Endpoints;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
// Secrets as files, one per setting (e.g. ConnectionStrings__Finance), the way Docker mounts them; none outside a container.
builder.Configuration.AddKeyPerFile("/run/secrets", optional: true);

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
var signInThroughClinicHub = (app.Configuration.GetSection("ClinicHub").Get<ClinicHubOptions>() ?? new ClinicHubOptions()).SignInEnabled;
if (signInThroughClinicHub)
    app.UseCsrfHeaderCheck();

app.MapGet("/healthz", () => "ok").AllowAnonymous();
// How people sign in: "clinichub", or the development user.
app.MapGet("/api/auth/settings", () => new { signIn = signInThroughClinicHub ? "clinichub" : "demo" }).AllowAnonymous();
if (signInThroughClinicHub)
{
    app.MapGet("/api/auth/sso/start", (string? returnUrl) => Results.Challenge(
        new AuthenticationProperties { RedirectUri = returnUrl is { Length: > 0 } && returnUrl.StartsWith('/') && !returnUrl.StartsWith("//") ? returnUrl : "/" },
        [ClinicHubSignIn.Scheme])).AllowAnonymous();
    // One sign-out: here and at ClinicHub.
    app.MapGet("/api/auth/sso/signout", () => Results.SignOut(new AuthenticationProperties { RedirectUri = "/" },
        [CookieAuthenticationDefaults.AuthenticationScheme, ClinicHubSignIn.Scheme])).AllowAnonymous();
}
app.MapLedgerEndpoints();
app.MapReportEndpoints();
app.MapIntegrationEndpoints();

app.Run();

public partial class Program;
