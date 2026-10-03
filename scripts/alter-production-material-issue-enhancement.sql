/*
  Issue-to-Production enhancement: line-level excess-issue audit reason.
  Idempotent and safe to run after alter-production-material-issue-draft.sql.
*/
SET NOCOUNT ON;
SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
BEGIN TRANSACTION;

IF COL_LENGTH(N'dbo.PrMaterialIssueLine', N'ExcessIssueReason') IS NULL
BEGIN
    ALTER TABLE dbo.PrMaterialIssueLine
        ADD ExcessIssueReason nvarchar(250) NULL;
END;

IF NOT EXISTS
(
    SELECT 1
    FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'dbo.PrMaterialIssueLine')
      AND name = N'IX_PrMaterialIssueLine_PostingLink_Material'
)
BEGIN
    CREATE INDEX IX_PrMaterialIssueLine_PostingLink_Material
        ON dbo.PrMaterialIssueLine(PostingLinkID, WorkOrderMaterialID);
END;

COMMIT TRANSACTION;
GO
