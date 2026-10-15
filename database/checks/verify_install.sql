-- Read only. After erp_install.sql: is everything in place, and what is waiting for the ledger?
-- Safe on staging and production; changes nothing. Each row says OK or what is wrong.
SET NOCOUNT ON;

SELECT Item, Found, Expected, CASE WHEN Found = Expected THEN 'OK' ELSE 'CHECK' END AS Result
FROM (VALUES
    (N'Scripts recorded in dbo.ErpMigration',     (SELECT COUNT(*) FROM dbo.ErpMigration WHERE Script IN (N'005_powerapp_baseline.sql', N'006_closed_period_constraints.sql', N'010_erp_schema.sql', N'011_expense_posting.sql', N'020_seed_reference.sql', N'030_reports.sql')), 6),
    (N'ERP schemas (org, gl, tax, ar, ap, integ)', (SELECT COUNT(*) FROM sys.schemas WHERE name IN (N'org', N'gl', N'tax', N'ar', N'ap', N'integ')), 6),
    (N'Trigger on dbo.Expenses (must be gone)',     (SELECT COUNT(*) FROM sys.triggers WHERE parent_id = OBJECT_ID(N'dbo.Expenses')), 0),
    (N'Closed-month constraints on dbo.Expenses',   (SELECT COUNT(*) FROM sys.check_constraints WHERE parent_object_id = OBJECT_ID(N'dbo.Expenses') AND name IN (N'CK_Expenses_PeriodOpen', N'CK_Expenses_DecisionHasApprover')), 2),
    (N'Businesses with a default (org.Entity)',     (SELECT COUNT(*) FROM org.Entity WHERE IsDefault = 1), 1),
    (N'Journal entries (none until posting)',       (SELECT COUNT(*) FROM gl.JournalEntry), 0)
) AS c (Item, Found, Expected);

SELECT
    (SELECT COUNT(*) FROM gl.Account)                                     AS Accounts,
    (SELECT COUNT(*) FROM org.Entity)                                     AS Businesses,
    (SELECT COUNT(*) FROM dbo.AccountingPeriods)                          AS Periods,
    (SELECT COUNT(*) FROM dbo.Expenses e WHERE e.Status = 'Approved'
        AND NOT EXISTS (SELECT 1 FROM ap.ExpensePosting p WHERE p.ExpenseId = e.ExpenseId)) AS ApprovedExpensesNotInLedger;
