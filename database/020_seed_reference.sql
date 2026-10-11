-- Reference data: HST codes, a chart of accounts for an Ontario clinic group, the posting maps, the businesses
-- of the demo platform and the payers every Ontario clinic deals with. GIFI codes are the usual CRA lines for
-- these accounts; an accountant should confirm them before a real T2.

SET NOCOUNT ON;

-- Months are unique (the Power App never created two of the same month; this keeps it that way).
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_AccountingPeriods_YearMonth')
    CREATE UNIQUE INDEX UX_AccountingPeriods_YearMonth ON dbo.AccountingPeriods ([Year], [Month]);

-- Every month of 2025 and 2026 exists; months already there keep their state.
WITH m AS (SELECT y.[Year], n.[Month] FROM (VALUES (2025),(2026)) y([Year]) CROSS JOIN (VALUES (1),(2),(3),(4),(5),(6),(7),(8),(9),(10),(11),(12)) n([Month]))
INSERT dbo.AccountingPeriods ([Year], [Month], IsClosed)
SELECT m.[Year], m.[Month], 0 FROM m
WHERE NOT EXISTS (SELECT 1 FROM dbo.AccountingPeriods p WHERE p.[Year] = m.[Year] AND p.[Month] = m.[Month]);

INSERT tax.TaxCode (Code, Name, RatePercent, Recoverable, Description) VALUES
('HST13',  N'HST 13 % (Ontario)', 13.00, 1, N'Taxable supplies in Ontario: over-the-counter products, most third-party forms and reports, cosmetic services.'),
('ZERO',   N'Zero-rated',          0.00, 1, N'Prescription drugs and their dispensing fees, many medical devices. No tax charged; input tax credits can be claimed.'),
('EXEMPT', N'Exempt',              0.00, 0, N'Health care services by physicians, nurses and other practitioners, and lab and diagnostic services. No tax charged; no input tax credits.'),
('NONE',   N'Outside HST',         0.00, 0, N'Not a supply: transfers, wages, OHIP payments for exempt services already coded EXEMPT.');

-- ---------------------------------------------------------------- chart of accounts

INSERT gl.Account (Number, Name, [Type], Subtype, IsPostable, GifiCode, Description) VALUES
('1000', N'Bank – operating account',            'Asset', 'Bank',          1, '1001', N'OHIP direct deposits, ODB payments, EFT and card settlements arrive here.'),
('1010', N'Cash on hand',                        'Asset', 'Cash',          1, '1001', N'The front desk cash drawer.'),
('1020', N'Card payments clearing',              'Asset', 'Clearing',      1, '1001', N'Debit and credit card payments until the processor settles them to the bank.'),
('1100', N'Accounts receivable – OHIP',          'Asset', 'Receivable',    1, '1060', N'Claims submitted to the Ministry of Health, waiting for the remittance advice.'),
('1105', N'Accounts receivable – other provinces','Asset','Receivable',    1, '1060', N'Reciprocal medical billing (RMB) for patients covered by another province.'),
('1110', N'Accounts receivable – ODB',           'Asset', 'Receivable',    1, '1060', N'Ontario Drug Benefit claims adjudicated online, paid by the Ministry.'),
('1120', N'Accounts receivable – WSIB',          'Asset', 'Receivable',    1, '1060', N'Workplace Safety and Insurance Board.'),
('1130', N'Accounts receivable – insurers',      'Asset', 'Receivable',    1, '1060', N'Private drug and extended health plans.'),
('1140', N'Accounts receivable – patients',      'Asset', 'Receivable',    1, '1060', N'Uninsured services and co-payments owed by patients (kept by invoice number, not by name).'),
('1200', N'Inventory – drugs',                   'Asset', 'Inventory',     1, '1121', N'Pharmacy stock at cost.'),
('1210', N'Inventory – medical supplies',        'Asset', 'Inventory',     1, '1121', NULL),
('1300', N'HST input tax credits',               'Asset', 'TaxRecoverable',1, '1066', N'HST paid on purchases, claimed back on the HST return.'),
('2000', N'Accounts payable – suppliers',        'Liability', 'Payable',   1, '2620', NULL),
('2010', N'Payable to physicians',               'Liability', 'Payable',   1, '2620', N'Each physician''s share of their OHIP billings, net of the clinic''s overhead.'),
('2100', N'HST payable',                         'Liability', 'TaxPayable',1, '2680', N'HST charged on taxable supplies.'),
('3000', N'Share capital',                       'Equity', NULL,           1, '3500', NULL),
('3100', N'Retained earnings',                   'Equity', 'RetainedEarnings', 1, '3600', N'Opening balances and prior years'' results.'),
('4000', N'OHIP – professional fees',            'Revenue', NULL,          1, '8000', N'Insured physician and nurse practitioner services.'),
('4010', N'OHIP – technical fees',               'Revenue', NULL,          1, '8000', N'Technical component of diagnostic services (ECG, echo, imaging).'),
('4020', N'Reciprocal billing – other provinces','Revenue', NULL,          1, '8000', NULL),
('4030', N'WSIB services',                       'Revenue', NULL,          1, '8000', NULL),
('4100', N'Uninsured services',                  'Revenue', NULL,          1, '8000', N'Sick notes, forms, travel medicine and other services OHIP does not pay for.'),
('4200', N'Laboratory – OHIP',                   'Revenue', NULL,          1, '8000', N'Insured laboratory tests.'),
('4210', N'Laboratory – uninsured',              'Revenue', NULL,          1, '8000', NULL),
('4300', N'Diagnostics – echo and ECG',          'Revenue', NULL,          1, '8000', N'Reports from ClinicReport.'),
('4400', N'Pharmacy – prescription drugs',       'Revenue', NULL,          1, '8000', N'Drug cost and mark-up on prescriptions (zero-rated).'),
('4410', N'Pharmacy – dispensing fees',          'Revenue', NULL,          1, '8000', NULL),
('4420', N'Pharmacy – front store (OTC)',        'Revenue', NULL,          1, '8000', N'Over-the-counter products, taxable.'),
('4900', N'Other income',                        'Revenue', NULL,          1, '8230', NULL),
('5000', N'Cost of drugs sold',                  'Expense', 'CostOfSales', 1, '8518', NULL),
('5010', N'Cost of supplies used',               'Expense', 'CostOfSales', 1, '8518', NULL),
('5100', N'Physician fees',                      'Expense', NULL,          1, '8860', N'Physicians'' share of their billings (they are independent contractors).'),
('5200', N'Claim refusals and write-offs',       'Expense', NULL,          1, '8590', N'OHIP refusals and short payments not resubmitted, uncollectible patient balances.'),
('6000', N'Salaries and wages',                  'Expense', NULL,          1, '9060', NULL),
('6100', N'Rent',                                'Expense', NULL,          1, '8910', NULL),
('6200', N'Office supplies',                     'Expense', NULL,          1, '8811', NULL),
('6210', N'Medical supplies',                    'Expense', NULL,          1, '8811', NULL),
('6300', N'Meals and entertainment',             'Expense', NULL,          1, '8523', N'50 % deductible for income tax.'),
('6310', N'Travel',                              'Expense', NULL,          1, '9200', NULL),
('6400', N'Professional fees – accounting and legal','Expense', NULL,      1, '8860', NULL),
('6500', N'Insurance',                           'Expense', NULL,          1, '8690', N'Including CMPA and malpractice where the business pays it.'),
('6600', N'Utilities and telephone',             'Expense', NULL,          1, '9220', NULL),
('6700', N'Software and IT',                     'Expense', NULL,          1, '8810', NULL),
('6900', N'Other expenses',                      'Expense', NULL,          1, '9270', NULL);

DECLARE @a TABLE (Number varchar(10) PRIMARY KEY, Id int);
INSERT @a SELECT Number, AccountId FROM gl.Account;

-- Categories the applications use, and the Power App's expense categories.
INSERT gl.CategoryMap (EntityId, Category, AccountId, Description)
SELECT NULL, c.Category, a.Id, c.Description FROM (VALUES
    ('OHIP.Professional',     '4000', N'ClinicHub: insured visit fees'),
    ('OHIP.Technical',        '4010', N'Technical fee of a diagnostic service'),
    ('OHIP.Reciprocal',       '4020', N'ClinicHub: reciprocal medical billing'),
    ('WSIB.Services',         '4030', NULL),
    ('Uninsured.Services',    '4100', N'ClinicHub: uninsured services invoiced to the patient'),
    ('Lab.OHIP',              '4200', N'LabFlow: insured tests'),
    ('Lab.Uninsured',         '4210', N'LabFlow: tests paid by the patient'),
    ('Diagnostics.Echo',      '4300', N'ClinicReport: echocardiography'),
    ('Diagnostics.ECG',       '4300', N'ClinicReport: ECG'),
    ('Pharmacy.Drug',         '4400', N'PharmaFlow: drug cost and mark-up'),
    ('Pharmacy.DispensingFee','4410', N'PharmaFlow: professional fee'),
    ('Pharmacy.OTC',          '4420', N'PharmaFlow: front store'),
    ('Pharmacy.COGS',         '5000', N'PharmaFlow: cost of a dispensed drug'),
    ('Inventory.Drugs',       '1200', N'PharmaFlow: drugs received from a supplier'),
    ('Inventory.Supplies',    '1210', NULL),
    ('Adjustment.Refused',    '5200', N'A claim refused by the payer and not resubmitted'),
    ('Adjustment.ShortPaid',  '5200', N'Paid less than claimed'),
    ('Adjustment.WriteOff',   '5200', N'Uncollectible balance'),
    ('Expense.PhysicianShare','5100', N'ClinicHub: a physician''s share of their payments'),
    ('Other.Income',          '4900', NULL),
    -- The Power App's categories (dbo.Expenses.Category).
    ('Food',                  '6300', N'Power App expense category'),
    ('Meals',                 '6300', N'Power App expense category'),
    ('Office Supplies',       '6200', N'Power App expense category'),
    ('Travel',                '6310', N'Power App expense category'),
    ('Rent',                  '6100', NULL),
    ('Salaries',              '6000', NULL),
    ('Medical Supplies',      '6210', NULL),
    ('Insurance',             '6500', NULL),
    ('Utilities',             '6600', NULL),
    ('Software',              '6700', NULL),
    ('Professional Fees',     '6400', NULL),
    ('Other',                 '6900', NULL)
) c(Category, Number, Description) JOIN @a a ON a.Number = c.Number;

INSERT gl.ControlMap (PartyType, Side, AccountId)
SELECT c.PartyType, c.Side, a.Id FROM (VALUES
    ('OHIP','Receivable','1100'), ('Province','Receivable','1105'), ('ODB','Receivable','1110'), ('WSIB','Receivable','1120'),
    ('Insurer','Receivable','1130'), ('Patient','Receivable','1140'), ('Other','Receivable','1140'),
    ('Supplier','Payable','2000'), ('Provider','Payable','2010'), ('Other','Payable','2000')
) c(PartyType, Side, Number) JOIN @a a ON a.Number = c.Number;

INSERT gl.CashMap (Method, AccountId)
SELECT c.Method, a.Id FROM (VALUES ('Bank','1000'), ('EFT','1000'), ('Cheque','1000'), ('Cash','1010'), ('Debit','1020'), ('Credit','1020')) c(Method, Number)
JOIN @a a ON a.Number = c.Number;

-- ---------------------------------------------------------------- businesses and payers

INSERT org.Entity (Code, Name, Kind, HstNumber, IsDefault) VALUES
('GROUP', N'Clinic group (head office)',          'Holding',  NULL, 1),
('HV',    N'Harbourview Medical Centre (demo)',   'Clinic',   NULL, 0),
('LS',    N'Lakeshore Family Health (demo)',      'Clinic',   NULL, 0),
('LAB',   N'LabFlow community laboratory (demo)', 'Lab',      NULL, 0),
('RX',    N'PharmaFlow pharmacy (demo)',          'Pharmacy', NULL, 0);

INSERT org.EntitySource (Source, SourceKey, EntityId)
SELECT s.Source, s.SourceKey, e.EntityId FROM (VALUES
    ('clinichub', 'harbourview', 'HV'), ('clinichub', 'lakeshore', 'LS'),
    ('clinicreport', 'harbourview', 'HV'), ('clinicreport', 'lakeshore', 'LS'), ('clinicreport', 'default', 'HV'),
    -- LabFlow with one lab database sends "lab"; with a database per clinic, the clinic's collection room is the clinic's.
    ('labflow', 'lab', 'LAB'), ('labflow', 'harbourview', 'HV'), ('labflow', 'lakeshore', 'LS'),
    ('pharmaflow', 'pharmacy', 'RX'), ('pharmaflow', 'harbourview', 'RX'), ('pharmaflow', 'lakeshore', 'RX')
) s(Source, SourceKey, Code) JOIN org.Entity e ON e.Code = s.Code;

INSERT org.Party (PartyType, Code, Name, PaymentTermsDays) VALUES
('OHIP',     'OHIP',    N'Ontario Ministry of Health – OHIP',            30),
('ODB',      'ODB',     N'Ontario Ministry of Health – Ontario Drug Benefit', 30),
('WSIB',     'WSIB',    N'Workplace Safety and Insurance Board',          45),
('Patient',  'PATIENT', N'Patients (private pay)',                        0),
('Province', 'BC', N'British Columbia – Medical Services Plan', 60),
('Province', 'AB', N'Alberta Health Care Insurance Plan', 60),
('Province', 'NS', N'Nova Scotia MSI', 60),
('Province', 'MB', N'Manitoba Health', 60),
('Insurer',  'GENERIC', N'Private insurer (unspecified)', 30);
GO
