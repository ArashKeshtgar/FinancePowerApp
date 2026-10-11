-- Demo: each business opens 2026 with money in the bank, so the balance sheet has something to show before the
-- applications send their first documents. Skipped by deploy.ps1 -NoDemo.

SET NOCOUNT ON;

DECLARE @period int = (SELECT PeriodId FROM dbo.AccountingPeriods WHERE [Year] = 2026 AND [Month] = 1);
DECLARE @bank int = (SELECT AccountId FROM gl.Account WHERE Number = '1000');
DECLARE @capital int = (SELECT AccountId FROM gl.Account WHERE Number = '3000');

DECLARE @opening TABLE (Code varchar(20), Amount decimal(14,2));
INSERT @opening VALUES ('HV', 85000), ('LS', 40000), ('LAB', 120000), ('RX', 150000);

DECLARE @code varchar(20), @amount decimal(14,2), @entity int, @je bigint;
DECLARE c CURSOR LOCAL FAST_FORWARD FOR SELECT Code, Amount FROM @opening;
OPEN c;
FETCH NEXT FROM c INTO @code, @amount;
WHILE @@FETCH_STATUS = 0
BEGIN
    SET @entity = (SELECT EntityId FROM org.Entity WHERE Code = @code);
    INSERT gl.JournalEntry (EntityId, Number, EntryDate, PeriodId, Source, Memo, [Status], CreatedBy, PostedBy, PostedUtc)
    VALUES (@entity, CONCAT('JE-', @code, '-', FORMAT(NEXT VALUE FOR gl.JournalNumber, '000000')), '2026-01-01', @period, 'Manual',
            N'Opening balance (demo)', 'Posted', N'demo', N'demo', SYSUTCDATETIME());
    SET @je = SCOPE_IDENTITY();
    INSERT gl.JournalLine (JournalEntryId, LineNumber, AccountId, Debit, Credit, Memo) VALUES
        (@je, 1, @bank, @amount, 0, N'Opening bank balance'),
        (@je, 2, @capital, 0, @amount, N'Owners'' capital');
    FETCH NEXT FROM c INTO @code, @amount;
END
CLOSE c;
DEALLOCATE c;
GO
