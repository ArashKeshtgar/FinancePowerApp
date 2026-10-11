using FinancePowerApp.Api.Data;
using FinancePowerApp.Api.Domain;

namespace FinancePowerApp.Tests;

public class PostingEngineTests
{
    private const int Bank = 1, ArOhip = 2, ArPatient = 3, Hst = 4, Itc = 5, OhipFees = 6, Uninsured = 7, Refused = 8, Ap = 9, Supplies = 10, Cogs = 11, Inventory = 12;

    private static readonly PostingMaps Maps = new()
    {
        Categories = new Dictionary<string, int>
        {
            ["OHIP.Professional"] = OhipFees, ["Uninsured.Services"] = Uninsured, ["Adjustment.Refused"] = Refused,
            ["Medical Supplies"] = Supplies, ["Pharmacy.COGS"] = Cogs, ["Inventory.Drugs"] = Inventory,
        },
        Controls = new Dictionary<(string, string), int>
        {
            [("OHIP", "Receivable")] = ArOhip, [("Patient", "Receivable")] = ArPatient, [("Supplier", "Payable")] = Ap,
        },
        Cash = new Dictionary<string, int> { ["EFT"] = Bank, ["Debit"] = Bank },
        TaxCodes = new Dictionary<string, TaxCode>
        {
            ["HST13"] = new() { Code = "HST13", RatePercent = 13, Recoverable = true },
            ["EXEMPT"] = new() { Code = "EXEMPT", RatePercent = 0, Recoverable = false },
        },
        TaxPayableAccountId = Hst,
        TaxRecoverableAccountId = Itc,
    };

    private static readonly Entity Clinic = new() { Code = "HV", Kind = EntityKinds.Clinic };
    private static readonly Entity Pharmacy = new() { Code = "RX", Kind = EntityKinds.Pharmacy };
    private static readonly DocumentParty Ohip = new("OHIP", "OHIP");

    private static FinancialDocument Doc(string kind, DocumentParty? party, string? method, params DocumentLine[] lines) =>
        new("harbourview", Guid.NewGuid().ToString(), kind, new DateOnly(2026, 10, 5), "AB12-000001", party, lines, method);

    [Fact]
    public void An_ohip_claim_is_owed_by_ohip_and_earns_professional_fees()
    {
        var plan = PostingEngine.Plan(Doc(DocumentKinds.Invoice, Ohip, null, new DocumentLine("OHIP.Professional", 37.95m, "EXEMPT")), Clinic, Maps);
        Assert.True(plan.Complete);
        Assert.Equal(37.95m, plan.Lines.Single(l => l.AccountId == ArOhip).Debit);
        Assert.Equal(37.95m, plan.Lines.Single(l => l.AccountId == OhipFees).Credit);
        Assert.DoesNotContain(plan.Lines, l => l.AccountId == Hst);
        Assert.Equal(new ItemEffect("AB12-000001", Raised: 37.95m), Assert.Single(plan.Effects));
    }

    [Fact]
    public void A_taxable_uninsured_service_charges_hst()
    {
        var plan = PostingEngine.Plan(Doc(DocumentKinds.Invoice, new("Patient", "PATIENT"), null, new DocumentLine("Uninsured.Services", 40m, "HST13")), Clinic, Maps);
        Assert.Equal(45.20m, plan.Lines.Single(l => l.AccountId == ArPatient).Debit);
        Assert.Equal(5.20m, plan.Lines.Single(l => l.AccountId == Hst).Credit);
        Assert.Equal(plan.Debits, plan.Credits);
    }

    [Fact]
    public void A_remittance_pays_part_and_the_refusal_is_adjusted()
    {
        var plan = PostingEngine.Plan(Doc(DocumentKinds.Receipt, Ohip, "EFT",
            new DocumentLine(null, 30m, AppliesTo: "AB12-000001"), new DocumentLine("Adjustment.Refused", 7.95m, AppliesTo: "AB12-000001")), Clinic, Maps);
        Assert.True(plan.Complete);
        Assert.Equal(30m, plan.Lines.Single(l => l.AccountId == Bank).Debit);
        Assert.Equal(7.95m, plan.Lines.Single(l => l.AccountId == Refused).Debit);
        Assert.Equal(37.95m, plan.Lines.Single(l => l.AccountId == ArOhip).Credit);
        Assert.Equal(new ItemEffect("AB12-000001", Paid: 30m, Adjusted: 7.95m), Assert.Single(plan.Effects));
    }

    [Fact]
    public void A_clinic_cannot_recover_hst_on_a_bill_but_a_pharmacy_can()
    {
        var bill = Doc(DocumentKinds.Bill, new("Supplier", "MCKESSON"), null, new DocumentLine("Medical Supplies", 100m, "HST13"));
        var clinic = PostingEngine.Plan(bill, Clinic, Maps);
        Assert.Equal(113m, clinic.Lines.Single(l => l.AccountId == Supplies).Debit);
        Assert.DoesNotContain(clinic.Lines, l => l.AccountId == Itc);

        var pharmacy = PostingEngine.Plan(bill, Pharmacy, Maps);
        Assert.Equal(100m, pharmacy.Lines.Single(l => l.AccountId == Supplies).Debit);
        Assert.Equal(13m, pharmacy.Lines.Single(l => l.AccountId == Itc).Debit);
        Assert.Equal(113m, pharmacy.Lines.Single(l => l.AccountId == Ap).Credit);
    }

    [Fact]
    public void Usage_moves_cost_out_of_inventory()
    {
        var plan = PostingEngine.Plan(Doc(DocumentKinds.Usage, null, null, new DocumentLine("Pharmacy.COGS", 12.40m, OffsetCategory: "Inventory.Drugs")), Pharmacy, Maps);
        Assert.Equal(12.40m, plan.Lines.Single(l => l.AccountId == Cogs).Debit);
        Assert.Equal(12.40m, plan.Lines.Single(l => l.AccountId == Inventory).Credit);
    }

    [Fact]
    public void An_unknown_category_or_method_says_what_is_missing()
    {
        var plan = PostingEngine.Plan(Doc(DocumentKinds.Receipt, Ohip, "Bitcoin", new DocumentLine(null, 5m), new DocumentLine("Adjustment.Mystery", 1m)), Clinic, Maps);
        Assert.False(plan.Complete);
        Assert.Contains(plan.Missing, m => m.Contains("Bitcoin"));
        Assert.Contains(plan.Missing, m => m.Contains("Adjustment.Mystery"));
    }

    [Fact]
    public void Documents_are_validated_before_anything_is_stored()
    {
        var codes = new[] { "HST13", "EXEMPT" };
        Assert.Empty(DocumentValidation.Validate(Doc(DocumentKinds.Invoice, Ohip, null, new DocumentLine("OHIP.Professional", 10m, "EXEMPT")), codes));
        var bad = DocumentValidation.Validate(Doc(DocumentKinds.Invoice, Ohip, null, new DocumentLine(null, 10.005m, "VAT")), codes);
        Assert.Contains("lines[0].amount", bad.Keys);
        Assert.Contains("lines[0].taxCode", bad.Keys);
        Assert.Contains("lines[0].category", bad.Keys);
        Assert.Contains("method", DocumentValidation.Validate(Doc(DocumentKinds.Payment, new("Supplier", "X"), null, new DocumentLine(null, 1m)), codes).Keys);
    }
}
