using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;

namespace FinancePowerApp.Tests;

/// <summary>The API over a database built from database/*.sql (the same scripts deploy.ps1 runs), dropped afterwards.</summary>
public sealed class ApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string ClinicHubKey = "test-clinichub-key";
    public const string PharmaKey = "test-pharmaflow-key";

    private readonly string _server = Environment.GetEnvironmentVariable("FINANCE_TEST_SQL") ?? "Server=.;Integrated Security=True;TrustServerCertificate=True";
    private readonly string _name = $"FinancePowerApp_Test_{Guid.NewGuid():N}";
    public string ConnectionString => new SqlConnectionStringBuilder(_server) { InitialCatalog = _name }.ConnectionString;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Finance", ConnectionString);
        builder.UseSetting("Integration:DevKeys:clinichub", ClinicHubKey);
        builder.UseSetting("Integration:DevKeys:pharmaflow", PharmaKey);
    }

    public async Task InitializeAsync()
    {
        await using (var master = new SqlConnection(_server))
        {
            await master.OpenAsync();
            await new SqlCommand($"CREATE DATABASE [{_name}]", master).ExecuteNonQueryAsync();
        }
        await using var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync();
        var dir = Path.Combine(AppContext.BaseDirectory, "database");
        foreach (var file in Directory.GetFiles(dir, "*.sql").Where(f => Regex.IsMatch(Path.GetFileName(f), @"^\d{3}_")).Order())
            foreach (var batch in Regex.Split(await File.ReadAllTextAsync(file), @"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase))
                if (!string.IsNullOrWhiteSpace(batch))
                    await new SqlCommand(batch, conn).ExecuteNonQueryAsync();
    }

    public HttpClient App(string? key = ClinicHubKey)
    {
        var c = CreateClient();
        if (key is not null) c.DefaultRequestHeaders.Add("X-Finance-Key", key);
        return c;
    }

    public async Task<T?> Scalar<T>(string sql)
    {
        await using var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync();
        var v = await new SqlCommand(sql, conn).ExecuteScalarAsync();
        return v is null or DBNull ? default : (T)Convert.ChangeType(v, typeof(T));
    }

    public async Task Exec(string sql)
    {
        await using var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync();
        await new SqlCommand(sql, conn).ExecuteNonQueryAsync();
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await base.DisposeAsync();
        SqlConnection.ClearAllPools();
        await using var master = new SqlConnection(_server);
        await master.OpenAsync();
        await new SqlCommand($"ALTER DATABASE [{_name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{_name}];", master).ExecuteNonQueryAsync();
    }
}
