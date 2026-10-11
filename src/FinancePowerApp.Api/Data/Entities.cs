namespace FinancePowerApp.Api.Data;

// Classes for the tables in database/*.sql (the scripts are the schema; EF only reads and writes).

public class Entity
{
    public int EntityId { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string Kind { get; set; } = EntityKinds.Clinic;
    public string? HstNumber { get; set; }
    public string Province { get; set; } = "ON";
    public bool IsDefault { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedUtc { get; set; }

    /// <summary>
    /// A business making zero-rated or taxable supplies (a pharmacy) claims back the HST it pays; one making only
    /// exempt health care supplies (a clinic, a lab) cannot, and the HST becomes part of the cost.
    /// </summary>
    public bool ClaimsInputTaxCredits => Kind is EntityKinds.Pharmacy or EntityKinds.Holding;
}

public static class EntityKinds
{
    public const string Clinic = "Clinic";
    public const string Lab = "Lab";
    public const string Diagnostics = "Diagnostics";
    public const string Pharmacy = "Pharmacy";
    public const string Holding = "Holding";
    public static readonly string[] All = [Clinic, Lab, Diagnostics, Pharmacy, Holding];
}

public class EntitySource
{
    public string Source { get; set; } = "";
    public string SourceKey { get; set; } = "";
    public int EntityId { get; set; }
}

public class Party
{
    public int PartyId { get; set; }
    public string PartyType { get; set; } = "";
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public int PaymentTermsDays { get; set; } = 30;
    public bool IsActive { get; set; } = true;
}

public static class PartyTypes
{
    public const string Ohip = "OHIP";
    public const string Odb = "ODB";
    public const string Wsib = "WSIB";
    public const string Province = "Province";
    public const string Insurer = "Insurer";
    public const string Patient = "Patient";
    public const string Provider = "Provider";
    public const string Supplier = "Supplier";
    public const string Other = "Other";
    public static readonly string[] All = [Ohip, Odb, Wsib, Province, Insurer, Patient, Provider, Supplier, Other];
}

public class TaxCode
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public decimal RatePercent { get; set; }
    public bool Recoverable { get; set; }
    public string? Description { get; set; }
}

public class Account
{
    public int AccountId { get; set; }
    public string Number { get; set; } = "";
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
    public string? Subtype { get; set; }
    public int? ParentId { get; set; }
    public bool IsPostable { get; set; } = true;
    public bool IsActive { get; set; } = true;
    public string? GifiCode { get; set; }
    public string? Description { get; set; }
}

public static class AccountTypes
{
    public const string Asset = "Asset";
    public const string Liability = "Liability";
    public const string Equity = "Equity";
    public const string Revenue = "Revenue";
    public const string Expense = "Expense";
    public static readonly string[] All = [Asset, Liability, Equity, Revenue, Expense];

    /// <summary>Assets and expenses grow with debits; the rest with credits.</summary>
    public static bool DebitNormal(string type) => type is Asset or Expense;
}

public class Period
{
    public int PeriodId { get; set; }
    public int Year { get; set; }
    public int Month { get; set; }
    public bool IsClosed { get; set; }
    public DateTime? ClosedDate { get; set; }

    public DateOnly Start => new(Year, Month, 1);
    public DateOnly End => Start.AddMonths(1).AddDays(-1);
    public string Label => $"{Year:0000}-{Month:00}";
}

public class JournalEntry
{
    public long JournalEntryId { get; set; }
    public int EntityId { get; set; }
    public Entity Entity { get; set; } = null!;
    public string Number { get; set; } = "";
    public DateOnly EntryDate { get; set; }
    public int PeriodId { get; set; }
    public Period Period { get; set; } = null!;
    public string Source { get; set; } = JournalSources.Manual;
    public long? DocumentId { get; set; }
    public string? Memo { get; set; }
    public string Status { get; set; } = JournalStatus.Draft;
    public long? ReversalOfId { get; set; }
    public bool LatePosting { get; set; }
    public string CreatedBy { get; set; } = "";
    public DateTime CreatedUtc { get; set; }
    public string? PostedBy { get; set; }
    public DateTime? PostedUtc { get; set; }
    public List<JournalLine> Lines { get; set; } = [];
}

public static class JournalStatus
{
    public const string Draft = "Draft";
    public const string Posted = "Posted";
    public const string Reversed = "Reversed";
}

public static class JournalSources
{
    public const string Manual = "Manual";
    public const string Expense = "Expense";
}

public class JournalLine
{
    public long JournalLineId { get; set; }
    public long JournalEntryId { get; set; }
    public short LineNumber { get; set; }
    public int AccountId { get; set; }
    public Account Account { get; set; } = null!;
    public decimal Debit { get; set; }
    public decimal Credit { get; set; }
    public int? PartyId { get; set; }
    public Party? Party { get; set; }
    public string? Department { get; set; }
    public string? ProviderCode { get; set; }
    public string? TaxCode { get; set; }
    public string? Memo { get; set; }
}

public class CategoryMap
{
    public int CategoryMapId { get; set; }
    public int? EntityId { get; set; }
    public string Category { get; set; } = "";
    public int AccountId { get; set; }
    public Account Account { get; set; } = null!;
    public string? Description { get; set; }
}

public class ControlMap
{
    public int ControlMapId { get; set; }
    public string PartyType { get; set; } = "";
    public string Side { get; set; } = "";
    public int AccountId { get; set; }
}

public class CashMap
{
    public string Method { get; set; } = "";
    public int AccountId { get; set; }
}

public class ArItem
{
    public long ItemId { get; set; }
    public int EntityId { get; set; }
    public int PartyId { get; set; }
    public Party Party { get; set; } = null!;
    public string Source { get; set; } = "";
    public string Reference { get; set; } = "";
    public long? DocumentId { get; set; }
    public DateOnly ItemDate { get; set; }
    public DateOnly DueDate { get; set; }
    public decimal Amount { get; set; }
    public decimal Paid { get; set; }
    public decimal Adjusted { get; set; }
    public decimal Balance { get; private set; }
}

public class ApItem
{
    public long ItemId { get; set; }
    public int EntityId { get; set; }
    public int PartyId { get; set; }
    public Party Party { get; set; } = null!;
    public string Source { get; set; } = "";
    public string Reference { get; set; } = "";
    public long? DocumentId { get; set; }
    public DateOnly ItemDate { get; set; }
    public DateOnly DueDate { get; set; }
    public decimal Amount { get; set; }
    public decimal Paid { get; set; }
    public decimal Balance { get; private set; }
}

public class Document
{
    public long DocumentId { get; set; }
    public string Source { get; set; } = "";
    public string ExternalId { get; set; } = "";
    public string SourceKey { get; set; } = "";
    public int? EntityId { get; set; }
    public string Kind { get; set; } = "";
    public string? PartyType { get; set; }
    public string? PartyCode { get; set; }
    public DateOnly DocumentDate { get; set; }
    public string Reference { get; set; } = "";
    public decimal Total { get; set; }
    public string PayloadJson { get; set; } = "";
    public string Status { get; set; } = DocumentStatus.Received;
    public string? Error { get; set; }
    public long? JournalEntryId { get; set; }
    public DateTime ReceivedUtc { get; set; }
    public DateTime? PostedUtc { get; set; }
}

public static class DocumentStatus
{
    public const string Received = "Received";
    public const string Posted = "Posted";
    public const string NeedsMapping = "NeedsMapping";
    public const string Rejected = "Rejected";
    public const string Reversed = "Reversed";
}

/// <summary>dbo.Expenses: the Power App's expenses. Approved ones are posted to the ledger as bills.</summary>
public class Expense
{
    public int ExpenseId { get; set; }
    public decimal Amount { get; set; }
    public string? Category { get; set; }
    public DateOnly? ExpenseDate { get; set; }
    public string? Vendor { get; set; }
    public string? Description { get; set; }
    public string? Status { get; set; }
    public DateTime? CreatedAt { get; set; }
    public int? PeriodId { get; set; }
    public string? ApprovedBy { get; set; }
    public DateTime? ApprovedDate { get; set; }
    public int? CategoryId { get; set; }
    public decimal? TaxAmount { get; set; }
}

/// <summary>Which journal entry an approved expense became (kept apart: the Power App's trigger owns dbo.Expenses).</summary>
public class ExpensePosting
{
    public int ExpenseId { get; set; }
    public int EntityId { get; set; }
    public long JournalEntryId { get; set; }
    public DateTime PostedUtc { get; set; }
}
