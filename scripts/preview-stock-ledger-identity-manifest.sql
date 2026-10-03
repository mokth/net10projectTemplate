/*
  Read-only identity-level cutover manifest. Review the two result sets and all
  lineage/location exceptions on a restored database before approving its hash.
  The hash algorithm must match activate-stock-ledger-epoch.sql exactly.
*/
SET NOCOUNT ON;

DECLARE @CompanyCode nvarchar(5) = N'DEMO';
DECLARE @BranchCode nvarchar(5) = N'HQ';
DECLARE @EffectiveFrom datetime2(7) = '2026-10-01T00:00:00';

IF EXISTS (SELECT 1 FROM dbo.PrProductionBalLot lot
           WHERE lot.CompanyCode = @CompanyCode AND lot.BranchCode = @BranchCode AND lot.BaseQty > 0
             AND (lot.BalanceStage IS NULL OR lot.ProductionLocationID IS NULL
                  OR lot.StockStatusCode IS NULL OR lot.OriginType IS NULL))
    THROW 51003, 'Positive production stock has unresolved stage, location, status, or origin.', 1;

DECLARE @ProductionManifest nvarchar(max) = (
    SELECT lot.UID, lot.Kind, lot.ItemCode, lot.Qty, lot.UOM, lot.BaseQty, lot.BaseUOM,
           lot.ConversionFactorToBase, lot.WorkOrderID, lot.WorkOrderMaterialID,
           lot.OriginalIssueMovementID, lot.SourceIvBalLocID, lot.ProducingRouteStepID,
           lot.WorkOrderOperationID, lot.LotNo, lot.PoolCode, lot.PhysicalLotNo,
           lot.BalanceStage, lot.ProductionLocationID, lot.StockStatusCode, lot.OriginType
    FROM dbo.PrProductionBalLot lot
    WHERE lot.CompanyCode = @CompanyCode AND lot.BranchCode = @BranchCode AND lot.BaseQty > 0
    ORDER BY lot.UID FOR JSON PATH, INCLUDE_NULL_VALUES);
DECLARE @InventoryManifest nvarchar(max) = (
    SELECT b.ID, b.ICode, b.WHCode, b.LocCode, b.LotNo, b.LotId,
           b.IStatus, b.StdQty, b.StdUOM
    FROM dbo.IvBalLoc b
    WHERE b.CompanyCode = @CompanyCode AND b.BranchCode = @BranchCode AND b.StdQty > 0
    ORDER BY b.ID FOR JSON PATH, INCLUDE_NULL_VALUES);
DECLARE @ManifestPayload nvarchar(max) = CONCAT(
    N'v2|', @CompanyCode, N'|', @BranchCode, N'|', CONVERT(nvarchar(33), @EffectiveFrom, 126),
    N'|PRODUCTION|', @ProductionManifest, N'|INVENTORY|', @InventoryManifest);

SELECT N'PRODUCTION' AS LedgerArea, @ProductionManifest AS IdentityManifestJson;
SELECT N'INVENTORY' AS LedgerArea, @InventoryManifest AS IdentityManifestJson;
SELECT CONVERT(char(64), CONVERT(varchar(64), HASHBYTES('SHA2_256', @ManifestPayload), 2)) AS ProposedManifestHash;
