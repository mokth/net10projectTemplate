/*
  Widen CK_PrWorkOrder_SnapshotFormat to allow SnapshotFormatVersion 1, 2, and 3.

  Confirm Refresh / Create Draft stamp ProductionSnapshotFormatVersions.Current (= 3).
  Older deployments may still have CHECK (SnapshotFormatVersion IN (1, 2)) because
  create-production-workorder.sql only ADDs the constraint when missing and never
  widens an existing one.

  Idempotent. Safe to re-run.
*/
SET NOCOUNT ON;
SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

IF OBJECT_ID(N'dbo.PrWorkOrder', N'U') IS NULL
BEGIN
    PRINT N'dbo.PrWorkOrder is missing — run create-production-workorder.sql first.';
    RETURN;
END;
GO

DECLARE @definition nvarchar(max) =
(
    SELECT cc.definition
    FROM sys.check_constraints AS cc
    WHERE cc.parent_object_id = OBJECT_ID(N'dbo.PrWorkOrder')
      AND cc.name = N'CK_PrWorkOrder_SnapshotFormat'
);

-- SQL Server may store IN (1,2,3) as OR of =(1)=(2)=(3). Accept any form that allows 3.
IF @definition IS NOT NULL
   AND (
        @definition LIKE N'%=(3)%'
        OR @definition LIKE N'%,(3)%'
        OR @definition LIKE N'%, 3)%'
        OR @definition LIKE N'% 3)%'
        OR @definition LIKE N'%IN (1, 2, 3)%'
        OR @definition LIKE N'%IN ((1), (2), (3))%'
      )
BEGIN
    PRINT N'CK_PrWorkOrder_SnapshotFormat already allows SnapshotFormatVersion 3.';
END
ELSE
BEGIN
    IF @definition IS NOT NULL
    BEGIN
        ALTER TABLE dbo.PrWorkOrder DROP CONSTRAINT CK_PrWorkOrder_SnapshotFormat;
        PRINT N'Dropped outdated CK_PrWorkOrder_SnapshotFormat.';
    END;

    ALTER TABLE dbo.PrWorkOrder WITH CHECK
        ADD CONSTRAINT CK_PrWorkOrder_SnapshotFormat
        CHECK (SnapshotFormatVersion IN (1, 2, 3));

    PRINT N'CK_PrWorkOrder_SnapshotFormat ensured: SnapshotFormatVersion IN (1, 2, 3).';
END;
GO
