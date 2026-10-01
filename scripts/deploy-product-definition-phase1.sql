-- Product Definition Phase 1 deployment driver for an existing/legacy ERP database.
--
-- Run this file using either:
--   1. SSMS with Query > SQLCMD Mode enabled, or
--   2. sqlcmd with the scripts directory as the current working directory.
--
-- :On Error exit is important: a failed prerequisite stops deployment instead of
-- allowing later scripts to emit cascading "object does not exist" errors.

:On Error exit
:setvar ScriptRoot "C:\wincom\net10projects\scripts"

:r $(ScriptRoot)\alter-prdefbom-multilevel.sql
:r $(ScriptRoot)\create-product-definition-routing.sql
:r $(ScriptRoot)\alter-prdefbom-process-ownership.sql
:r $(ScriptRoot)\alter-product-definition-phase1-foundation.sql
:r $(ScriptRoot)\alter-bom-alternate-group.sql
:r $(ScriptRoot)\alter-product-definition-multiple-definitions.sql

PRINT N'Product Definition Phase 1 + multiple-definitions deployment sequence completed.';
GO
