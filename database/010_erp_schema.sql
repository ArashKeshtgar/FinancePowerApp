-- FinancePowerApp as a small ERP for an Ontario clinic group: several businesses (clinics, a lab, a pharmacy),
-- one chart of accounts, a double-entry general ledger, receivables and payables as open items, HST, and an
-- inbox where ClinicHub, LabFlow, ClinicReport and PharmaFlow drop their financial documents.
--
-- Schemas: org (businesses and parties), gl (accounts, journal), tax (HST codes), ar / ap (open items),
-- integ (documents from the applications and how they turn into journal entries).
-- The original dbo tables stay as they are; dbo.Expenses only gains two nullable columns.

CREATE SCHEMA org;
GO
CREATE SCHEMA gl;
GO
CREATE SCHEMA tax;
GO
CREATE SCHEMA ar;
GO
CREATE SCHEMA ap;
GO
CREATE SCHEMA integ;
GO

-- ---------------------------------------------------------------- org

-- A business that keeps its own books: each clinic, the lab, the pharmacy. In Ontario each is usually its own
-- corporation with its own HST registration (BN + RT0001).
CREATE TABLE org.Entity (
    EntityId        int IDENTITY(1,1) CONSTRAINT PK_Entity PRIMARY KEY,
    Code            varchar(20)   NOT NULL CONSTRAINT UQ_Entity_Code UNIQUE,
    Name            nvarchar(120) NOT NULL,
    Kind            varchar(20)   NOT NULL CONSTRAINT CK_Entity_Kind CHECK (Kind IN ('Clinic','Lab','Diagnostics','Pharmacy','Holding')),
    HstNumber       varchar(15)   NULL CONSTRAINT CK_Entity_Hst CHECK (HstNumber IS NULL OR HstNumber LIKE '[0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9]RT[0-9][0-9][0-9][0-9]'),
    Province        char(2)       NOT NULL CONSTRAINT DF_Entity_Province DEFAULT 'ON',
    IsDefault       bit           NOT NULL CONSTRAINT DF_Entity_IsDefault DEFAULT 0,
    IsActive        bit           NOT NULL CONSTRAINT DF_Entity_IsActive DEFAULT 1,
    CreatedUtc      datetime2(3)  NOT NULL CONSTRAINT DF_Entity_Created DEFAULT SYSUTCDATETIME()
);
CREATE UNIQUE INDEX UX_Entity_Default ON org.Entity (IsDefault) WHERE IsDefault = 1;

-- Which business a document from an application belongs to: ClinicHub's "harbourview" is the Harbourview clinic,
-- LabFlow's single lab database is the lab.
CREATE TABLE org.EntitySource (
    Source     varchar(30) NOT NULL,
    SourceKey  varchar(60) NOT NULL,
    EntityId   int NOT NULL CONSTRAINT FK_EntitySource_Entity REFERENCES org.Entity(EntityId),
    CONSTRAINT PK_EntitySource PRIMARY KEY (Source, SourceKey)
);

-- Who owes or is owed: payers (OHIP, ODB, WSIB, another province, an insurer, patients as a group),
-- providers paid their share, suppliers. Patients are never named here: patient balances are tracked by the
-- application's invoice number, and the application keeps who the patient is.
CREATE TABLE org.Party (
    PartyId    int IDENTITY(1,1) CONSTRAINT PK_Party PRIMARY KEY,
    PartyType  varchar(20)   NOT NULL CONSTRAINT CK_Party_Type CHECK (PartyType IN ('OHIP','ODB','WSIB','Province','Insurer','Patient','Provider','Supplier','Other')),
    Code       varchar(40)   NOT NULL,
    Name       nvarchar(150) NOT NULL,
    Email      nvarchar(150) NULL,
    Phone      varchar(25)   NULL,
    PaymentTermsDays int     NOT NULL CONSTRAINT DF_Party_Terms DEFAULT 30,
    IsActive   bit           NOT NULL CONSTRAINT DF_Party_IsActive DEFAULT 1,
    CONSTRAINT UQ_Party UNIQUE (PartyType, Code)
);

-- ---------------------------------------------------------------- tax

-- Ontario HST. Physician and other health care services are exempt; prescription drugs and many medical devices
-- are zero-rated; over-the-counter products and most third-party forms are taxable at 13 %.
CREATE TABLE tax.TaxCode (
    Code         varchar(10)  NOT NULL CONSTRAINT PK_TaxCode PRIMARY KEY,
    Name         nvarchar(80) NOT NULL,
    RatePercent  decimal(5,2) NOT NULL CONSTRAINT CK_TaxCode_Rate CHECK (RatePercent BETWEEN 0 AND 100),
    Recoverable  bit          NOT NULL CONSTRAINT DF_TaxCode_Recoverable DEFAULT 1,
    Description  nvarchar(300) NULL
);

-- ---------------------------------------------------------------- gl

CREATE TABLE gl.Account (
    AccountId   int IDENTITY(1,1) CONSTRAINT PK_Account PRIMARY KEY,
    Number      varchar(10)   NOT NULL CONSTRAINT UQ_Account_Number UNIQUE,
    Name        nvarchar(120) NOT NULL,
    [Type]      varchar(10)   NOT NULL CONSTRAINT CK_Account_Type CHECK ([Type] IN ('Asset','Liability','Equity','Revenue','Expense')),
    Subtype     varchar(20)   NULL CONSTRAINT CK_Account_Subtype CHECK (Subtype IN ('Bank','Cash','Clearing','Receivable','Inventory','TaxRecoverable','Payable','TaxPayable','RetainedEarnings','CostOfSales')),
    ParentId    int NULL CONSTRAINT FK_Account_Parent REFERENCES gl.Account(AccountId),
    IsPostable  bit NOT NULL CONSTRAINT DF_Account_Postable DEFAULT 1,
    IsActive    bit NOT NULL CONSTRAINT DF_Account_Active DEFAULT 1,
    -- CRA's General Index of Financial Information line, for the T2 return.
    GifiCode    char(4) NULL,
    Description nvarchar(300) NULL
);

CREATE SEQUENCE gl.JournalNumber AS int START WITH 1;

CREATE TABLE gl.JournalEntry (
    JournalEntryId  bigint IDENTITY(1,1) CONSTRAINT PK_JournalEntry PRIMARY KEY,
    EntityId        int NOT NULL CONSTRAINT FK_JournalEntry_Entity REFERENCES org.Entity(EntityId),
    Number          varchar(20) NOT NULL CONSTRAINT UQ_JournalEntry_Number UNIQUE,
    EntryDate       date NOT NULL,
    PeriodId        int NOT NULL CONSTRAINT FK_JournalEntry_Period REFERENCES dbo.AccountingPeriods(PeriodId),
    -- Manual, Expense (the Power App's approved expenses), or the application: clinichub, labflow, clinicreport, pharmaflow.
    Source          varchar(30) NOT NULL,
    DocumentId      bigint NULL,
    Memo            nvarchar(300) NULL,
    [Status]        varchar(10) NOT NULL CONSTRAINT CK_JournalEntry_Status CHECK ([Status] IN ('Draft','Posted','Reversed')),
    ReversalOfId    bigint NULL CONSTRAINT FK_JournalEntry_Reversal REFERENCES gl.JournalEntry(JournalEntryId),
    -- The document's own date fell in a closed month: posted in the next open one, and says so.
    LatePosting     bit NOT NULL CONSTRAINT DF_JournalEntry_Late DEFAULT 0,
    CreatedBy       nvarchar(100) NOT NULL,
    CreatedUtc      datetime2(3) NOT NULL CONSTRAINT DF_JournalEntry_Created DEFAULT SYSUTCDATETIME(),
    PostedBy        nvarchar(100) NULL,
    PostedUtc       datetime2(3) NULL,
    CONSTRAINT CK_JournalEntry_Posted CHECK ([Status] = 'Draft' OR (PostedUtc IS NOT NULL AND PostedBy IS NOT NULL))
);
CREATE INDEX IX_JournalEntry_Entity_Date ON gl.JournalEntry (EntityId, EntryDate);
CREATE INDEX IX_JournalEntry_Period ON gl.JournalEntry (PeriodId);
CREATE UNIQUE INDEX UX_JournalEntry_Reversal ON gl.JournalEntry (ReversalOfId) WHERE ReversalOfId IS NOT NULL;

CREATE TABLE gl.JournalLine (
    JournalLineId   bigint IDENTITY(1,1) CONSTRAINT PK_JournalLine PRIMARY KEY,
    JournalEntryId  bigint NOT NULL CONSTRAINT FK_JournalLine_Entry REFERENCES gl.JournalEntry(JournalEntryId) ON DELETE CASCADE,
    LineNumber          smallint NOT NULL,
    AccountId       int NOT NULL CONSTRAINT FK_JournalLine_Account REFERENCES gl.Account(AccountId),
    Debit           decimal(14,2) NOT NULL CONSTRAINT DF_JournalLine_Debit DEFAULT 0,
    Credit          decimal(14,2) NOT NULL CONSTRAINT DF_JournalLine_Credit DEFAULT 0,
    PartyId         int NULL CONSTRAINT FK_JournalLine_Party REFERENCES org.Party(PartyId),
    -- Analysis: which department of the business (Clinic, Lab, Echo, Pharmacy), which provider (OHIP billing number).
    Department      varchar(30) NULL,
    ProviderCode    varchar(20) NULL,
    TaxCode         varchar(10) NULL CONSTRAINT FK_JournalLine_Tax REFERENCES tax.TaxCode(Code),
    Memo            nvarchar(200) NULL,
    CONSTRAINT UQ_JournalLine UNIQUE (JournalEntryId, LineNumber),
    CONSTRAINT CK_JournalLine_Amounts CHECK (Debit >= 0 AND Credit >= 0 AND (Debit = 0 OR Credit = 0) AND Debit + Credit > 0)
);
CREATE INDEX IX_JournalLine_Account ON gl.JournalLine (AccountId) INCLUDE (Debit, Credit);

-- How documents become journal lines (the "posting rules"). The application says what a line is, in its own words:
-- the category of revenue or cost ("OHIP.Professional", "Pharmacy.Drug"), the payer or supplier type, how money
-- moved. These maps turn those words into accounts, for every business or for one (EntityId set).
CREATE TABLE gl.CategoryMap (
    CategoryMapId int IDENTITY(1,1) CONSTRAINT PK_CategoryMap PRIMARY KEY,
    EntityId      int NULL CONSTRAINT FK_CategoryMap_Entity REFERENCES org.Entity(EntityId),
    Category      varchar(60) NOT NULL,
    AccountId     int NOT NULL CONSTRAINT FK_CategoryMap_Account REFERENCES gl.Account(AccountId),
    Description   nvarchar(200) NULL
);
CREATE UNIQUE INDEX UX_CategoryMap ON gl.CategoryMap (Category, EntityId);
CREATE UNIQUE INDEX UX_CategoryMap_Default ON gl.CategoryMap (Category) WHERE EntityId IS NULL;

-- The control account for each kind of party: what OHIP owes sits in "Accounts receivable – OHIP".
CREATE TABLE gl.ControlMap (
    ControlMapId int IDENTITY(1,1) CONSTRAINT PK_ControlMap PRIMARY KEY,
    PartyType    varchar(20) NOT NULL,
    Side         varchar(10) NOT NULL CONSTRAINT CK_ControlMap_Side CHECK (Side IN ('Receivable','Payable')),
    AccountId    int NOT NULL CONSTRAINT FK_ControlMap_Account REFERENCES gl.Account(AccountId),
    CONSTRAINT UQ_ControlMap UNIQUE (PartyType, Side)
);

-- Where money goes in or out: the bank (EFT, OHIP's direct deposit), the cash drawer, the card terminal's clearing account.
CREATE TABLE gl.CashMap (
    Method    varchar(20) NOT NULL CONSTRAINT PK_CashMap PRIMARY KEY,
    AccountId int NOT NULL CONSTRAINT FK_CashMap_Account REFERENCES gl.Account(AccountId)
);

-- ---------------------------------------------------------------- ar / ap: open items

-- Something owed to a business: an OHIP claim, a patient invoice, an ODB claim. Paid down by receipts and
-- adjustments (a refused or short-paid claim).
CREATE TABLE ar.Item (
    ItemId        bigint IDENTITY(1,1) CONSTRAINT PK_ArItem PRIMARY KEY,
    EntityId      int NOT NULL CONSTRAINT FK_ArItem_Entity REFERENCES org.Entity(EntityId),
    PartyId       int NOT NULL CONSTRAINT FK_ArItem_Party REFERENCES org.Party(PartyId),
    Source        varchar(30) NOT NULL,
    Reference     varchar(60) NOT NULL,
    DocumentId    bigint NULL,
    ItemDate      date NOT NULL,
    DueDate       date NOT NULL,
    Amount        decimal(14,2) NOT NULL CONSTRAINT CK_ArItem_Amount CHECK (Amount > 0),
    Paid          decimal(14,2) NOT NULL CONSTRAINT DF_ArItem_Paid DEFAULT 0,
    Adjusted      decimal(14,2) NOT NULL CONSTRAINT DF_ArItem_Adjusted DEFAULT 0,
    Balance       AS (Amount - Paid - Adjusted) PERSISTED,
    CONSTRAINT UQ_ArItem UNIQUE (EntityId, Source, Reference),
    CONSTRAINT CK_ArItem_Balance CHECK (Paid >= 0 AND Adjusted >= 0)
);
CREATE INDEX IX_ArItem_Party ON ar.Item (EntityId, PartyId) INCLUDE (Amount, Paid, Adjusted);

-- Something a business owes: a supplier's bill, a provider's share of their billings, an approved expense.
CREATE TABLE ap.Item (
    ItemId        bigint IDENTITY(1,1) CONSTRAINT PK_ApItem PRIMARY KEY,
    EntityId      int NOT NULL CONSTRAINT FK_ApItem_Entity REFERENCES org.Entity(EntityId),
    PartyId       int NOT NULL CONSTRAINT FK_ApItem_Party REFERENCES org.Party(PartyId),
    Source        varchar(30) NOT NULL,
    Reference     varchar(60) NOT NULL,
    DocumentId    bigint NULL,
    ItemDate      date NOT NULL,
    DueDate       date NOT NULL,
    Amount        decimal(14,2) NOT NULL CONSTRAINT CK_ApItem_Amount CHECK (Amount > 0),
    Paid          decimal(14,2) NOT NULL CONSTRAINT DF_ApItem_Paid DEFAULT 0,
    Balance       AS (Amount - Paid) PERSISTED,
    CONSTRAINT UQ_ApItem UNIQUE (EntityId, Source, Reference),
    CONSTRAINT CK_ApItem_Paid CHECK (Paid >= 0)
);

-- ---------------------------------------------------------------- integ

-- A financial document from an application, kept as it arrived. Sent again (same source and external id) it is
-- recognised and not posted twice.
CREATE TABLE integ.Document (
    DocumentId     bigint IDENTITY(1,1) CONSTRAINT PK_Document PRIMARY KEY,
    Source         varchar(30)  NOT NULL,
    ExternalId     varchar(200) NOT NULL,
    SourceKey      varchar(60)  NOT NULL,
    EntityId       int NULL CONSTRAINT FK_Document_Entity REFERENCES org.Entity(EntityId),
    Kind           varchar(20)  NOT NULL CONSTRAINT CK_Document_Kind CHECK (Kind IN ('Invoice','Receipt','Adjustment','Bill','Payment','Usage','Reversal')),
    PartyType      varchar(20)  NULL,
    PartyCode      varchar(40)  NULL,
    DocumentDate   date         NOT NULL,
    Reference      varchar(60)  NOT NULL,
    Total          decimal(14,2) NOT NULL,
    PayloadJson    nvarchar(max) NOT NULL,
    [Status]       varchar(15)  NOT NULL CONSTRAINT CK_Document_Status CHECK ([Status] IN ('Received','Posted','NeedsMapping','Rejected','Reversed')),
    Error          nvarchar(1000) NULL,
    JournalEntryId bigint NULL CONSTRAINT FK_Document_Journal REFERENCES gl.JournalEntry(JournalEntryId),
    ReceivedUtc    datetime2(3) NOT NULL CONSTRAINT DF_Document_Received DEFAULT SYSUTCDATETIME(),
    PostedUtc      datetime2(3) NULL,
    CONSTRAINT UQ_Document UNIQUE (Source, ExternalId)
);
CREATE INDEX IX_Document_Status ON integ.Document ([Status], ReceivedUtc);

ALTER TABLE gl.JournalEntry ADD CONSTRAINT FK_JournalEntry_Document FOREIGN KEY (DocumentId) REFERENCES integ.Document(DocumentId);
ALTER TABLE ar.Item ADD CONSTRAINT FK_ArItem_Document FOREIGN KEY (DocumentId) REFERENCES integ.Document(DocumentId);
ALTER TABLE ap.Item ADD CONSTRAINT FK_ApItem_Document FOREIGN KEY (DocumentId) REFERENCES integ.Document(DocumentId);
GO

-- ---------------------------------------------------------------- the Power App's expenses join the ledger

-- Which business an expense belongs to (null: the default one) and the journal entry its approval produced.
ALTER TABLE dbo.Expenses ADD EntityId int NULL CONSTRAINT FK_Expenses_Entity REFERENCES org.Entity(EntityId),
                             JournalEntryId bigint NULL CONSTRAINT FK_Expenses_Journal REFERENCES gl.JournalEntry(JournalEntryId);
GO
