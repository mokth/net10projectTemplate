# Sales Delivery Tracking, Trip, POD & KPI — AI Coding Agent Plan
## Repository: `mokth/net10projectTemplate`
## Branch baseline: `productionv2`
## Baseline commit reviewed: `e893d79b291da2088c7d3d1e17bb79bdeab0fe55`
## Status: APPROVED FOR IMPLEMENTATION — AI-AGENT EXECUTION READY — 10/10
## Review level: Repository-verified implementation contract; no unresolved core design decisions may be guessed by the coding agent
## Target: Practical Malaysian SME delivery management without disturbing existing DO posting/inventory/costing/invoicing

---

## 1. Objective

Add an **optional, lightweight physical delivery-management layer** around the existing Sales Delivery Order (DO).

The feature must answer these operational questions quickly:

1. Which DOs are waiting to be delivered?
2. Which trip/driver/vehicle is responsible?
3. Which deliveries are out now?
4. Which customers received their goods?
5. Which deliveries failed/partially succeeded and why?
6. Which outstanding deliveries need follow-up/rescheduling?
7. Are deliveries on time?
8. How efficiently are drivers/trips performing?
9. What simple delivery cost is incurred?

This is **not** a replacement for the existing DO transaction workflow.

---

## 2. Non-negotiable safety boundary

### 2.1 Existing core behavior that must remain authoritative

Do not change the business meaning of:

- `SaDO.Status`
- DO New/Post/Rollback/Delete/Force Close
- shipment allocation / `IvSpShipmentService`
- stock availability and stock posting
- inventory costing / valuation
- SO -> DO consumption
- DO -> Sales Invoice billing
- e-Invoice
- document totals/tax/pricing
- Delivery Request / Production traceability

Physical delivery status is a **parallel operational state**.

Example:

- DO document status = `POSTED`
- physical delivery status = `OUT_FOR_DELIVERY`

Those are valid simultaneously.

### 2.2 No hidden stock/accounting side effects

The new delivery services must **never**:

- deduct stock;
- restore stock;
- post inventory;
- create invoice;
- alter costing;
- alter DO quantities;
- change DO billing status;
- rollback DO;
- generate stock returns.

A failed/partial physical delivery is evidence and follow-up only. Any commercial or stock correction must continue through the proper existing ERP transaction.

---

## 3. Important current-repo findings

Current relevant artifacts include:

- `ErpWeb.Model/Entities/Sales/SaDo.cs`
- `ErpWeb.Model/Configurations/Sales/SaDoConfiguration.cs`
- `ErpWeb.Core/Sales/ISaDoService.cs`
- `ErpWeb.Core/Sales/SaDoService.cs`
- `ErpWeb.UI/Sales/Transactions/SaDo.razor`
- `ErpWeb.UI/Sales/Transactions/SaDo.razor.cs`
- `ErpWeb.UI/Sales/Transactions/SaDoList.razor`
- `ErpWeb.UI/Sales/Transactions/SaDoList.razor.cs`
- `ErpWeb.UI/Sales/Transactions/SaDoShipmentEditor.razor`
- `ErpWeb.Model/Data/AppDbContext.cs`
- `ErpWeb.Core/CoreServiceCollectionExtensions.cs`
- `ErpWeb.Core/Menus/MenuCodes.cs`

`SaDO` already contains legacy physical-delivery-like columns:

- `ShipOutDate`
- `DriverName`
- `DriverPlate`
- `RecordTime`
- `ShipOutRemark`

These single-value fields are insufficient for:

- multiple DOs per trip;
- multiple attempts;
- rescheduling;
- historical driver changes;
- POD;
- exceptions;
- KPI history.

### Decision

Do **not** remove or repurpose these columns in this implementation.

New normalized delivery tables become the authoritative source for the new module.

Legacy columns remain compatible/untouched unless a later explicit migration plan is approved. Do not dual-write them in Phase 1; dual-write would create two competing sources of truth.

---

# 4. Feature toggle / optional adoption

Add an application setting:

`DELIVERY_TRACKING_ENABLED`

Default: `false` for existing companies unless deliberately enabled.

Implementation contract:

- add `AppSettingCatalogue.SalesKeys.DeliveryTrackingEnabled = "DELIVERY_TRACKING_ENABLED"`;
- add `SalesDeliveryTrackingEnabled` definition under `AppSettingModules.Sales`;
- type = `AppSettingType.Flag`;
- scope = `AppSettingScope.Company | AppSettingScope.Branch`;
- default = `"false"`;
- normal `AdSmParam`-backed setting; **do not** add an existing-column provider;
- add it to `AppSettingCatalogue.All`;
- extend `AppSettingCatalogueTests` so duplicate keys/default parsing/catalogue inclusion remain guarded.

Use the repository's existing `IAppSettingService`; do not invent a new settings store or provider.

When disabled:

- **all delivery service mutation/query entry points must reject/return feature-disabled**, so direct URLs/API/service calls cannot bypass the toggle;
- existing DO entry/list behavior remains unchanged;
- no new tracking fields are mandatory;
- DO posting remains unchanged;
- no tracking validation is called from DO posting;
- delivery-management menus should be hidden/not seeded for companies that do not adopt it, or the pages must clearly report the feature is disabled according to the repo's menu/settings convention.

When enabled:

- delivery-management pages become usable;
- DO pages can display tracking summary/action links.

---

# 5. Domain model

Use company + branch scope consistently with `SaDO`.

## 5.1 `SaDeliveryDriver`

Purpose: small driver master.

Recommended columns:

- `CompanyCode`
- `BranchCode`
- `DriverId` — internal stable key/code
- `DriverName`
- `MobileNo`
- `DriverType` — `EMPLOYEE`, `EXTERNAL`
- `EmployeeCode` nullable — optional reference only if an existing employee source is available
- `TransporterName` nullable
- `Active`
- `Remarks`
- `CreatedDate`, `CreatedBy`
- `ModifiedDate`, `ModifiedBy`
- `RowVersion`

Do not force HR integration.

## 5.2 `SaDeliveryVehicle`

Columns:

- `CompanyCode`
- `BranchCode`
- `VehicleId`
- `RegistrationNo`
- `VehicleType` — Van/Lorry/Truck/Other
- `Description`
- `CapacityWeight` nullable
- `CapacityVolume` nullable
- `TransportType` — `OWN`, `THIRD_PARTY`
- `TransporterName` nullable
- `Active`
- `Remarks`
- audit fields
- `RowVersion`

Capacity is advisory in first implementation. Do not block trips unless later explicitly configured.

## 5.3 `SaDeliveryTrip`

One trip represents one driver/vehicle run.

Columns:

- `CompanyCode`
- `BranchCode`
- `TripNo`
- `TripDate`
- `Status`
- `DriverId` nullable
- `DriverNameSnapshot`
- `DriverMobileSnapshot`
- `VehicleId` nullable
- `VehicleRegistrationSnapshot`
- `TransportType`
- `TransporterNameSnapshot` nullable
- `PlannedDepartureAt` nullable
- `ActualDepartureAt` nullable
- `CompletedAt` nullable
- `CancelledAt` nullable
- `CancelledBy` nullable
- `CancelReason` nullable
- `Remarks`
- `FuelCost` nullable
- `TollCost` nullable
- `ParkingCost` nullable
- `OtherCost` nullable
- `RowVersion`
- audit fields

### Trip statuses

Use constants, not magic strings:

- `PLANNED`
- `OUT_FOR_DELIVERY`
- `COMPLETED`
- `CANCELLED`

Do not derive trip status from DO status.

## 5.4 `SaDeliveryTripStop`

One row represents a customer/address stop on a trip.

Columns:

- `CompanyCode`
- `BranchCode`
- `TripNo`
- `StopId`
- `StopSequence`
- `CustCode`
- `CustNameSnapshot`
- delivery-address snapshot fields
- `ContactNameSnapshot`
- `ContactPhoneSnapshot`
- `PlannedArrivalAt` nullable
- `Status`
- `LastAttemptNo` nullable
- `CompletedAt` nullable
- `Remarks`
- `RowVersion`

### Stop statuses

- `PLANNED`
- `OUT_FOR_DELIVERY`
- `DELIVERED`
- `PARTIALLY_DELIVERED`
- `FAILED`
- `RESCHEDULED`
- `CANCELLED`

## 5.5 `SaDeliveryTripStopDo`

Many DOs may belong to one stop.

Columns:

- company/branch
- `TripNo`
- `StopId`
- `DoNo`
- `PromisedDeliveryDateSnapshot` nullable
- `PromisedFromTimeSnapshot` nullable
- `PromisedToTimeSnapshot` nullable
- `DoDateSnapshot`
- `DoTotalSnapshot` nullable if price permission concerns require omission
- audit fields

Unique constraint:

`CompanyCode + BranchCode + TripNo + StopId + DoNo`

Add `IsActiveAssignment bit NOT NULL DEFAULT 1` to this link.

Database invariant:

- create a **filtered unique index** on `(CompanyCode, BranchCode, DoNo)` where `IsActiveAssignment = 1`;
- this is the final concurrency authority preventing one DO from being dispatched on two active trips;
- application pre-checks are for friendly messages only and do not replace the database invariant.

Lifecycle:

- trip creation / reschedule target link -> `IsActiveAssignment = 1`;
- successful final delivery -> set `0`;
- failed attempt that remains actionable on the same trip -> remains `1`;
- reschedule to a new trip -> old link set `0`, new link inserted as `1` in one transaction;
- trip cancellation -> all still-active links set `0`;
- planned assignment removal -> set `0` (retain row for history rather than hard-delete once auditing matters);
- completed trip must have no ambiguous active link state: successfully delivered DO links are `0`; explicitly rescheduled DOs have the active link only on their new trip.

Do not forbid historical completed/rescheduled links.

## 5.6 `SaDeliveryAttempt`

This is essential for truthful first-attempt KPI and rescheduling.

Columns:

- company/branch
- `AttemptId`
- `TripNo`
- `StopId`
- `AttemptNo`
- `StartedAt` nullable
- `ArrivedAt` nullable
- `CompletedAt`
- `Result`
- `ReasonCode` nullable
- `Responsibility` nullable
- `ReceivedBy` nullable
- `ReceiverContact` nullable
- `Remark`
- `Latitude` nullable
- `Longitude` nullable
- `RecordedBy`
- `RecordedAt`
- `RowVersion`

Result:

- `DELIVERED`
- `PARTIAL`
- `FAILED`

Responsibility:

- `DRIVER`
- `WAREHOUSE`
- `CUSTOMER`
- `SALES`
- `VEHICLE`
- `EXTERNAL`
- `WEATHER_TRAFFIC`
- `OTHER`

This field prevents unfair driver KPI attribution.

## 5.7 `SaDeliveryAttemptDo`

Attempt result per DO at a stop.

Columns:

- company/branch
- `AttemptId`
- `DoNo`
- `Result`
- `Remark`

This is necessary because one customer stop may contain DO001 + DO002 and the customer may accept one but reject the other.

Do not pretend stop-level success means every DO succeeded.

## 5.8 `SaDeliveryPodAttachment`

POD evidence metadata.

Columns:

- company/branch
- `AttachmentId`
- `AttemptId`
- `DoNo` nullable
- `AttachmentType` — `PHOTO`, `SIGNATURE`, `DOCUMENT`
- `OriginalFileName`
- `StoredFileName` / storage key
- `ContentType`
- `FileSize`
- `CreatedDate`
- `CreatedBy`

Use the repository's established attachment/file-storage convention already demonstrated by `PoOrderAttachmentService` + `AttachmentStorageOptions`:

- private root under configured `Attachments:RootPath` (default `App_Data/attachments`);
- company/branch/document-scoped folders;
- metadata in SQL;
- physical file outside `wwwroot`;
- path canonicalisation / root-escape protection;
- extension + MIME validation;
- bounded file size/count;
- authorization on list/upload/download/delete.

Create a delivery-specific attachment service; do not reuse PO menu authorization or PO entity tables.

For POD Phase 1, restrict uploads to image/PDF evidence actually needed for delivery (recommended `.jpg`, `.jpeg`, `.png`, `.pdf`) rather than inheriting office-document extensions blindly.

Do not store large image bytes directly in SQL.

POD is optional.

---

# 6. Promised delivery date: KPI contract

An on-time KPI is meaningless without a promised date.

### Source resolution — repo verified

The repository already defines the authoritative planning fields on `SaSoDetail`:

- `DeliveryDate` — documented in code as **customer promised / requested delivery date**;
- `Eta` — estimated arrival;
- `Etd` — estimated departure.

For a DO line carrying Sales Order lineage, resolve the promise from the **exact source SO revision/line** identified by:

`SaDoDetail.SoNo + SaDoDetail.SoLine + SaDoDetail.CustRel`

Primary KPI promise source:

`SaSoDetail.DeliveryDate`

Do **not** substitute `Eta`, `Etd`, `SaDo.DoDate`, or the current customer master address/date as the promised-delivery KPI target.

If one DO contains multiple source SO lines with different `DeliveryDate` values, persist the line-derived dates and define the DO-level on-time target conservatively as the latest applicable promised date only when the whole DO is confirmed together. Prefer adding `SaDeliveryTripStopDoLinePromise` (or equivalent compact snapshot rows keyed by DO line) if mixed promised dates must remain analytically exact. The coding agent must not collapse conflicting source dates silently.

For a standalone DO line with no valid SO lineage, promise date is null unless a future explicit DO promised-date field is introduced.

Store the resolved result as a **snapshot** in `SaDeliveryTripStopDo` (and line-level promise snapshots when needed for mixed dates).

If no reliable promised date exists:

- leave it null;
- exclude that DO from On-Time Delivery denominator;
- show `No promise date`.

Never use `DODate` as an implicit promise date.

### On-time definition

If only a promised date exists:

`Delivered local date <= PromisedDeliveryDateSnapshot`

If a promised time window exists:

`Actual completion <= promised window end`

Historical KPI must use the snapshot, not today's changed SO/customer data.

---

# 7. Exception reason master

Prefer a small controlled reason table rather than hardcoded UI text:

`SaDeliveryExceptionReason`

Columns:

- company/branch
- `ReasonCode`
- `Description`
- `Responsibility`
- `Active`
- `SortOrder`

Seed practical defaults:

- `CUSTOMER_CLOSED`
- `CUSTOMER_UNAVAILABLE`
- `CUSTOMER_REJECTED`
- `WRONG_ADDRESS`
- `WAREHOUSE_NOT_READY`
- `SHORT_GOODS`
- `DAMAGED_GOODS`
- `VEHICLE_BREAKDOWN`
- `TRAFFIC`
- `WEATHER`
- `DRIVER_ISSUE`
- `OTHER`

Users may maintain descriptions/active state, but code should preserve stable reason codes.

---

# 8. Database and EF implementation

Create:

- entity classes under `ErpWeb.Model/Entities/Sales/`
- EF configurations under `ErpWeb.Model/Configurations/Sales/`
- DbSets in `AppDbContext`
- idempotent SQL deployment script under `scripts/`

Suggested script:

`scripts/create-sales-delivery-tracking.sql`

Required indexes include:

1. trip by company/branch/date/status;
2. trip by driver/date;
3. trip by vehicle/date;
4. stop by trip/sequence;
5. StopDO by DO;
6. attempt by stop/date/result;
7. attempt by reason/responsibility/date;
8. exception reason active lookup.

All foreign keys must include tenant keys where applicable.

Use SQL Server `rowversion` for mutable operational records consistent with existing concurrency patterns.

---

# 8A. Exact relational key and integrity contract

The coding agent must implement explicit composite tenant keys. Do not rely on navigation conventions to infer them.

Recommended primary keys:

| Table | Primary key |
|---|---|
| `SaDeliveryDriver` | `(CompanyCode, BranchCode, DriverId)` |
| `SaDeliveryVehicle` | `(CompanyCode, BranchCode, VehicleId)` |
| `SaDeliveryTrip` | `(CompanyCode, BranchCode, TripNo)` |
| `SaDeliveryTripStop` | `(CompanyCode, BranchCode, TripNo, StopId)` |
| `SaDeliveryTripStopDo` | `(CompanyCode, BranchCode, TripNo, StopId, DoNo)` |
| `SaDeliveryAttempt` | `(CompanyCode, BranchCode, AttemptId)` |
| `SaDeliveryAttemptDo` | `(CompanyCode, BranchCode, AttemptId, DoNo)` |
| `SaDeliveryPodAttachment` | `(CompanyCode, BranchCode, AttachmentId)` |
| `SaDeliveryExceptionReason` | `(CompanyCode, BranchCode, ReasonCode)` |

Identity strategy:

- `StopId`, `AttemptId`, `AttachmentId` may be `bigint IDENTITY` surrogate values, but tenant columns remain part of the EF/database key where specified above;
- `TripNo` is the user-visible document key generated by `IDocumentNumberingService`;
- `DriverId` and `VehicleId` are stable user-facing codes, not mutable names.

Required FKs:

- Stop -> Trip;
- StopDO -> Stop and -> `SaDO` using `(CompanyCode, BranchCode, DoNo)`;
- Attempt -> Stop;
- AttemptDO -> Attempt and -> `SaDO`;
- POD -> Attempt; optional DO reference must remain in the same tenant;
- Trip -> Driver/Vehicle should be optional/restrictive references while snapshots preserve history;
- Reason reference from an attempt must use same tenant scope.

Delete behavior:

- **no cascade delete from `SaDO` into delivery evidence**;
- master deletes should normally be soft/inactive; do not cascade driver/vehicle deletion into trips;
- historical trip/attempt/POD rows are retained;
- planned-only rows can be cancelled/deactivated through service logic.

Column lengths should follow the source columns they snapshot where possible (`CompanyCode` 10, `BranchCode` 10, `DoNo` 30, etc.). The agent must inspect the source EF configuration before choosing each snapshot length; do not silently truncate.

---

# 8B. Address snapshot and stop-grouping contract

Stop grouping must be deterministic.

Build a normalized ship-to key from the DO's persisted shipping snapshot, not the current customer master:

`CustCode + ShipName + ShipAddress1..4 + ShipCity + ShipState + ShipPostalCode + ShipCountry`

Normalization for grouping only:

- trim;
- null -> empty;
- collapse repeated whitespace;
- compare case-insensitively.

Persist the original DO shipping text into the stop snapshot for display/audit. Do not overwrite it with normalized text.

If selected DOs for the same customer differ materially in ship-to snapshot, create separate stops.

Do not use today's customer address master because it can change after the DO was created.

---

# 9. Running number

Use the repository's existing **`IDocumentNumberingService`** pattern, matching `SaDoService.SaveNewAsync`.

Configure a dedicated document-numbering module, recommended module token:

`DT`

Trip number example:

`DT000001`

The service must call `IDocumentNumberingService.NextAsync(...)` inside the trip-create transaction and handle the same configuration/overflow/concurrency exception families used by DO numbering.

Do not add a `RunningNumberKeys` constant merely to generate trip documents and do not implement `MAX(TripNo)+1`.

Deployment must include/describe the required `AdSmNum`/numbering configuration for `DT`; otherwise a new company must receive a clear “Delivery Trip numbering is not configured” business error.

---

# 10. Core services

Do **not** bloat `ISaDoService`.

Create dedicated contracts:

- `ISaDeliveryDriverService`
- `ISaDeliveryVehicleService`
- `ISaDeliveryTripService`
- `ISaDeliveryTrackingInquiryService`
- `ISaDeliveryDashboardService`
- `ISaDeliveryPodService`

Register them in:

`ErpWeb.Core/CoreServiceCollectionExtensions.cs`

## 10.1 Trip service responsibilities

- create trip;
- select eligible DOs;
- group selected DOs into customer/address stops;
- reorder stops;
- assign/reassign driver before departure;
- assign/reassign vehicle before departure;
- start trip;
- record attempt;
- complete trip;
- cancel trip;
- reschedule failed/partial DOs;
- save simple trip expenses.

## 10.2 Eligibility for trip assignment

Initial safe rule:

- feature enabled;
- DO exists;
- DO not deleted;
- DO status is **`SaDoStatuses.Posted`** for the initial implementation;
- DO is not deleted (`DeletedAtUtc == null`);
- DO is not already fully physically delivered;
- DO is not on another active trip;
- DO belongs to the current company + branch.

`NEW` is not eligible because stock shipment/posting has not been committed.
`CLOSED` is not eligible for a new trip because force-close is terminal commercial/shipment history.
Use the existing `SaDoStatuses` constants; never duplicate status literals.

If business later requires pre-dispatch planning before DO posting, that is a separate enhancement and must not be smuggled into this implementation.

Do not make DO posting call the delivery service.

Do not make DO posting call the delivery service.

---

# 10A. Transaction, locking and idempotency contract

All state-changing service methods must be server authoritative.

## Create trip

One DB transaction must:

1. validate feature + permission + tenant;
2. validate all selected DOs are `POSTED`, not deleted and current-branch;
3. lock/re-read selected DOs or otherwise revalidate them inside the transaction;
4. issue `TripNo` with `IDocumentNumberingService`;
5. create trip/stops/snapshots;
6. insert active StopDO links;
7. rely on the filtered unique active-assignment index to reject races;
8. commit once.

If one selected DO fails, default behavior is **fail the whole trip creation** and return per-DO validation reasons. Do not silently create a partial trip unless the user explicitly removes invalid rows and retries.

## Start trip

One transaction:

- require `PLANNED`;
- require at least one active DO;
- require driver (unless an explicit third-party transporter-only scenario is represented);
- revalidate every active DO still exists, is POSTED, and is not deleted;
- set `ActualDepartureAt` once;
- transition Trip and applicable Stops to `OUT_FOR_DELIVERY`;
- repeated submission after success must return a deterministic already-started result, not stamp a second departure.

## Record attempt

One transaction:

- lock/re-read Trip + Stop;
- require trip has started and is not cancelled/completed;
- allocate `AttemptNo = previous max + 1` under the stop lock/transaction;
- validate one result for every DO included in that attempt;
- require reason for Partial/Failed;
- derive responsibility from reason master server-side;
- insert Attempt + AttemptDO;
- update stop summary state;
- deactivate successfully delivered DO links;
- retain failed/partial actionable assignment until rescheduled/completed according to explicit service rule;
- save once.

POD file bytes cannot be made fully atomic with SQL. Persist attempt first, then upload through the POD service. Failed file upload must not roll back a valid delivery confirmation; show the upload failure and allow retry.

## Reschedule

One transaction:

- source DO must have failed/partial evidence and remain actionable;
- deactivate old active assignment;
- insert the new planned assignment;
- preserve all old attempt rows;
- unique filtered index guarantees no duplicate active assignment;
- do not change DO stock/accounting state.

## Complete trip

One transaction:

- require all stops terminal;
- require no unresolved active DO assignment still pointing at this trip unless that state is explicitly the terminal/rescheduled representation;
- stamp `CompletedAt` once;
- transition to `COMPLETED`;
- repeated submit is deterministic/no duplicate history.

## Cancel trip

- `PLANNED`: cancel directly and deactivate links;
- after departure: cancellation requires reason and must preserve attempts/evidence; do not erase history;
- a DO requiring further delivery becomes actionable/reschedulable through the explicit reschedule path.

All commands use rowversion where a mutable aggregate exposes it. Catch `DbUpdateConcurrencyException` and return the repository-style reload/retry message.

---

# 11. State transition rules

## Trip

`PLANNED -> OUT_FOR_DELIVERY -> COMPLETED`

`PLANNED -> CANCELLED`

Cancellation after departure requires explicit reason and must not erase attempt history.

## Stop

`PLANNED -> OUT_FOR_DELIVERY -> DELIVERED`

or

`... -> PARTIALLY_DELIVERED`

or

`... -> FAILED -> RESCHEDULED`

A reschedule creates a future assignment/attempt path; it does not overwrite prior failure evidence.

## Completion

A trip may be completed only when every non-cancelled stop is in a terminal state:

- Delivered
- Partial
- Failed
- Rescheduled
- Cancelled

Never silently mark remaining stops delivered.

---

# 12. DO integration

## 12.1 `SaDo.razor`

When feature enabled and document exists, add a compact **Delivery Tracking** section/tab.

Display only:

- Current delivery status
- Trip No
- Driver
- Vehicle
- Planned/actual departure
- Last attempt result
- Delivered date/time
- Received by
- POD count
- Exception reason
- `View Delivery` action

Do not turn normal DO entry into a logistics form.

## 12.2 `SaDoList`

Add optional lightweight tracking columns/filter only when enabled:

- Delivery Status
- Trip No
- Driver

Avoid loading full attempt/POD graphs per row.

Implement server-side projection/query to prevent N+1 queries.

Add multi-select action:

`CREATE DELIVERY TRIP`

Only eligible selected DOs are accepted. Ineligible rows return clear per-DO reasons.

---

# 13. Delivery Trip Planning UI

New page:

`ErpWeb.UI/Sales/Delivery/SaDeliveryTripEntry.razor`

Follow existing productionv2 list/entry UI conventions.

### Header

- Trip No
- Trip Date
- Driver lookup
- Vehicle lookup
- Transport Type
- Planned Departure
- Remarks

### DO picker

Search/filter:

- DO No
- Customer
- Delivery area/state/city
- DO date
- promised date
- unassigned only

Selected DOs automatically group into stops using:

`CustCode + normalized delivery address`

Do not merge stops based only on customer code because one customer can have multiple ship-to addresses.

### Stops grid

Columns:

- sequence
- customer
- address
- DO count
- promised date/time
- status

Actions:

- Move Up
- Move Down
- Open details
- Remove (only before departure)

Do not build route optimization in Phase 1.

---

# 14. Daily Delivery Board

New page:

`SaDeliveryBoard.razor`

This is the supervisor's primary daily screen.

Filters:

- date
- status
- driver
- vehicle
- customer
- trip

KPI chips:

- Planned
- Out for Delivery
- Delivered
- Partial
- Failed
- Rescheduled
- Outstanding

Each chip must drill/filter the same underlying data.

Display trip cards/grid with:

- trip
- driver
- vehicle
- departure
- stops total
- delivered
- failed
- outstanding
- progress %

The board must be useful without opening every trip.

---

# 15. Outstanding Delivery Inquiry

New page:

`SaDeliveryOutstandingInquiry.razor`

Definition:

A DO/stop that should have been delivered or was attempted but is not successfully delivered and remains actionable.

Show:

- DO
- customer
- promised date
- days overdue
- last trip
- driver
- last attempt
- failure reason
- responsibility
- rescheduled date/trip
- action: open DO / open trip

This page is more operationally valuable than a decorative dashboard.

---

# 16. Delivery confirmation / POD UI

Create reusable component:

`SaDeliveryConfirmPopup.razor`

Fields:

- Result — Delivered / Partial / Failed
- Actual date/time — default current business/local time
- Received By — required for Delivered unless company later configures otherwise
- Receiver Contact — optional
- Failure/partial reason — required for Failed/Partial
- Responsibility — derived from reason but editable only if permitted
- Remark
- Photo upload — optional
- Signature — optional
- GPS — optional and never required in Phase 1

If a stop has multiple DOs, show each DO with its own result selector.

Fast path:

For normal successful stop, user should be able to complete confirmation with minimal typing.

---

# 17. Mobile-friendly driver view

New route/page:

`/sales/delivery/my-trips`

Keep it responsive rather than building a separate mobile app.

Driver sees:

- today's assigned trips;
- stop sequence;
- customer;
- address;
- phone;
- DO numbers;
- delivery instructions;
- `Navigate`;
- `Confirm Delivery`;
- `Report Problem`.

Navigation should launch an external map URL from the address/location; do not implement routing/navigation engine.

Security requirement:

A driver user may only see trips assigned to their mapped identity unless they hold supervisor-level delivery permission.

If driver-user mapping is not reliable in current repo, keep the page permission-gated for office/supervisor use in Phase 1 rather than implementing an insecure name match.

---

# 18. KPI definitions

Create one shared calculation contract/service so dashboard, inquiry, exports, and tests use identical formulas.

## 18.1 Delivery Success %

Denominator:
all completed delivery attempts in period where result is a valid delivery attempt.

Numerator:
attempts with result `DELIVERED`.

Display Partial separately.

## 18.2 First-Attempt Success %

Per DO:

Numerator:
DO successfully delivered on attempt #1.

Denominator:
DOs with at least one completed attempt.

Do not calculate this only at stop level.

## 18.3 On-Time Delivery %

Numerator:
successfully delivered DOs whose actual completion is on/before snapshotted promise target.

Denominator:
successfully delivered DOs with a valid promise snapshot.

Exclude no-promise-date rows and display excluded count.

## 18.4 Average Delivery Time

For completed successful stops:

`CompletedAt - ActualDepartureAt`

This is an operational elapsed time, not driving time.

Name the KPI accordingly, e.g. `Avg elapsed delivery time`.

Do not call it travel time.

## 18.5 Outstanding Deliveries

Count actionable DOs not yet successfully delivered.

Avoid counting cancelled DOs/trips as outstanding.

## 18.6 Driver Productivity

Show:

- Trips
- Attempted stops
- Successful stops
- Successful DOs
- First-attempt success %
- On-time %
- Driver-responsible failures
- Other-responsibility failures

Do not publish a single simplistic “best driver” score.

## 18.7 Cost per Successful Stop

When trip costs exist:

`(Fuel + Toll + Parking + Other) / successful stops`

Do not allocate this into inventory/COGS/accounting.

It is management analysis only.

---

# 19. Driver Performance Inquiry

New page:

`SaDeliveryDriverPerformanceInquiry.razor`

Filters:

- date range
- driver
- vehicle
- transport type

Columns:

- driver
- trips
- attempted stops
- successful stops
- delivered DOs
- on-time %
- first-attempt %
- failed
- driver-responsible failed
- customer-caused failed
- warehouse-caused failed
- avg elapsed delivery time

Drill down to underlying trips/attempts.

This avoids misleading management decisions from raw delivery counts.

---

# 20. Delivery Performance Dashboard

New page:

`SaDeliveryPerformanceDashboard.razor`

Keep it operational, not BI-heavy.

Top KPIs:

1. Due/Planned
2. Delivered
3. Out for Delivery
4. Failed
5. Outstanding
6. On-Time %
7. First-Attempt Success %

Secondary:

- top exception reasons;
- failure responsibility split;
- deliveries by driver;
- delivery trend by day/week;
- optional cost per successful stop.

Every KPI/chart must support drill-down or a clear navigation to the corresponding inquiry.

---

# 21. Permissions and menu codes

Add explicit codes in `MenuCodes.cs`, following the repository convention.

Suggested:

- `SA_DELIVERY_MGMT` — parent
- `SA_DELIVERY_TRIP`
- `SA_DELIVERY_BOARD`
- `SA_DELIVERY_OUTSTANDING`
- `SA_DELIVERY_PERF`
- `SA_DELIVERY_DRIVER_PERF`
- `SA_DELIVERY_DRIVER`
- `SA_DELIVERY_VEHICLE`
- `SA_DELIVERY_REASON`

Use existing permission framework.

Use the repository's **existing built-in `PermissionCodes`** rather than inventing new permission tokens.

Recommended mapping:

- view pages/inquiries -> `ACCESS`
- create trip/master row -> `ADD`
- edit planned trip/master row -> `EDIT`
- start trip -> `POST` (operational release/start)
- confirm delivery / record attempt -> `POST`
- cancel trip -> `CANCEL`
- delete unused master/planned row where allowed -> `DELETE`
- export inquiry -> `EXPORT`
- see trip costs -> `VIEW_COST`

Do not add `START_TRIP`, `CONFIRM_DELIVERY`, or other new permission-code constants unless a later security requirement proves the built-in vocabulary insufficient.

Keep separate **menu codes** so roles can grant different permissions per delivery function. Do not piggyback all actions on `SA_DO` Edit permission.

Create idempotent menu seed script.

---

# 22. Query/performance rules

1. All queries must be company/branch scoped.
2. Dashboard uses aggregate SQL/projections, not loading entities then aggregating in memory.
3. DO list tracking data uses one bounded projection, not per-row service calls.
4. POD binary/file data is never loaded in dashboard/list queries.
5. Index by date/status/driver/DO.
6. Apply bounded paging to list/inquiry screens.
7. Do not `Include()` full graph for KPI calculations.
8. Use `AsNoTracking()` for read-only inquiries.

---

# 23. Audit and history

Never overwrite historical facts such as:

- who actually drove;
- vehicle used;
- delivery address used;
- promise target;
- attempt result;
- exception reason;
- receiver;
- completion time.

Use snapshots.

Driver/vehicle master edits must not rewrite old trip history.

Attempts should be append-oriented. Corrections require explicit audited correction behavior rather than silently replacing historical attempt rows.

---

# 24. DO rollback/delete interaction — repo-verified safety rule

The current `SaDoService.RollbackOneAsync` performs a real accounting/inventory reversal:

- only `POSTED` DOs can roll back;
- DO -> SO allocations are reversed;
- `SoConsumedQty` is reset;
- a POSTED SP shipment batch is rolled back through stock posting;
- DO status becomes `NEW`.

Therefore physical-delivery evidence cannot be ignored during rollback.

### 24.1 Planned assignment only — no departure/attempt

A DO that is merely on a `PLANNED` trip has no physical-delivery evidence yet.

Before DO rollback/delete proceeds:

- detach/cancel that DO's planned delivery assignment in the **same logical user operation**;
- if automatic detachment cannot be made transactionally safe, reject with a clear message such as:
  `Remove DO xxxx from planned Delivery Trip DTxxxx before rollback.`
- do not leave an active trip pointing at a NEW/deleted DO.

### 24.2 Delivery has started / attempt evidence exists

If any of these exist for the DO:

- trip actual departure covering the DO;
- attempt started/arrived/completed;
- Delivered/Partial/Failed result;
- POD evidence;

**block DO rollback and delete**.

Reason: current DO rollback reverses stock-out. Allowing it after physical delivery activity would make inventory say the goods were restored although the truck/customer flow says they left.

User-facing message must direct the user to the proper physical/commercial correction flow (e.g. delivery exception plus existing return/correction transaction), not tell them to erase delivery evidence.

### 24.3 Force close

Do not automatically block the existing POSTED -> CLOSED force-close solely because delivery evidence exists. Force-close currently does **not** reverse stock and is designed to retain shipment evidence. Add regression tests to ensure delivery tracking remains linked/readable after force-close.

### 24.4 Integration point

The delivery guard must be enforced **server-side** from the DO rollback/delete path, not only by disabling UI buttons.

Keep the guard narrow: planned-only assignment is removable; physical evidence blocks stock-restoring rollback/delete.

Add focused tests before merge.

---

# 25. Delivery cost scope

Phase 1 supports only:

- Fuel
- Toll
- Parking
- Other

No:

- payroll allocation;
- depreciation;
- maintenance allocation;
- GL posting;
- COGS;
- inventory costing;
- complex freight allocation.

This keeps the feature SME-friendly.

---

# 26. Out of scope for first implementation

Explicitly do NOT implement:

- live continuous GPS tracking;
- geofencing;
- route optimization;
- fleet preventive maintenance;
- telematics;
- fuel-card integration;
- automatic WhatsApp/SMS;
- accounting posting of trip costs;
- complex freight costing;
- driver payroll/commission;
- customer portal;
- native mobile app.

These can be later enhancements.

---

# 26A. AI-agent file map

The agent should create/modify approximately this structure. Exact split may follow existing repository conventions, but responsibilities must not be collapsed into `SaDoService`.

### Model

Create under `ErpWeb.Model/Entities/Sales/`:

- `SaDeliveryDriver.cs`
- `SaDeliveryVehicle.cs`
- `SaDeliveryTrip.cs`
- `SaDeliveryTripStop.cs`
- `SaDeliveryTripStopDo.cs`
- `SaDeliveryAttempt.cs`
- `SaDeliveryAttemptDo.cs`
- `SaDeliveryPodAttachment.cs`
- `SaDeliveryExceptionReason.cs`

Create matching files under `ErpWeb.Model/Configurations/Sales/`.

Modify:

- `ErpWeb.Model/Data/AppDbContext.cs`

### Core

Create under `ErpWeb.Core/Sales/Delivery/` (preferred dedicated namespace/folder):

- status/result/reason constants;
- request/DTO/query models;
- driver service + interface;
- vehicle service + interface;
- trip service + interface;
- tracking inquiry service + interface;
- dashboard/KPI service + interface;
- POD service + interface;
- shared promise resolver;
- delivery guard queried by DO rollback/delete.

Modify:

- `ErpWeb.Core/CoreServiceCollectionExtensions.cs`
- `ErpWeb.Core/Settings/AppSettingCatalogue.cs`
- `ErpWeb.Core/Menus/MenuCodes.cs`
- the narrow server-side DO rollback/delete paths in `SaDoService.cs` only to invoke the delivery guard;
- DO list query/projection contracts only as required for bounded delivery-summary projection.

Do **not** move stock posting, shipment allocation, pricing, costing or invoice logic into delivery services.

### UI

Create under `ErpWeb.UI/Sales/Delivery/`:

- driver master/list;
- vehicle master/list;
- reason master/list;
- trip list;
- `SaDeliveryTripEntry.razor` + code-behind if matching repo style;
- `SaDeliveryBoard.razor`;
- `SaDeliveryOutstandingInquiry.razor`;
- `SaDeliveryDriverPerformanceInquiry.razor`;
- `SaDeliveryPerformanceDashboard.razor`;
- `SaDeliveryConfirmPopup.razor`;
- optional secure `SaDeliveryMyTrips.razor`.

Modify narrowly:

- `SaDo.razor` / `.razor.cs`;
- `SaDoList.razor` / `.razor.cs`;
- navigation/menu registration files actually used by the repository.

Follow existing Sales Order/DO list-entry layout and `CommonDataGridEx` conventions. Do not create a new visual design system.

### Tests

Add a dedicated Sales Delivery test folder/classes rather than bloating `SaDoServiceTests`.

Modify/add:

- `AppSettingCatalogueTests`;
- menu/permission tests if existing;
- focused `SaDoServiceTests` only for rollback/delete delivery guard regression;
- service tests for trip lifecycle, tenant isolation, unique active assignment, KPI formulas, promise snapshots and POD authorization.

### SQL

Create:

- `scripts/create-sales-delivery-tracking.sql`
- `scripts/init-sales-delivery-menu.sql`
- `scripts/init-sales-delivery-numbering.sql` or an explicit documented idempotent `DT` numbering seed consistent with current `AdSmNum` schema.

Every script must be rerunnable without destructive reset.

---

# 27. Implementation phases

## Phase A — foundation

**Agent rule:** implement and verify one phase before continuing. Do not generate the whole feature in one unverified mega-change.

1. Reconfirm baseline branch/commit and stop if repository structure materially changed.
2. Confirm existing DO/SO promised-date sources.
3. Add feature setting.
4. Add driver, vehicle, exception-reason entities.
4. Add trip/stop/StopDO/attempt/AttemptDO/POD metadata entities.
5. Add EF configurations and DbSets.
6. Add SQL deployment script.
7. Add indexes/FKs/concurrency.
8. Add service contracts and DI.

Exit gate:
schema compiles, migration script is idempotent, tenant isolation tests pass.

## Phase B — trip operations

1. Driver/vehicle/reason masters.
2. Trip list + entry.
3. DO picker.
4. customer/address grouping.
5. stop ordering.
6. start/cancel/complete.
7. attempt recording.
8. rescheduling.

Exit gate:
multiple DO/customer stops and multiple attempts work without touching stock/posting.

## Phase C — DO integration

1. Tracking summary in DO view.
2. tracking columns/filter in DO list.
3. multi-select `Create Delivery Trip`.
4. open trip from DO.

Exit gate:
feature disabled = current DO UX/business behavior unchanged.

## Phase D — operational inquiries

1. Daily Delivery Board.
2. Outstanding Delivery Inquiry.
3. POD popup/storage.
4. mobile-responsive My Trips if secure driver mapping is available.

## Phase E — KPI

1. shared KPI calculator/query.
2. performance dashboard.
3. driver performance inquiry.
4. cost summary.
5. drill-down.

## Phase F — hardening

1. concurrency;
2. permissions;
3. rollback/delete interaction;
4. attachment validation/security;
5. performance;
6. regression suite.

---

# 27A. Mandatory agent verification gates

At the end of **every phase**:

1. build the solution;
2. run the directly affected test class(es);
3. run existing Sales DO tests when DO code changed;
4. inspect generated SQL/EF mapping for tenant keys and rowversion;
5. report changed files and any deviation from this plan;
6. do not proceed while compile errors or failing regression tests remain.

Before final approval:

- full solution build succeeds;
- all existing tests pass;
- all new delivery tests pass;
- SQL scripts can be executed twice safely;
- feature OFF smoke test shows no visible/behavioral DO regression;
- feature ON end-to-end test covers POSTED DO -> trip -> departure -> attempt -> POD -> KPI/inquiry;
- concurrency test proves two users cannot actively assign the same DO to two trips;
- rollback guard test proves stock-restoring rollback is blocked after physical evidence;
- planned-only rollback path proves stale trip links cannot remain;
- force-close regression proves delivery history remains readable;
- tenant test proves Company A/Branch A cannot read/mutate Company B/Branch B delivery rows.

The coding agent must not mark implementation complete merely because the project compiles.

---

# 28. Required tests

## Domain/service tests

Must cover at least:

1. create trip with one DO;
2. create trip with multiple DOs same customer/address -> one stop;
3. same customer different address -> separate stops;
4. multiple customers -> separate stops;
5. prevent same DO on two active trips;
6. start trip;
7. successful delivery;
8. partial delivery;
9. failed delivery requires reason;
10. multiple DOs at one stop with mixed results;
11. failed attempt then successful second attempt;
12. first-attempt KPI false after retry success;
13. on-time with promise date;
14. late delivery;
15. no promise date excluded from on-time denominator;
16. driver-responsible failure classification;
17. customer/warehouse failure not counted as driver-responsible;
18. trip cannot complete with nonterminal stops;
19. cancelled trip excluded from active outstanding assignment;
20. historical snapshot survives driver master rename;
21. feature-disabled behavior;
22. company/branch isolation;
23. rowversion concurrency;
24. POD metadata permission/file validation;
25. trip cost calculation;
26. two concurrent trip creations with same DO -> exactly one active assignment succeeds;
27. trip creation with one invalid selected DO -> whole create fails, no partial trip remains;
28. attempt double-submit -> no duplicate attempt/history;
29. reschedule preserves old attempt and moves active assignment atomically;
30. DO rollback while PLANNED -> detach/reject safely with no stale active assignment;
31. DO rollback after departure/attempt/POD -> blocked server-side;
32. DO force-close with delivery evidence -> history remains readable;
33. promise snapshot remains unchanged after SO delivery date later changes;
34. same DO/SO mixed line promise dates are not silently collapsed;
35. POD path traversal/file-type/size/count authorization tests;
36. cost fields hidden when caller lacks `VIEW_COST`;
37. feature disabled blocks direct URL/service mutation, not only menu visibility;
38. cross-tenant guessed TripNo/AttemptId/AttachmentId returns not found/access denied without data leakage;
39. inactive driver/vehicle cannot be newly assigned but historical trips still render snapshots;
40. cancellation/reschedule leaves no duplicate active DO assignment.

## Regression tests

Existing `SaDoServiceTests` and related Sales tests must remain green.

Specifically verify no behavior change to:

- Save DO
- Post DO
- Rollback DO
- Delete DO
- Force Close
- shipment allocation
- SO consumption
- billing/invoice eligibility
- stock posting
- costing

---

# 29. UI acceptance criteria

The feature is not approved merely because it stores data.

### Supervisor

Within a few clicks can:

- see today's outstanding deliveries;
- create a trip from selected DOs;
- assign driver/vehicle;
- see progress;
- see failed deliveries and reason;
- reschedule.

### Driver/operator

Can:

- see today's stops;
- see address/contact/DO;
- mark Delivered/Partial/Failed quickly;
- capture receiver/POD when needed.

### Sales

From DO can immediately see:

- delivery status;
- who is delivering;
- whether delivered;
- when;
- received by;
- why failed.

### Management

Can answer:

- on-time rate;
- first-attempt success;
- outstanding deliveries;
- main failure reasons;
- driver-responsible failures;
- driver/trip productivity;
- simple delivery cost.

---

# 30. Agent implementation guardrails

The coding agent must:

1. inspect exact current classes before modifying them;
2. preserve naming/style conventions;
3. use DevExpress binding correctly (`@bind-*` or provide matching `*Expression` when using Value/Text + Changed);
4. use existing `CommonDataGridEx` where appropriate;
5. follow current Sales list/entry visual standard;
6. use existing access-right and tenant services;
7. use existing current-date service rather than scattered `DateTime.Now` for business dates;
8. use cancellation tokens on async DB/service calls;
9. use rowversion concurrency;
10. avoid N+1 queries;
11. add tests with every state transition;
12. not modify stock/costing logic to make delivery tracking “work”;
13. not invent SO/DO field semantics—verify them first;
14. stop and report if a required secure driver-user mapping cannot be established.

---

# 31. Definition of Done

Implementation is complete only when:

- optional setting works;
- existing companies can continue without using delivery tracking;
- driver/vehicle/reason masters work;
- multiple DOs can form one trip;
- same customer/address DOs can form one stop;
- delivery attempts preserve history;
- Delivered/Partial/Failed/Rescheduled work;
- POD is optional;
- outstanding inquiry is accurate;
- promise-date snapshot is traceable;
- on-time denominator is defensible;
- first-attempt KPI is correct;
- failure responsibility prevents unfair driver attribution;
- DO entry/list expose useful tracking information;
- dashboard numbers drill to underlying records;
- simple trip cost does not affect accounting/costing;
- permissions and tenant boundaries are enforced;
- concurrent edits are handled;
- existing DO posting/rollback/shipment/invoice/costing tests remain green;
- new focused tests pass.

---

# 31A. Repo-verification amendments that are mandatory

The following are no longer optional interpretation points; they were verified against the current `productionv2` code and must be followed:

1. **Promise source:** `SaSoDetail.DeliveryDate` is the documented customer promised/requested delivery date. Resolve it through exact DO -> SO line/revision lineage.
2. **ETA/ETD:** `SaSoDetail.Eta` and `Etd` are planning estimates only and are not the on-time KPI promise.
3. **Feature setting:** add `DELIVERY_TRACKING_ENABLED` to `AppSettingCatalogue.SalesKeys`, add a `Flag` definition under `SALES`, default `false`, include it in `AppSettingCatalogue.All`, and extend catalogue/settings tests. Do not invent a provider because this is not an existing-column-backed setting.
4. **Numbering:** use `IDocumentNumberingService` and a configured delivery-trip module (`DT`) in the same transactional style as DO numbering.
5. **Attachments:** follow the private-file pattern used by `PoOrderAttachmentService`/`AttachmentStorageOptions`, but create delivery-specific authorization/entity/service.
6. **Permissions:** reuse built-in `PermissionCodes` and isolate authority through delivery menu codes.
7. **Rollback:** physical delivery evidence must block DO rollback/delete because current rollback reverses stock and SO allocations. Planned-only assignment may be detached.
8. **Force close:** do not conflate force-close with rollback; current force-close retains the posted shipment and does not reverse stock.
9. **Legacy `SaDO` fields:** `ShipOutDate`, `DriverName`, `DriverPlate`, `RecordTime`, `ShipOutRemark` remain untouched and non-authoritative for the new module.
10. **DO list performance:** extend server-side projections/query service; never call a delivery service once per DO row.
11. **Business dates:** use `ICurrentDateService` / existing stock business-time mechanisms as appropriate; preserve the repo's distinction between business-local dates and UTC audit timestamps.
12. **Status constants:** locate/reuse the existing `SaDoStatuses` authority; never duplicate `NEW/POSTED/CLOSED` literals in delivery code.

---

# 32. Approval assessment

**Status: APPROVED FOR IMPLEMENTATION — AI-AGENT EXECUTION READY**

**Implementation-plan score: 10/10**

The 10/10 rating applies to this plan as an implementation contract against the reviewed `productionv2` baseline. It does not mean generated code is automatically approved: each phase must satisfy the build/test/verification gates above.

This design is intentionally smaller than a transport-management system. It targets the daily needs of an SME ERP:

- dispatch;
- responsibility;
- proof of delivery;
- failed-delivery follow-up;
- outstanding control;
- useful delivery/driver KPI.

The design preserves the current Sales Delivery Order as the commercial/inventory document and adds physical delivery as an independent operational layer. This separation is the key safety decision for `productionv2`.


---

# 33. Final instruction to the coding agent

Implement this plan **as an additive delivery-management module**.

Priority order when conflicts arise:

1. preserve existing DO/stock/costing/invoice correctness;
2. preserve tenant/security boundaries;
3. preserve immutable delivery evidence/history;
4. enforce concurrency/database invariants;
5. keep operator workflow simple;
6. optimize inquiry performance;
7. add optional convenience only after the above are safe.

If the current repository differs from the reviewed baseline in a way that changes a named service, entity, status, permission, numbering contract, or rollback invariant, **stop that affected phase, inspect the new implementation, and update the smallest affected part of the plan before coding**. Do not guess around a changed core invariant.

Do not redesign unrelated Sales, Inventory, Production, Procurement, pricing, costing, e-Invoice, or accounting code while implementing this feature.

The implementation is approved only when all Definition-of-Done items and mandatory verification gates pass.
