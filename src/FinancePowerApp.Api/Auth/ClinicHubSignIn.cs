using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace FinancePowerApp.Api.Auth;

/// <summary>
/// People sign in to FinancePowerApp through ClinicHub (OpenID Connect, code flow with PKCE); ClinicHub decides who may
/// use the books and with which of <see cref="FinancePermissions"/> (its "finance:*" keys). The session is a cookie that
/// is re-checked with ClinicHub every few minutes, so removing a role there ends it here. Needs <c>ClinicHub:Authority</c>
/// and <c>ClinicHub:ClientSecret</c>; without them there is only the development user (and FinancePowerApp refuses to
/// start outside development).
/// </summary>
public static class ClinicHubSignIn
{
    public const string Scheme = "ClinicHub";
    public const string CsrfHeader = "X-Finance";
    public const string ClinicName = "clinic_name";
    public const string CheckedAt = "checked_at";

    public static AuthenticationBuilder AddClinicHubSignIn(this AuthenticationBuilder auth, ClinicHubOptions hub)
    {
        auth.AddCookie(o =>
        {
            o.Cookie.Name = "finance.auth";
            o.Cookie.HttpOnly = true;
            // Lax: the browser arrives from ClinicHub's sign-in. Unsafe API calls must also carry X-Finance.
            o.Cookie.SameSite = SameSiteMode.Lax;
            o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
            o.ExpireTimeSpan = TimeSpan.FromHours(12);
            o.SlidingExpiration = true;
            o.Events.OnRedirectToLogin = ctx => { ctx.Response.StatusCode = 401; return Task.CompletedTask; };
            o.Events.OnRedirectToAccessDenied = ctx => { ctx.Response.StatusCode = 403; return Task.CompletedTask; };
            o.Events.OnValidatePrincipal = ValidateAsync;
        });
        auth.AddOpenIdConnect(Scheme, "ClinicHub", o =>
        {
            o.Authority = hub.Authority;
            o.ClientId = hub.ClientId;
            o.ClientSecret = hub.ClientSecret;
            o.RequireHttpsMetadata = hub.RequireHttpsMetadata;
            o.SignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
            o.ResponseType = OpenIdConnectResponseType.Code;
            o.ResponseMode = OpenIdConnectResponseMode.Query;
            o.UsePkce = true;
            o.Scope.Clear();
            foreach (var s in new[] { "openid", "profile", "clinic", "offline_access" })
                o.Scope.Add(s);
            o.SaveTokens = true;
            o.MapInboundClaims = false;
            o.GetClaimsFromUserInfoEndpoint = false;
            o.TokenValidationParameters.NameClaimType = "name";
            o.CorrelationCookie.SameSite = SameSiteMode.Lax;
            o.CorrelationCookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
            o.NonceCookie.SameSite = SameSiteMode.Lax;
            o.NonceCookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
            // Keep only the refresh token (re-checks) and the id token (sign-out hint): the access token would only make the cookie bigger.
            o.Events.OnTicketReceived = ctx =>
            {
                ctx.Properties!.StoreTokens(ctx.Properties!.GetTokens().Where(t => t.Name is "refresh_token" or "id_token").ToList());
                return Task.CompletedTask;
            };
            o.Events.OnTokenValidated = ctx =>
            {
                ctx.Principal = ToLocalPrincipal(ctx.Principal!, out var allowed);
                if (!allowed)
                    ctx.Fail("access_denied");
                return Task.CompletedTask;
            };
            o.Events.OnRemoteFailure = ctx =>
            {
                ctx.Response.Redirect("/?signInError=" + Uri.EscapeDataString(ctx.Failure?.Message is { } m && m.Contains("access_denied")
                    ? "ClinicHub: you have no role that allows FinancePowerApp at the clinic you chose."
                    : "Signing in through ClinicHub did not complete. Try again."));
                ctx.HandleResponse();
                return Task.CompletedTask;
            };
        });
        return auth;
    }

    /// <summary>ClinicHub's person with the permissions it grants here. No local user record: entries name the person.</summary>
    private static ClaimsPrincipal ToLocalPrincipal(ClaimsPrincipal hub, out bool allowed)
    {
        var permissions = hub.FindAll(AuthSetup.PermissionClaim).Select(c => c.Value).Where(FinancePermissions.All.Contains).Distinct().ToList();
        allowed = permissions.Count > 0;
        var name = hub.FindFirstValue("name") ?? hub.FindFirstValue("preferred_username") ?? hub.FindFirstValue("sub") ?? "unknown";
        return allowed ? Principal(hub.FindFirstValue("sub")!, name, hub.FindFirstValue(ClinicName), permissions, DateTime.UtcNow) : hub;
    }

    private static ClaimsPrincipal Principal(string sub, string name, string? clinicName, IEnumerable<string> permissions, DateTime checkedAt)
    {
        var claims = new List<Claim>
        {
            new("sub", sub), new("name", name),
            new(CheckedAt, new DateTimeOffset(checkedAt).ToUnixTimeSeconds().ToString()),
        };
        if (clinicName is { Length: > 0 })
            claims.Add(new(ClinicName, clinicName));
        claims.AddRange(permissions.Where(FinancePermissions.All.Contains).Distinct().Select(p => new Claim(AuthSetup.PermissionClaim, p)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme, "name", "role"));
    }

    /// <summary>
    /// Every few minutes ClinicHub is asked again with the refresh token: a refusal (role removed, signed out everywhere)
    /// ends the session; an unreachable ClinicHub is tolerated for a while.
    /// </summary>
    public static async Task ValidateAsync(CookieValidatePrincipalContext ctx)
    {
        var principal = ctx.Principal!;
        var options = ctx.HttpContext.RequestServices.GetRequiredService<IOptions<ClinicHubOptions>>().Value;
        var checkedAt = DateTimeOffset.FromUnixTimeSeconds(long.Parse(principal.FindFirstValue(CheckedAt) ?? "0")).UtcDateTime;
        var permissions = principal.FindAll(AuthSetup.PermissionClaim).Select(c => c.Value).ToList();
        if (DateTime.UtcNow - checkedAt >= TimeSpan.FromMinutes(options.RecheckMinutes))
        {
            switch (await Refresh(ctx, options))
            {
                case RefreshResult.Refused:
                case RefreshResult.Unreachable when DateTime.UtcNow - checkedAt > TimeSpan.FromMinutes(options.OfflineGraceMinutes):
                    await Reject(ctx);
                    return;
                case RefreshResult.Ok(var granted):
                    permissions = granted;
                    checkedAt = DateTime.UtcNow;
                    ctx.ShouldRenew = true;
                    break;
            }
        }
        if (permissions.Count == 0)
        {
            await Reject(ctx);
            return;
        }
        ctx.ReplacePrincipal(Principal(principal.FindFirstValue("sub") ?? "", principal.FindFirstValue("name") ?? "unknown",
            principal.FindFirstValue(ClinicName), permissions, checkedAt));
    }

    private abstract record RefreshResult
    {
        public sealed record Ok(List<string> Permissions) : RefreshResult;
        public sealed record Refused : RefreshResult;
        public sealed record Unreachable : RefreshResult;
    }

    private static async Task<RefreshResult> Refresh(CookieValidatePrincipalContext ctx, ClinicHubOptions options)
    {
        var refreshToken = ctx.Properties.GetTokenValue("refresh_token");
        if (refreshToken is null)
            return new RefreshResult.Refused();
        var services = ctx.HttpContext.RequestServices;
        var oidc = services.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>().Get(Scheme);
        try
        {
            var configuration = await oidc.ConfigurationManager!.GetConfigurationAsync(ctx.HttpContext.RequestAborted);
            using var response = await oidc.Backchannel.PostAsync(configuration.TokenEndpoint, new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token", ["refresh_token"] = refreshToken,
                ["client_id"] = options.ClientId, ["client_secret"] = options.ClientSecret ?? "",
            }));
            if (!response.IsSuccessStatusCode)
                return (int)response.StatusCode is >= 400 and < 500 ? new RefreshResult.Refused() : new RefreshResult.Unreachable();
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            // Straight from ClinicHub's token endpoint over the authenticated back channel (OpenID Connect Core 3.1.3.7).
            var idToken = json.RootElement.GetProperty("id_token").GetString()!;
            ctx.Properties.UpdateTokenValue("id_token", idToken);
            if (json.RootElement.TryGetProperty("refresh_token", out var rt))
                ctx.Properties.UpdateTokenValue("refresh_token", rt.GetString()!);
            return new RefreshResult.Ok(new JwtSecurityTokenHandler().ReadJwtToken(idToken).Claims.Where(c => c.Type == AuthSetup.PermissionClaim)
                .Select(c => c.Value).Where(FinancePermissions.All.Contains).ToList());
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            services.GetRequiredService<ILoggerFactory>().CreateLogger("ClinicHub").LogWarning(ex, "ClinicHub could not be reached to check a session.");
            return new RefreshResult.Unreachable();
        }
    }

    private static async Task Reject(CookieValidatePrincipalContext ctx)
    {
        ctx.RejectPrincipal();
        await ctx.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    }

    /// <summary>
    /// Cookie sign-in is ambient, so unsafe /api requests must carry X-Finance (a browser can't add it cross-site without
    /// CORS). The applications' documents carry a bearer token instead, which a browser never adds on its own.
    /// </summary>
    public static IApplicationBuilder UseCsrfHeaderCheck(this IApplicationBuilder app) => app.Use(async (ctx, next) =>
    {
        var unsafeMethod = !(HttpMethods.IsGet(ctx.Request.Method) || HttpMethods.IsHead(ctx.Request.Method) || HttpMethods.IsOptions(ctx.Request.Method));
        if (unsafeMethod && ctx.Request.Path.StartsWithSegments("/api") && !ctx.Request.Headers.ContainsKey(CsrfHeader)
            && !ctx.Request.Headers.Authorization.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            await ctx.Response.WriteAsJsonAsync(new { title = $"Missing {CsrfHeader} header." });
            return;
        }
        await next();
    });
}
