-- The original FinancePowerApp schema (expenses, categories, periods) used by the Power Apps canvas app.
-- On the existing database every object is already there and nothing here runs; on a new database this
-- recreates it exactly, so the ERP scripts that follow always start from the same place.
-- These objects are never changed by later scripts except by adding nullable columns.

IF OBJECT_ID(N'dbo.AccountingPeriods', N'U') IS NULL
CREATE TABLE dbo.AccountingPeriods (
    PeriodId   int IDENTITY(1,1) NOT NULL CONSTRAINT PK_AccountingPeriods PRIMARY KEY,
    [Year]     int NOT NULL,
    [Month]    int NOT NULL,
    IsClosed   bit NOT NULL CONSTRAINT DF_AccountingPeriods_IsClosed DEFAULT ((0)),
    ClosedDate datetime NULL
);
GO

IF OBJECT_ID(N'dbo.ExpenseCategories', N'U') IS NULL
CREATE TABLE dbo.ExpenseCategories (
    CategoryId    int IDENTITY(1,1) NOT NULL CONSTRAINT PK_ExpenseCategories PRIMARY KEY,
    CategoryName  nvarchar(50) NULL,
    TaxDeductible bit NULL
);
GO

IF OBJECT_ID(N'dbo.Expenses', N'U') IS NULL
CREATE TABLE dbo.Expenses (
    ExpenseId    int IDENTITY(1,1) NOT NULL CONSTRAINT PK_Expenses PRIMARY KEY,
    Amount       decimal(12,2) NOT NULL,
    Category     varchar(50) NULL,
    ExpenseDate  date NULL,
    Vendor       varchar(100) NULL,
    [Description] varchar(255) NULL,
    [Status]     varchar(20) NULL,
    CreatedAt    datetime NULL CONSTRAINT DF_Expenses_CreatedAt DEFAULT (getdate()),
    PeriodId     int NULL CONSTRAINT FK_Expenses_AccountingPeriods REFERENCES dbo.AccountingPeriods(PeriodId),
    ApprovedBy   nvarchar(100) NULL,
    ApprovedDate datetime NULL,
    CategoryId   int NULL CONSTRAINT FK_Expenses_Category REFERENCES dbo.ExpenseCategories(CategoryId),
    TaxAmount    decimal(12,2) NULL
);
GO

IF OBJECT_ID(N'dbo.sp_InsertExpense', N'P') IS NULL
EXEC(N'CREATE PROCEDURE sp_InsertExpense
    @Amount DECIMAL(12,2),
    @Category VARCHAR(50),
    @ExpenseDate DATE,
    @Vendor VARCHAR(100),
    @Description VARCHAR(255)
AS
BEGIN
    SET NOCOUNT ON;
    IF @Amount <= 0
    BEGIN
        RAISERROR (''Amount must be greater than zero'', 16, 1);
        RETURN;
    END
    BEGIN TRY
        BEGIN TRAN;
        INSERT INTO Expenses (Amount, Category, ExpenseDate, Vendor, Description, Status)
        VALUES (@Amount, @Category, @ExpenseDate, @Vendor, @Description, ''Pending'');
        COMMIT TRAN;
    END TRY
    BEGIN CATCH
        ROLLBACK TRAN;
        THROW;
    END CATCH
END;');
GO

IF OBJECT_ID(N'dbo.vw_ExpenseKPI', N'V') IS NULL
EXEC(N'CREATE VIEW vw_ExpenseKPI AS
SELECT COUNT(*) AS TotalCount, SUM(Amount) AS TotalAmount FROM Expenses WHERE Status = ''Pending'';');
GO

IF OBJECT_ID(N'dbo.vw_ExpenseList', N'V') IS NULL
EXEC(N'CREATE VIEW [dbo].[vw_ExpenseList] AS
SELECT ExpenseId, ExpenseDate, Vendor, Category, Amount, Status, CreatedAt, PeriodId FROM Expenses;');
GO

IF OBJECT_ID(N'dbo.vw_TaxLedger', N'V') IS NULL
EXEC(N'CREATE VIEW [dbo].[vw_TaxLedger] AS
SELECT e.ExpenseId, p.Year, p.Month, p.PeriodId, e.ExpenseDate, e.Category, e.Vendor, e.Amount, e.TaxAmount
FROM Expenses e JOIN AccountingPeriods p ON e.PeriodId = p.PeriodId
WHERE p.IsClosed = 1 AND e.Status = ''Approved'';');
GO

IF OBJECT_ID(N'dbo.sp_MonthlyTaxSummary', N'P') IS NULL
EXEC(N'CREATE PROCEDURE sp_MonthlyTaxSummary @Year INT, @Month INT AS
BEGIN
    SELECT Category, SUM(Amount) AS TotalAmount FROM vw_TaxLedger WHERE Year = @Year AND Month = @Month GROUP BY Category;
END');
GO

IF OBJECT_ID(N'dbo.sp_AnnualTaxSummary', N'P') IS NULL
EXEC(N'CREATE PROCEDURE sp_AnnualTaxSummary @Year INT AS
BEGIN
    SELECT Category, SUM(Amount) AS AnnualTotal FROM vw_TaxLedger WHERE Year = @Year GROUP BY Category;
END');
GO

IF OBJECT_ID(N'dbo.vw_AnnualTaxSummary', N'V') IS NULL
EXEC(N'CREATE VIEW vw_AnnualTaxSummary AS
SELECT p.Year, e.Category, SUM(e.TaxAmount) AS AnnualTotal
FROM vw_TaxLedger e JOIN AccountingPeriods p ON e.PeriodId = p.PeriodId
GROUP BY p.Year, e.Category;');
GO

IF OBJECT_ID(N'dbo.vw_AccountingPeriodsDisplay', N'V') IS NULL
EXEC(N'CREATE VIEW vw_AccountingPeriodsDisplay AS
SELECT PeriodId, Year, Month, FORMAT(Year, ''0000'') + ''-'' + FORMAT(Month, ''00'') AS PeriodLabel, IsClosed FROM AccountingPeriods;');
GO

IF OBJECT_ID(N'dbo.vw_ExpenseAnomalyStats_v2', N'V') IS NULL
EXEC(N'CREATE VIEW [dbo].[vw_ExpenseAnomalyStats_v2] AS
SELECT ExpenseId, Category, Amount, AvgAmount, StdDevAmount, SampleSize, ZScore,
    CASE WHEN ABS(ZScore) < 2 THEN ''Normal'' WHEN ABS(ZScore) < 3 THEN ''Medium'' ELSE ''High'' END AS RiskLevel
FROM (
    SELECT ExpenseId, Category, Amount,
        AVG(Amount) OVER (PARTITION BY Category, YEAR(ExpenseDate), MONTH(ExpenseDate)) AS AvgAmount,
        STDEV(Amount) OVER (PARTITION BY Category, YEAR(ExpenseDate), MONTH(ExpenseDate)) AS StdDevAmount,
        COUNT(*) OVER (PARTITION BY Category, YEAR(ExpenseDate), MONTH(ExpenseDate)) AS SampleSize,
        CASE WHEN STDEV(Amount) OVER (PARTITION BY Category, YEAR(ExpenseDate), MONTH(ExpenseDate)) = 0
                  OR STDEV(Amount) OVER (PARTITION BY Category, YEAR(ExpenseDate), MONTH(ExpenseDate)) IS NULL THEN 0
             ELSE (Amount - AVG(Amount) OVER (PARTITION BY Category, YEAR(ExpenseDate), MONTH(ExpenseDate)))
                  / STDEV(Amount) OVER (PARTITION BY Category, YEAR(ExpenseDate), MONTH(ExpenseDate))
        END AS ZScore
    FROM Expenses WHERE Status = ''Approved'' AND PeriodId IS NOT NULL
) AS base;');
GO

IF OBJECT_ID(N'dbo.vw_ExpenseStats', N'V') IS NULL
EXEC(N'CREATE VIEW vw_ExpenseStats AS
WITH BaseData AS (
    SELECT Category, YEAR(ExpenseDate) AS Year, MONTH(ExpenseDate) AS Month, Amount
    FROM Expenses WHERE Status = ''Approved'' AND PeriodId IS NOT NULL
), Stats AS (
    SELECT Category, Year, Month, AVG(Amount) AS AvgAmount, STDEV(Amount) AS StdDevAmount, COUNT(*) AS SampleSize
    FROM BaseData GROUP BY Category, Year, Month
)
SELECT e.ExpenseId, e.Category, e.Amount, s.AvgAmount, s.StdDevAmount, s.SampleSize,
    CASE WHEN s.StdDevAmount = 0 OR s.StdDevAmount IS NULL THEN 0 ELSE (e.Amount - s.AvgAmount) / s.StdDevAmount END AS ZScore
FROM Expenses e JOIN Stats s ON e.Category = s.Category AND YEAR(e.ExpenseDate) = s.Year AND MONTH(e.ExpenseDate) = s.Month
WHERE e.Status = ''Approved'';');
GO

IF OBJECT_ID(N'dbo.vw_ExpenseAnomalyStats', N'V') IS NULL
EXEC(N'CREATE VIEW vw_ExpenseAnomalyStats AS
SELECT s.ExpenseId, s.Category, s.Amount, s.AvgAmount, s.StdDevAmount, s.SampleSize, s.ZScore,
    CASE WHEN ABS(s.ZScore) < 2 THEN ''Normal'' WHEN ABS(s.ZScore) < 3 THEN ''Medium'' ELSE ''High'' END AS RiskLevel
FROM vw_ExpenseStats s;');
GO

-- The Power App's guard: expenses in a closed month can't be inserted or changed.
IF OBJECT_ID(N'dbo.trg_BlockChangesOnClosedPeriod', N'TR') IS NULL
EXEC(N'CREATE TRIGGER [dbo].[trg_BlockChangesOnClosedPeriod]
ON [dbo].[Expenses]
INSTEAD OF INSERT, UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    IF EXISTS (SELECT 1 FROM inserted i JOIN AccountingPeriods p ON i.PeriodId = p.PeriodId WHERE p.IsClosed = 1)
    BEGIN
        RAISERROR (''Accounting period is closed. No changes allowed.'', 16, 1);
        RETURN;
    END
    IF NOT EXISTS (SELECT 1 FROM deleted)
    BEGIN
        INSERT INTO Expenses (Amount, Category, ExpenseDate, Vendor, Description, Status, PeriodId, ApprovedBy, ApprovedDate, CategoryId, TaxAmount)
        SELECT Amount, Category, ExpenseDate, Vendor, Description, Status, PeriodId, ApprovedBy, ApprovedDate, CategoryId, TaxAmount FROM inserted;
    END
    ELSE
    BEGIN
        UPDATE e SET e.Amount = i.Amount, e.Category = i.Category, e.ExpenseDate = i.ExpenseDate, e.Vendor = i.Vendor,
            e.Description = i.Description, e.Status = i.Status, e.PeriodId = i.PeriodId, e.ApprovedBy = i.ApprovedBy,
            e.ApprovedDate = i.ApprovedDate, e.CategoryId = i.CategoryId, e.TaxAmount = i.TaxAmount
        FROM Expenses e JOIN inserted i ON e.ExpenseId = i.ExpenseId;
    END
END;');
GO
