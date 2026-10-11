-- Views for reporting (the API, Power BI, or the Power App itself). Only posted entries count; a reversed entry
-- and its reversal both stay posted and cancel each other.

CREATE VIEW gl.vw_LedgerLines AS
SELECT
    l.JournalLineId, e.JournalEntryId, e.EntityId, en.Code AS EntityCode, e.Number, e.EntryDate, e.PeriodId,
    FORMAT(p.[Year], '0000') + '-' + FORMAT(p.[Month], '00') AS PeriodLabel,
    e.Source, e.DocumentId, e.Memo AS EntryMemo, e.LatePosting,
    a.AccountId, a.Number AS AccountNumber, a.Name AS AccountName, a.[Type] AS AccountType, a.Subtype AS AccountSubtype,
    l.Debit, l.Credit, l.Debit - l.Credit AS Amount,
    l.PartyId, pa.PartyType, pa.Name AS PartyName, l.Department, l.ProviderCode, l.TaxCode, l.Memo
FROM gl.JournalLine l
JOIN gl.JournalEntry e ON e.JournalEntryId = l.JournalEntryId
JOIN org.Entity en ON en.EntityId = e.EntityId
JOIN dbo.AccountingPeriods p ON p.PeriodId = e.PeriodId
JOIN gl.Account a ON a.AccountId = l.AccountId
LEFT JOIN org.Party pa ON pa.PartyId = l.PartyId
WHERE e.[Status] IN ('Posted', 'Reversed');
GO

-- Movement per business, month and account; a trial balance is the sum up to a month.
CREATE VIEW gl.vw_PeriodMovement AS
SELECT EntityId, PeriodId, AccountId, SUM(Debit) AS Debit, SUM(Credit) AS Credit, SUM(Debit - Credit) AS Net
FROM gl.vw_LedgerLines
GROUP BY EntityId, PeriodId, AccountId;
GO

-- The GST/HST return (GST34) per business and month: line 103 tax collected, 106 input tax credits, 109 net tax.
CREATE VIEW tax.vw_HstReturn AS
SELECT
    EntityId, EntityCode, PeriodId, PeriodLabel,
    SUM(CASE WHEN AccountType = 'Revenue' THEN Credit - Debit ELSE 0 END) AS Line101Sales,
    SUM(CASE WHEN AccountSubtype = 'TaxPayable' THEN Credit - Debit ELSE 0 END) AS Line103TaxCollected,
    SUM(CASE WHEN AccountSubtype = 'TaxRecoverable' THEN Debit - Credit ELSE 0 END) AS Line106InputTaxCredits,
    SUM(CASE WHEN AccountSubtype = 'TaxPayable' THEN Credit - Debit ELSE 0 END)
      - SUM(CASE WHEN AccountSubtype = 'TaxRecoverable' THEN Debit - Credit ELSE 0 END) AS Line109NetTax
FROM gl.vw_LedgerLines
GROUP BY EntityId, EntityCode, PeriodId, PeriodLabel;
GO

-- Receivables by age, as of today, by due date.
CREATE VIEW ar.vw_Aging AS
SELECT
    i.ItemId, i.EntityId, e.Code AS EntityCode, i.PartyId, p.PartyType, p.Name AS PartyName, i.Source, i.Reference,
    i.ItemDate, i.DueDate, i.Amount, i.Paid, i.Adjusted, i.Balance,
    DATEDIFF(day, i.DueDate, CAST(SYSDATETIME() AS date)) AS DaysOverdue,
    CASE WHEN DATEDIFF(day, i.DueDate, CAST(SYSDATETIME() AS date)) <= 0 THEN 'Current'
         WHEN DATEDIFF(day, i.DueDate, CAST(SYSDATETIME() AS date)) <= 30 THEN '1-30'
         WHEN DATEDIFF(day, i.DueDate, CAST(SYSDATETIME() AS date)) <= 60 THEN '31-60'
         WHEN DATEDIFF(day, i.DueDate, CAST(SYSDATETIME() AS date)) <= 90 THEN '61-90'
         ELSE '90+' END AS Bucket
FROM ar.Item i
JOIN org.Entity e ON e.EntityId = i.EntityId
JOIN org.Party p ON p.PartyId = i.PartyId
WHERE i.Balance <> 0;
GO
