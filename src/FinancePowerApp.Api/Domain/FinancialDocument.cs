using FinancePowerApp.Api.Data;

namespace FinancePowerApp.Api.Domain;

/// <summary>
/// What an application sends when money changes hands, in its own words. It never names accounts: it says what
/// kind of document it is, who the other party is, and what each line is (a category such as "OHIP.Professional").
/// The ledger's maps decide the accounts. No health information: no patient names, diagnoses or service codes.
/// </summary>
/// <param name="SourceKey">The application's own name for the business: ClinicHub's clinic slug, "lab", "pharmacy".</param>
/// <param name="ExternalId">Unique within the application; sending the same id again changes nothing.</param>
/// <param name="Kind">Invoice, Receipt, Adjustment, Bill, Payment, Usage or Reversal (see <see cref="DocumentKinds"/>).</param>
/// <param name="Reference">The open item it creates or settles: a claim's accounting number, an invoice number.</param>
public record FinancialDocument(
    string SourceKey, string ExternalId, string Kind, DateOnly Date, string Reference, DocumentParty? Party,
    DocumentLine[] Lines, string? Method = null, DateOnly? DueDate = null, string? Memo = null, string? Reverses = null);

public record DocumentParty(string Type, string Code, string? Name = null);

/// <param name="Amount">Before tax.</param>
/// <param name="Tax">The HST the application charged or paid; computed from the tax code when left out.</param>
/// <param name="AppliesTo">For a receipt, adjustment or payment: the reference it settles (default: the document's).</param>
/// <param name="OffsetCategory">For usage: where it comes out of ("Inventory.Drugs").</param>
public record DocumentLine(
    string? Category, decimal Amount, string? TaxCode = null, decimal? Tax = null, string? Department = null,
    string? ProviderCode = null, string? AppliesTo = null, string? OffsetCategory = null, string? Memo = null);

public static class DocumentKinds
{
    /// <summary>Money owed to the business: an OHIP claim, a patient invoice, an ODB claim.</summary>
    public const string Invoice = "Invoice";
    /// <summary>Money received against invoices; lines in an Adjustment.* category settle the rest (refused, short-paid).</summary>
    public const string Receipt = "Receipt";
    /// <summary>A receivable reduced without money: a refusal not resubmitted, a write-off.</summary>
    public const string Adjustment = "Adjustment";
    /// <summary>Money the business owes: a supplier's invoice, a physician's share.</summary>
    public const string Bill = "Bill";
    /// <summary>Money paid against bills.</summary>
    public const string Payment = "Payment";
    /// <summary>Stock used: cost of a dispensed drug out of inventory.</summary>
    public const string Usage = "Usage";
    /// <summary>Undoes an earlier document (named in Reverses).</summary>
    public const string Reversal = "Reversal";
    public static readonly string[] All = [Invoice, Receipt, Adjustment, Bill, Payment, Usage, Reversal];
}

public static class DocumentValidation
{
    public static readonly string[] Methods = ["Bank", "EFT", "Cheque", "Cash", "Debit", "Credit"];

    public static Dictionary<string, string[]> Validate(FinancialDocument d, IReadOnlyCollection<string> taxCodes)
    {
        var errors = new Dictionary<string, string[]>();
        void Check(bool bad, string field, string message) { if (bad && !errors.ContainsKey(field)) errors[field] = [message]; }

        Check(string.IsNullOrWhiteSpace(d.SourceKey) || d.SourceKey.Length > 60, "sourceKey", "Name the business (at most 60 characters).");
        Check(string.IsNullOrWhiteSpace(d.ExternalId) || d.ExternalId.Length > 200, "externalId", "A unique id, at most 200 characters.");
        Check(!DocumentKinds.All.Contains(d.Kind), "kind", $"One of {string.Join(", ", DocumentKinds.All)}.");
        Check(d.Date.Year < 2000 || d.Date.Year > 2100, "date", "Check the date.");
        Check(string.IsNullOrWhiteSpace(d.Reference) || d.Reference.Length > 60, "reference", "A reference, at most 60 characters.");
        Check(d.Memo?.Length > 300, "memo", "At most 300 characters.");

        if (d.Kind == DocumentKinds.Reversal)
        {
            Check(string.IsNullOrWhiteSpace(d.Reverses), "reverses", "Name the document to undo (its externalId).");
            return errors;
        }

        if (d.Kind != DocumentKinds.Usage)
        {
            Check(d.Party is null, "party", "Who owes or is owed.");
            if (d.Party is { } p)
            {
                Check(!PartyTypes.All.Contains(p.Type), "party.type", $"One of {string.Join(", ", PartyTypes.All)}.");
                Check(string.IsNullOrWhiteSpace(p.Code) || p.Code.Length > 40, "party.code", "A code, at most 40 characters.");
                Check(p.Name?.Length > 150, "party.name", "At most 150 characters.");
            }
        }
        Check(d.Kind is DocumentKinds.Payment && (d.Method is null || !Methods.Contains(d.Method)), "method", $"How it was paid: {string.Join(", ", Methods)}.");
        Check(d.Kind is DocumentKinds.Receipt && d.Lines.Any(IsCash) && (d.Method is null || !Methods.Contains(d.Method)), "method", $"How it was paid: {string.Join(", ", Methods)}.");

        Check(d.Lines is null || d.Lines.Length == 0, "lines", "At least one line.");
        Check(d.Lines?.Length > 500, "lines", "At most 500 lines.");
        for (var i = 0; i < (d.Lines?.Length ?? 0); i++)
        {
            var l = d.Lines![i];
            var at = $"lines[{i}]";
            Check(l.Amount <= 0 || decimal.Round(l.Amount, 2) != l.Amount, $"{at}.amount", "A positive amount in dollars and cents.");
            Check(l.Tax is < 0 || (l.Tax is { } t && decimal.Round(t, 2) != t), $"{at}.tax", "Tax in dollars and cents, not negative.");
            Check(l.TaxCode is not null && !taxCodes.Contains(l.TaxCode), $"{at}.taxCode", $"One of {string.Join(", ", taxCodes)}.");
            Check(l.Category?.Length > 60, $"{at}.category", "At most 60 characters.");
            Check(d.Kind is DocumentKinds.Invoice or DocumentKinds.Bill or DocumentKinds.Adjustment or DocumentKinds.Usage && string.IsNullOrWhiteSpace(l.Category),
                $"{at}.category", "What this line is (a category).");
            Check(d.Kind == DocumentKinds.Usage && string.IsNullOrWhiteSpace(l.OffsetCategory), $"{at}.offsetCategory", "Where the stock comes out of.");
            Check(l.Department?.Length > 30, $"{at}.department", "At most 30 characters.");
            Check(l.ProviderCode?.Length > 20, $"{at}.providerCode", "At most 20 characters.");
            Check(l.AppliesTo?.Length > 60, $"{at}.appliesTo", "At most 60 characters.");
        }
        return errors;
    }

    /// <summary>A receipt line that is money in (not an adjustment).</summary>
    public static bool IsCash(DocumentLine l) => string.IsNullOrWhiteSpace(l.Category) || l.Category == "Payment";
}
