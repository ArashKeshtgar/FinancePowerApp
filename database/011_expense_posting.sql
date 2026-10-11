-- The Power App's dbo.Expenses has an INSTEAD OF trigger (trg_BlockChangesOnClosedPeriod) that rewrites every update
-- with its own column list and refuses changes in closed months. So the ledger never writes to dbo.Expenses: which
-- journal entry an approved expense became is kept here, and the two columns 010 added are taken back.

ALTER TABLE dbo.Expenses DROP CONSTRAINT FK_Expenses_Entity;
ALTER TABLE dbo.Expenses DROP CONSTRAINT FK_Expenses_Journal;
ALTER TABLE dbo.Expenses DROP COLUMN EntityId, JournalEntryId;
GO

CREATE TABLE ap.ExpensePosting (
    ExpenseId       int NOT NULL CONSTRAINT PK_ExpensePosting PRIMARY KEY CONSTRAINT FK_ExpensePosting_Expense REFERENCES dbo.Expenses(ExpenseId),
    EntityId        int NOT NULL CONSTRAINT FK_ExpensePosting_Entity REFERENCES org.Entity(EntityId),
    JournalEntryId  bigint NOT NULL CONSTRAINT FK_ExpensePosting_Journal REFERENCES gl.JournalEntry(JournalEntryId),
    PostedUtc       datetime2(3) NOT NULL CONSTRAINT DF_ExpensePosting_Posted DEFAULT SYSUTCDATETIME()
);
GO
