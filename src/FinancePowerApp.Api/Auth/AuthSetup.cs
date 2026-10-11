using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace FinancePowerApp.Api.Auth;

/// <summary>What a person may do in the finance application. Keys reach it from ClinicHub as "finance:…" without the prefix.</summary>
public static class FinancePermissions
{
    public const string View = "ledger.view";
    public const string Post = "ledger.post";
    public const string Admin = "admin";
    public static readonly string[] All = [View, Post, Admin];
}

public class ClinicHubOptions
{
    /// <summary>ClinicHub's address. Set: documents from the applications are checked against ClinicHub's tokens.</summary>
    public string? Authority { get; set; }
    public string ClientId { get; set; } = "finance";
    public string? ClientSecret { get; set; }
    public bool RequireHttpsMetadata { get; set; } = true;
}

public static class AuthSetup
{
    public const string DevScheme = "Dev";
    public const string ServiceScheme = "ClinicHubService";
    public const string FinanceScope = "finance";
    public const string PermissionClaim = "permission";

    /// <summary>The applications that may send financial documents (their ClinicHub client ids).</summary>
    public static readonly string[] Sources = ["clinichub", "labflow", "clinicreport", "pharmaflow"];

    public static void AddFinanceAuth(this WebApplicationBuilder builder)
    {
        var hub = builder.Configuration.GetSection("ClinicHub").Get<ClinicHubOptions>() ?? new ClinicHubOptions();
        builder.Services.Configure<ClinicHubOptions>(builder.Configuration.GetSection("ClinicHub"));
        if (!builder.Environment.IsDevelopment() && builder.Environment.EnvironmentName != "Testing")
            throw new InvalidOperationException("Outside development, people sign in through ClinicHub. Configure ClinicHub:Authority.");

        // People: in development, everyone is the developer with every permission (sign-in through ClinicHub comes with phase 2).
        var auth = builder.Services.AddAuthentication(DevScheme)
            .AddScheme<AuthenticationSchemeOptions, DevAuthHandler>(DevScheme, _ => { });

        // Applications: tokens issued or signed by ClinicHub for the audience "finance".
        if (!string.IsNullOrWhiteSpace(hub.Authority))
            auth.AddJwtBearer(ServiceScheme, o =>
            {
                o.Authority = hub.Authority;
                o.RequireHttpsMetadata = hub.RequireHttpsMetadata;
                o.MapInboundClaims = false;
                o.TokenValidationParameters = new TokenValidationParameters { ValidAudience = hub.ClientId, ValidateAudience = true };
            });

        builder.Services.AddAuthorization(o =>
        {
            foreach (var p in FinancePermissions.All)
                o.AddPolicy(p, policy => policy.RequireAuthenticatedUser().RequireAssertion(ctx =>
                    ctx.User.HasClaim(PermissionClaim, p) || ctx.User.HasClaim(PermissionClaim, FinancePermissions.Admin)));
        });
    }

    public static string UserName(this ClaimsPrincipal user) => user.FindFirstValue("name") ?? user.Identity?.Name ?? "unknown";

    /// <summary>
    /// Which application sent a document: a ClinicHub token (client credentials for the applications, ClinicHub's own
    /// signed token for itself) with the finance scope, or in development a per-application key.
    /// </summary>
    public static async Task<string?> DocumentSourceAsync(HttpContext http)
    {
        var hub = http.RequestServices.GetRequiredService<IOptions<ClinicHubOptions>>().Value;
        if (!string.IsNullOrWhiteSpace(hub.Authority) && http.Request.Headers.Authorization.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            var result = await http.AuthenticateAsync(ServiceScheme);
            if (!result.Succeeded)
                return null;
            var p = result.Principal!;
            var scopes = (p.FindFirstValue("scope") ?? "").Split(' ');
            if (!scopes.Contains(FinanceScope))
                return null;
            var client = p.FindFirstValue("client_id") ?? p.FindFirstValue("azp") ?? (p.FindFirstValue("sub") == "clinichub" ? "clinichub" : null);
            return client is not null && Sources.Contains(client) ? client : null;
        }
        var env = http.RequestServices.GetRequiredService<IHostEnvironment>();
        if (!env.IsDevelopment() && env.EnvironmentName != "Testing")
            return null;
        var keys = http.RequestServices.GetRequiredService<IConfiguration>().GetSection("Integration:DevKeys").Get<Dictionary<string, string>>() ?? [];
        var key = http.Request.Headers["X-Finance-Key"].ToString();
        return key.Length > 0 ? keys.FirstOrDefault(k => k.Value == key && Sources.Contains(k.Key)).Key : null;
    }
}

/// <summary>Development only: the developer, with every permission.</summary>
public class DevAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var claims = new List<Claim> { new("sub", "dev"), new("name", "Developer (development only)") };
        claims.AddRange(FinancePermissions.All.Select(p => new Claim(AuthSetup.PermissionClaim, p)));
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, AuthSetup.DevScheme, "name", "role"));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, AuthSetup.DevScheme)));
    }
}
