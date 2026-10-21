-- When each open item was paid: the receipts posted from the inbox (integ.Document, kind Receipt) say, line by line, which
-- reference they settle (`appliesTo`, else the document's own reference) and on what date. A view over them, without any
-- text: the item, the date and the amount. It is what "days to pay" is built from (Analytics reads it through its
-- read-only login, see database/tools/analytics_reader.sql); the inbox itself stays closed to that login.
CREATE OR ALTER VIEW ar.vw_ItemPayments AS
SELECT i.ItemId, i.EntityId, d.DocumentDate AS PaymentDate, SUM(l.Amount) AS Amount
FROM integ.Document d
CROSS APPLY OPENJSON(d.PayloadJson, '$.lines') WITH (Amount decimal(14, 2) '$.amount', AppliesTo nvarchar(60) '$.appliesTo') l
JOIN ar.Item i ON i.EntityId = d.EntityId AND i.Reference = COALESCE(l.AppliesTo, d.Reference)
WHERE d.Kind = 'Receipt' AND d.Status = 'Posted'
GROUP BY i.ItemId, i.EntityId, d.DocumentId, d.DocumentDate;
GO
