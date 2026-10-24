# FinancePowerApp

Accounting for an Ontario clinic group: the books of each clinic, the lab and the pharmacy, fed by the applications
of the [ClinicHub](https://github.com/ArashKeshtgar/ClinicHub) platform.

It started as a Power Apps canvas app over Azure SQL that recorded expenses by accounting period. That app keeps
working unchanged: its tables, views, procedures and trigger stay as they were. Around them this repository adds a
small ERP:

- **Businesses:** each clinic, the lab, the pharmacy and a head office keep their own books in one database. A clinic
  or a lab makes HST-exempt supplies (it can't claim input tax credits); a pharmacy's prescriptions are zero-rated (it
  can).
- **Chart of accounts** for an Ontario clinic group, with the CRA GIFI line of each account: receivables per payer
  (OHIP, ODB, WSIB, other provinces, insurers, patients), revenue per line of business (OHIP professional and
  technical fees, uninsured services, lab, echo and ECG, prescriptions, dispensing fees, front store), physician fees,
  cost of drugs.
- **General ledger:** double entry, on the Power App's accounting periods. Posted entries are never edited; they are
  reversed. A closed month takes no entries; a document dated in it is posted in the next open month and marked late.
- **Receivables and payables** as open items: an OHIP claim stays open until the remittance pays it or adjusts it
  (refused, short-paid).
- **Inbox:** the applications send financial documents; each is kept as it arrived, posted, or held until its words
  are mapped to accounts.
- **Reports:** dashboard, trial balance, income statement, balance sheet, GST/HST return lines (GST34 101, 103, 106,
  109), receivable and payable aging; CSV downloads.
- **The Power App's expenses:** approved ones post as bills of the head office (full amount to the category's
  expense account). The Power App's tax column is the income-tax deductible part, not HST, so no input tax credit
  is taken.

## What the applications send

`POST /api/integration/documents` with an array of documents. An application never names an account: it says what
happened in its own words, and the posting maps (Setup → Posting maps) decide the accounts. **No health information:**
no patient names, health card numbers, diagnoses or fee codes. Patients are one party, "Patients (private pay)";
the application keeps who they are, by its invoice number.

```json
{
  "sourceKey": "harbourview",
  "externalId": "claim:1234:ready",
  "kind": "Invoice",
  "date": "2026-10-05",
  "reference": "AB12-001234",
  "party": { "type": "OHIP", "code": "OHIP" },
  "lines": [ { "category": "OHIP.Professional", "amount": 37.95, "taxCode": "EXEMPT", "providerCode": "123456", "department": "Clinic" } ]
}
```

| Kind | Means | Posted as |
|---|---|---|
| `Invoice` | Owed to the business (claim, invoice) | Dr receivable of the party type, Cr each line's category, Cr HST payable |
| `Receipt` | Money in (`method`: EFT, Cash, Debit…); lines in an `Adjustment.*` category settle the rest | Dr cash/bank, Dr adjustment, Cr receivable; the open item is paid down |
| `Adjustment` | A receivable reduced without money (write-off) | Dr category, Cr receivable |
| `Bill` | Owed by the business (supplier, physician's share) | Dr category (+ input tax credit where the business can claim it), Cr payable |
| `Payment` | Money out against bills | Dr payable, Cr cash/bank |
| `Usage` | Stock used (`offsetCategory`) | Dr category (cost of drugs), Cr offset (inventory) |
| `Reversal` | Undoes an earlier document (`reverses`: its externalId) | The mirror entry; the open item is taken back |

- `externalId` is unique per application: sending a document again changes nothing.
- `sourceKey` is the application's name for the business (ClinicHub's clinic slug, LabFlow's `lab`); Setup →
  Businesses maps it to a business.
- Tax codes: `HST13`, `ZERO`, `EXEMPT`, `NONE`. A line's `tax` is what the application charged; left out, it is
  computed from the code.
- Categories in use: `OHIP.Professional`, `OHIP.Technical`, `OHIP.Reciprocal`, `WSIB.Services`, `Uninsured.Services`,
  `Lab.OHIP`, `Lab.Uninsured`, `Diagnostics.Echo`, `Diagnostics.ECG`, `Pharmacy.Drug`, `Pharmacy.DispensingFee`,
  `Pharmacy.OTC`, `Pharmacy.COGS`, `Inventory.Drugs`, `Adjustment.Refused`, `Adjustment.ShortPaid`,
  `Adjustment.WriteOff`, `Expense.PhysicianShare`. A new one waits as *NeedsMapping* until it is mapped, then posts.

**Who may send:** a ClinicHub access token for the audience `finance` with the scope `finance` (the applications use
client credentials; ClinicHub signs its own). In development, `X-Finance-Key` with a per-application key from
`appsettings.Development.json`.

## Build and run

Requires SQL Server and `sqlcmd`.

```powershell
./database/deploy.ps1                       # upgrades the existing FinancePowerApp database (scripts run once each)
./database/deploy.ps1 -Database FinancePowerApp_New -Recreate
dotnet run --project src/FinancePowerApp.Api   # http://localhost:5400
cd web; npm install; npm run dev               # http://localhost:5401
dotnet test                                    # builds its own database from database/*.sql
```

The upgrade adds schemas `org`, `gl`, `tax`, `ar`, `ap`, `integ`, a unique index on `AccountingPeriods (Year, Month)`
and the months of 2025 and 2026; it changes nothing the Power App uses. Take a backup first on a production database.

**Analytics' read-only login** (`analytics_reader`: SELECT on `org`, `gl`, `ar`, `ap`, `tax` and `AccountingPeriods`,
refused personal and free-text columns, nothing in `integ` except one view: `060_item_payments.sql` makes `ar.vw_ItemPayments`,
which item was paid when and how much, from the posted receipts, for Analytics' "days to pay"). Not a numbered script, because it needs a password and
`deploy.ps1` runs every numbered one; run it once per server and put the password only in the Analytics settings:

```powershell
sqlcmd -S . -E -C -b -v ReaderPassword="<a new password>" -i database/tools/analytics_reader.sql
```

## Status

- [x] Ledger, receivables, payables, HST, posting maps, inbox, reports, web UI
- [x] Approved Power App expenses into the ledger
- [x] Sign-in through ClinicHub (`finance:*` permissions, re-checked every 5 minutes): on with `ClinicHub:Authority`
      and `ClinicHub:ClientSecret` (`--launch-profile connected`, web on :5401); without them, development has a single
      developer user and any other environment refuses to start
- [x] Documents from ClinicHub (claims, remittances, physicians' shares, uninsured invoices and payments), LabFlow
      (patient invoices, online payments; insured work once fees are set) and ClinicReport (signed ordered exams once
      template fees are set). Run with `--launch-profile connected` so their ClinicHub tokens are checked
- [x] Documents from PharmaFlow ([Drugstore](https://github.com/ArashKeshtgar/Drugstore)): supplier invoices, dispenses (plan and patient shares, payment), cost of drugs
- [ ] Bank reconciliation, year-end closing entry, T4A slips for physicians
