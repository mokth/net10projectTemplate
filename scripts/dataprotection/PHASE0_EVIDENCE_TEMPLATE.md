# Phase 0 evidence template (operator-only)

Do not commit this file if it contains production paths tied to sensitive infrastructure. Store completed copies in restricted operator storage.

| Field | Value |
|-------|--------|
| Date (UTC) | |
| Operator | |
| Server(s) | |
| IIS site / app path | |
| App pool name | |
| Pool identity | |
| Load User Profile | |
| Worker process count | |
| Content root physical path | |
| Effective old key ring path(s) | |
| Approved durable `DataProtection:KeysPath` | |
| Backup location (encrypted) | |

## Key manifest (filenames and SHA-256 only — never paste XML)

| Directory | FileName | SHA-256 | Key GUID (from filename) |
|-----------|----------|---------|---------------------------|
| | | | |

## Incident key search

Target key ID: `855d60ca-6640-46b7-9648-59c673a6042e`

- [ ] Found in current content-root ring
- [ ] Found in backup / old release folder
- [ ] Not found in scanned locations

## Classification

**Diagnosis:** `FOUND_RECOVERABLE_KEY` | `KEY_NOT_FOUND` | `MULTI_INSTANCE_RING_MISMATCH` | `ACL/IDENTITY_PROBLEM` | `UNKNOWN`

**Notes:**

## Gate 0 sign-off

- [ ] Topology verified (single-server IIS assumed for DPAPI plan)
- [ ] Secure backup taken before any migration
- [ ] Rollback procedure documented
- [ ] Approved maintenance window (if migration required)
