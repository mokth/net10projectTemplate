-- Install after the development database has been created from the current
-- model. This script installs write guards only; it does not create or alter
-- tables or migrate an existing database.
CREATE OR ALTER TRIGGER dbo.TR_FifoReversedIssueGeneration_Insert
ON dbo.StockFifoReversedIssueGeneration AFTER INSERT
AS
BEGIN
    SET NOCOUNT ON;
    IF EXISTS
    (
        SELECT 1
        FROM inserted g
        JOIN dbo.StockReplayRun run ON run.Id = g.ReplayRunId
        JOIN dbo.StockPosting root ON root.Id = g.ReplayStockPostingId
        JOIN dbo.IvTrxHistory issue ON issue.ID = g.IssueHistoryId
        JOIN dbo.IvTrxHistory reversal ON reversal.ID = g.ReversalHistoryId
        JOIN dbo.PrProductionBalLotMovement movement ON movement.UID = g.IssueMovementId
        JOIN dbo.PrProductionBalLotMovement reverseMovement ON reverseMovement.UID = g.ReversalMovementId
        LEFT JOIN dbo.StockFifoReversedIssueGeneration prior ON prior.Id = g.PreviousGenerationId
        LEFT JOIN dbo.StockPosting priorRoot ON priorRoot.Id = prior.ReplayStockPostingId
        WHERE ISNULL(TRY_CONVERT(bigint, SESSION_CONTEXT(N'STOCK_LEDGER_V2_POSTING_ID')), 0) <> g.ReplayStockPostingId
           OR root.SealedAtUtc IS NOT NULL OR run.RootStockPostingId <> root.Id
           OR run.CompanyCode <> g.CompanyCode OR run.BranchCode <> g.BranchCode
           OR root.CompanyCode <> g.CompanyCode OR root.BranchCode <> g.BranchCode
           OR issue.CompanyCode <> g.CompanyCode OR issue.BranchCode <> g.BranchCode
           OR reversal.CompanyCode <> g.CompanyCode OR reversal.BranchCode <> g.BranchCode
           OR ISNULL(reversal.ReversesHistoryId, 0) <> issue.ID
           OR movement.CompanyCode <> g.CompanyCode OR movement.BranchCode <> g.BranchCode
           OR reverseMovement.CompanyCode <> g.CompanyCode OR reverseMovement.BranchCode <> g.BranchCode
           OR ISNULL(reverseMovement.OriginalMovementId, 0) <> movement.UID
           OR (g.PreviousGenerationId IS NOT NULL AND
               (prior.Id IS NULL OR prior.CompanyCode <> g.CompanyCode OR prior.BranchCode <> g.BranchCode
                OR prior.IssueHistoryId <> g.IssueHistoryId OR prior.ReversalHistoryId <> g.ReversalHistoryId
                OR prior.IssueMovementId <> g.IssueMovementId OR prior.ReversalMovementId <> g.ReversalMovementId
                OR priorRoot.SealedAtUtc IS NULL OR priorRoot.PostingSequence >= root.PostingSequence))
    )
        THROW 51131, 'FIFO reversed issue generation has invalid root or owner.', 1;
END;
GO

CREATE OR ALTER TRIGGER dbo.TR_FifoReversedIssueSplit_Insert
ON dbo.StockFifoReversedIssueSplit AFTER INSERT
AS
BEGIN
    SET NOCOUNT ON;
    IF EXISTS
    (
        SELECT 1
        FROM inserted split
        JOIN dbo.StockFifoReversedIssueGeneration g ON g.Id = split.GenerationId
        JOIN dbo.StockPosting root ON root.Id = g.ReplayStockPostingId
        JOIN dbo.IvTrxHistory issue ON issue.ID = g.IssueHistoryId
        JOIN dbo.StockFifoLayer layer ON layer.Id = split.FifoLayerId
        JOIN dbo.StockPosting layerOwner ON layerOwner.Id = layer.OriginStockPostingId
        WHERE ISNULL(TRY_CONVERT(bigint, SESSION_CONTEXT(N'STOCK_LEDGER_V2_POSTING_ID')), 0) <> g.ReplayStockPostingId
           OR root.SealedAtUtc IS NOT NULL OR split.CompanyCode <> g.CompanyCode OR split.BranchCode <> g.BranchCode
           OR layer.CompanyCode <> g.CompanyCode OR layer.BranchCode <> g.BranchCode
           OR layer.ItemCode <> issue.ICode OR split.ConsumedQty > layer.OriginalQty
           OR split.ConsumedValue > layer.OriginalValue
           OR (layerOwner.SealedAtUtc IS NULL AND layerOwner.Id <> root.Id)
    )
        THROW 51132, 'FIFO reversed issue split has invalid root, tenant, or layer.', 1;
END;
GO

CREATE OR ALTER TRIGGER dbo.TR_FifoReversedIssueGeneration_Immutable
ON dbo.StockFifoReversedIssueGeneration AFTER UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    THROW 51133, 'FIFO reversed issue generations are immutable.', 1;
END;
GO

CREATE OR ALTER TRIGGER dbo.TR_FifoReversedIssueSplit_Immutable
ON dbo.StockFifoReversedIssueSplit AFTER UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    THROW 51134, 'FIFO reversed issue splits are immutable.', 1;
END;
GO
