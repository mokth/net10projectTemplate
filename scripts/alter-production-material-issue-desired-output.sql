/*
  Issue-to-Production Desired Output (ProductionQtyThisIssue) on PrProductionPostingLink.
  Idempotent upgrade for existing databases.
*/
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET ARITHABORT ON;
GO

IF COL_LENGTH(N'dbo.PrProductionPostingLink', N'ProductionQtyThisIssue') IS NULL
BEGIN
    ALTER TABLE dbo.PrProductionPostingLink
    ADD ProductionQtyThisIssue decimal(18,4) NULL;
END;
GO

PRINT N'PrProductionPostingLink.ProductionQtyThisIssue verified.';
GO
