-- Idempotent backfill of SaDocApplication from posted SO-linked DO / Invoice lines.
-- Rerun-safe: IF NOT EXISTS on unique source+target; skip audit unique on violators.
-- Manual DBA script — do NOT run at app startup.
-- Requires: create-sadocapplication.sql + alter-saso-allocation-columns.sql
GO

SET NOCOUNT ON;

IF OBJECT_ID(N'dbo.SaDocApplication', N'U') IS NULL
    RAISERROR(N'SaDocApplication missing. Run create-sadocapplication.sql first.', 16, 1);
GO

/* ---- SO_DO from posted SO-linked DO lines ---- */
DECLARE @do CURSOR;
SET @do = CURSOR LOCAL FAST_FORWARD FOR
    SELECT d.CompanyCode, d.BranchCode, d.DONo, d.Line, d.SONo, ISNULL(d.CustRel, 1), d.SOLine,
           CASE WHEN d.SoConsumedQty > 0 THEN d.SoConsumedQty ELSE d.Qty END AS AppliedQty,
           d.NetAmount
    FROM dbo.SaDODetail d
    INNER JOIN dbo.SaDO h
        ON h.CompanyCode = d.CompanyCode AND h.BranchCode = d.BranchCode AND h.DONo = d.DONo
    WHERE h.Status = N'POSTED'
      AND NULLIF(LTRIM(RTRIM(d.SONo)), N'') IS NOT NULL
      AND d.SOLine IS NOT NULL AND d.SOLine > 0;

DECLARE @CompanyCode nvarchar(10), @BranchCode nvarchar(10), @DoNo nvarchar(30), @Line smallint;
DECLARE @SoNo nvarchar(30), @CustRel smallint, @SoLine smallint, @AppliedQty decimal(18,4), @NetAmount decimal(18,2);

OPEN @do;
FETCH NEXT FROM @do INTO @CompanyCode, @BranchCode, @DoNo, @Line, @SoNo, @CustRel, @SoLine, @AppliedQty, @NetAmount;
WHILE @@FETCH_STATUS = 0
BEGIN
    IF @AppliedQty IS NULL OR @AppliedQty <= 0
    BEGIN
        IF NOT EXISTS (
            SELECT 1 FROM dbo.SaDocApplicationBackfillSkip
            WHERE CompanyCode = @CompanyCode AND BranchCode = @BranchCode
              AND DocType = N'DO' AND DocId = @DoNo AND Line = @Line AND ViolationCode = N'BAD_QTY')
            INSERT INTO dbo.SaDocApplicationBackfillSkip
                (CompanyCode, BranchCode, DocType, DocId, Line, ViolationCode, Reason, SourceValues, TargetValues)
            VALUES (@CompanyCode, @BranchCode, N'DO', @DoNo, @Line, N'BAD_QTY',
                    N'AppliedQty must be > 0',
                    CONCAT(N'SO=', @SoNo, N';Line=', @SoLine), CONCAT(N'DO=', @DoNo, N';Line=', @Line));
    END
    ELSE IF NOT EXISTS (
        SELECT 1 FROM dbo.SaSODetail s
        WHERE s.CompanyCode = @CompanyCode AND s.BranchCode = @BranchCode
          AND s.SONo = @SoNo AND s.Line = @SoLine)
    BEGIN
        IF NOT EXISTS (
            SELECT 1 FROM dbo.SaDocApplicationBackfillSkip
            WHERE CompanyCode = @CompanyCode AND BranchCode = @BranchCode
              AND DocType = N'DO' AND DocId = @DoNo AND Line = @Line AND ViolationCode = N'MISSING_SO')
            INSERT INTO dbo.SaDocApplicationBackfillSkip
                (CompanyCode, BranchCode, DocType, DocId, Line, ViolationCode, Reason, SourceValues, TargetValues)
            VALUES (@CompanyCode, @BranchCode, N'DO', @DoNo, @Line, N'MISSING_SO',
                    N'SO line not found',
                    CONCAT(N'SO=', @SoNo, N';Line=', @SoLine), CONCAT(N'DO=', @DoNo, N';Line=', @Line));
    END
    ELSE IF NOT EXISTS (
        SELECT 1 FROM dbo.SaDocApplication
        WHERE CompanyCode = @CompanyCode AND BranchCode = @BranchCode
          AND SourceDocType = N'SO' AND SourceDocId = @SoNo AND SourceCustRel = 1 AND SourceLineId = @SoLine
          AND TargetDocType = N'DO' AND TargetDocId = @DoNo AND TargetLineId = @Line)
    BEGIN
        INSERT INTO dbo.SaDocApplication (
            CompanyCode, BranchCode,
            SourceDocType, SourceDocId, SourceCustRel, SourceLineId,
            TargetDocType, TargetDocId, TargetCustRel, TargetLineId,
            RelatedSONo, RelatedCustRel, RelatedSOLine,
            AppliedQty, AppliedAmount, Created, CreatedUID)
        VALUES (
            @CompanyCode, @BranchCode,
            N'SO', @SoNo, 1, @SoLine,
            N'DO', @DoNo, 0, @Line,
            @SoNo, 1, @SoLine,
            @AppliedQty, @NetAmount, SYSUTCDATETIME(), N'BACKFILL');
    END

    FETCH NEXT FROM @do INTO @CompanyCode, @BranchCode, @DoNo, @Line, @SoNo, @CustRel, @SoLine, @AppliedQty, @NetAmount;
END
CLOSE @do;
DEALLOCATE @do;
GO

/* ---- SO_INV from posted invoices LinkDo=0 with SO ---- */
DECLARE @inv CURSOR;
SET @inv = CURSOR LOCAL FAST_FORWARD FOR
    SELECT d.CompanyCode, d.BranchCode, d.InvNo, CAST(d.Line AS smallint), d.SONo, ISNULL(d.CustRel, 1), d.SOLine,
           CASE WHEN d.SoConsumedQty > 0 THEN d.SoConsumedQty ELSE d.Qty END AS AppliedQty,
           d.NetAmount, d.LinkDo
    FROM dbo.SaInvoiceDetail d
    INNER JOIN dbo.SaInvoice h
        ON h.CompanyCode = d.CompanyCode AND h.BranchCode = d.BranchCode AND h.InvNo = d.InvNo
    WHERE h.Status = N'POSTED'
      AND NULLIF(LTRIM(RTRIM(d.SONo)), N'') IS NOT NULL
      AND d.SOLine IS NOT NULL AND d.SOLine > 0;

DECLARE @ICompany nvarchar(10), @IBranch nvarchar(10), @InvNo nvarchar(30), @ILine smallint;
DECLARE @ISoNo nvarchar(30), @ICustRel smallint, @ISoLine smallint, @IQty decimal(18,4), @IAmt decimal(18,2), @LinkDo bit;

OPEN @inv;
FETCH NEXT FROM @inv INTO @ICompany, @IBranch, @InvNo, @ILine, @ISoNo, @ICustRel, @ISoLine, @IQty, @IAmt, @LinkDo;
WHILE @@FETCH_STATUS = 0
BEGIN
    IF @LinkDo = 1
    BEGIN
        /* DO_INV path handled below; skip SO_INV */
        GOTO NextInv;
    END

    IF @IQty IS NULL OR @IQty <= 0
    BEGIN
        IF NOT EXISTS (
            SELECT 1 FROM dbo.SaDocApplicationBackfillSkip
            WHERE CompanyCode = @ICompany AND BranchCode = @IBranch
              AND DocType = N'INV' AND DocId = @InvNo AND Line = @ILine AND ViolationCode = N'BAD_QTY')
            INSERT INTO dbo.SaDocApplicationBackfillSkip
                (CompanyCode, BranchCode, DocType, DocId, Line, ViolationCode, Reason, SourceValues, TargetValues)
            VALUES (@ICompany, @IBranch, N'INV', @InvNo, @ILine, N'BAD_QTY',
                    N'AppliedQty must be > 0',
                    CONCAT(N'SO=', @ISoNo), CONCAT(N'INV=', @InvNo, N';Line=', @ILine));
    END
    ELSE IF NOT EXISTS (
        SELECT 1 FROM dbo.SaDocApplication
        WHERE CompanyCode = @ICompany AND BranchCode = @IBranch
          AND SourceDocType = N'SO' AND SourceDocId = @ISoNo AND SourceCustRel = 1 AND SourceLineId = @ISoLine
          AND TargetDocType = N'INV' AND TargetDocId = @InvNo AND TargetLineId = @ILine)
    BEGIN
        INSERT INTO dbo.SaDocApplication (
            CompanyCode, BranchCode,
            SourceDocType, SourceDocId, SourceCustRel, SourceLineId,
            TargetDocType, TargetDocId, TargetCustRel, TargetLineId,
            RelatedSONo, RelatedCustRel, RelatedSOLine,
            AppliedQty, AppliedAmount, Created, CreatedUID)
        VALUES (
            @ICompany, @IBranch,
            N'SO', @ISoNo, 1, @ISoLine,
            N'INV', @InvNo, 0, @ILine,
            @ISoNo, 1, @ISoLine,
            @IQty, @IAmt, SYSUTCDATETIME(), N'BACKFILL');
    END

    NextInv:
    FETCH NEXT FROM @inv INTO @ICompany, @IBranch, @InvNo, @ILine, @ISoNo, @ICustRel, @ISoLine, @IQty, @IAmt, @LinkDo;
END
CLOSE @inv;
DEALLOCATE @inv;
GO

/* ---- DO_INV from posted LinkDo=1 (rare / should be none pre-cutover) ---- */
DECLARE @dinv CURSOR;
SET @dinv = CURSOR LOCAL FAST_FORWARD FOR
    SELECT d.CompanyCode, d.BranchCode, d.InvNo, CAST(d.Line AS smallint),
           ISNULL(NULLIF(LTRIM(RTRIM(d.DONo)), N''), N''), d.DOLine,
           CASE WHEN d.SoConsumedQty > 0 THEN d.SoConsumedQty ELSE d.Qty END AS AppliedQty,
           d.NetAmount
    FROM dbo.SaInvoiceDetail d
    INNER JOIN dbo.SaInvoice h
        ON h.CompanyCode = d.CompanyCode AND h.BranchCode = d.BranchCode AND h.InvNo = d.InvNo
    WHERE h.Status = N'POSTED' AND d.LinkDo = 1;

DECLARE @DCompany nvarchar(10), @DBranch nvarchar(10), @DInv nvarchar(30), @DLine smallint;
DECLARE @DDoNo nvarchar(30), @DDoLine smallint, @DQty decimal(18,4), @DAmt decimal(18,2);
DECLARE @RSo nvarchar(30), @RRel smallint, @RLine smallint;

OPEN @dinv;
FETCH NEXT FROM @dinv INTO @DCompany, @DBranch, @DInv, @DLine, @DDoNo, @DDoLine, @DQty, @DAmt;
WHILE @@FETCH_STATUS = 0
BEGIN
    IF @DDoNo = N'' OR @DDoLine IS NULL OR @DDoLine <= 0
    BEGIN
        IF NOT EXISTS (
            SELECT 1 FROM dbo.SaDocApplicationBackfillSkip
            WHERE CompanyCode = @DCompany AND BranchCode = @DBranch
              AND DocType = N'INV' AND DocId = @DInv AND Line = @DLine AND ViolationCode = N'MISSING_DO')
            INSERT INTO dbo.SaDocApplicationBackfillSkip
                (CompanyCode, BranchCode, DocType, DocId, Line, ViolationCode, Reason, SourceValues, TargetValues)
            VALUES (@DCompany, @DBranch, N'INV', @DInv, @DLine, N'MISSING_DO',
                    N'LinkDo invoice line missing DONo/DOLine',
                    N'', CONCAT(N'INV=', @DInv, N';Line=', @DLine));
        GOTO NextDinv;
    END

    SELECT @RSo = NULLIF(LTRIM(RTRIM(dd.SONo)), N''), @RRel = ISNULL(dd.CustRel, 1), @RLine = dd.SOLine
    FROM dbo.SaDODetail dd
    WHERE dd.CompanyCode = @DCompany AND dd.BranchCode = @DBranch
      AND dd.DONo = @DDoNo AND dd.Line = @DDoLine;

    IF @RSo IS NULL OR @RLine IS NULL OR @RLine <= 0
    BEGIN
        IF NOT EXISTS (
            SELECT 1 FROM dbo.SaDocApplicationBackfillSkip
            WHERE CompanyCode = @DCompany AND BranchCode = @DBranch
              AND DocType = N'INV' AND DocId = @DInv AND Line = @DLine AND ViolationCode = N'STANDALONE_DO')
            INSERT INTO dbo.SaDocApplicationBackfillSkip
                (CompanyCode, BranchCode, DocType, DocId, Line, ViolationCode, Reason, SourceValues, TargetValues)
            VALUES (@DCompany, @DBranch, N'INV', @DInv, @DLine, N'STANDALONE_DO',
                    N'Standalone DO cannot be DO_INV source',
                    CONCAT(N'DO=', @DDoNo, N';Line=', @DDoLine), CONCAT(N'INV=', @DInv));
        GOTO NextDinv;
    END

    IF @DQty IS NULL OR @DQty <= 0
    BEGIN
        IF NOT EXISTS (
            SELECT 1 FROM dbo.SaDocApplicationBackfillSkip
            WHERE CompanyCode = @DCompany AND BranchCode = @DBranch
              AND DocType = N'INV' AND DocId = @DInv AND Line = @DLine AND ViolationCode = N'BAD_QTY')
            INSERT INTO dbo.SaDocApplicationBackfillSkip
                (CompanyCode, BranchCode, DocType, DocId, Line, ViolationCode, Reason, SourceValues, TargetValues)
            VALUES (@DCompany, @DBranch, N'INV', @DInv, @DLine, N'BAD_QTY',
                    N'AppliedQty must be > 0',
                    CONCAT(N'DO=', @DDoNo), CONCAT(N'INV=', @DInv));
        GOTO NextDinv;
    END

    IF NOT EXISTS (
        SELECT 1 FROM dbo.SaDocApplication
        WHERE CompanyCode = @DCompany AND BranchCode = @DBranch
          AND SourceDocType = N'DO' AND SourceDocId = @DDoNo AND SourceCustRel = 0 AND SourceLineId = @DDoLine
          AND TargetDocType = N'INV' AND TargetDocId = @DInv AND TargetLineId = @DLine)
    BEGIN
        INSERT INTO dbo.SaDocApplication (
            CompanyCode, BranchCode,
            SourceDocType, SourceDocId, SourceCustRel, SourceLineId,
            TargetDocType, TargetDocId, TargetCustRel, TargetLineId,
            RelatedSONo, RelatedCustRel, RelatedSOLine,
            AppliedQty, AppliedAmount, Created, CreatedUID)
        VALUES (
            @DCompany, @DBranch,
            N'DO', @DDoNo, 0, @DDoLine,
            N'INV', @DInv, 0, @DLine,
            @RSo, @RRel, @RLine,
            @DQty, @DAmt, SYSUTCDATETIME(), N'BACKFILL');
    END

    NextDinv:
    FETCH NEXT FROM @dinv INTO @DCompany, @DBranch, @DInv, @DLine, @DDoNo, @DDoLine, @DQty, @DAmt;
END
CLOSE @dinv;
DEALLOCATE @dinv;
GO

/* ---- Projection recalc for SO lines that have allocations or non-zero shipped ---- */
;WITH SoAgg AS (
    SELECT
        s.CompanyCode, s.BranchCode, s.SONo, s.Line, s.OrderQty,
        Delivered = ISNULL((
            SELECT SUM(a.AppliedQty)
            FROM dbo.SaDocApplication a
            WHERE a.CompanyCode = s.CompanyCode AND a.BranchCode = s.BranchCode
              AND a.SourceDocType = N'SO' AND a.SourceDocId = s.SONo
              AND a.SourceCustRel = 1 AND a.SourceLineId = s.Line
              AND a.TargetDocType = N'DO'), 0),
        InvoicedDirect = ISNULL((
            SELECT SUM(a.AppliedQty)
            FROM dbo.SaDocApplication a
            WHERE a.CompanyCode = s.CompanyCode AND a.BranchCode = s.BranchCode
              AND a.SourceDocType = N'SO' AND a.SourceDocId = s.SONo
              AND a.SourceCustRel = 1 AND a.SourceLineId = s.Line
              AND a.TargetDocType = N'INV'), 0),
        InvoicedViaDo = ISNULL((
            SELECT SUM(a.AppliedQty)
            FROM dbo.SaDocApplication a
            WHERE a.CompanyCode = s.CompanyCode AND a.BranchCode = s.BranchCode
              AND a.TargetDocType = N'INV'
              AND a.RelatedSONo = s.SONo AND a.RelatedCustRel = 1 AND a.RelatedSOLine = s.Line
              AND a.SourceDocType = N'DO'), 0)
    FROM dbo.SaSODetail s
)
UPDATE s
SET
    s.DeliveredQty = a.Delivered,
    s.InvoicedQty = a.InvoicedDirect + a.InvoicedViaDo,
    s.ShippedQty = a.Delivered,
    s.BalanceQty = a.OrderQty - a.Delivered
FROM dbo.SaSODetail s
INNER JOIN SoAgg a
    ON a.CompanyCode = s.CompanyCode AND a.BranchCode = s.BranchCode
   AND a.SONo = s.SONo AND a.Line = s.Line;
GO

/* ---- SO header status from projections ---- */
;WITH SoHdr AS (
    SELECT
        h.CompanyCode, h.BranchCode, h.SONo,
        AnyDelivered = CASE WHEN EXISTS (
            SELECT 1 FROM dbo.SaSODetail d
            WHERE d.CompanyCode = h.CompanyCode AND d.BranchCode = h.BranchCode AND d.SONo = h.SONo
              AND d.DeliveredQty > 0) THEN 1 ELSE 0 END,
        AllDelivered = CASE WHEN NOT EXISTS (
            SELECT 1 FROM dbo.SaSODetail d
            WHERE d.CompanyCode = h.CompanyCode AND d.BranchCode = h.BranchCode AND d.SONo = h.SONo
              AND d.DeliveredQty < d.OrderQty) AND EXISTS (
            SELECT 1 FROM dbo.SaSODetail d
            WHERE d.CompanyCode = h.CompanyCode AND d.BranchCode = h.BranchCode AND d.SONo = h.SONo)
            THEN 1 ELSE 0 END,
        AnyInvoiced = CASE WHEN EXISTS (
            SELECT 1 FROM dbo.SaSODetail d
            WHERE d.CompanyCode = h.CompanyCode AND d.BranchCode = h.BranchCode AND d.SONo = h.SONo
              AND d.InvoicedQty > 0) THEN 1 ELSE 0 END,
        AllInvoiced = CASE WHEN NOT EXISTS (
            SELECT 1 FROM dbo.SaSODetail d
            WHERE d.CompanyCode = h.CompanyCode AND d.BranchCode = h.BranchCode AND d.SONo = h.SONo
              AND d.InvoicedQty < d.OrderQty) AND EXISTS (
            SELECT 1 FROM dbo.SaSODetail d
            WHERE d.CompanyCode = h.CompanyCode AND d.BranchCode = h.BranchCode AND d.SONo = h.SONo)
            THEN 1 ELSE 0 END
    FROM dbo.SaSO h
    WHERE h.ClosedReason IS NULL OR h.ClosedReason <> N'FORCE_CLOSED'
)
UPDATE h
SET
    h.FulfillmentStatus = CASE
        WHEN a.AllDelivered = 1 THEN N'FULL'
        WHEN a.AnyDelivered = 1 THEN N'PARTIAL'
        ELSE N'NONE' END,
    h.BillingStatus = CASE
        WHEN a.AllInvoiced = 1 THEN N'FULL'
        WHEN a.AnyInvoiced = 1 THEN N'PARTIAL'
        ELSE N'NONE' END,
    h.Status = CASE
        WHEN a.AllDelivered = 1 AND a.AllInvoiced = 1 THEN N'CLOSED'
        WHEN a.AnyDelivered = 1 THEN N'SHIPPED'
        ELSE N'NEW' END,
    h.ClosedReason = CASE
        WHEN a.AllDelivered = 1 AND a.AllInvoiced = 1 THEN N'FULLY_CONSUMED'
        ELSE NULL END,
    h.ClosedDate = CASE
        WHEN a.AllDelivered = 1 AND a.AllInvoiced = 1 THEN ISNULL(h.ClosedDate, SYSUTCDATETIME())
        ELSE NULL END
FROM dbo.SaSO h
INNER JOIN SoHdr a
    ON a.CompanyCode = h.CompanyCode AND a.BranchCode = h.BranchCode AND a.SONo = h.SONo;
GO

/* ---- DO BillingStatus ---- */
;WITH DoAgg AS (
    SELECT
        d.CompanyCode, d.BranchCode, d.DONo, d.Line, d.Qty,
        Billed = ISNULL((
            SELECT SUM(a.AppliedQty)
            FROM dbo.SaDocApplication a
            WHERE a.CompanyCode = d.CompanyCode AND a.BranchCode = d.BranchCode
              AND a.SourceDocType = N'DO' AND a.SourceDocId = d.DONo
              AND a.SourceCustRel = 0 AND a.SourceLineId = d.Line
              AND a.TargetDocType = N'INV'), 0)
    FROM dbo.SaDODetail d
)
UPDATE h
SET h.BillingStatus = CASE
    WHEN NOT EXISTS (SELECT 1 FROM DoAgg a WHERE a.CompanyCode = h.CompanyCode AND a.BranchCode = h.BranchCode AND a.DONo = h.DONo)
        THEN N'NONE'
    WHEN NOT EXISTS (SELECT 1 FROM DoAgg a WHERE a.CompanyCode = h.CompanyCode AND a.BranchCode = h.BranchCode AND a.DONo = h.DONo AND a.Billed < a.Qty)
         AND EXISTS (SELECT 1 FROM DoAgg a WHERE a.CompanyCode = h.CompanyCode AND a.BranchCode = h.BranchCode AND a.DONo = h.DONo AND a.Billed > 0)
        THEN N'FULL'
    WHEN EXISTS (SELECT 1 FROM DoAgg a WHERE a.CompanyCode = h.CompanyCode AND a.BranchCode = h.BranchCode AND a.DONo = h.DONo AND a.Billed > 0)
        THEN N'PARTIAL'
    ELSE N'NONE' END
FROM dbo.SaDO h;
GO

PRINT N'backfill-sadocapplication.sql completed.';
GO
