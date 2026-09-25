# Revised Sales Module Reports Implementation Plan

**Version:** 2.0  
**Date:** 2026-09-26  
**Status:** Revised based on review feedback  
**Approval Status:** PENDING - Requires implementation gates

---

## Executive Summary

This revised plan addresses all critical issues identified in the technical review. The plan now includes:

1. **Mandatory "Inspect Existing System First" rule** - No implementation without verification
2. **Database-level pagination** - UNION ALL approach for Customer Transaction Inquiry
3. **Full-dataset aggregation** - Totals calculated before pagination
4. **Explicit business rules** - Documented for all reports
5. **Complete document flow mapping** - Verified relationships from existing code
6. **AR/payment integration** - Required for Customer Statement
7. **Explicit sales definitions** - Gross vs Net sales clearly defined
8. **Complete KPI specifications** - Business rules for all dashboard KPIs
9. **Security patterns** - Following existing authorization model
10. **Approval gates** - 8-gate implementation process

---

## 1. CRITICAL RULE: Inspect Existing System First

### 1.1 Mandatory Pre-Implementation Inspection

**BEFORE ANY CODING OR IMPLEMENTATION, THE AI AGENT MUST:**

1. **Inspect the existing Sales module**
   - Read all entities in `ErpWeb.Model/Entities/Sales/`
   - Read all repositories in `ErpWeb.Model/Repositories/Sales/`
   - Read all services in `ErpWeb.Core/Sales/`
   - Read all UI pages in `ErpWeb.UI/Sales/`

2. **Inspect existing Sales Analysis services**
   - Read `ISaSalesAnalysisService.cs` and `SaSalesAnalysisService.cs`
   - Understand existing sales summary logic
   - Verify CN/DN treatment (positive stored, netted once)
   - Document existing dimension grouping

3. **Inspect existing document-flow implementation**
   - Read `SaDocFlowQuery.cs` completely
   - Understand `SaDocApplication` table structure
   - Map all document relationships
   - Verify QT→SO conversion logic

4. **Inspect existing authorization/tenant/branch scope**
   - Read `IInventoryTenantContext` implementation
   - Understand `TryBranchScope()` behavior
   - Verify multi-branch support model
   - Document existing permission checks

5. **Inspect existing e-Invoice implementation**
   - Read `EInvDocSubmission.cs` and `SaEInvoiceLog.cs`
   - Understand submission/status tracking
   - Verify all IRBM* fields on invoice entities

6. **Inspect existing AR/payment implementation**
   - **SEARCH** for payment/receipt/AR entities
   - **SEARCH** for accounting integration
   - **DOCUMENT** whether payment data exists
   - **STOP** if payment data not found

7. **Inspect existing costing implementation**
   - **SEARCH** for cost/COGS entities
   - **SEARCH** for inventory costing methods
   - **DOCUMENT** whether cost data exists
   - **STOP** if cost data not found

8. **Verify every entity, table, view, field, status value, and relationship**
   - Cross-reference with actual database schema
   - Verify field names match entity properties
   - Verify status values from existing code
   - Document any discrepancies

### 1.2 Hard Rule for Coding Agent

**DO NOT create or modify implementation based on assumed table/entity/field names.** 

**First inspect the existing project and identify the authoritative implementation for each business rule.**

**If the existing implementation cannot be found, STOP and mark the item as requiring clarification instead of inventing a new rule.**

---

## 2. Database-Level Pagination Strategy

### 2.1 Customer Transaction Inquiry - UNION ALL Approach

**Problem:** Current implementation materializes all documents in memory before pagination.

**Solution:** Use database-level UNION ALL for efficient pagination.

```sql
-- File: scripts/views/vCustomerTransactions.sql
CREATE VIEW vCustomerTransactions AS
-- Quotations
SELECT 
    'QT' AS DocType,
    CompanyCode,
    BranchCode,
    QtNo AS DocNo,
    QtDate AS DocDate,
    CustCode,
    CustName,
    SalesRep,
    Status,
    TotAmnt AS Amount,
    CustPo AS Reference,
    CreatedDate
FROM SaQt
WHERE IsCurrent = 1

UNION ALL

-- Sales Orders
SELECT 
    'SO' AS DocType,
    CompanyCode,
    BranchCode,
    SoNo AS DocNo,
    SoDate AS DocDate,
    CustCode,
    CustName,
    SalesRep,
    Status,
    TotAmnt AS Amount,
    CustPo AS Reference,
    CreatedDate
FROM SaSo
WHERE IsCurrent = 1

UNION ALL

-- Delivery Orders
SELECT 
    'DO' AS DocType,
    CompanyCode,
    BranchCode,
    DoNo AS DocNo,
    DoDate AS DocDate,
    CustCode,
    CustName,
    SalesRep,
    Status,
    TotAmnt AS Amount,
    NULL AS Reference,
    CreatedDate
FROM SaDo

UNION ALL

-- Invoices
SELECT 
    'INV' AS DocType,
    CompanyCode,
    BranchCode,
    InvNo AS DocNo,
    InvDate AS DocDate,
    CustCode,
    CustName,
    SalesmanCode AS SalesRep,
    Status,
    TotAmnt AS Amount,
    PoNo AS Reference,
    CreatedDate
FROM SaInvoice

UNION ALL

-- Credit Notes
SELECT 
    'CN' AS DocType,
    CompanyCode,
    BranchCode,
    DocNo,
    DocDate,
    CustCode,
    CustName,
    SalesRep,
    Status,
    TotAmnt AS Amount,
    RefNo AS Reference,
    CreatedDate
FROM SaCdn
WHERE Type = 'CN'

UNION ALL

-- Debit Notes
SELECT 
    'DN' AS DocType,
    CompanyCode,
    BranchCode,
    DocNo,
    DocDate,
    CustCode,
    CustName,
    SalesRep,
    Status,
    TotAmnt AS Amount,
    RefNo AS Reference,
    CreatedDate
FROM SaCdn
WHERE Type = 'DN';
```

**Service Implementation with Database Pagination:**

```csharp
// File: ErpWeb.Core/Sales/SalesReportService.cs
public async Task<IvMasterOperationResult<CustomerTransactionResult>> GetCustomerTransactionsAsync(
    CustomerTransactionQuery query,
    CancellationToken cancellationToken = default)
{
    var scope = _tenant.TryBranchScope();
    if (scope is null)
    {
        return IvMasterOperationResult<CustomerTransactionResult>.Fail(
            IvMasterErrorCode.Unauthorized, "No tenant context");
    }

    var company = scope.CompanyCode;
    var branch = scope.BranchCode;

    await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

    // Build base query from view
    var baseQuery = db.Set<CustomerTransactionView>().AsNoTracking()
        .Where(x => x.CompanyCode == company && x.BranchCode == branch);

    // Apply filters
    baseQuery = ApplyTransactionFilters(baseQuery, query);

    // CRITICAL: Calculate totals BEFORE pagination
    var totalCount = await baseQuery.CountAsync(cancellationToken);
    
    var totals = await baseQuery
        .GroupBy(x => 1) // Group all rows
        .Select(g => new CustomerTransactionTotals
        {
            TotalDocuments = g.Count(),
            TotalAmount = g.Sum(x => x.Amount),
            QuotationCount = g.Count(x => x.DocType == "QT"),
            SalesOrderCount = g.Count(x => x.DocType == "SO"),
            DeliveryOrderCount = g.Count(x => x.DocType == "DO"),
            InvoiceCount = g.Count(x => x.DocType == "INV"),
            CreditNoteCount = g.Count(x => x.DocType == "CN"),
            DebitNoteCount = g.Count(x => x.DocType == "DN")
        })
        .FirstOrDefaultAsync(cancellationToken) ?? new CustomerTransactionTotals();

    // Apply sorting
    var sortedQuery = ApplyTransactionSorting(baseQuery, query.SortField, query.SortDescending);

    // Apply pagination at database level
    var rows = await sortedQuery
        .Skip(query.Skip)
        .Take(query.Take)
        .ToListAsync(cancellationToken);

    return IvMasterOperationResult<CustomerTransactionResult>.Ok(new CustomerTransactionResult
    {
        Rows = rows,
        Totals = totals,
        TotalCount = totalCount
    });
}

private IQueryable<CustomerTransactionView> ApplyTransactionFilters(
    IQueryable<CustomerTransactionView> query,
    CustomerTransactionQuery filter)
{
    if (!string.IsNullOrWhiteSpace(filter.CustCode))
    {
        var cust = filter.CustCode.Trim();
        query = query.Where(x => x.CustCode == cust);
    }

    if (!string.IsNullOrWhiteSpace(filter.SalesmanCode))
    {
        var rep = filter.SalesmanCode.Trim();
        query = query.Where(x => x.SalesRep == rep);
    }

    if (!string.IsNullOrWhiteSpace(filter.Status))
    {
        var status = filter.Status.Trim();
        query = query.Where(x => x.Status == status);
    }

    if (!string.IsNullOrWhiteSpace(filter.DocType))
    {
        var docType = filter.DocType.Trim();
        query = query.Where(x => x.DocType == docType);
    }

    if (filter.DateFrom is DateTime from)
    {
        var fromDate = from.Date;
        query = query.Where(x => x.DocDate >= fromDate);
    }

    if (filter.DateTo is DateTime to)
    {
        var toExclusive = to.Date.AddDays(1);
        query = query.Where(x => x.DocDate < toExclusive);
    }

    return query;
}
```

### 2.2 Global Rule: Totals Before Pagination

**RULE: For ALL reports, totals must be calculated from the complete filtered dataset BEFORE pagination.**

```csharp
// Correct pattern for ALL reports:
public async Task<IvMasterOperationResult<T>> GetReportAsync<T>(
    IQueryable<T> baseQuery,
    ReportQuery query,
    CancellationToken cancellationToken)
{
    // 1. Apply filters
    var filteredQuery = ApplyFilters(baseQuery, query);

    // 2. Calculate totals from FULL filtered dataset
    var totalCount = await filteredQuery.CountAsync(cancellationToken);
    var totals = await CalculateTotalsAsync(filteredQuery, cancellationToken);

    // 3. Apply sorting
    var sortedQuery = ApplySorting(filteredQuery, query.SortField, query.SortDescending);

    // 4. Apply pagination
    var rows = await sortedQuery
        .Skip(query.Skip)
        .Take(query.Take)
        .ToListAsync(cancellationToken);

    return CreateResult(rows, totals, totalCount);
}
```

---

## 3. Explicit Business Rules Documentation

### 3.1 Sales Order Outstanding Business Rule

**MUST DISCOVER FROM EXISTING ERP CODE:**

Before implementing, the AI agent must:

1. **Search for existing SO fulfillment logic** in `SaSoFulfillment.cs` or similar
2. **Search for existing document application logic** in `SaDocApplicationService.cs`
3. **Search for existing delivery order creation logic** to understand how SO quantities are consumed
4. **Verify the actual business rule** for outstanding quantities

**Possible Business Rules (MUST VERIFY):**

| Rule | Formula | Use Case |
|------|---------|----------|
| **Delivered Outstanding** | Ordered - Delivered | When DO is the fulfillment milestone |
| **Invoiced Outstanding** | Ordered - Invoiced | When Invoice is the fulfillment milestone |
| **Applied Outstanding** | Ordered - Max(AppliedQty) | When using document application ledger |
| **Remaining Outstanding** | Ordered - Delivered - Allocated | When considering stock allocation |

**Required Verification:**

```csharp
// Search in existing code for:
// 1. SaSoFulfillment.cs - look for fulfillment status calculation
// 2. SaDocApplicationService.cs - look for quantity application
// 3. SaDoService.cs - look for SO quantity consumption
// 4. SaInvoiceService.cs - look for DO/SO quantity consumption
```

**Document the actual rule found and use it in the implementation.**

### 3.2 Sales Definition Rules

**MUST DEFINE EXPLICITLY:**

For all sales analysis reports, define:

| Report | Sales Definition | CN/DN Treatment | Status Filter |
|--------|------------------|-----------------|---------------|
| **Sales by Item** | Gross Invoice Sales | Exclude CN/DN | POSTED only |
| **Sales by Customer** | Net Sales | Include CN/DN (netted) | POSTED only |
| **Sales by Salesman** | Net Sales | Include CN/DN (netted) | POSTED only |
| **Sales by Category** | Gross Invoice Sales | Exclude CN/DN | POSTED only |
| **Sales by Warehouse** | Gross Invoice Sales | Exclude CN/DN | POSTED only |
| **Sales Trend** | Net Sales | Include CN/DN (netted) | POSTED only |

**Verification Required:**

```csharp
// Search existing code for:
// 1. SaSalesAnalysisService.cs - how does existing Sales Summary calculate?
// 2. Look for CN/DN netting logic
// 3. Look for status filtering (POSTED vs all)
// 4. Document the actual implementation
```

### 3.3 Dashboard KPI Business Rules

**COMPLETE KPI SPECIFICATION:**

| KPI | Source Table | Date Field | Status Condition | Amount Field | CN/DN Treatment | Business Definition |
|-----|--------------|------------|------------------|--------------|-----------------|---------------------|
| **Sales Today** | SaInvoice | InvDate | POSTED | TotAmnt | Exclude | Sum of posted invoices where InvDate = today |
| **Sales This Month** | SaInvoice | InvDate | POSTED | TotAmnt | Exclude | Sum of posted invoices where InvDate in current month |
| **Sales This Year** | SaInvoice | InvDate | POSTED | TotAmnt | Exclude | Sum of posted invoices where InvDate in current year |
| **Open Quotations** | SaQt | - | NEW, SENT, ACCEPTED | - | N/A | Count of current quotations with open status |
| **Open Sales Orders** | SaSo | - | NEW, CONFIRMED | - | N/A | Count of current SOs with open status |
| **Pending Deliveries** | SaSo, SaDo | - | - | - | N/A | Count of SOs with outstanding delivery quantity |
| **Outstanding Invoices** | SaInvoice | DueDate | POSTED | TotAmnt | Exclude | Sum of posted invoices where DueDate >= today |
| **Overdue Invoices** | SaInvoice | DueDate | POSTED | TotAmnt | Exclude | Sum of posted invoices where DueDate < today |
| **Credit Notes This Month** | SaCdn | DocDate | POSTED | TotAmnt | Type=CN | Sum of posted CNs where DocDate in current month |
| **Debit Notes This Month** | SaCdn | DocDate | POSTED | TotAmnt | Type=DN | Sum of posted DNs where DocDate in current month |

**Verification Required:**

```csharp
// Search existing code for:
// 1. How does existing Sales Summary calculate period totals?
// 2. What status values are considered "open" for QT/SO?
// 3. How is "outstanding" defined for invoices?
// 4. What is the actual definition of "pending delivery"?
```

---

## 4. Complete Document Flow Mapping

### 4.1 Document Relationship Matrix

**MUST VERIFY FROM EXISTING CODE:**

| Source | Target | Relationship | Source Table/Field | Target Table/Field | Verification Required |
|--------|--------|--------------|-------------------|-------------------|----------------------|
| **QT** | **SO** | Conversion | SaSo.QtNo, SaSo.QtCustRel | SaQt.QtNo, SaQt.CustRel | ✅ Verified in SaDocFlowQuery |
| **SO** | **DO** | Delivery | SaDoDetail.SoNo, SaDoDetail.SoLine | SaSo.SoNo, SaSoDetail.Line | ✅ Verified in entity |
| **DO** | **INV** | Invoice | SaInvoiceDetail.DoNo, SaInvoiceDetail.DoLine | SaDo.DoNo, SaDoDetail.Line | ✅ Verified in entity |
| **INV** | **CN** | Credit | SaCdn.InvNo | SaInvoice.InvNo | ✅ Verified in entity |
| **INV** | **DN** | Debit | SaCdn.InvNo | SaInvoice.InvNo | ✅ Verified in entity |
| **SO** | **INV** | Direct Invoice | SaInvoiceDetail.SoNo, SaInvoiceDetail.SoLine | SaSo.SoNo, SaSoDetail.Line | ⚠️ Verify if DO-less invoicing exists |
| **QT** | **INV** | Direct Invoice | SaInvoiceDetail.SoNo → SaSo.QtNo | SaQt.QtNo | ⚠️ Verify chain |

### 4.2 Document Flow Ledger

**Verified from `SaDocApplication` entity:**

```csharp
// SaDocApplication tracks:
// - SourceDocType (SO, DO, INV)
// - SourceDocId (document number)
// - SourceLineId (line number)
// - TargetDocType (SO, DO, INV)
// - TargetDocId (document number)
// - TargetLineId (line number)
// - AppliedQty (quantity applied)
// - AppliedAmount (amount applied)
```

**Relationships tracked in ledger:**
- SO → DO (delivery fulfillment)
- DO → INV (invoice creation)
- SO → INV (direct invoice without DO)

### 4.3 Document Flow Implementation

**Enhanced `QueryFullChainAsync` must:**

1. **Start from any document type**
2. **Traverse upstream** using:
   - SaDocApplication for SO/DO/INV relationships
   - SaSo.QtNo for QT→SO conversion
   - SaCdn.InvNo for INV→CN/DN relationships
3. **Traverse downstream** using:
   - SaDocApplication for SO/DO/INV relationships
   - SaSo.QtNo for QT→SO conversion
   - SaCdn.InvNo for INV→CN/DN relationships
4. **Include e-Invoice status** where available
5. **Include document details** (date, status, amount)

---

## 5. AR/Payment Integration

### 5.1 Customer Statement Requirement

**CRITICAL: Customer Statement is NOT complete without AR/Payment data.**

**Required Actions:**

1. **SEARCH for existing payment/receipt entities:**
   ```
   Search for: Payment, Receipt, AR, AccountReceivable, CashReceipt
   Look in: ErpWeb.Model/Entities/, ErpWeb.Core/, database schema
   ```

2. **If payment data exists:**
   - Document the entity/table structure
   - Document the relationship to invoices
   - Integrate into Customer Statement

3. **If payment data does NOT exist:**
   - **RENAME** feature to "Customer Transaction Statement"
   - **DOCUMENT** that this is a preliminary statement
   - **DEFER** full Customer Statement until AR integration exists
   - **DO NOT** invent payment tables or logic

### 5.2 Customer Transaction Statement (Preliminary)

**If no AR/payment data exists, implement as:**

```csharp
public sealed class CustomerTransactionStatementResult
{
    public decimal OpeningBalance { get; init; } // Invoices + DN - CN before date range
    public decimal ClosingBalance { get; init; } // Opening + transactions in range
    public IReadOnlyList<CustomerTransactionRow> Rows { get; init; } = [];
    public CustomerTransactionStatementTotals Totals { get; init; } = new();
}

public sealed class CustomerTransactionStatementTotals
{
    public decimal TotalInvoiceAmount { get; init; }
    public decimal TotalCreditNoteAmount { get; init; }
    public decimal TotalDebitNoteAmount { get; init; }
    public decimal NetAmount { get; init; } // Invoice + DN - CN
}
```

**Document clearly:**
- This statement does NOT include payments/receipts
- This is a transaction-based statement, not a financial statement
- Full AR integration required for complete statement

---

## 6. Complete Report Specifications

### 6.1 Phase 1: Core Operational Inquiries

#### 6.1.1 Customer Transaction Inquiry
**Status:** Fully specified (see Section 2.1)

#### 6.1.2 Sales Order Outstanding
**Status:** Business rule verification required

**Specification:**
- **Data Source:** SQL view combining SaSo, SaSoDetail, SaDoDetail, SaInvoiceDetail
- **Business Rule:** MUST VERIFY from existing ERP
- **Filters:** Customer, Salesman, Warehouse, Item, Date range, Status
- **Pagination:** Database-level
- **Totals:** Full dataset before pagination

**Verification Tasks:**
1. Search `SaSoFulfillment.cs` for fulfillment status logic
2. Search `SaDocApplicationService.cs` for quantity application
3. Document actual outstanding calculation rule

#### 6.1.3 Document Flow Inquiry
**Status:** Fully specified (see Section 4)

### 6.2 Phase 2: Sales Analysis Reports

#### 6.2.1 Sales by Item
**Specification:**
- **Sales Definition:** MUST VERIFY - likely Gross Invoice Sales
- **Data Source:** SaInvoiceDetail joined with IvStockMaster
- **Filters:** Date range, Item, Warehouse, Category, Customer
- **Grouping:** By item code
- **Metrics:** Quantity, Amount, Invoice count
- **CN/DN Treatment:** MUST VERIFY

#### 6.2.2 Sales by Customer
**Specification:**
- **Sales Definition:** MUST VERIFY - likely Net Sales (INV + DN - CN)
- **Data Source:** SaInvoice, SaCdn
- **Filters:** Date range, Customer, Salesman, Area, Industry, Channel
- **Grouping:** By customer code
- **Metrics:** Gross Sales, CN Amount, DN Amount, Net Sales, Invoice count
- **Status:** POSTED only

#### 6.2.3 Sales by Salesman
**Specification:**
- **Sales Definition:** MUST VERIFY - likely Net Sales (INV + DN - CN)
- **Data Source:** SaInvoice, SaCdn
- **Filters:** Date range, Salesman, Customer
- **Grouping:** By salesman code
- **Metrics:** Gross Sales, CN Amount, DN Amount, Net Sales, Customer count, Invoice count
- **Status:** POSTED only

#### 6.2.4 Sales by Category
**Specification:**
- **Sales Definition:** MUST VERIFY - likely Gross Invoice Sales
- **Data Source:** SaInvoiceDetail, IvStockMaster
- **Filters:** Date range, Category, Warehouse
- **Grouping:** By item category
- **Metrics:** Quantity, Amount, Item count, Invoice count
- **Status:** POSTED only

#### 6.2.5 Sales by Warehouse
**Specification:**
- **Sales Definition:** MUST VERIFY - likely Gross Invoice Sales
- **Data Source:** SaInvoiceDetail
- **Filters:** Date range, Warehouse, Item
- **Grouping:** By warehouse code
- **Metrics:** Quantity, Amount, Item count, Invoice count
- **Status:** POSTED only

### 6.3 Phase 3: Dashboard

**Status:** Fully specified (see Section 3.3)

**Additional Verification Required:**
1. Search for existing status constants (e.g., `SaQtStatuses.New`, `SaSoStatuses.Confirmed`)
2. Verify "open" status values for each document type
3. Verify "pending delivery" calculation logic
4. Verify "outstanding invoice" calculation logic

### 6.4 Phase 4: Advanced Analysis

#### 6.4.1 Customer Statement
**Status:** AR integration required (see Section 5)

#### 6.4.2 Profitability Analysis
**CRITICAL: Cost source identification required**

**Verification Tasks:**
1. **SEARCH** for cost/COGS entities:
   ```
   Search for: Cost, COGS, CostPrice, AverageCost, FIFO, StandardCost
   Look in: ErpWeb.Model/Entities/Inventory/, ErpWeb.Core/Inventory/
   ```
2. **SEARCH** for inventory costing methods:
   ```
   Search for: IvCosting, IvStockCost, IvTrxCost
   Look in: database schema, existing reports
   ```
3. **DOCUMENT** whether cost data exists
4. **STOP** if cost data not found - do NOT invent cost logic

**If cost data exists:**
- Document the costing method (Average, FIFO, Standard)
- Document the cost fields on inventory transactions
- Implement profitability using actual cost data

**If cost data does NOT exist:**
- **RENAME** to "Sales Margin Analysis (Estimated)"
- **DOCUMENT** that this uses estimated margins
- **DEFER** true profitability until costing integration exists

---

## 7. Security and Authorization

### 7.1 Existing Authorization Model

**MUST FOLLOW EXISTING PATTERNS:**

```csharp
// Existing pattern from SaSalesAnalysisService.cs:
private async Task<(string? CompanyCode, string? Error)> GateAsync(
    string menuCode,
    CancellationToken cancellationToken)
{
    // 1. Check menu access
    var access = await _accessRights.CheckMenuAccessAsync(menuCode, cancellationToken);
    if (!access.Allowed)
    {
        return (null, "Access denied");
    }

    // 2. Get tenant scope
    var scope = _tenant.TryBranchScope();
    if (scope is null)
    {
        return (null, "No tenant context");
    }

    return (scope.CompanyCode, null);
}
```

### 7.2 Report Authorization Requirements

**For each report, verify:**

1. **Menu Access:** Does the user have access to the report menu?
2. **Company Access:** Is the user authorized for the company?
3. **Branch Access:** Is the user authorized for the branch?
4. **Data Visibility:** Are there any data-level restrictions?
5. **Amount Visibility:** Are sensitive amounts restricted?

**Implementation Pattern:**

```csharp
public async Task<IvMasterOperationResult<T>> GetReportAsync<T>(
    string menuCode,
    ReportQuery query,
    CancellationToken cancellationToken)
{
    // 1. Check authorization
    var gate = await GateAsync(menuCode, cancellationToken);
    if (gate.Error is not null)
    {
        return IvMasterOperationResult<T>.Fail(IvMasterErrorCode.Unauthorized, gate.Error);
    }

    var company = gate.CompanyCode!;
    var branch = _tenant.TryBranchScope()?.BranchCode;

    // 2. Apply company/branch filters
    // 3. Execute query
    // 4. Return results
}
```

### 7.3 Branch/Tenant Scope Clarification

**MUST VERIFY:**

1. **Does the application support multi-branch selection?**
   - Search for branch selection UI components
   - Search for branch filtering in existing reports
   - Document the actual behavior

2. **Current Implementation:**
   - `_tenant.TryBranchScope()` returns current branch
   - Query objects have `BranchCode` parameter
   - **VERIFY:** Can users select multiple branches?

**If multi-branch NOT supported:**
- Remove `BranchCode` from query objects
- Use `_tenant.TryBranchScope()?.BranchCode` only
- Document that reports are branch-scoped

**If multi-branch IS supported:**
- Implement branch selection in UI
- Filter by authorized branches only
- Document the multi-branch logic

---

## 8. SQL View Deployment Strategy

### 8.1 Single Source of Truth

**RULE: SQL views must have a single authoritative definition.**

**Recommended Approach:**

1. **Source of Truth:** Migration files in `ErpWeb.Model/Migrations/`
2. **Deployment:** EF Core migrations apply views
3. **Versioning:** Each view change is a new migration
4. **Upgrades:** Use `ALTER VIEW` or `CREATE OR ALTER VIEW`

**Migration Pattern:**

```csharp
// File: ErpWeb.Model/Migrations/XXXXXXXXXXXXXX_AddSalesReportViews.cs
public partial class AddSalesReportViews : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Use CREATE OR ALTER for safe upgrades
        migrationBuilder.Sql(@"
            CREATE OR ALTER VIEW vCustomerTransactions AS
            -- SQL definition
        ");
        
        migrationBuilder.Sql(@"
            CREATE OR ALTER VIEW vSalesOrderOutstanding AS
            -- SQL definition
        ");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("DROP VIEW IF EXISTS vCustomerTransactions");
        migrationBuilder.Sql("DROP VIEW IF EXISTS vSalesOrderOutstanding");
    }
}
```

### 8.2 View Maintenance

**For each view:**
1. **Version control:** Track all changes in migrations
2. **Testing:** Test view with sample data before deployment
3. **Performance:** Validate execution plan
4. **Indexing:** Create appropriate indexes for view columns

---

## 9. Testing Strategy

### 9.1 Business-Rule Tests

**CRITICAL: Tests must verify business rules, not just code execution.**

#### 9.1.1 Customer Transaction Tests

```csharp
[TestMethod]
public async Task CustomerTransaction_MultipleDocTypes_ReturnsAllTypes()
{
    // Arrange: Create test data with QT, SO, DO, INV, CN, DN
    // Act: Run report
    // Assert: Verify all document types present with correct counts
}

[TestMethod]
public async Task CustomerTransaction_DateBoundary_IncludesCorrectDocuments()
{
    // Arrange: Create documents on boundary dates
    // Act: Run report with date range
    // Assert: Verify date filtering is correct (half-open pattern)
}

[TestMethod]
public async Task CustomerTransaction_CancelledDocuments_Excluded()
{
    // Arrange: Create cancelled documents
    // Act: Run report
    // Assert: Verify cancelled documents not included
}

[TestMethod]
public async Task CustomerTransaction_Pagination_TotalsFromFullDataset()
{
    // Arrange: Create 100 documents
    // Act: Run report with Take=20
    // Assert: Verify totals represent all 100 documents, not just 20
}
```

#### 9.1.2 Sales Order Outstanding Tests

```csharp
[TestMethod]
public async Task SOOutstanding_PartialDelivery_CalculatesCorrectly()
{
    // Arrange: SO with Qty=100, DO with Qty=30
    // Act: Run report
    // Assert: Outstanding = 70 (verify actual business rule)
}

[TestMethod]
public async Task SOOutstanding_FullDelivery_ShowsZero()
{
    // Arrange: SO with Qty=100, DO with Qty=100
    // Act: Run report
    // Assert: Outstanding = 0
}

[TestMethod]
public async Task SOOutstanding_MultipleDOs_SumsCorrectly()
{
    // Arrange: SO with Qty=100, DO1=30, DO2=40
    // Act: Run report
    // Assert: Outstanding = 30 (verify actual business rule)
}
```

#### 9.1.3 Customer Statement Tests

```csharp
[TestMethod]
public async Task CustomerStatement_OpeningBalance_CalculatedCorrectly()
{
    // Arrange: Create transactions before date range
    // Act: Run statement
    // Assert: Opening balance = Invoice + DN - CN before date range
}

[TestMethod]
public async Task CustomerStatement_ClosingBalance_CalculatedCorrectly()
{
    // Arrange: Create transactions in date range
    // Act: Run statement
    // Assert: Closing balance = Opening + transactions in range
}

[TestMethod]
public async Task CustomerStatement_WithoutPayments_DocumentsLimitation()
{
    // Arrange: Verify AR/payment data exists
    // Act: Run statement
    // Assert: If no payment data, verify statement is labeled as preliminary
}
```

#### 9.1.4 Document Flow Tests

```csharp
[TestMethod]
public async Task DocumentFlow_FullChain_QTtoSOtoDOtoINVtoCN()
{
    // Arrange: Create QT → SO → DO → INV → CN chain
    // Act: Query document flow from any document
    // Assert: Verify complete chain traversal
}

[TestMethod]
public async Task DocumentFlow_WithEInvoice_IncludesStatus()
{
    // Arrange: Create invoice with e-Invoice submission
    // Act: Query document flow
    // Assert: Verify e-Invoice status included
}
```

### 9.2 Performance Tests

```csharp
[TestMethod]
public async Task CustomerTransaction_LargeDataset_CompletesWithinTimeout()
{
    // Arrange: Create 100,000 documents
    // Act: Run report with filters
    // Assert: Completes within 5 seconds
}

[TestMethod]
public async Task SalesOrderOutstanding_ComplexQuery_UsesIndex()
{
    // Arrange: Verify index exists on filter columns
    // Act: Run report
    // Assert: Execution plan uses index seek, not table scan
}
```

---

## 10. Performance Requirements

### 10.1 Execution Plan Validation

**For each SQL view:**

1. **Analyze execution plan:**
   ```sql
   SET STATISTICS IO ON;
   SET STATISTICS TIME ON;
   
   SELECT * FROM vCustomerTransactions
   WHERE CompanyCode = 'TEST' AND BranchCode = '001'
   AND DocDate >= '2024-01-01' AND DocDate < '2024-02-01';
   
   SET STATISTICS IO OFF;
   SET STATISTICS TIME OFF;
   ```

2. **Verify index usage:**
   - Check for index seek operations
   - Avoid table scans on large tables
   - Create covering indexes where needed

3. **Test with realistic data:**
   - 100,000+ documents
   - Multiple companies/branches
   - Realistic date ranges

### 10.2 Indexing Strategy

```sql
-- Customer Transaction view indexes
CREATE INDEX IX_SaQt_Company_Branch_Date 
ON SaQt (CompanyCode, BranchCode, QtDate, IsCurrent)
INCLUDE (CustCode, SalesRep, Status, TotAmnt);

CREATE INDEX IX_SaSo_Company_Branch_Date 
ON SaSo (CompanyCode, BranchCode, SoDate, IsCurrent)
INCLUDE (CustCode, SalesRep, Status, TotAmnt);

CREATE INDEX IX_SaInvoice_Company_Branch_Date 
ON SaInvoice (CompanyCode, BranchCode, InvDate)
INCLUDE (CustCode, SalesmanCode, Status, TotAmnt);

CREATE INDEX IX_SaCdn_Company_Branch_Date 
ON SaCdn (CompanyCode, BranchCode, DocDate, Type)
INCLUDE (CustCode, SalesRep, Status, TotAmnt);
```

### 10.3 Query Optimization Rules

1. **Use IQueryable with filters before materialization**
2. **Implement pagination at database level**
3. **Use AsNoTracking() for read-only queries**
4. **Batch related queries where possible**
5. **Use compiled queries for frequently executed queries**
6. **Set appropriate SQL timeouts**
7. **Limit date range to prevent accidental multi-year queries**

### 10.4 Date Range Limits

**Implement reasonable limits:**

```csharp
public sealed class ReportQuery
{
    public DateTime? DateFrom { get; set; }
    public DateTime? DateTo { get; set; }
    
    // Validation
    public bool IsValidDateRange()
    {
        if (DateFrom == null || DateTo == null) return true;
        
        var range = DateTo.Value - DateFrom.Value;
        return range.TotalDays <= 365; // Max 1 year
    }
}
```

---

## 11. Cache Key Strategy

### 11.1 Tenant-Aware Cache Keys

**RULE: Cache keys must include tenant/company/branch scope.**

```csharp
public sealed class CacheKeyBuilder
{
    private readonly IInventoryTenantContext _tenant;
    
    public CacheKeyBuilder(IInventoryTenantContext tenant)
    {
        _tenant = tenant;
    }
    
    public string BuildKey(string baseKey)
    {
        var scope = _tenant.TryBranchScope();
        if (scope == null)
        {
            return baseKey;
        }
        
        return $"{baseKey}:{scope.CompanyCode}:{scope.BranchCode}";
    }
}

// Usage:
var cacheKey = _cacheKeyBuilder.BuildKey("customers_list");
```

### 11.2 Cache Invalidation

**Implement cache invalidation for:**
- Reference data changes (customers, items)
- Document status changes
- Company/branch configuration changes

---

## 12. Implementation Gates

### 12.1 Gate-Based Approval Process

**BEFORE IMPLEMENTATION, COMPLETE ALL GATES:**

#### Gate 1: Existing System Inspection
- [ ] Read all Sales entities
- [ ] Read all Sales repositories
- [ ] Read all Sales services
- [ ] Read all Sales UI pages
- [ ] Document findings

#### Gate 2: Entity/Table/View Verification
- [ ] Verify all entity names match database
- [ ] Verify all field names match database
- [ ] Verify all status values from existing code
- [ ] Document any discrepancies

#### Gate 3: Business-Rule Verification
- [ ] Verify SO outstanding calculation rule
- [ ] Verify sales definition (gross vs net)
- [ ] Verify CN/DN treatment
- [ ] Verify status filtering rules
- [ ] Document all business rules

#### Gate 4: Query/Performance Design
- [ ] Design SQL views for database-level pagination
- [ ] Validate execution plans
- [ ] Create appropriate indexes
- [ ] Test with realistic data volumes

#### Gate 5: Test Cases Defined
- [ ] Define business-rule tests for each report
- [ ] Define performance tests
- [ ] Define edge-case tests
- [ ] Review test coverage

#### Gate 6: Implementation
- [ ] Implement SQL views
- [ ] Implement services
- [ ] Implement UI pages
- [ ] Follow existing patterns

#### Gate 7: SQL/Data Validation
- [ ] Verify SQL view correctness
- [ ] Verify data consistency
- [ ] Verify performance benchmarks
- [ ] Verify index usage

#### Gate 8: UI/Smoke Testing
- [ ] Test all report filters
- [ ] Test pagination
- [ ] Test export functionality
- [ ] Test drill-down navigation
- [ ] Verify security restrictions

---

## 13. Revised Implementation Phases

### Phase 1: Foundation (Weeks 1-2)
**Focus:** Existing system inspection and foundation setup

**Tasks:**
1. Complete Gate 1: Existing System Inspection
2. Complete Gate 2: Entity/Table/View Verification
3. Complete Gate 3: Business-Rule Verification
4. Design SQL views for database-level pagination
5. Create base service classes and patterns

**Deliverables:**
- Existing system inspection report
- Business rules documentation
- SQL view designs
- Base service implementations

### Phase 2: Core Reports (Weeks 3-6)
**Focus:** Phase 1 core operational inquiries

**Reports:**
1. Customer Transaction Inquiry (fully specified)
2. Sales Order Outstanding (business rule verified)
3. Document Flow Inquiry (fully specified)

**Tasks:**
1. Complete Gate 4: Query/Performance Design
2. Complete Gate 5: Test Cases Defined
3. Implement SQL views
4. Implement services
5. Implement UI pages
6. Complete Gate 6-8

### Phase 3: Sales Analysis (Weeks 7-10)
**Focus:** Phase 2 sales analysis reports

**Reports:**
1. Sales by Item
2. Sales by Customer
3. Sales by Salesman
4. Sales by Category
5. Sales by Warehouse

**Tasks:**
1. Verify sales definitions for each report
2. Design SQL views
3. Implement services and UI
4. Complete all gates

### Phase 4: Dashboard (Weeks 11-12)
**Focus:** Phase 3 management dashboard

**Reports:**
1. Sales Dashboard with all KPIs

**Tasks:**
1. Verify KPI business rules
2. Design dashboard service
3. Implement dashboard UI
4. Complete all gates

### Phase 5: Advanced Analysis (Weeks 13-16)
**Focus:** Phase 4 advanced analysis

**Reports:**
1. Customer Transaction Statement (preliminary)
2. Profitability Analysis (if cost data exists)

**Tasks:**
1. Verify AR/payment data availability
2. Verify cost data availability
3. Implement or defer based on findings
4. Complete all gates

---

## 14. Final Approval Checklist

**Before implementation, verify:**

- [ ] All gates completed successfully
- [ ] All business rules documented and verified
- [ ] All SQL views designed and tested
- [ ] All test cases defined
- [ ] Performance requirements met
- [ ] Security model followed
- [ ] Existing patterns reused
- [ ] No invented business rules
- [ ] No assumed table/field names

**Approval Signature:** _________________ **Date:** _________

---

*Document Version: 2.0*  
*Last Updated: 2026-09-26*  
*Prepared by: AI Assistant*  
*Review Status: Revised based on feedback*