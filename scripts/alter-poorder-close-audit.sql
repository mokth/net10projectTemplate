-- Force-close audit columns on POOrder (procurement Phase 4).
-- CloseReason / ClosedBy / ClosedOn are stamped by PoStatusPolicy.ForceClose and cleared on Reopen.
-- Do NOT run at application startup. Apply via normal SQL deploy. Idempotent.
GO

IF COL_LENGTH(N'dbo.POOrder', N'CloseReason') IS NULL
    ALTER TABLE dbo.POOrder ADD CloseReason nvarchar(200) NULL;
GO

IF COL_LENGTH(N'dbo.POOrder', N'ClosedBy') IS NULL
    ALTER TABLE dbo.POOrder ADD ClosedBy nvarchar(20) NULL;
GO

IF COL_LENGTH(N'dbo.POOrder', N'ClosedOn') IS NULL
    ALTER TABLE dbo.POOrder ADD ClosedOn datetime2 NULL;
GO
