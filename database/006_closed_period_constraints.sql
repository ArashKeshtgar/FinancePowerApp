-- The Power App's closed-month guard as constraints instead of trg_BlockChangesOnClosedPeriod.
-- Power Apps saves with INSERT/UPDATE ... OUTPUT, which SQL Server refuses on a table with any enabled trigger
-- (Msg 334): with the trigger, the app could not record, assign, approve or reject anything. A CHECK constraint
-- has no such limit. No trigger goes back on dbo.Expenses for that reason.
--   CK_Expenses_PeriodOpen: no row is inserted or changed while its month is closed. The function takes every
--     column because SQL Server skips a CHECK when an UPDATE sets none of its columns. A column added to
--     dbo.Expenses later must be added to fn_ExpenseRowWritable too.
--   CK_Expenses_DecisionHasApprover: an Approved or Rejected expense names who decided and when.
-- Both WITH NOCHECK: rows already in closed months, or decided before, stay as they are.
-- Not covered: moving an expense out of a closed month (a CHECK sees only the new value), and deletes
-- (the app has none; DENY DELETE on dbo.Expenses to the app's database user).
-- Same as SmartLedgerAI sql/phase0/003_closed_period_constraints.sql, which has the tests and the rollback.

DROP TRIGGER IF EXISTS dbo.trg_BlockChangesOnClosedPeriod;
GO

CREATE OR ALTER FUNCTION dbo.fn_IsPeriodClosed (@PeriodId INT)
RETURNS BIT
AS
BEGIN
    RETURN ISNULL((SELECT IsClosed FROM dbo.AccountingPeriods WHERE PeriodId = @PeriodId), 0);
END;
GO

-- Only @PeriodId decides; the other parameters are there so the constraint names every column.
IF OBJECT_ID(N'dbo.fn_ExpenseRowWritable', N'FN') IS NULL
EXEC(N'CREATE FUNCTION dbo.fn_ExpenseRowWritable (
    @PeriodId INT, @Amount DECIMAL(12,2), @Category VARCHAR(50), @ExpenseDate DATE, @Vendor VARCHAR(100),
    @Description VARCHAR(255), @Status VARCHAR(20), @ApprovedBy NVARCHAR(100), @ApprovedDate DATETIME,
    @CategoryId INT, @TaxAmount DECIMAL(12,2))
RETURNS BIT
AS
BEGIN
    RETURN CASE WHEN dbo.fn_IsPeriodClosed(@PeriodId) = 1 THEN 0 ELSE 1 END;
END;');
GO

IF OBJECT_ID(N'dbo.CK_Expenses_PeriodOpen', N'C') IS NULL
    ALTER TABLE dbo.Expenses WITH NOCHECK ADD CONSTRAINT CK_Expenses_PeriodOpen CHECK (
        dbo.fn_ExpenseRowWritable(PeriodId, Amount, Category, ExpenseDate, Vendor, Description, Status,
                                  ApprovedBy, ApprovedDate, CategoryId, TaxAmount) = 1);
GO

IF OBJECT_ID(N'dbo.CK_Expenses_DecisionHasApprover', N'C') IS NULL
    ALTER TABLE dbo.Expenses WITH NOCHECK ADD CONSTRAINT CK_Expenses_DecisionHasApprover CHECK (
        Status IS NULL OR Status NOT IN ('Approved', 'Rejected')
        OR (ApprovedBy IS NOT NULL AND LEN(LTRIM(ApprovedBy)) > 0 AND ApprovedDate IS NOT NULL));
GO

