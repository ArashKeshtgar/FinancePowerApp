using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace FinancePowerApp.Tests;

public class LedgerApiTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static async Task<JsonNode> Json(HttpResponseMessage res)
    {
        var body = await res.Content.ReadAsStringAsync();
        Assert.True(res.IsSuccessStatusCode, $"{(int)res.StatusCode}: {body}");
        return JsonNode.Parse(body)!;
    }

    private static object Claim(string id, string reference, decimal amount, string clinic = "harbourview", string date = "2026-10-05") => new
    {
        sourceKey = clinic, externalId = $"claim:{id}:ready", kind = "Invoice", date, reference,
        party = new { type = "OHIP", code = "OHIP" },
        lines = new[] { new { category = "OHIP.Professional", amount, taxCode = "EXEMPT", providerCode = "123456", department = "Clinic" } },
    };

    private async Task<JsonNode> Send(HttpClient app, params object[] docs) => (await Json(await app.PostAsJsonAsync("/api/integration/documents", docs))).AsArray()[0]!;

    [Fact]
    public async Task Documents_need_a_known_sender()
    {
        var res = await factory.App(key: null).PostAsJsonAsync("/api/integration/documents", new[] { Claim("x", "AB12-X", 10m) });
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        res = await factory.App(key: "wrong").PostAsJsonAsync("/api/integration/documents", new[] { Claim("x", "AB12-X", 10m) });
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task A_claim_and_its_remittance_settle_the_receivable_and_sending_twice_changes_nothing()
    {
        var app = factory.App();
        var posted = await Send(app, Claim("1001", "AB12-001001", 37.95m));
        Assert.Equal("Posted", (string)posted["status"]!);
        var again = await Send(app, Claim("1001", "AB12-001001", 37.95m));
        Assert.True(again["duplicate"]!.GetValue<bool>());
        Assert.Equal(1, await factory.Scalar<int>("SELECT COUNT(*) FROM ar.Item WHERE Reference = 'AB12-001001'"));

        var remittance = await Send(app, new
        {
            sourceKey = "harbourview", externalId = "ra:2026-10:AB12-001001", kind = "Receipt", date = "2026-10-20", reference = "RA-2026-10", method = "EFT",
            party = new { type = "OHIP", code = "OHIP" },
            lines = new object[]
            {
                new { amount = 30.00m, appliesTo = "AB12-001001" },
                new { category = "Adjustment.ShortPaid", amount = 7.95m, appliesTo = "AB12-001001" },
            },
        });
        Assert.Equal("Posted", (string)remittance["status"]!);
        Assert.Equal(0m, await factory.Scalar<decimal>("SELECT Balance FROM ar.Item WHERE Reference = 'AB12-001001'"));

        var tb = await Json(await factory.App().GetAsync("/api/reports/trial-balance?asOf=2026-12-31"));
        Assert.Equal(tb["totalDebit"]!.GetValue<decimal>(), tb["totalCredit"]!.GetValue<decimal>());
    }

    [Fact]
    public async Task An_unmapped_category_waits_and_mapping_it_posts_the_document()
    {
        var app = factory.App();
        var doc = await Send(app, new
        {
            sourceKey = "harbourview", externalId = "invoice:new-kind", kind = "Invoice", date = "2026-10-06", reference = "INV-77",
            party = new { type = "Patient", code = "PATIENT" },
            lines = new[] { new { category = "Uninsured.TravelMedicine", amount = 80m, taxCode = "EXEMPT" } },
        });
        Assert.Equal("NeedsMapping", (string)doc["status"]!);
        Assert.Contains("Uninsured.TravelMedicine", (string)doc["error"]!);

        var account = await factory.Scalar<int>("SELECT AccountId FROM gl.Account WHERE Number = '4100'");
        var mapped = await Json(await app.PutAsJsonAsync("/api/maps/category", new { category = "Uninsured.TravelMedicine", accountId = account }));
        Assert.True(mapped["reposted"]!.GetValue<int>() >= 1);
        Assert.Equal("Posted", await factory.Scalar<string>("SELECT Status FROM integ.Document WHERE ExternalId = 'invoice:new-kind'"));
    }

    [Fact]
    public async Task A_document_for_a_closed_month_lands_in_the_next_open_one()
    {
        await factory.Exec("UPDATE dbo.AccountingPeriods SET IsClosed = 1, ClosedDate = SYSUTCDATETIME() WHERE [Year] = 2025 AND [Month] = 3");
        var doc = await Send(factory.App(), Claim("late", "AB12-LATE", 20m, date: "2025-03-15"));
        var entry = await Json(await factory.App().GetAsync($"/api/journal/{doc["journalEntryId"]}"));
        Assert.Equal("2025-04", (string)entry["entry"]!["period"]!);
        Assert.True(entry["entry"]!["latePosting"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Reversing_a_claim_takes_back_the_receivable()
    {
        var app = factory.App();
        await Send(app, Claim("2002", "AB12-002002", 50m));
        var reversal = await Send(app, new { sourceKey = "harbourview", externalId = "claim:2002:voided", kind = "Reversal", date = "2026-10-07", reference = "AB12-002002", lines = Array.Empty<object>(), reverses = "claim:2002:ready" });
        Assert.Equal("Posted", (string)reversal["status"]!);
        Assert.Equal(0m, await factory.Scalar<decimal>("SELECT Balance FROM ar.Item WHERE Reference = 'AB12-002002'"));
        Assert.Equal("Reversed", await factory.Scalar<string>("SELECT Status FROM integ.Document WHERE ExternalId = 'claim:2002:ready'"));
    }

    [Fact]
    public async Task A_pharmacy_purchase_claims_hst_back_and_shows_on_the_hst_return()
    {
        var pharmacy = factory.App(ApiFactory.PharmaKey);
        var doc = await Send(pharmacy, new
        {
            sourceKey = "pharmacy", externalId = "po:55", kind = "Bill", date = "2026-10-08", reference = "PO-55",
            party = new { type = "Supplier", code = "WHOLESALER", name = "Drug wholesaler" },
            lines = new[] { new { category = "Inventory.Drugs", amount = 1000m, taxCode = "ZERO" }, new { category = "Medical Supplies", amount = 200m, taxCode = "HST13" } },
        });
        Assert.Equal("Posted", (string)doc["status"]!);
        var rx = await factory.Scalar<int>("SELECT EntityId FROM org.Entity WHERE Code = 'RX'");
        var hst = (await Json(await factory.App().GetAsync($"/api/reports/hst?entityId={rx}&from=2026-10-01&to=2026-10-31"))).AsArray();
        Assert.Equal(26m, hst.Single()!["inputTaxCredits"]!.GetValue<decimal>());
        Assert.Equal(1226m, await factory.Scalar<decimal>("SELECT Balance FROM ap.Item WHERE Reference = 'PO-55'"));
    }

    [Fact]
    public async Task Manual_entries_must_balance_and_closed_months_refuse_them()
    {
        var api = factory.App();
        var hv = await factory.Scalar<int>("SELECT EntityId FROM org.Entity WHERE Code = 'HV'");
        var rent = await factory.Scalar<int>("SELECT AccountId FROM gl.Account WHERE Number = '6100'");
        var bank = await factory.Scalar<int>("SELECT AccountId FROM gl.Account WHERE Number = '1000'");
        var unbalanced = await api.PostAsJsonAsync("/api/journal", new { entityId = hv, date = "2026-10-01", memo = "Rent", post = true,
            lines = new[] { new { accountId = rent, debit = 3000m, credit = 0m }, new { accountId = bank, debit = 0m, credit = 2900m } } });
        Assert.Equal(HttpStatusCode.Conflict, unbalanced.StatusCode);

        var draft = await Json(await api.PostAsJsonAsync("/api/journal", new { entityId = hv, date = "2026-11-01", memo = "Rent", post = false,
            lines = new[] { new { accountId = rent, debit = 3000m, credit = 0m }, new { accountId = bank, debit = 0m, credit = 3000m } } }));
        var november = await factory.Scalar<int>("SELECT PeriodId FROM dbo.AccountingPeriods WHERE [Year] = 2026 AND [Month] = 11");
        Assert.Equal(HttpStatusCode.Conflict, (await api.PostAsync($"/api/periods/{november}/close", null)).StatusCode);   // a draft is waiting
        Assert.Equal(HttpStatusCode.NoContent, (await api.PostAsync($"/api/journal/{draft["id"]}/post", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await api.PostAsync($"/api/periods/{november}/close", null)).StatusCode);
        var late = await api.PostAsJsonAsync("/api/journal", new { entityId = hv, date = "2026-11-15", memo = "Late", post = true,
            lines = new[] { new { accountId = rent, debit = 1m, credit = 0m }, new { accountId = bank, debit = 0m, credit = 1m } } });
        Assert.Equal(HttpStatusCode.Conflict, late.StatusCode);
        await api.PostAsync($"/api/periods/{november}/reopen", null);
    }

    [Fact]
    public async Task Approved_power_app_expenses_become_bills_in_the_ledger()
    {
        // TaxAmount in the Power App is the deductible part for income tax, not HST: the whole 226 is the expense.
        await factory.Exec("INSERT dbo.Expenses (Amount, Category, ExpenseDate, Vendor, Description, Status, TaxAmount) VALUES (226.00, 'Office Supplies', '2026-10-03', 'Staples', 'Paper', 'Approved', 226.00), (99.00, 'Travel', '2026-10-03', 'VIA Rail', 'Trip', 'Pending', NULL), (40.00, 'Gadgets', '2026-10-04', 'Shop', 'Unmapped category', 'Approved', 40.00)");
        var result = await Json(await factory.App().PostAsync("/api/expenses/post-approved", null));
        Assert.Equal(2, result["posted"]!.GetValue<int>());
        Assert.Equal(2, await factory.Scalar<int>("SELECT COUNT(*) FROM ap.ExpensePosting"));
        var staples = "(SELECT p.JournalEntryId FROM ap.ExpensePosting p JOIN dbo.Expenses e ON e.ExpenseId = p.ExpenseId WHERE e.Vendor = 'Staples')";
        Assert.Equal(226m, await factory.Scalar<decimal>($"SELECT SUM(Debit) FROM gl.JournalLine l JOIN gl.Account a ON a.AccountId = l.AccountId WHERE a.Number = '6200' AND l.JournalEntryId = {staples}"));
        Assert.Equal(0, await factory.Scalar<int>($"SELECT COUNT(*) FROM gl.JournalLine l JOIN gl.Account a ON a.AccountId = l.AccountId WHERE a.Number = '1300' AND l.JournalEntryId = {staples}"));
        // A category the ledger doesn't know goes to Other expenses.
        Assert.Equal(40m, await factory.Scalar<decimal>("SELECT SUM(Debit) FROM gl.JournalLine l JOIN gl.Account a ON a.AccountId = l.AccountId WHERE a.Number = '6900'"));
        Assert.Equal(0, (await Json(await factory.App().PostAsync("/api/expenses/post-approved", null)))["posted"]!.GetValue<int>());
    }

    [Fact]
    public async Task Statements_add_up()
    {
        var api = factory.App();
        await Send(api, Claim("3003", "AB12-003003", 100m));
        var income = await Json(await api.GetAsync("/api/reports/income-statement?from=2026-01-01&to=2026-12-31"));
        var sheet = await Json(await api.GetAsync("/api/reports/balance-sheet?asOf=2026-12-31"));
        Assert.Equal(sheet["assets"]!["total"]!.GetValue<decimal>(), sheet["liabilitiesAndEquity"]!.GetValue<decimal>());
        Assert.True(income["revenue"]!["total"]!.GetValue<decimal>() >= 100m);
        var dash = await Json(await api.GetAsync("/api/reports/dashboard"));
        Assert.True(dash["cash"]!.GetValue<decimal>() >= 395000m);   // the demo opening balances
    }
}
