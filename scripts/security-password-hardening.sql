/* ============================================================================
   Password & User Security Hardening — new tables.
   Manual DBA script. Idempotent (safe to rerun). Does not seed data.

   - dbo.PasswordResetRequest  : admin-assisted password recovery requests.
   - dbo.SecurityAuditEvent    : append-only security audit trail.

   NOTE: the userlogin schema is intentionally NOT modified. Password write
   concurrency is enforced in the repository via guarded compare-and-set.
   ============================================================================ */

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

IF OBJECT_ID(N'dbo.PasswordResetRequest', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.PasswordResetRequest
    (
        PasswordResetRequestId  bigint IDENTITY(1,1) NOT NULL,
        UserUid                 int NOT NULL,
        CompanyCode             nvarchar(5) NOT NULL,
        Status                  nvarchar(20) NOT NULL CONSTRAINT DF_PasswordResetRequest_Status DEFAULT (N'PENDING'),
        RequestedAtUtc          datetime2 NOT NULL,
        ResolvedAtUtc           datetime2 NULL,
        ResolvedBy              nvarchar(10) NULL,
        CONSTRAINT PK_PasswordResetRequest PRIMARY KEY (PasswordResetRequestId)
    );

    CREATE INDEX IX_PasswordResetRequest_Company_Status
        ON dbo.PasswordResetRequest (CompanyCode, Status);

    -- One pending request per user, enforced at the database level.
    CREATE UNIQUE INDEX UQ_PasswordResetRequest_Pending
        ON dbo.PasswordResetRequest (UserUid)
        WHERE Status = N'PENDING';
END
GO

IF OBJECT_ID(N'dbo.SecurityAuditEvent', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.SecurityAuditEvent
    (
        SecurityAuditEventId    bigint IDENTITY(1,1) NOT NULL,
        CompanyCode             nvarchar(5) NULL,
        ActorUid                int NULL,
        ActorUserId             nvarchar(10) NULL,
        TargetUid               int NULL,
        EventType               nvarchar(50) NOT NULL,
        Outcome                 nvarchar(20) NOT NULL,
        OldValue                nvarchar(200) NULL,
        NewValue                nvarchar(200) NULL,
        OccurredAtUtc           datetime2 NOT NULL,
        Details                 nvarchar(500) NULL,
        CONSTRAINT PK_SecurityAuditEvent PRIMARY KEY (SecurityAuditEventId)
    );

    CREATE INDEX IX_SecurityAuditEvent_Company_Occurred
        ON dbo.SecurityAuditEvent (CompanyCode, OccurredAtUtc);
END
GO

/* ---------------------------------------------------------------------------
   Self-service password reset by email.

   Only the SHA-256 hash of the token is stored; the raw token exists solely in
   the emailed link. Single-use (UsedAtUtc) and time-limited (ExpiresAtUtc).
   --------------------------------------------------------------------------- */
IF OBJECT_ID(N'dbo.PasswordResetToken', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.PasswordResetToken
    (
        PasswordResetTokenId    bigint IDENTITY(1,1) NOT NULL,
        UserUid                 int NOT NULL,
        CompanyCode             nvarchar(5) NOT NULL,
        TokenHash               nvarchar(64) NOT NULL,
        CreatedAtUtc            datetime2 NOT NULL,
        ExpiresAtUtc            datetime2 NOT NULL,
        UsedAtUtc               datetime2 NULL,
        RequestedIp             nvarchar(45) NULL,
        CONSTRAINT PK_PasswordResetToken PRIMARY KEY (PasswordResetTokenId),
        CONSTRAINT FK_PasswordResetToken_userlogin_UserUid
            FOREIGN KEY (UserUid) REFERENCES dbo.userlogin (uid) ON DELETE CASCADE
    );

    CREATE UNIQUE INDEX UQ_PasswordResetToken_TokenHash
        ON dbo.PasswordResetToken (TokenHash);

    CREATE INDEX IX_PasswordResetToken_User_Used
        ON dbo.PasswordResetToken (UserUid, UsedAtUtc);
END
GO
