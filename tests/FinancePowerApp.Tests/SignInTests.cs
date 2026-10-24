using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace FinancePowerApp.Tests;

/// <summary>The API with sign-in through ClinicHub configured (nothing here reaches ClinicHub, so no database either).</summary>
public sealed class SignInFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Finance", "Server=unused;Database=unused");
        builder.UseSetting("ClinicHub:Authority", "http://localhost:1/");
        builder.UseSetting("ClinicHub:ClientSecret", "test-finance-secret");
        builder.UseSetting("ClinicHub:RequireHttpsMetadata", "false");
    }
}

public class SignInTests(SignInFactory factory) : IClassFixture<SignInFactory>
{
    private HttpClient Client() => factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    [Fact]
    public async Task Nobody_is_signed_in_until_ClinicHub_says_who()
    {
        var me = JsonNode.Parse(await Client().GetStringAsync("/api/me"))!;
        Assert.Null(me["name"]);
        Assert.Empty(me["permissions"]!.AsArray());
        var settings = JsonNode.Parse(await Client().GetStringAsync("/api/auth/settings"))!;
        Assert.Equal("clinichub", (string)settings["signIn"]!);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Client().GetAsync("/api/journal")).StatusCode);
    }

    [Fact]
    public async Task Unsafe_calls_need_the_app_header_but_documents_from_the_applications_do_not()
    {
        // Nobody signed in: refused before the header matters.
        var client = Client();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/periods/year/2027", new { })).StatusCode);

        // The documents endpoint is open to the sign-in (it checks the sender itself), so here the header check is what answers.
        var res = await client.PostAsJsonAsync("/api/integration/documents", Array.Empty<object>());
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Contains("X-Finance", await res.Content.ReadAsStringAsync());

        // A bearer token is not ambient, so it goes through to the sender check, which refuses this one.
        var app = Client();
        app.DefaultRequestHeaders.Authorization = new("Bearer", "not-a-clinichub-token");
        res = await app.PostAsJsonAsync("/api/integration/documents", Array.Empty<object>());
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        Assert.Contains("ClinicHub token", await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public void Outside_development_it_refuses_to_start_without_ClinicHub()
    {
        using var production = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Production");
            b.UseSetting("ConnectionStrings:Finance", "Server=unused;Database=unused");
        });
        var ex = Assert.ThrowsAny<Exception>(() => production.CreateClient());
        Assert.Contains("ClinicHub:ClientSecret", ex.ToString());
    }
}
