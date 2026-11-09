# PR/PO Approval — Phase 0 evidence appendix

**HEAD:** `f8edf0d2e3d09aba5f004b846449cd0681225d28`  
**Branch:** `fix/dataprotection-key-ring` (local checkout of productionv2 worktree)  
**Dirty tree:** unrelated Product Definition / docs / deploy files. No pre-existing PR/PO approval engine.

Classification: **B costing-adjacent**. Approval is an authorization gate only. No CostLayer/FIFO/WIP/receipt-qty/tax/rounding/posting/rollback changes.

## Owner sign-off used for implementation (plan I.2 defaults)

User asked to implement the attached v4+v5 plan in Agent mode. Defaults applied:

- Per-currency bands; fail closed if the document currency has no matching published band. No FX.
- Reject PO submit when header `Discount != 0` (proven: `PoOrderCalc.SumTotals` sums line `NetAmount`/`TaxAmount` only; `PoOrder.Discount` is persisted in `ApplyHeader` but not included in totals — `PoOrderService.cs` ~1882 and ~2001).
- Default `LegacyDraftMode = RequireGoingForward`. `LegacyExempt` remains configurable on enablement.
- Service-only PO commitment = `ForceCloseAsync`. Purchase invoice requires net received > 0 (`PoInvoiceService.ValidateMatchOnPostAsync` ~1633–1637). Self-billed invoice has no PO/GR lineage. CDN follows invoiced qty. No extra invoice/CDN gate.
- Assignee snapshot at submit; creator excluded from snapshot; ADMIN cannot vote unless assigned.

## Evidence rows

Format: path / method / lines / observed / proposed guard / concurrency.

1. **PR statuses** — `ErpWeb.Core/Purchase/PoPrCalc.cs` `PoPrStatuses` 3–11. `APPROVED` is a processing leftover. Guard never writes `POPR.PRStat` for the new engine.
2. **PO statuses** — `ErpWeb.Core/Purchase/PoStatusPolicy.cs` / `PoOrderCalc.cs` 5–14, 82–99. `PENDING`/`CHECKED` are view-only leftovers. Never repurpose.
3. **Keys** — `PoPrConfiguration` composite `CompanyCode+BranchCode+PrNo`; `PoOrder` adds `PoRelNo`. Instance binds to exact key + PO revision.
4. **PR lock** — `PoPrRepository.LockForUpdateAsync` 44–79 `POPR WITH (UPDLOCK, HOLDLOCK)`.
5. **PR save/update/copy/cancel/delete** — `PoPrService` public methods. Update currently forces `Status = NEW` (1013). Guard: deny material edits while pending/approved/consumed; remarks-only allowed; close pending cycle on cancel/delete.
6. **WO-generated PR** — `CreateFromWorkOrderMaterialAsync` ~519–854. Creates `NEW` with `CreatedBy = current uid`. No auto-submit. Self-approval uses stored `CreatedBy` only.
7. **PR attachments** — `PoPrAttachmentService.UploadAsync` ~308. Block upload/delete while pending/approved.
8. **PR picker** — `PoOrderService.SearchPrForPoAsync` 458–504 accepts `NEW`/`APPROVED`/`PARTIALLY_ORDERED`. Filter to current Approved/NotRequired/exemption when policy ON.
9. **PR consumption** — `LockConsumptionAsync` 1549–1557 locks PR headers sorted then details; `ValidateAndApplyPrConsumptionAsync` 1567–1602. Insert approval lock after headers; `AssertPrConvertible` under those locks. Mixed invalid PR fails whole save.
10. **PO save/update/copy** — `PoOrderService`. Draft allowed unapproved. Material edit blocked while pending/approved. Copy does not inherit approval.
11. **PO revise** — `ReviseAsync` / `SaveExistingAsync(revise:true)`. Locks latest via `LockLatestForUpdateAsync` (`POOrder WITH (UPDLOCK, HOLDLOCK)` by PoNo+PoRelNo). New `PoRelNo` needs new cycle. GR post uses the same `LockLatestForUpdateAsync` — serialize on latest revision; reject GR against stale `PoRelNo`.
12. **PO force close / reopen** — `ChangeStatusAsync` 1167+ locks requested PO then ForceClose/Reopen. `AssertPoCloseable` after lock for force-close. Reopen must not restore a stale gate.
13. **PO cancel/delete** — existing no-receipt rules; close pending cycle in same transaction.
14. **GR picker** — `IvGoodsReceiptService` ~213–228: latest revision, `NEW`/`RECEIVED`, `BalanceQty > 0`. Hide unapproved POs when policy ON.
15. **GR post** — `IvInventoryPostingService` ~1116 lock POs sorted by PoNo then `ValidateGoodsReceiptPoQtyAsync` 4220+. `AssertPoReceivable` after lock, before qty write. Rollback/vendor return: no approval gate.
16. **Invoice** — cannot invoice before GR (`net <= 0`). Not a no-GR bypass. No extra gate.
17. **CDN** — credit qty vs invoiced qty. Not a no-GR PO commitment.
18. **Self-billed** — `PoSbInvoice` remarks: no PO/GR lineage. Out of scope.
19. **Totals** — `PoPrCalc.SumTotals` 72–86 money 2 dp AwayFromZero. PR/PO list and `MapDocument` use it. No document FX. Bands compare that Total in document currency.
20. **Identity** — `UserLogin.id`, `active`, company roles via `UserRoleMapping`. No Groups.
21. **Permissions** — `SUBMIT`/`APPROVE`/`REJECT` exist; unused by PR/PO. `MenuSyncService` seeds ACCESS only. Seed explicit MenuPermission SQL.
22. **SelfViewEdit** — `PoPrService`/`PoOrderService` Get/Update. Approval preview is a separate read path.
23. **Audit** — do not use `SecurityAuditWriter`. Same-transaction `ProcurementApprovalEvent` (append-only, like `SaDeliveryRequestAuditEvent`).
24. **Tests to keep green OFF** — `PoPrServiceTests`, `PoOrderServiceTests`, `PoOrderSqlServerConcurrencyTests`, `IvGoodsReceiptServiceTests`. Guard is optional constructor arg; null/empty policy = OFF.

## Document revision strategy (B6)

Material fingerprint (canonical string of material header+line fields) stored on `ApprovalInstance`. Guards compare fingerprint, not remarks/`RowVersion` alone. Pending/approved Update that changes the fingerprint is denied. Remarks-only update allowed. Attachments blocked at attachment endpoints.

## Lock order (B2/B4)

Existing: PR headers sorted → PR lines; PO `LockLatestForUpdateAsync` / `LockForUpdateAsync` by PoNo then RelNo; GR locks PO numbers sorted then latest revision.  
New: after the document header lock, lock the matching approval instance (`UPDLOCK, HOLDLOCK`), then continue existing line locks. Never lock approval first.

## No-GR commitment conclusion

Verified no direct no-GR invoice/CDN/self-billed PO commitment. Service PO gate = Force Close. Stock PO gate = GR picker + GR post.

## Implementation record (Phases 1–4)

Default remains OFF. Optional `IProcurementApprovalGuard` constructor args keep existing tests green when the guard is omitted.

### SQL / menus
- `scripts/create-po-approval.sql` — additive tables, unique cycle, filtered unique active instance, unique vote.
- `scripts/init-po-approval-menu.sql` — `PO_APPROVAL_SETTINGS` (ACCESS/EDIT), `PO_APPROVAL_INBOX` / `PO_APPROVAL_ALL` (ACCESS), SUBMIT/APPROVE/REJECT on `PO_PR` / `PO_ORDER`.
- `ErpWeb/Menus/menus.xml` — same three menu codes (MenuDeploymentParityTests).

### Engine
- Orthogonal `PoApproval*` tables. No writes to `POPR.Status` / `POOrder.Status` / legacy approved-by columns.
- Submit snapshots policy version, fingerprint, totals, distinct active assignees (creator/submitter excluded).
- Same-transaction `PoApprovalEvent`. Pending survives policy disable.
- Guards: PR consumption, GR picker/post, Force Close, material edit, attachments, cancel/delete close pending.

### UI
- `/purchase/approval/settings` two-tab policy editor.
- `/purchase/approval/inbox` Mine / All pending / Completed, VIEW_COST masking, calendar-day age.
- PR/PO Submit / Withdraw and approval badge on existing document pages.
- SelfViewEdit write restriction kept; assigned approver / inbox permission is read-only.

### Tests run
- `ProcurementApprovalServiceTests`: 9 passed (OFF conversion, missing currency, pending block, self-approval, approve then convert, NotRequired, VIEW_COST mask, disable does not unlock pending, duplicate submit).
- Existing `PoPrServiceTests`, `PoOrderServiceTests`, `IvGoodsReceiptServiceTests`, `MenuDeploymentParityTests`: passed in the same filtered run (119 passed before the two submit-Uid failures, which were then fixed; approval suite 9/9 after remap-after-SaveChanges).
- SQL Server race harness is skippable until `create-po-approval.sql` is applied on DEMO.
