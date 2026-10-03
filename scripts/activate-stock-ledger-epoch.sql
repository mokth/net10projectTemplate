/*
  Activate a stock-ledger epoch and write sealed OPENING facts.
  Manual DBA script — do NOT run at app startup.
  Contains no credentials or connection strings.

  Parameters: @CompanyCode, @BranchCode, @EffectiveFrom, @ActivatedBy.
  Inserts PREPARED then ACTIVE StockLedgerEpoch.
  Writes StockPosting OPENING (sealed) + OPENING_IN production legs for each
  positive PrProductionBalLot, and inventory inbound IvTrxHistory
  (LedgerVersion=2, EntryRole=OPENING) for each IvBalLoc StdQty>0.
  Does NOT change live Qty / BaseQty / StdQty.
  Idempotent: same migration batch already ACTIVE is a no-op.
  Requires no other ACTIVE epoch on the branch.
*/

SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @CompanyCode   nvarchar(5)     = N'DEMO';
DECLARE @BranchCode    nvarchar(5)     = N'HQ';
DECLARE @EffectiveFrom datetime2(7)    = '2026-10-01T00:00:00';
DECLARE @ActivatedBy   nvarchar(100)   = N'DBA';

DECLARE @MigrationBatchId uniqueidentifier =
    CONVERT(uniqueidentifier,
        HASHBYTES('MD5',
            CONVERT(varbinary(8000),
                CONCAT(N'STOCK-LEDGER-CUTOVER|', @CompanyCode, N'|', @BranchCode, N'|', CONVERT(nvarchar(33), @EffectiveFrom, 126)))));

DECLARE @PeriodKey char(7) = CONVERT(char(7), LEFT(CONVERT(varchar(10), @EffectiveFrom, 126), 7));
DECLARE @PostedAtUtc datetime2(7) = SYSUTCDATETIME();
DECLARE @ActorShort nvarchar(10) = LEFT(ISNULL(@ActivatedBy, N'DBA'), 10);
DECLARE @ShaPrep char(64) = CONVERT(char(64), CONVERT(varchar(64), HASHBYTES('SHA2_256', N'PREPARED'), 2));

PRINT N'=== activate-stock-ledger-epoch.sql ===';
PRINT N'Company=' + @CompanyCode + N' Branch=' + @BranchCode;
PRINT N'MigrationBatchId=' + CONVERT(nvarchar(36), @MigrationBatchId);

IF @CompanyCode IS NULL OR LTRIM(RTRIM(@CompanyCode)) = N''
    OR @BranchCode IS NULL OR LTRIM(RTRIM(@BranchCode)) = N''
    OR @EffectiveFrom IS NULL
    OR @ActivatedBy IS NULL OR LTRIM(RTRIM(@ActivatedBy)) = N''
BEGIN
    RAISERROR(N'activate-stock-ledger-epoch.sql: @CompanyCode, @BranchCode, @EffectiveFrom, @ActivatedBy are required.', 16, 1);
    RETURN;
END;

IF OBJECT_ID(N'dbo.StockLedgerEpoch', N'U') IS NULL
   OR OBJECT_ID(N'dbo.StockPosting', N'U') IS NULL
   OR OBJECT_ID(N'dbo.StockPostingBranchSequence', N'U') IS NULL
   OR COL_LENGTH(N'dbo.PrProductionBalLotMovement', N'LedgerVersion') IS NULL
   OR COL_LENGTH(N'dbo.IvTrxHistory', N'LedgerVersion') IS NULL
   OR COL_LENGTH(N'dbo.IvTrxHistory', N'EntryRole') IS NULL
BEGIN
    RAISERROR(N'activate-stock-ledger-epoch.sql: ledger schema missing. Run create-stock-posting-ledger.sql, alter-production-stock-ledger.sql, alter-inventory-history-ledger.sql first.', 16, 1);
    RETURN;
END;

IF EXISTS (
    SELECT 1
    FROM dbo.StockLedgerEpoch e
    WHERE e.CompanyCode = @CompanyCode
      AND e.BranchCode = @BranchCode
      AND e.MigrationBatchId = @MigrationBatchId
      AND e.Status = N'ACTIVE')
BEGIN
    SELECT
        N'ALREADY_ACTIVE' AS FindingCode,
        e.Id,
        e.MigrationBatchId,
        e.EffectiveFrom,
        e.ActivatedAtUtc,
        e.ActivatedBy
    FROM dbo.StockLedgerEpoch e
    WHERE e.CompanyCode = @CompanyCode
      AND e.BranchCode = @BranchCode
      AND e.MigrationBatchId = @MigrationBatchId
      AND e.Status = N'ACTIVE';

    PRINT N'Idempotent no-op: this migration batch is already ACTIVE.';
    RETURN;
END;

IF EXISTS (
    SELECT 1
    FROM dbo.StockLedgerEpoch e
    WHERE e.CompanyCode = @CompanyCode
      AND e.BranchCode = @BranchCode
      AND e.Status = N'ACTIVE')
BEGIN
    RAISERROR(N'activate-stock-ledger-epoch.sql: an ACTIVE epoch already exists for this branch.', 16, 1);
    RETURN;
END;

IF EXISTS (
    SELECT 1
    FROM dbo.StockLedgerEpoch e
    WHERE e.CompanyCode = @CompanyCode
      AND e.BranchCode = @BranchCode
      AND e.MigrationBatchId = @MigrationBatchId
      AND e.Status <> N'ACTIVE')
BEGIN
    RAISERROR(N'activate-stock-ledger-epoch.sql: leftover non-ACTIVE epoch exists for this migration batch. Review before retry.', 16, 1);
    RETURN;
END;

IF EXISTS (
    SELECT 1
    FROM dbo.PrProductionBalLot lot
    WHERE lot.CompanyCode = @CompanyCode
      AND lot.BranchCode = @BranchCode
      AND (lot.Qty < 0 OR lot.BaseQty < 0 OR (lot.Qty > 0 AND (lot.BaseQty <= 0 OR lot.ConversionFactorToBase <= 0))))
BEGIN
    RAISERROR(N'activate-stock-ledger-epoch.sql: negative or invalid production balances / UOM factors. Run preflight first.', 16, 1);
    RETURN;
END;

IF OBJECT_ID(N'dbo.IvBalLoc', N'U') IS NOT NULL
   AND EXISTS (
        SELECT 1
        FROM dbo.IvBalLoc b
        WHERE b.CompanyCode = @CompanyCode
          AND b.BranchCode = @BranchCode
          AND b.StdQty < 0)
BEGIN
    RAISERROR(N'activate-stock-ledger-epoch.sql: negative IvBalLoc.StdQty. Run inventory preflight first.', 16, 1);
    RETURN;
END;

BEGIN TRY
    BEGIN TRANSACTION;

    DECLARE @ProdQtyBefore decimal(18, 4) =
        (SELECT ISNULL(SUM(BaseQty), 0) FROM dbo.PrProductionBalLot
         WHERE CompanyCode = @CompanyCode AND BranchCode = @BranchCode);
    DECLARE @IvQtyBefore decimal(18, 4) =
        CASE WHEN OBJECT_ID(N'dbo.IvBalLoc', N'U') IS NULL THEN 0
             ELSE (SELECT ISNULL(SUM(StdQty), 0) FROM dbo.IvBalLoc
                   WHERE CompanyCode = @CompanyCode AND BranchCode = @BranchCode) END;

    DECLARE @ProdOpenCount int =
        (SELECT COUNT(*) FROM dbo.PrProductionBalLot
         WHERE CompanyCode = @CompanyCode AND BranchCode = @BranchCode AND Qty > 0);
    DECLARE @ProdOpenBase decimal(18, 4) =
        (SELECT ISNULL(SUM(BaseQty), 0) FROM dbo.PrProductionBalLot
         WHERE CompanyCode = @CompanyCode AND BranchCode = @BranchCode AND Qty > 0);
    DECLARE @IvOpenCount int =
        CASE WHEN OBJECT_ID(N'dbo.IvBalLoc', N'U') IS NULL THEN 0
             ELSE (SELECT COUNT(*) FROM dbo.IvBalLoc
                   WHERE CompanyCode = @CompanyCode AND BranchCode = @BranchCode AND StdQty > 0) END;
    DECLARE @IvOpenQty decimal(18, 4) =
        CASE WHEN OBJECT_ID(N'dbo.IvBalLoc', N'U') IS NULL THEN 0
             ELSE (SELECT ISNULL(SUM(StdQty), 0) FROM dbo.IvBalLoc
                   WHERE CompanyCode = @CompanyCode AND BranchCode = @BranchCode AND StdQty > 0) END;
    DECLARE @ManifestPayload nvarchar(800) = CONCAT(
        N'{"company":"', @CompanyCode, N'","branch":"', @BranchCode,
        N'","effectiveFrom":"', CONVERT(nvarchar(33), @EffectiveFrom, 126),
        N'","prodOpenCount":', @ProdOpenCount,
        N',"prodOpenBase":', CONVERT(varchar(32), @ProdOpenBase),
        N',"ivOpenCount":', @IvOpenCount,
        N',"ivOpenQty":', CONVERT(varchar(32), @IvOpenQty), N'}');

    DECLARE @ManifestHash char(64) =
        CONVERT(char(64), CONVERT(varchar(64), HASHBYTES('SHA2_256', @ManifestPayload), 2));

    INSERT INTO dbo.StockLedgerEpoch
    (
        CompanyCode, BranchCode, EffectiveFrom, CutoverPostingSequence, Version, Status,
        MigrationBatchId, ReconciliationManifestHash, ActivatedAtUtc, ActivatedBy
    )
    VALUES
    (
        @CompanyCode, @BranchCode, @EffectiveFrom, 0, 2, N'PREPARED',
        @MigrationBatchId, @ShaPrep, NULL, NULL
    );

    DECLARE @EpochId bigint = SCOPE_IDENTITY();

    DECLARE @LastSequence bigint = 0;
    DECLARE @ProdPostingId bigint = NULL;
    DECLARE @IvPostingId bigint = NULL;
    DECLARE @ProdLines int = 0;
    DECLARE @IvLines int = 0;

    IF EXISTS (
        SELECT 1 FROM dbo.PrProductionBalLot
        WHERE CompanyCode = @CompanyCode AND BranchCode = @BranchCode AND Qty > 0)
    BEGIN
        SET @LastSequence += 1;

        DECLARE @ProdRequestId uniqueidentifier =
            CONVERT(uniqueidentifier, HASHBYTES('MD5', CONVERT(varbinary(8000), CONCAT(CONVERT(nvarchar(36), @MigrationBatchId), N'|PRODUCTION'))));
        DECLARE @ProdSnapshot nvarchar(max) = CONCAT(N'{"role":"OPENING","area":"PRODUCTION","epochId":', @EpochId, N'}');
        DECLARE @ProdSnapHash char(64) = CONVERT(char(64), CONVERT(varchar(64), HASHBYTES('SHA2_256', @ProdSnapshot), 2));
        DECLARE @ProdFp char(64) = CONVERT(char(64), CONVERT(varchar(64), HASHBYTES('SHA2_256', CONCAT(N'OPENING|PRODUCTION|', CONVERT(nvarchar(36), @MigrationBatchId))), 2));

        INSERT INTO dbo.StockPosting
        (
            CompanyCode, BranchCode, LedgerEpochId, PostingSequence,
            RequestId, CommandType, RequestFingerprint,
            SourceModule, SourceDocumentType, SourceDocumentId, SourceDocumentNo,
            DocumentRevision, PostingRole,
            SourceSnapshotJson, SourceSnapshotHash, SourceSnapshotSchemaVersion,
            EffectiveAt, BusinessDate, PeriodKey, PostedAtUtc, PostedBy,
            ReasonCode, ReasonText, SealedAtUtc
        )
        VALUES
        (
            @CompanyCode, @BranchCode, @EpochId, @LastSequence,
            @ProdRequestId, N'OPENING', @ProdFp,
            N'PRODUCTION', N'OPENING', CONVERT(nvarchar(64), @MigrationBatchId), N'OPENING',
            0, N'OPENING',
            @ProdSnapshot, @ProdSnapHash, 1,
            @EffectiveFrom, CONVERT(date, @EffectiveFrom), @PeriodKey, @PostedAtUtc, @ActivatedBy,
            N'CUTOVER', N'Stock ledger production opening', NULL
        );

        SET @ProdPostingId = SCOPE_IDENTITY();

        INSERT INTO dbo.PrProductionBalLotMovement
        (
            ProductionBalLotID, MovementType, Qty, UOM, BaseQty, BaseUOM,
            UnitCost, TotalCost, WorkOrderID, WorkOrderMaterialID, WorkOrderOperationID, RouteStepID,
            PostingLinkID, DocumentType, DocumentNo, MovementDate, CreatedDate, CreatedBy,
            LedgerVersion, LedgerEpochID, StockPostingID, PostingLineNo,
            CompanyCode, BranchCode, ItemCode, ItemDescription, BalanceStage,
            ProductionLocationID, ProductionLocationCode, WorkCentreCode, ProcessCode,
            LotIdentity, PhysicalLotNo, StockStatusCode, WorkOrderNo, ConversionFactorToBase,
            SourceLineId, SplitOrdinal, ValuationStatus
        )
        SELECT
            lot.UID,
            N'OPENING_IN',
            lot.Qty,
            lot.UOM,
            lot.BaseQty,
            lot.BaseUOM,
            lot.AverageUnitCost,
            lot.TotalCost,
            lot.WorkOrderID,
            lot.WorkOrderMaterialID,
            lot.WorkOrderOperationID,
            lot.ProducingRouteStepID,
            0,
            N'OPENING',
            N'OPENING',
            @EffectiveFrom,
            @PostedAtUtc,
            @ActorShort,
            CONVERT(tinyint, 2),
            @EpochId,
            @ProdPostingId,
            ROW_NUMBER() OVER (ORDER BY lot.UID),
            lot.CompanyCode,
            lot.BranchCode,
            lot.ItemCode,
            lot.Description,
            lot.BalanceStage,
            lot.ProductionLocationID,
            loc.Code,
            lot.WorkCentreCode,
            lot.ProcessCode,
            CASE
                WHEN NULLIF(LTRIM(RTRIM(lot.PhysicalLotNo)), N'') IS NOT NULL THEN lot.PhysicalLotNo
                WHEN NULLIF(LTRIM(RTRIM(lot.PoolCode)), N'') IS NOT NULL THEN lot.PoolCode
                ELSE lot.LotNo
            END,
            lot.PhysicalLotNo,
            lot.StockStatusCode,
            lot.WorkOrderNo,
            lot.ConversionFactorToBase,
            CONVERT(nvarchar(64), lot.UID),
            0,
            N'UNVALUED'
        FROM dbo.PrProductionBalLot lot
        LEFT JOIN dbo.PrProductionLocation loc
            ON loc.Id = lot.ProductionLocationID
           AND loc.CompanyCode = lot.CompanyCode
           AND loc.BranchCode = lot.BranchCode
        WHERE lot.CompanyCode = @CompanyCode
          AND lot.BranchCode = @BranchCode
          AND lot.Qty > 0
          AND NOT EXISTS (
                SELECT 1
                FROM dbo.PrProductionBalLotMovement existing
                WHERE existing.ProductionBalLotID = lot.UID
                  AND existing.MovementType = N'OPENING_IN'
                  AND existing.LedgerEpochID = @EpochId);

        SET @ProdLines = @@ROWCOUNT;

        UPDATE dbo.StockPosting
        SET SealedAtUtc = @PostedAtUtc
        WHERE Id = @ProdPostingId
          AND CompanyCode = @CompanyCode
          AND BranchCode = @BranchCode
          AND SealedAtUtc IS NULL;
    END;

    IF OBJECT_ID(N'dbo.IvBalLoc', N'U') IS NOT NULL
       AND EXISTS (
            SELECT 1 FROM dbo.IvBalLoc
            WHERE CompanyCode = @CompanyCode AND BranchCode = @BranchCode AND StdQty > 0)
    BEGIN
        SET @LastSequence += 1;

        DECLARE @IvRequestId uniqueidentifier =
            CONVERT(uniqueidentifier, HASHBYTES('MD5', CONVERT(varbinary(8000), CONCAT(CONVERT(nvarchar(36), @MigrationBatchId), N'|INVENTORY'))));
        DECLARE @IvSnapshot nvarchar(max) = CONCAT(N'{"role":"OPENING","area":"INVENTORY","epochId":', @EpochId, N'}');
        DECLARE @IvSnapHash char(64) = CONVERT(char(64), CONVERT(varchar(64), HASHBYTES('SHA2_256', @IvSnapshot), 2));
        DECLARE @IvFp char(64) = CONVERT(char(64), CONVERT(varchar(64), HASHBYTES('SHA2_256', CONCAT(N'OPENING|INVENTORY|', CONVERT(nvarchar(36), @MigrationBatchId))), 2));

        INSERT INTO dbo.StockPosting
        (
            CompanyCode, BranchCode, LedgerEpochId, PostingSequence,
            RequestId, CommandType, RequestFingerprint,
            SourceModule, SourceDocumentType, SourceDocumentId, SourceDocumentNo,
            DocumentRevision, PostingRole,
            SourceSnapshotJson, SourceSnapshotHash, SourceSnapshotSchemaVersion,
            EffectiveAt, BusinessDate, PeriodKey, PostedAtUtc, PostedBy,
            ReasonCode, ReasonText, SealedAtUtc
        )
        VALUES
        (
            @CompanyCode, @BranchCode, @EpochId, @LastSequence,
            @IvRequestId, N'OPENING', @IvFp,
            N'INVENTORY', N'OPENING', CONVERT(nvarchar(64), @MigrationBatchId), N'OPENING',
            0, N'OPENING',
            @IvSnapshot, @IvSnapHash, 1,
            @EffectiveFrom, CONVERT(date, @EffectiveFrom), @PeriodKey, @PostedAtUtc, @ActivatedBy,
            N'CUTOVER', N'Stock ledger inventory opening', NULL
        );

        SET @IvPostingId = SCOPE_IDENTITY();

        INSERT INTO dbo.IvTrxHistory
        (
            FromBalLocId, ToBalLocId, FromLotId, ToLotId,
            CompanyCode, BranchCode, BatchNo, TrxLineNo, TrxDtTime, TrxType, BatchStatus,
            ICode, IDesc, ToWarehouse, ToLocation, ToLot, ToStdQty, ToStdUOM,
            IStatus, Remarks, UnitPrice, LocationCode, Created, UserID,
            LedgerVersion, LedgerEpochID, StockPostingID, PostingLineNo, DocumentRevision, EntryRole
        )
        SELECT
            NULL,
            b.ID,
            NULL,
            b.LotId,
            b.CompanyCode,
            b.BranchCode,
            0,
            CAST(ROW_NUMBER() OVER (ORDER BY b.ID) AS smallint),
            @EffectiveFrom,
            N'OPENING',
            N'POSTED',
            b.ICode,
            m.IDesc,
            b.WHCode,
            b.LocCode,
            b.LotNo,
            b.StdQty,
            b.StdUOM,
            b.IStatus,
            N'Stock ledger opening',
            b.UnitPrice,
            b.LocationCode,
            @PostedAtUtc,
            @ActorShort,
            CONVERT(tinyint, 2),
            @EpochId,
            @IvPostingId,
            ROW_NUMBER() OVER (ORDER BY b.ID),
            0,
            N'OPENING'
        FROM dbo.IvBalLoc b
        LEFT JOIN dbo.IvStockMaster m
            ON m.CompanyCode = b.CompanyCode AND m.ICode = b.ICode
        WHERE b.CompanyCode = @CompanyCode
          AND b.BranchCode = @BranchCode
          AND b.StdQty > 0
          AND NOT EXISTS (
                SELECT 1
                FROM dbo.IvTrxHistory h
                WHERE h.StockPostingID = @IvPostingId
                  AND h.ToBalLocId = b.ID
                  AND h.EntryRole = N'OPENING');

        SET @IvLines = @@ROWCOUNT;

        UPDATE dbo.StockPosting
        SET SealedAtUtc = @PostedAtUtc
        WHERE Id = @IvPostingId
          AND CompanyCode = @CompanyCode
          AND BranchCode = @BranchCode
          AND SealedAtUtc IS NULL;
    END;

    IF EXISTS (
        SELECT 1 FROM dbo.StockPostingBranchSequence
        WHERE CompanyCode = @CompanyCode AND BranchCode = @BranchCode)
    BEGIN
        UPDATE dbo.StockPostingBranchSequence
        SET LastSequence = CASE WHEN LastSequence < @LastSequence THEN @LastSequence ELSE LastSequence END,
            UpdatedAtUtc = @PostedAtUtc
        WHERE CompanyCode = @CompanyCode
          AND BranchCode = @BranchCode;
    END
    ELSE
    BEGIN
        INSERT INTO dbo.StockPostingBranchSequence (CompanyCode, BranchCode, LastSequence, UpdatedAtUtc)
        VALUES (@CompanyCode, @BranchCode, @LastSequence, @PostedAtUtc);
    END;

    UPDATE dbo.StockLedgerEpoch
    SET Status = N'ACTIVE',
        CutoverPostingSequence = @LastSequence,
        ReconciliationManifestHash = @ManifestHash,
        ActivatedAtUtc = @PostedAtUtc,
        ActivatedBy = @ActivatedBy
    WHERE Id = @EpochId
      AND CompanyCode = @CompanyCode
      AND BranchCode = @BranchCode
      AND Status = N'PREPARED';

    DECLARE @ProdQtyAfter decimal(18, 4) =
        (SELECT ISNULL(SUM(BaseQty), 0) FROM dbo.PrProductionBalLot
         WHERE CompanyCode = @CompanyCode AND BranchCode = @BranchCode);
    DECLARE @IvQtyAfter decimal(18, 4) =
        CASE WHEN OBJECT_ID(N'dbo.IvBalLoc', N'U') IS NULL THEN 0
             ELSE (SELECT ISNULL(SUM(StdQty), 0) FROM dbo.IvBalLoc
                   WHERE CompanyCode = @CompanyCode AND BranchCode = @BranchCode) END;

    IF @ProdQtyBefore <> @ProdQtyAfter OR @IvQtyBefore <> @IvQtyAfter
    BEGIN
        RAISERROR(N'activate-stock-ledger-epoch.sql: live balances changed during opening insert. Rolling back.', 16, 1);
    END;

    COMMIT TRANSACTION;

    SELECT
        N'EPOCH_ACTIVATED' AS FindingCode,
        @EpochId AS EpochId,
        @MigrationBatchId AS MigrationBatchId,
        @LastSequence AS CutoverPostingSequence,
        @ProdPostingId AS ProductionOpeningPostingId,
        @ProdLines AS ProductionOpeningLines,
        @IvPostingId AS InventoryOpeningPostingId,
        @IvLines AS InventoryOpeningLines,
        @ProdQtyAfter AS ProductionBaseQtyUnchanged,
        @IvQtyAfter AS InventoryStdQtyUnchanged;

    PRINT N'activate-stock-ledger-epoch.sql committed.';
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0
        ROLLBACK TRANSACTION;

    DECLARE @Err nvarchar(4000) = ERROR_MESSAGE();
    RAISERROR(N'activate-stock-ledger-epoch.sql failed: %s', 16, 1, @Err);
END CATCH;
