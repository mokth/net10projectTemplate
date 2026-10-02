/*
  Add route-output execution contract columns to PrWorkOrderRouteStep for SnapshotHash V3.

  Idempotent. Safe to re-run.
*/
SET NOCOUNT ON;
SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

IF OBJECT_ID(N'dbo.PrWorkOrderRouteStep', N'U') IS NULL
BEGIN
    PRINT N'dbo.PrWorkOrderRouteStep is missing — run create-production-workorder.sql first.';
    RETURN;
END;
GO

IF COL_LENGTH(N'dbo.PrWorkOrderRouteStep', N'OutputType') IS NULL
    ALTER TABLE dbo.PrWorkOrderRouteStep ADD OutputType nvarchar(20) NULL;
GO

IF COL_LENGTH(N'dbo.PrWorkOrderRouteStep', N'YieldPercent') IS NULL
    ALTER TABLE dbo.PrWorkOrderRouteStep ADD YieldPercent decimal(9,4) NULL;
GO

IF COL_LENGTH(N'dbo.PrWorkOrderRouteStep', N'OutputBaseUOM') IS NULL
    ALTER TABLE dbo.PrWorkOrderRouteStep ADD OutputBaseUOM nvarchar(10) NULL;
GO

IF COL_LENGTH(N'dbo.PrWorkOrderRouteStep', N'OutputConversionFactorToBase') IS NULL
    ALTER TABLE dbo.PrWorkOrderRouteStep ADD OutputConversionFactorToBase decimal(18,8) NULL;
GO

PRINT N'PrWorkOrderRouteStep route-output contract columns ready.';
GO
