using FinancePowerApp.Api.Data;

namespace FinancePowerApp.Api.Domain;

/// <summary>The maps that turn a document's words into accounts, for one business.</summary>
public sealed class PostingMaps
{
    public required IReadOnlyDictionary<string, int> Categories { get; init; }
    public required IReadOnlyDictionary<(string PartyType, string Side), int> Controls { get; init; }
    public required IReadOnlyDictionary<string, int> Cash { get; init; }
    public required IReadOnlyDictionary<string, TaxCode> TaxCodes { get; init; }
    public required int TaxPayableAccountId { get; init; }
    public required int TaxRecoverableAccountId { get; init; }
}

public record PlannedLine(int AccountId, decimal Debit, decimal Credit, string? Department = null, string? ProviderCode = null,
    string? TaxCode = null, string? Memo = null, bool WithParty = false);

/// <summary>An effect on an open item: invoice/bill raised, or paid/adjusted down.</summary>
public record ItemEffect(string Reference, decimal Raised = 0, decimal Paid = 0, decimal Adjusted = 0);

public record PostingPlan(List<PlannedLine> Lines, List<ItemEffect> Effects, List<string> Missing)
{
    public bool Complete => Missing.Count == 0;
    public decimal Debits => Lines.Sum(l => l.Debit);
    public decimal Credits => Lines.Sum(l => l.Credit);
}

/// <summary>
/// Double entry for each kind of document. Pure: given the document, the business and the maps, it returns the
/// journal lines and what happens to receivables and payables, or which words have no account yet.
/// </summary>
public static class PostingEngine
{
    public const string Receivable = "Receivable";
    public const string Payable = "Payable";

    public static PostingPlan Plan(FinancialDocument d, Entity entity, PostingMaps maps)
    {
        var lines = new List<PlannedLine>();
        var effects = new List<ItemEffect>();
        var missing = new List<string>();

        int? Category(string? category)
        {
            if (category is not null && maps.Categories.TryGetValue(category, out var id)) return id;
            missing.Add($"No account for category \"{category}\"");
            return null;
        }
        int? Control(string side)
        {
            var type = d.Party!.Type;
            if (maps.Controls.TryGetValue((type, side), out var id) || maps.Controls.TryGetValue((PartyTypes.Other, side), out id)) return id;
            missing.Add($"No {side.ToLowerInvariant()} account for {type}");
            return null;
        }
        int? Cash()
        {
            if (d.Method is not null && maps.Cash.TryGetValue(d.Method, out var id)) return id;
            missing.Add($"No account for payment method \"{d.Method}\"");
            return null;
        }

        switch (d.Kind)
        {
            case DocumentKinds.Invoice:
            {
                var control = Control(Receivable);
                decimal total = 0;
                foreach (var l in d.Lines)
                {
                    var tax = Tax(l, maps);
                    if (Category(l.Category) is { } acc) lines.Add(new(acc, 0, l.Amount, l.Department, l.ProviderCode, l.TaxCode, l.Memo));
                    if (tax > 0) lines.Add(new(maps.TaxPayableAccountId, 0, tax, l.Department, null, l.TaxCode, "HST collected"));
                    total += l.Amount + tax;
                }
                if (control is { } c) lines.Insert(0, new(c, total, 0, Memo: d.Memo, WithParty: true));
                effects.Add(new(d.Reference, Raised: total));
                break;
            }
            case DocumentKinds.Receipt:
            case DocumentKinds.Adjustment:
            {
                var control = Control(Receivable);
                int? cash = d.Kind == DocumentKinds.Receipt && d.Lines.Any(DocumentValidation.IsCash) ? Cash() : null;
                foreach (var group in d.Lines.GroupBy(l => l.AppliesTo ?? d.Reference))
                {
                    decimal paid = 0, adjusted = 0;
                    foreach (var l in group)
                    {
                        if (d.Kind == DocumentKinds.Receipt && DocumentValidation.IsCash(l))
                        {
                            if (cash is { } k) lines.Add(new(k, l.Amount, 0, l.Department, l.ProviderCode, Memo: l.Memo));
                            paid += l.Amount;
                        }
                        else
                        {
                            if (Category(l.Category) is { } acc) lines.Add(new(acc, l.Amount, 0, l.Department, l.ProviderCode, Memo: l.Memo ?? l.Category));
                            adjusted += l.Amount;
                        }
                    }
                    if (control is { } c) lines.Add(new(c, 0, paid + adjusted, Memo: group.Key, WithParty: true));
                    effects.Add(new(group.Key, Paid: paid, Adjusted: adjusted));
                }
                break;
            }
            case DocumentKinds.Bill:
            {
                var control = Control(Payable);
                decimal total = 0;
                foreach (var l in d.Lines)
                {
                    var tax = Tax(l, maps);
                    var recoverable = tax > 0 && entity.ClaimsInputTaxCredits && maps.TaxCodes.TryGetValue(l.TaxCode ?? "", out var code) && code.Recoverable;
                    // A business that makes only exempt supplies can't recover the HST it pays: it is part of the cost.
                    if (Category(l.Category) is { } acc) lines.Add(new(acc, recoverable ? l.Amount : l.Amount + tax, 0, l.Department, l.ProviderCode, l.TaxCode, l.Memo));
                    if (recoverable) lines.Add(new(maps.TaxRecoverableAccountId, tax, 0, l.Department, null, l.TaxCode, "HST input tax credit"));
                    total += l.Amount + tax;
                }
                if (control is { } c) lines.Add(new(c, 0, total, Memo: d.Memo, WithParty: true));
                effects.Add(new(d.Reference, Raised: total));
                break;
            }
            case DocumentKinds.Payment:
            {
                var control = Control(Payable);
                var cash = Cash();
                foreach (var group in d.Lines.GroupBy(l => l.AppliesTo ?? d.Reference))
                {
                    var amount = group.Sum(l => l.Amount);
                    if (control is { } c) lines.Add(new(c, amount, 0, Memo: group.Key, WithParty: true));
                    effects.Add(new(group.Key, Paid: amount));
                }
                if (cash is { } k) lines.Add(new(k, 0, d.Lines.Sum(l => l.Amount), Memo: d.Memo));
                break;
            }
            case DocumentKinds.Usage:
                foreach (var l in d.Lines)
                {
                    var debit = Category(l.Category);
                    var credit = Category(l.OffsetCategory);
                    if (debit is { } dr && credit is { } cr)
                    {
                        lines.Add(new(dr, l.Amount, 0, l.Department, l.ProviderCode, Memo: l.Memo));
                        lines.Add(new(cr, 0, l.Amount, l.Department, l.ProviderCode, Memo: l.Memo));
                    }
                }
                break;
            default:
                missing.Add($"A {d.Kind} is not posted by the engine");
                break;
        }
        return new PostingPlan(lines, effects, missing.Distinct().ToList());
    }

    /// <summary>The tax on a line: as the application sent it, or the code's rate on the amount, rounded to the cent.</summary>
    public static decimal Tax(DocumentLine l, PostingMaps maps) =>
        l.Tax ?? (l.TaxCode is not null && maps.TaxCodes.TryGetValue(l.TaxCode, out var code)
            ? decimal.Round(l.Amount * code.RatePercent / 100m, 2, MidpointRounding.AwayFromZero) : 0m);
}
