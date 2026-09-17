-- Sales-analysis Phase 1: one covering index for the aggregate sales queries (manual DBA script).
-- Target database: same as ConnectionStrings:DefaultConnection.
--
-- JUSTIFICATION (R8): every Phase 1 analysis query filters CompanyCode + Status = POSTED and ranges
-- on InvDate, then sums TotAmnt and groups by SalesmanCode / BranchCode. The existing
-- IX_SaInvoice_Company_Status helps the seek but not the range or the covering columns.
--
-- This is deliberately the ONLY index Phase 1 adds. Review the actual execution plan and row counts
-- before adding any per-dimension index — extra indexes raise INSERT/UPDATE cost on every invoice post.
--
-- Idempotent: the guard makes a second run a clean no-op.

SET XACT_ABORT ON;
SET NOCOUNT ON;

IF OBJECT_ID(N'dbo.SaInvoice', N'U') IS NULL
BEGIN
    RAISERROR(N'SaInvoice table does not exist.', 16, 1);
    RETURN;
END

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE name = N'IX_SaInvoice_Company_Status_InvDate'
      AND object_id = OBJECT_ID(N'dbo.SaInvoice'))
BEGIN
    CREATE NONCLUSTERED INDEX IX_SaInvoice_Company_Status_InvDate
        ON dbo.SaInvoice (CompanyCode, Status, InvDate)
        INCLUDE (TotAmnt, SalesmanCode, BranchCode);

    PRINT N'Created IX_SaInvoice_Company_Status_InvDate';
END
ELSE
BEGIN
    PRINT N'IX_SaInvoice_Company_Status_InvDate already present - no change.';
END
GO
