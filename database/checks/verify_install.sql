-- Read only. After erp_install.sql: is everything in place, and what is waiting for the ledger?
-- Safe on staging and production; changes nothing. One result, so the Portal's Query editor shows all of it:
-- each check says OK or CHECK; the last rows are counts for information.
SET NOCOUNT ON;

SELECT Item, Found, Expected,
       CASE WHEN Expected IS NULL THEN 'info' WHEN Found = Expected THEN 'OK' ELSE 'CHECK' END AS Result
FROM (VALUES
    (1, N'Scripts recorded in dbo.ErpMigration',     (SELECT COUNT(*) FROM dbo.ErpMigration WHERE Script IN (N'005_powerapp_baseline.sql', N'006_closed_period_constraints.sql', N'010_erp_schema.sql', N'011_expense_posting.sql', N'020_seed_reference.sql', N'030_reports.sql')), 6),
    (2, N'ERP schemas (org, gl, tax, ar, ap, integ)', (SELECT COUNT(*) FROM sys.schemas WHERE name IN (N'org', N'gl', N'tax', N'ar', N'ap', N'integ')), 6),
    (3, N'Trigger on dbo.Expenses (must be gone)',     (SELECT COUNT(*) FROM sys.triggers WHERE parent_id = OBJECT_ID(N'dbo.Expenses')), 0),
    (4, N'Closed-month constraints on dbo.Expenses',   (SELECT COUNT(*) FROM sys.check_constraints WHERE parent_object_id = OBJECT_ID(N'dbo.Expenses') AND name IN (N'CK_Expenses_PeriodOpen', N'CK_Expenses_DecisionHasApprover')), 2),
    (5, N'Businesses with a default (org.Entity)',     (SELECT COUNT(*) FROM org.Entity WHERE IsDefault = 1), 1),
    (6, N'Journal entries (none until posting)',       (SELECT COUNT(*) FROM gl.JournalEntry), 0),
    (7, N'Accounts',                                   (SELECT COUNT(*) FROM gl.Account), NULL),
    (8, N'Businesses',                                 (SELECT COUNT(*) FROM org.Entity), NULL),
    (9, N'Accounting periods',                         (SELECT COUNT(*) FROM dbo.AccountingPeriods), NULL),
    (10, N'Approved expenses not yet in the ledger',   (SELECT COUNT(*) FROM dbo.Expenses e WHERE e.Status = 'Approved'
                                                          AND NOT EXISTS (SELECT 1 FROM ap.ExpensePosting p WHERE p.ExpenseId = e.ExpenseId)), NULL)
) AS c (Ord, Item, Found, Expected)
ORDER BY Ord;
