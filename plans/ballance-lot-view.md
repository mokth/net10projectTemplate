---
name: Inventory Balance by Lot Inquiry
overview: "Create a professional inquiry page for viewing on-hand inventory balance by lot (IvBalLoc table). The page enables warehouse and stock personnel to query current stock positions with flexible filtering by item, lot, warehouse, and quantity criteria. Designed as a read-only analytical tool following 2026 modern ERP UI standards with project theme consistency. **CRITICAL: Phase 0 validation required before implementation to verify business rules against existing ERP.**"
todos:
  - id: phase0
    content: "Phase 0: Validate ERP business rules and data structures"
    status: pending
  - id: model
    content: Create models with shared filter definition
    status: pending
  - id: service
    content: Create service with shared filter builder (NOT CTE reuse)
    status: pending
  - id: ui
    content: Create IvBalanceLotView.razor page with filters and grid
    status: pending
  - id: css
    content: Create IvBalanceLotView.razor.css with component styles
    status: pending
  - id: menu
    content: Register menu code and route
    status: pending
  - id: export
    content: Implement server-side streaming CSV export
    status: pending
  - id: test
    content: Comprehensive testing (functional, data accuracy, security, performance)
    status: pending
isProject: false
---

# Inventory Balance by Lot Inquiry

## Purpose

Provide warehouse managers, stock controllers, and inventory planners with a dedicated read-only inquiry screen to query current on-hand stock positions at the lot level. This replaces ad-hoc queries and spreadsheet exports with a real-time, filterable view into `IvBalLoc`.

**Important Scope Note:** This page displays *current* on-hand balances. It does NOT provide historical transaction movement analysis unless Phase 0 confirms `TransDate` represents last movement date.

---

## ⚠️ HARD RULE FOR COPILOT/AI AGENT

**Do NOT invent missing ERP behavior or infrastructure APIs.**

Complete Phase 0 by inspecting the existing source code, entities, services, database-access patterns, and existing export implementations. Only then replace the Phase 0 placeholders with verified project-specific implementations.

If a required API or pattern does not exist in the project, STOP and ask the user how to proceed. Do NOT assume a library, method, or pattern exists.

---

## ⚠️ PHASE 0: EXISTING ERP VALIDATION (MANDATORY BEFORE IMPLEMENTATION)

**AI MUST complete ALL Phase 0 validations before creating any code.**

### 0.1 Inspect IvBalLoc Entity Structure

Verify the following fields and their business meaning:

| Field | Verify |
|-------|--------|
| `Id` | Primary key type and generation |
| `CompanyCode` | Company isolation scope |
| `BranchCode` | Branch isolation scope |
| `ICode` | Item code relationship to IvStockMaster |
| `WhCode` | Warehouse code relationship to IvWarehouse |
| `LocCode` | Location code relationship to IvLocation |
| `LocationCode` | **VERIFY:** Does this field exist? Is it duplicate of LocCode? If duplicate, standardize on LocCode and remove from plan |
| `LotId` | Lot ID relationship to IvLot |
| `LotNo` | Lot number (denormalized or lookup?) |
| `IStatus` | Inventory status meaning and codes |
| `StdQty` | Standard quantity behavior |
| `StdUom` | Standard UOM source |
| `TransDate` | **VERIFY BUSINESS MEANING** - Is this last movement date? Or creation/update date? |
| `Cost` | **VERIFY** - What cost? Standard? Actual? Weighted average? |
| `UnitPrice` | **VERIFY** - Selling price? List price? |
| `PoNo` | PO reference meaning |
| `RefNo` | Reference number meaning |
| `Remarks` | Remarks content |

### 0.2 Verify Related Table Company/Branch Scope AND JOIN Cardinality

Check actual entity definitions and document scope:

```
IvStockMaster:
    CompanyCode: [YES/NO]
    BranchCode: [YES/NO]
    JOIN: [code only / company / company+branch]
    Cardinality: [one-to-one / one-to-many] ← MUST BE one-to-one or one-to-zero

IvWarehouse:
    CompanyCode: [YES/NO]
    BranchCode: [YES/NO]
    JOIN: [code only / company / company+branch]
    Cardinality: [one-to-one / one-to-many] ← MUST BE one-to-one or one-to-zero

IvLocation:
    CompanyCode: [YES/NO]
    BranchCode: [YES/NO]
    JOIN: [code only / company / company+branch]
    Cardinality: [one-to-one / one-to-many] ← MUST BE one-to-one or one-to-zero

IvStatus:
    CompanyCode: [YES/NO]
    BranchCode: [YES/NO]
    JOIN: [code only / company / company+branch]
    Cardinality: [one-to-one / one-to-many] ← MUST BE one-to-one or one-to-zero

IvLot:
    CompanyCode: [YES/NO]
    BranchCode: [YES/NO]
    JOIN: [code only / company / company+branch]
    Cardinality: [one-to-one / one-to-many] ← MUST BE one-to-one or one-to-zero
```

**CRITICAL:** Every JOIN must be one-to-zero/one from IvBalLoc. If any lookup can produce multiple rows, COUNT(*) and SUM() will over-count. Fix the JOIN before proceeding.

### 0.3 Verify Inventory Posting Logic

Inspect existing inventory posting services to determine:

1. **How IvBalLoc records are created**
   - On goods receipt? On opening balance?

2. **How IvBalLoc records are updated**
   - On every transaction? On batch posting only?

3. **Zero-quantity behavior** (CRITICAL)
   - Are zero-quantity rows deleted from IvBalLoc?
   - Are zero-quantity rows retained but marked inactive?
   - Does "zero stock" mean StdQty = 0, or does it mean no row exists?
   - Can StdQty go negative? If yes, is this valid or a data error?

4. **TransDate meaning** (CRITICAL)
   - Is it updated on every transaction?
   - Is it the creation date?
   - Is it the last physical movement date?

5. **Cost population logic**
   - How is IvBalLoc.Cost populated?
   - Is it standard cost, actual cost, or weighted average?
   - Is it updated on every receipt or fixed at entry?

6. **Inventory value calculation** (CRITICAL)
   - How does the existing ERP calculate inventory value?
   - Is it Cost × Qty? Or some other formula?
   - Does it use UnitPrice at all for valuation?
   - **DOCUMENT THE EXACT FORMULA**

7. **Available quantity**
   - Is Available Qty = StdQty? Or StdQty minus reservations?
   - Does the ERP have reservation/allocation logic?

### 0.4 Inspect Existing Inquiry Pages and Infrastructure

Review these files for patterns to reuse:

- `ErpWeb.UI/Sales/PriceInquiry.razor` - Inquiry page pattern
- `ErpWeb.UI/Inventory/Transactions/IvGoodsReceiptList.razor` - List page pattern
- `ErpWeb.UI/Inventory/Masters/IvStockMasterList.razor` - Master list pattern
- `ErpWeb.UI/Inventory/Lookups/IvBalLocSearchPopup.razor` - Existing balance lookup

**Also inspect:**
- Database access pattern (Dapper? EF Core? What connection type?)
- Existing export functionality (CsvWriter? StreamWriter? What pattern?)
- Existing business-date provider (ICompanyContext? IBusinessDateProvider?)
- Number format conventions (decimal places for qty, cost, price)
- Existing streaming or batch-read patterns

### 0.5 Confirm Business Definitions

**DO NOT IMPLEMENT** until these are verified:

| Term | Verification Required |
|------|----------------------|
| `Cost` | What cost method? Standard/Actual/Weighted? |
| `Inventory Value` | **EXACT FORMULA:** [document here] |
| `TransDate` | What date does it represent? Last movement? Creation? |
| `ExpiryDate` | Source: IvLot table? Manual entry? |
| `Zero Stock` | StdQty = 0? Or row doesn't exist? Can rows be deleted? |
| `Lot` | Lot tracking mandatory? Optional per item? |
| `Available Qty` | Same as StdQty? Or minus reservations? |
| `UOM Precision` | Decimal places for qty? For cost? For price? |
| `Negative Qty` | Allowed? Invalid? Data error indicator? |
| `Business Date` | Server date? ERP config? Accounting period? |

---

## Architecture: Shared Filter Definition (NOT CTE Reuse)

### Critical Design Rule

**A CTE exists only for the single statement immediately following it.**

Therefore, the grid query and summary query CANNOT share a CTE.

### Correct Architecture

```
IvBalanceLotQueryBuilder (C# class)
        │
        ├── BuildJoinClause()      ← Shared by all queries
        ├── BuildWhereClause()     ← Shared by all queries
        ├── BuildCostValueExpression() ← Single source of truth for valuation
        │
        ├── BuildGridSql()         ← Full SQL: SELECT + JOIN + WHERE + ORDER BY + OFFSET
        ├── BuildCountSql()        ← COUNT(*) with same JOIN + WHERE
        └── BuildSummarySql()      ← SUM/AGG with same JOIN + WHERE
```

All three SQL statements use the **same filter logic** (same JOINs, same WHERE clauses), but they are **separate SQL statements**.

```csharp
public class IvBalanceLotQueryBuilder
{
    private readonly IvBalanceLotFilter _filter;
    private readonly string _companyCode;
    private readonly string _branchCode;
    private readonly DateTime _businessDate;
    
    // Phase 0: These JOIN clauses are built based on actual schema
    // CRITICAL: Verify each table's company/branch scope AND cardinality
    private string BuildJoinClause()
    {
        // Phase 0: Inspect actual entities and construct JOINs accordingly
        // Example (verify against actual schema):
        //
        // LEFT JOIN IvStockMaster sm 
        //     ON bl.ICode = sm.ICode
        //     [AND sm.CompanyCode = bl.CompanyCode] ← only if table has this column
        //     [AND sm.BranchCode = bl.BranchCode]   ← only if table has this column
        //
        // Do NOT add company/branch conditions to tables that don't have those columns
        
        throw new InvalidOperationException(
            "JOIN clause must be constructed after Phase 0 schema verification.");
    }
    
    private string BuildWhereClause()
    {
        // Phase 0: Construct WHERE based on verified schema
        // Use @businessDate parameter for expiry comparison
        
        throw new InvalidOperationException(
            "WHERE clause must be constructed after Phase 0 verification.");
    }
    
    // SINGLE SOURCE OF TRUTH for inventory value calculation
    // Phase 0: Verify the exact ERP valuation formula
    private string BuildCostValueExpression()
    {
        // Phase 0: Return the VERIFIED formula, e.g.:
        // return "(COALESCE(bl.Cost, 0) * bl.StdQty)";
        // OR
        // return "[ERP_SPECIFIC_FORMULA]";
        
        throw new InvalidOperationException(
            "Inventory valuation formula must be confirmed during Phase 0. " +
            "Inspect existing ERP reports and posting logic to determine the exact formula.");
    }
    
    // Phase 0: SELECT columns depend on actual schema
    private string BuildSelectColumns()
    {
        // Phase 0: Return SELECT list based on verified fields
        // Include or exclude LocationCode based on Phase 0 findings
        
        throw new InvalidOperationException(
            "SELECT columns must be constructed after Phase 0 schema verification.");
    }
    
    public string BuildGridQuery(string sortField, bool sortDescending, int skip, int take)
    {
        var orderBy = BuildOrderByClause(sortField, sortDescending);
        var costValue = BuildCostValueExpression();
        
        return $@"
            {BuildSelectColumns()},
            {costValue} AS CostValue
            FROM IvBalLoc bl
            {BuildJoinClause()}
            {BuildWhereClause()}
            {orderBy}
            OFFSET @skip ROWS FETCH NEXT @take ROWS ONLY";
    }
    
    public string BuildCountQuery()
    {
        return $@"
            SELECT COUNT(*)
            FROM IvBalLoc bl
            {BuildJoinClause()}
            {BuildWhereClause()}";
    }
    
    public string BuildSummaryQuery()
    {
        var costValue = BuildCostValueExpression();
        
        return $@"
            SELECT 
                COUNT(*) AS TotalRows,
                COALESCE(SUM(bl.StdQty), 0) AS TotalQty,
                COALESCE(SUM({costValue}), 0) AS TotalCostValue,
                COALESCE(SUM(CASE WHEN bl.StdQty = 0 THEN 1 ELSE 0 END), 0) AS ZeroQtyRowCount,
                COALESCE(SUM(CASE
                    WHEN lot.ExpiryDate IS NOT NULL AND lot.ExpiryDate < @businessDate
                    THEN 1 ELSE 0
                END), 0) AS ExpiredCount
            FROM IvBalLoc bl
            {BuildJoinClause()}
            {BuildWhereClause()}";
    }
    
    // All queries use the same parameters
    public object BuildParameters()
    {
        return new
        {
            companyCode = _companyCode,
            branchCode = _branchCode,
            businessDate = _businessDate,
            iCode = _filter.ICode,
            lotNo = _filter.LotNo,
            whCode = _filter.WhCode,
            locCode = _filter.LocCode,
            iStatus = _filter.IStatus,
            includeZeroQty = _filter.IncludeZeroQty ? 1 : 0,
            onlyExpired = _filter.OnlyExpired ? 1 : 0,
            expiryBefore = _filter.ExpiryBefore,
            transDateFrom = _filter.TransDateFrom,
            transDateTo = _filter.TransDateTo,
            minQty = _filter.MinQty,
            maxQty = _filter.MaxQty,
            searchText = _filter.SearchText
        };
    }
}
```

### ORDER BY: Map to Output Column Names (NOT Table Aliases)

**Critical:** The ORDER BY clause references columns in the SELECT list, not table aliases.

```csharp
private string BuildOrderByClause(string? sortField, bool sortDescending)
{
    // Map from sort field name to OUTPUT column name
    var column = sortField?.ToUpperInvariant() switch
    {
        "ICODE"      => "ICode",
        "IDESC"      => "IDesc",
        "WHCODE"     => "WhCode",
        "LOCCODE"    => "LocCode",
        "LOTNO"      => "LotNo",
        "STDQTY"     => "StdQty",
        "EXPIRYDATE" => "ExpiryDate",
        "TRANSDATE"  => "TransDate",
        "COST"       => "Cost",
        "COSTVALUE"  => "CostValue",
        _            => null
    };
    
    // Default sort if invalid or not specified
    if (column is null)
        return "ORDER BY ICode, WhCode, LocCode, LotNo";
    
    var direction = sortDescending ? "DESC" : "ASC";
    return $"ORDER BY {column} {direction}, ICode, WhCode, LocCode, LotNo";
}
```

**Result SQL (correct):**
```sql
SELECT 
    bl.ICode AS ICode,
    sm.IDesc AS IDesc,
    ...
    (COALESCE(bl.Cost, 0) * bl.StdQty) AS CostValue  -- From BuildCostValueExpression()
FROM IvBalLoc bl
...
ORDER BY ICode ASC, WhCode, LocCode, LotNo  -- Uses OUTPUT column names
OFFSET 0 ROWS FETCH NEXT 50 ROWS ONLY;
```

---

## Data Model (REVISED)

### IvBalanceLotViewRow

```csharp
public class IvBalanceLotViewRow
{
    public int Id { get; set; }
    public string ICode { get; set; } = string.Empty;
    public string? IDesc { get; set; }
    public string? IBarcode { get; set; }
    public string WhCode { get; set; } = string.Empty;
    public string? WhDesc { get; set; }
    public string LocCode { get; set; } = string.Empty;
    public string? LocDesc { get; set; }
    public string LotNo { get; set; } = string.Empty;
    public string IStatus { get; set; } = string.Empty;
    public string? IStatusDesc { get; set; }
    public decimal StdQty { get; set; }
    public string? StdUom { get; set; }
    
    // Phase 0: Label depends on verified meaning
    // If TransDate = last movement date → "Last Move"
    // If TransDate = creation date → "Created"
    // If TransDate = update date → "Updated"
    public DateTime? TransDate { get; set; }
    
    public DateTime? ExpiryDate { get; set; }
    public string? PoNo { get; set; }
    public string? RefNo { get; set; }
    public string? Remarks { get; set; }
    
    // Cost and price are separate concepts
    public decimal? Cost { get; set; }
    public decimal? UnitPrice { get; set; }
    
    // Phase 0: Value calculation depends on ERP valuation method
    // This is populated by SQL using BuildCostValueExpression()
    public decimal CostValue { get; set; }
    
    // NOTE: IsExpired is NOT in the model
    // Expiry status is calculated in the UI using business date from service
    
    public string? CompanyCode { get; set; }
    public string? BranchCode { get; set; }
}
```

### Filter Model

```csharp
public class IvBalanceLotFilter
{
    public string? ICode { get; set; }
    public string? LotNo { get; set; }
    public string? WhCode { get; set; }
    public string? LocCode { get; set; }
    public string? IStatus { get; set; }
    
    // Phase 0: If zero rows are deleted, this option is meaningless
    // Keep for now; remove if Phase 0 confirms deletion behavior
    public bool IncludeZeroQty { get; set; } = false;
    
    public bool OnlyExpired { get; set; } = false;
    public DateTime? ExpiryBefore { get; set; }
    public DateTime? TransDateFrom { get; set; }
    public DateTime? TransDateTo { get; set; }
    public decimal? MinQty { get; set; }
    public decimal? MaxQty { get; set; }
    public string? SearchText { get; set; }
    
    // Server-side sorting
    public string? SortField { get; set; }
    public bool SortDescending { get; set; } = false;
}
```

### Summary Model

```csharp
public class IvBalanceLotSummary
{
    public int TotalRows { get; set; }
    public decimal TotalQty { get; set; }
    public decimal TotalCostValue { get; set; }
    public int ZeroQtyRowCount { get; set; }
    public int ExpiredCount { get; set; }
    
    // Phase 0: If UOM aggregation is meaningless, display as "Total Lines" instead
    // Different items may have different UOMs, so sum of qty may not be meaningful
}
```

### Sort Fields (Whitelist)

```csharp
public static class IvBalanceLotSortFields
{
    public const string ICode = "ICode";
    public const string IDesc = "IDesc";
    public const string WhCode = "WhCode";
    public const string LocCode = "LocCode";
    public const string LotNo = "LotNo";
    public const string StdQty = "StdQty";
    public const string ExpiryDate = "ExpiryDate";
    public const string TransDate = "TransDate";
    public const string Cost = "Cost";
    public const string CostValue = "CostValue";
    
    public static readonly IReadOnlyList<string> All = 
    [
        ICode, IDesc, WhCode, LocCode, LotNo, 
        StdQty, ExpiryDate, TransDate, Cost, CostValue
    ];
    
    public static bool IsValid(string? field) => 
        field is not null && All.Contains(field, StringComparer.OrdinalIgnoreCase);
}
```

---

## UI Layout

### Page Structure

```
┌──────────────────────────────────────────────────────────────────────────────┐
│ [Hero Section]                                                               │
│   Icon: fa-solid fa-boxes-stacked                                            │
│   Eyebrow: Inventory                                                         │
│   Title: Balance by Lot                                                      │
│   Chips: Total Rows | Filtered                                               │
│   KPI: Total Qty | Total Cost Value                                          │
├──────────────────────────────────────────────────────────────────────────────┤
│ [Toolbar]                                                                    │
│   [Search Box: Item code, lot, warehouse, location...]       [FILTER] [EXPORT]│
│   Placeholder explicitly states prefix search semantics for codes            │
├──────────────────────────────────────────────────────────────────────────────┤
│ [Summary Cards]                                                              │
│   ┌─────────────┐ ┌─────────────┐ ┌─────────────┐ ┌─────────────┐          │
│   │ Total Rows  │ │ Total Qty   │ │ Cost Value  │ │ Zero Qty    │          │
│   │    1,234    │ │   45,678    │ │ $1,234,567  │ │     23      │          │
│   └─────────────┘ └─────────────┘ └─────────────┘ └─────────────┘          │
│   NOTE: Total Qty may not be meaningful if items have different UOMs        │
├──────────────────────────────────────────────────────────────────────────────┤
│ [Data Grid]                                                                  │
│                                                                              │
│  DEFAULT COLUMNS (10):                                                       │
│   - Item Code (frozen)                                                       │
│   - Description                                                              │
│   - Warehouse                                                                │
│   - Location                                                                 │
│   - Lot No                                                                   │
│   - Status                                                                   │
│   - Qty                                                                      │
│   - UOM                                                                      │
│   - Expiry Date (highlight if expired using business date from service)     │
│   - Cost Value                                                               │
│                                                                              │
│  OPTIONAL COLUMNS (via column chooser):                                      │
│   - Trans Date [label TBD: Last Move / Created / Updated]                   │
│   - PO No                                                                    │
│   - Cost (unit)                                                              │
│   - Unit Price                                                               │
│   - Remarks                                                                  │
│                                                                              │
│  REMOVED: High-value highlighting (no business threshold defined)           │
├──────────────────────────────────────────────────────────────────────────────┤
│ [Footer]                                                                     │
│   Page info | Row count | Navigation                                         │
└──────────────────────────────────────────────────────────────────────────────┘
```

### Search Placeholder (Document Semantics)

```razor
<DxTextBox NullText="Search item code, lot, warehouse, location (prefix match)..."
           ... />
```

This explicitly tells users that code searches are prefix-based.

### Filter Popup

```
┌─────────────────────────────────────────────────────────────┐
│ Filter Balance by Lot                                        │
├─────────────────────────────────────────────────────────────┤
│                                                              │
│  Item Code              Lot No                               │
│  ┌──────────────────┐   ┌──────────────────┐                │
│  │ [IvStockMaster]  │   │ [Text input]     │                │
│  └──────────────────┘   └──────────────────┘                │
│                                                              │
│  Warehouse              Location                             │
│  ┌──────────────────┐   ┌──────────────────┐                │
│  │ [IvWarehouse]    │   │ [IvLocation]     │                │
│  └──────────────────┘   └──────────────────┘                │
│                                                              │
│  Status                 Include Zero Qty                     │
│  ┌──────────────────┐   ┌──────────────────┐                │
│  │ [IvStatus]       │   │ [☐ Checkbox]     │                │
│  └──────────────────┘   └──────────────────┘                │
│  NOTE: Remove IncludeZeroQty if Phase 0 confirms rows are   │
│  deleted when qty reaches zero                               │
│                                                              │
│  ─── Advanced Filters ───                                    │
│                                                              │
│  Expiry Date Range      Transaction Date Range               │
│  ┌──────────────────┐   ┌──────────────────┐                │
│  │ [Before date]    │   │ [From] [To]      │                │
│  └──────────────────┘   └──────────────────┘                │
│                                                              │
│  Quantity Range         Show Only Expired                    │
│  ┌──────────────────┐   ┌──────────────────┐                │
│  │ [Min]    [Max]   │   │ [☐ Checkbox]     │                │
│  └──────────────────┘   └──────────────────┘                │
│                                                              │
├─────────────────────────────────────────────────────────────┤
│  [Clear]                                    [Apply]          │
└─────────────────────────────────────────────────────────────┘
```

---

## Theme Integration

### Design Tokens (from site.css)

| Token | Light | Dark | Usage |
|-------|-------|------|-------|
| `--bg` | #edf0f3 | #0f141c | Page background |
| `--surface` | #ffffff | #1a2332 | Card/panel background |
| `--text` | #1c2430 | #e8edf4 | Primary text |
| `--muted` | #667085 | #9aa4b2 | Secondary text |
| `--line` | #d7dde5 | #2d3a4f | Borders |
| `--accent` | #2f6fed | #5b8cff | Primary actions |
| `--navy` | #1a2332 | #0b1018 | Hero background |

### Component Classes (follow existing patterns)

- `.iv-page` - Page container
- `.iv-hero` - Header section with icon, title, KPIs
- `.iv-card` - Content card with surface background
- `.iv-toolbar-row` - Search and action buttons
- `.iv-toast` - Status/error notifications
- `.iv-chip` - Information badges
- `.iv-status` - Status indicators

### Grid Styling

- Frozen first column (Item Code) for horizontal scrolling
- Conditional row highlighting:
  - `.row-expired` - Warning background for expired lots (calculated in UI using business date)
  - `.row-zero` - Muted text for zero quantity
- Number formatting: **Reuse existing ERP conventions** (Phase 0 to verify)

---

## Service Layer

### Service Interface

```csharp
public interface IIvBalanceLotViewService
{
    Task<PagedResult<IvBalanceLotViewRow>> SearchAsync(
        string companyCode,
        string branchCode,
        IvBalanceLotFilter filter,
        int skip,
        int take,
        CancellationToken ct = default);

    Task<IvBalanceLotSummary> GetSummaryAsync(
        string companyCode,
        string branchCode,
        IvBalanceLotFilter filter,
        CancellationToken ct = default);
}
```

### Business Date Provider

**Phase 0:** Inspect the project for an existing business-date provider:
- `ICompanyContext`
- `IBusinessDateProvider`
- `IAccountingPeriodService`
- Or similar

If found, reuse it. If not found, create a simple provider:

```csharp
public interface IBusinessDateProvider
{
    DateTime GetBusinessDate();
}

// Phase 0: Determine the actual source
// Options:
// 1. Server date (DateTime.Today)
// 2. ERP configuration
// 3. Accounting period
public class ServerBusinessDateProvider : IBusinessDateProvider
{
    public DateTime GetBusinessDate() => DateTime.Today;
}
```

**Do NOT hardcode DateTime.Today in the view service.** Business-date policy should be centralized.

### Service Implementation Pattern

```csharp
public class IvBalanceLotViewService : IIvBalanceLotViewService
{
    // Phase 0: Use the project's actual database access pattern
    // If project uses Dapper: IDbConnection + Dapper
    // If project uses EF Core: DbContext
    // Do NOT introduce a second data-access stack
    
    private readonly IDbConnection _db; // Phase 0: Verify actual type
    private readonly IBusinessDateProvider _businessDateProvider;
    
    public async Task<PagedResult<IvBalanceLotViewRow>> SearchAsync(
        string companyCode,
        string branchCode,
        IvBalanceLotFilter filter,
        int skip,
        int take,
        CancellationToken ct = default)
    {
        var businessDate = _businessDateProvider.GetBusinessDate();
        
        var queryBuilder = new IvBalanceLotQueryBuilder(
            filter, companyCode, branchCode, businessDate);
        
        // Grid query with sorting and pagination
        var gridSql = queryBuilder.BuildGridQuery(
            filter.SortField, 
            filter.SortDescending, 
            skip, 
            take);
        
        // Count query (same filters, no sorting/pagination)
        var countSql = queryBuilder.BuildCountQuery();
        
        var parameters = queryBuilder.BuildParameters();
        
        // Phase 0: Use actual project's database access pattern
        var items = await _db.QueryAsync<IvBalanceLotViewRow>(gridSql, parameters);
        var totalCount = await _db.ExecuteScalarAsync<int>(countSql, parameters);
        
        return new PagedResult<IvBalanceLotViewRow>
        {
            Items = items.ToList(),
            TotalCount = totalCount
        };
    }
    
    public async Task<IvBalanceLotSummary> GetSummaryAsync(
        string companyCode,
        string branchCode,
        IvBalanceLotFilter filter,
        CancellationToken ct = default)
    {
        var businessDate = _businessDateProvider.GetBusinessDate();
        
        var queryBuilder = new IvBalanceLotQueryBuilder(
            filter, companyCode, branchCode, businessDate);
        
        var sql = queryBuilder.BuildSummaryQuery();
        var parameters = queryBuilder.BuildParameters();
        
        // SQL uses COALESCE, so result is never null
        return await _db.QuerySingleAsync<IvBalanceLotSummary>(sql, parameters);
    }
}
```

### Export Service

**Phase 0:** Inspect existing project export patterns before implementing:
- Search for `CsvWriter`, `ExportToCsv`, `StreamWriter`, file download patterns
- Reuse the project's existing approach

```csharp
public interface IIvBalanceLotExportService
{
    // Streams all filtered rows
    // Does NOT load all rows into memory
    Task ExportAsync(
        string companyCode,
        string branchCode,
        IvBalanceLotFilter filter,
        Stream outputStream,
        CancellationToken ct = default);
    
    // Returns count for limit checking
    Task<int> GetExportCountAsync(
        string companyCode,
        string branchCode,
        IvBalanceLotFilter filter,
        CancellationToken ct = default);
}

public class IvBalanceLotExportService : IIvBalanceLotExportService
{
    // Phase 0: Use actual project's database access pattern
    private readonly IDbConnection _db;
    private readonly IBusinessDateProvider _businessDateProvider;
    
    // Export volume limit
    private const int MaxExportRows = 100_000;
    
    public async Task<int> GetExportCountAsync(
        string companyCode,
        string branchCode,
        IvBalanceLotFilter filter,
        CancellationToken ct = default)
    {
        var businessDate = _businessDateProvider.GetBusinessDate();
        var queryBuilder = new IvBalanceLotQueryBuilder(
            filter, companyCode, branchCode, businessDate);
        
        var sql = queryBuilder.BuildCountQuery();
        var parameters = queryBuilder.BuildParameters();
        
        return await _db.ExecuteScalarAsync<int>(sql, parameters);
    }
    
    public async Task ExportAsync(
        string companyCode,
        string branchCode,
        IvBalanceLotFilter filter,
        Stream outputStream,
        CancellationToken ct = default)
    {
        // Check limit BEFORE starting export
        var count = await GetExportCountAsync(companyCode, branchCode, filter, ct);
        
        if (count > MaxExportRows)
        {
            throw new ExportLimitExceededException(
                $"Export contains {count:N0} rows, which exceeds the limit of {MaxExportRows:N0}. " +
                "Please narrow your filters to reduce the result set.");
        }
        
        var businessDate = _businessDateProvider.GetBusinessDate();
        var queryBuilder = new IvBalanceLotQueryBuilder(
            filter, companyCode, branchCode, businessDate);
        
        // Export query: same filter, same sort, no pagination
        var sql = queryBuilder.BuildExportQuery(); // No OFFSET/FETCH
        var parameters = queryBuilder.BuildParameters();
        
        // Phase 0: Use actual project's export pattern
        // If project uses CsvWriter: use CsvWriter
        // If project uses StreamWriter: use StreamWriter
        // Do NOT invent a new export API
        
        // Example pattern (verify against project):
        await using var writer = new StreamWriter(outputStream, leaveOpen: true);
        
        // Write header
        await writer.WriteLineAsync("Item Code,Description,Warehouse,Location,Lot,Status,Qty,UOM,Expiry,Cost Value");
        
        // Stream rows in batches
        // Phase 0: Verify if _db supports streaming or use batched reads
        var rows = await _db.QueryAsync<IvBalanceLotViewRow>(sql, parameters);
        
        foreach (var row in rows)
        {
            ct.ThrowIfCancellationRequested();
            
            var line = string.Join(",",
                EscapeCsv(row.ICode),
                EscapeCsv(row.IDesc),
                EscapeCsv(row.WhCode),
                EscapeCsv(row.LocCode),
                EscapeCsv(row.LotNo),
                EscapeCsv(row.IStatus),
                row.StdQty.ToString("N2"),
                EscapeCsv(row.StdUom),
                row.ExpiryDate?.ToString("dd/MM/yyyy") ?? "",
                row.CostValue.ToString("N2"));
            
            await writer.WriteLineAsync(line);
        }
        
        await writer.FlushAsync(ct);
    }
    
    private static string EscapeCsv(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n'))
            return $"\"{value.Replace("\"", "\"\"")}\"";
        return value;
    }
}

public class ExportLimitExceededException : Exception
{
    public ExportLimitExceededException(string message) : base(message) { }
}
```

---

## Menu & Route

### Menu Code
```csharp
// MenuCodes.cs
public const string InventoryBalanceLotView = "INV_BAL_LOT_VIEW";
```

### Route
```
/inventory/balance-lot-view
```

### Menu XML
```xml
<Menu MenuCode="INV_BAL_LOT_VIEW"
      Description="Balance by Lot"
      ParentMenuCode="INV_INQUIRY"
      Url="/inventory/balance-lot-view"
      SortOrder="10"
      IsActive="true" />
```

---

## Responsive Design

### Desktop (>1200px)
- Full grid with default columns + column chooser for optional columns
- Summary cards in horizontal row
- Filter popup as modal

### Tablet (768-1200px)
- Grid with horizontal scroll
- Summary cards stacked 2x2
- Filter popup as modal

### Mobile (<768px)
- Compact card list view (similar to IvStockMasterList)
- Summary cards stacked vertically
- Filter as bottom sheet

---

## Accessibility

- ARIA labels on all interactive elements
- Keyboard navigation for grid
- Screen reader announcements for filter changes
- High contrast support for status indicators
- Focus management in filter popup

---

## Performance Considerations

- Server-side pagination (default 50 rows per page)
- Debounced search input (300ms)
- Cached lookup data (warehouses, locations, statuses)
- Indexed queries on IvBalLoc (CompanyCode, BranchCode, ICode, WhCode)
- Summary uses separate SQL with same filters (not CTE reuse)
- Prefix searches for codes, contains for descriptions
- Server-side streaming for export (no browser memory overload)
- **Performance Review Item:** Consider generating only active predicates instead of `(@param IS NULL OR ...)` patterns after initial testing

---

## Future Enhancements (Out of Scope V1)

- Real-time stock updates via SignalR
- Drill-down to transaction history
- Stock aging analysis (requires TransDate = last movement confirmation)
- Batch print/export
- Saved filter presets
- Dashboard widgets
- Stock alerts (low stock, expiry warnings)
- High-value highlighting (requires business threshold definition)
- Available quantity (requires reservation/allocation logic)

---

## Implementation Phases

### Phase 0: ERP Validation (MANDATORY)
**Duration:** 1-2 hours  
**Gate:** Must complete ALL items before Phase 1

- [ ] 0.1 Inspect IvBalLoc entity and all field meanings
- [ ] 0.2 Verify related table company/branch scoping AND JOIN cardinality
- [ ] 0.3 Inspect inventory posting logic for:
  - [ ] How IvBalLoc records are created/updated
  - [ ] Zero-quantity behavior (delete? retain? deactivate?)
  - [ ] TransDate business meaning (last move? creation? update?)
  - [ ] Cost population logic
  - [ ] **EXACT inventory value formula**
  - [ ] Available quantity logic (if reservation exists)
  - [ ] Negative quantity behavior
- [ ] 0.4 Review existing inquiry page patterns
- [ ] 0.5 Document all business definitions
- [ ] 0.6 Document number format conventions (qty/cost/price decimals)
- [ ] 0.7 **Inspect database access pattern** (Dapper? EF Core? Connection type?)
- [ ] 0.8 **Inspect existing export functionality** (CsvWriter? StreamWriter? Pattern?)
- [ ] 0.9 **Inspect existing business-date provider** (ICompanyContext? IBusinessDateProvider?)
- [ ] **GATE:** All items verified before proceeding

### Phase 1: Model Layer
**Duration:** 30 minutes

- [ ] 1.1 Create `IvBalanceLotViewRow.cs` (with verified field meanings)
- [ ] 1.2 Create `IvBalanceLotFilter.cs`
- [ ] 1.3 Create `IvBalanceLotSummary.cs`
- [ ] 1.4 Create `IvBalanceLotSortFields.cs`

### Phase 2: Service Layer
**Duration:** 1-2 hours

- [ ] 2.1 Create `IvBalanceLotQueryBuilder.cs` (shared filter logic)
  - [ ] Implement `BuildJoinClause()` using verified schema
  - [ ] Implement `BuildWhereClause()` using verified schema
  - [ ] Implement `BuildCostValueExpression()` using verified formula
  - [ ] Implement `BuildSelectColumns()` using verified fields
  - [ ] Implement `BuildGridQuery()` with safe sorting
  - [ ] Implement `BuildCountQuery()` with same filters
  - [ ] Implement `BuildSummaryQuery()` with same filters
- [ ] 2.2 Create `IBusinessDateProvider.cs` (or reuse existing)
- [ ] 2.3 Create `IIvBalanceLotViewService.cs`
- [ ] 2.4 Create `IvBalanceLotViewService.cs`
- [ ] 2.5 Register services in `CoreServiceCollectionExtensions.cs`

### Phase 3: UI Layer
**Duration:** 2-3 hours

- [ ] 3.1 Create `IvBalanceLotView.razor` in `ErpWeb.UI/Inventory/Inquiry/`
- [ ] 3.2 Create `IvBalanceLotView.razor.cs` code-behind
- [ ] 3.3 Create `IvBalanceLotView.razor.css` component styles
- [ ] 3.4 Create `_Imports.razor` if new folder
- [ ] 3.5 Implement default columns (10) and optional columns (5)
- [ ] 3.6 Implement expiry highlighting using business date from provider

### Phase 4: Menu & Navigation
**Duration:** 15 minutes

- [ ] 4.1 Add `InventoryBalanceLotView` to `MenuCodes.cs`
- [ ] 4.2 Add menu entry to `menus.xml`

### Phase 5: Export Functionality
**Duration:** 30 minutes

- [ ] 5.1 Create `IIvBalanceLotExportService.cs`
- [ ] 5.2 Create `IvBalanceLotExportService.cs` (reusing project's export pattern)
- [ ] 5.3 Implement limit check before export
- [ ] 5.4 Test export with various filter combinations
- [ ] 5.5 Test export limit exceeded behavior

### Phase 6: Testing (COMPREHENSIVE)
**Duration:** 2-3 hours

#### 6.1 Data Correctness Tests
- [ ] Verify quantity matches existing inventory balance reports
- [ ] Verify lot matches source records
- [ ] Verify warehouse/location accuracy
- [ ] Verify expiry date source (IvLot table)
- [ ] Verify cost values against item master
- [ ] Verify cost value calculation matches verified formula
- [ ] Verify company/branch isolation

#### 6.2 Filter Correctness Tests
- [ ] Test single filter: Item
- [ ] Test single filter: Warehouse
- [ ] Test single filter: Lot
- [ ] Test combination: Item + Warehouse
- [ ] Test combination: Item + Lot
- [ ] Test combination: Warehouse + Location
- [ ] Test filter: Status
- [ ] Test filter: Expiry date range
- [ ] Test filter: Transaction date range
- [ ] Test filter: Quantity range
- [ ] Test global search functionality

#### 6.3 Boundary Tests
- [ ] Test Qty = 0 (with IncludeZeroQty = true)
- [ ] Test Qty < 0 (document behavior based on Phase 0)
- [ ] Test Qty > 0
- [ ] Test Expiry = today (should not be expired)
- [ ] Test Expiry < today (should be expired)
- [ ] Test Expiry = null (no expiry tracking)
- [ ] Test TransDate = from date (should include)
- [ ] Test TransDate = to date (should include)
- [ ] Test TransDate = to date + 1 day (should exclude)

#### 6.4 Security / Isolation Tests
- [ ] Verify Company A cannot see Company B data
- [ ] Verify Branch A cannot see Branch B data (if branch-scoped)
- [ ] Verify unauthorized users cannot access page

#### 6.5 Summary Consistency Tests
- [ ] Verify grid filtered rows = summary filtered dataset
- [ ] Verify KPI values match same filter conditions
- [ ] Verify summary updates when filters change
- [ ] Verify COALESCE handles null costs correctly

#### 6.6 Performance Tests
- [ ] Test with 10,000 rows
- [ ] Test with 100,000 rows
- [ ] Test with 1,000,000+ rows (if applicable)
- [ ] Verify pagination performance
- [ ] Verify sort performance
- [ ] Verify search performance

#### 6.7 Export Tests
- [ ] Test export with small result set
- [ ] Test export with MaxExportRows limit
- [ ] Test export exceeds limit (should show error before streaming)
- [ ] Test export cancellation

#### 6.8 UI Tests
- [ ] Test responsive layouts (desktop, tablet, mobile)
- [ ] Test dark/light mode
- [ ] Test keyboard navigation
- [ ] Test screen reader compatibility
- [ ] Test column chooser functionality
- [ ] Test export button behavior

---

## Approval Criteria (BEFORE IMPLEMENTATION)

**Plan is APPROVED for implementation only when ALL of these are confirmed:**

### Data Model Verification
- [ ] IvBalLoc structure verified
- [ ] IvLot relationship verified
- [ ] Company/Branch isolation verified for all JOINs
- [ ] **JOIN cardinality verified (one-to-zero/one for all lookups)**
- [ ] Cost business meaning verified
- [ ] **EXACT inventory value formula documented**
- [ ] TransDate meaning verified
- [ ] Zero-quantity lifecycle verified (delete/retain/deactivate)
- [ ] LocationCode vs LocCode resolved
- [ ] AvailableQty definition resolved
- [ ] Negative quantity behavior documented

### Architecture Verification
- [ ] Shared filter definition implemented (NOT CTE reuse)
- [ ] ORDER BY uses output column names (NOT table aliases)
- [ ] **BuildCountQuery() implemented with same filters**
- [ ] **BuildCostValueExpression() is single source of truth**
- [ ] Server-side sorting implemented with safe whitelist
- [ ] Date boundary behavior defined (< DATEADD for inclusive end dates)
- [ ] SQL search semantics documented (prefix vs contains)
- [ ] **Export limit check before streaming**
- [ ] **Export uses same filter + same sort + no pagination**

### Infrastructure Verification
- [ ] **Database access pattern verified (Dapper/EF Core)**
- [ ] **Existing export pattern verified and reused**
- [ ] **Business date provider verified and reused**
- [ ] **Number format conventions verified**

### Pattern Verification
- [ ] Existing ERP inquiry patterns inspected
- [ ] Company/branch filtering pattern documented
- [ ] Authorization pattern documented
- [ ] Service registration pattern documented

### Testing Coverage
- [ ] Functional test cases defined
- [ ] Multi-company/branch test cases defined
- [ ] Data accuracy test cases defined
- [ ] Performance test cases defined
- [ ] Export test cases defined

---

## Key Decisions

1. **Read-only inquiry** - No edit/delete capabilities; pure viewing tool ✓
2. **Shared filter definition** - Grid and summary use same filter logic via C# builder ✓
3. **Server-side processing** - All filtering, pagination, and sorting on server ✓
4. **Safe sorting** - Whitelist of allowed fields, maps to output column names ✓
5. **Default exclude zero qty** - Users typically want to see active stock; toggle to include ✓
   - **Conditional:** Remove if Phase 0 confirms rows are deleted when qty reaches zero
6. **Summary KPIs** - Real-time totals in hero section for quick overview ✓
   - **Note:** TotalQty may not be meaningful if items have different UOMs
7. **Responsive grid** - Desktop shows full table; mobile shows compact cards ✓
8. **Server-side export with limit check** - Checks count before streaming ✓
9. **Theme consistency** - Uses existing iv-* classes and CSS variables ✓
10. **DevExpress DxGrid** - Consistent with other list pages in project ✓
11. **No high-value highlighting in V1** - Requires business threshold definition ✓
12. **Separate cost and selling values** - Do not mix Cost and UnitPrice ✓
13. **Business date from provider** - Not hardcoded in model or service ✓
14. **Reuse existing infrastructure** - DB access, export, business date ✓

---

## Related Files

### To Create
- `ErpWeb.Model/Entities/Inventory/IvBalanceLotViewRow.cs`
- `ErpWeb.Model/Entities/Inventory/IvBalanceLotFilter.cs`
- `ErpWeb.Model/Entities/Inventory/IvBalanceLotSummary.cs`
- `ErpWeb.Model/Entities/Inventory/IvBalanceLotSortFields.cs`
- `ErpWeb.Core/Inventory/IvBalanceLotQueryBuilder.cs`
- `ErpWeb.Core/Inventory/IBusinessDateProvider.cs` (or reuse existing)
- `ErpWeb.Core/Inventory/IIvBalanceLotViewService.cs`
- `ErpWeb.Core/Inventory/IvBalanceLotViewService.cs`
- `ErpWeb.Core/Inventory/IIvBalanceLotExportService.cs`
- `ErpWeb.Core/Inventory/IvBalanceLotExportService.cs`
- `ErpWeb.UI/Inventory/Inquiry/IvBalanceLotView.razor`
- `ErpWeb.UI/Inventory/Inquiry/IvBalanceLotView.razor.cs`
- `ErpWeb.UI/Inventory/Inquiry/IvBalanceLotView.razor.css`
- `ErpWeb.UI/Inventory/Inquiry/_Imports.razor`

### To Modify
- `ErpWeb.Core/Menus/MenuCodes.cs` - Add menu constant
- `ErpWeb/Menus/menus.xml` - Add menu entry
- `ErpWeb.Core/CoreServiceCollectionExtensions.cs` - Register services

### To Inspect (Phase 0)
- `ErpWeb.Model/Entities/Inventory/IvBalLoc.cs` - Entity definition
- `ErpWeb.Model/Entities/Inventory/IvStockMaster.cs` - Company/branch scope + cardinality
- `ErpWeb.Model/Entities/Inventory/IvWarehouse.cs` - Company/branch scope + cardinality
- `ErpWeb.Model/Entities/Inventory/IvLocation.cs` - Company/branch scope + cardinality
- `ErpWeb.Model/Entities/Inventory/IvLot.cs` - Company/branch scope + cardinality
- `ErpWeb.Core/Inventory/IvInventoryPostingService.cs` - Posting logic
- `ErpWeb.Core/Inventory/IvGoodsReceiptService.cs` - Receipt logic
- `ErpWeb.UI/Inventory/Lookups/IvBalLocSearchPopup.razor` - Existing pattern
- **Existing export implementations** - Search for CsvWriter, ExportToCsv, StreamWriter
- **Existing business-date providers** - Search for ICompanyContext, IBusinessDateProvider

---

## Change Log

| Date | Change | Reason |
|------|--------|--------|
| 2026-01-XX | **Added hard rule for AI agent** | Prevent inventing missing ERP behavior or APIs |
| 2026-01-XX | Removed unsafe default from BuildValueColumn() | Conflicts with Phase 0 gate; should throw until verified |
| 2026-01-XX | Removed unsafe default from GetBusinessDate() | Should use provider; not hardcoded |
| 2026-01-XX | Added BuildCountQuery() | GetTotalCountAsync() was not defined |
| 2026-01-XX | Centralized CostValue expression | Single source of truth via BuildCostValueExpression() |
| 2026-01-XX | Added COALESCE to ZeroQtyRowCount and ExpiredCount | Prevent NULL when no rows |
| 2026-01-XX | Added IBusinessDateProvider | Centralize business-date policy |
| 2026-01-XX | Added JOIN cardinality verification | Prevent duplicate aggregation |
| 2026-01-XX | Added infrastructure verification | DB access, export pattern, business-date provider |
| 2026-01-XX | Added export limit check before streaming | Don't start streaming if limit exceeded |
| 2026-01-XX | Added ExportLimitExceededException | Clear error message for users |
| 2026-01-XX | Added ExportAsync with leaveOpen: true | Prevent StreamWriter from disposing response stream |
| 2026-01-XX | Added negative quantity documentation | Document behavior based on Phase 0 |
| 2026-01-XX | Added export sort definition | Use same filter + same sort + no pagination |
| 2026-01-XX | Added performance review item | Consider active predicates instead of IS NULL patterns |
| 2026-01-XX | Added Phase 0 items 0.7, 0.8, 0.9 | Verify DB access, export pattern, business-date provider |