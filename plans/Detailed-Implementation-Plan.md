# Detailed Technical Implementation Plan

## Overview

This document provides a comprehensive technical implementation plan for the Sales module reports. It includes specific code patterns, file locations, and implementation guidelines based on the existing architecture.

## 1. Implementation Architecture

### 1.1 Project Structure

**Existing Structure to Follow:**
```
ErpWeb.Core/Sales/
├── ISalesReportService.cs          # Service interface
├── SalesReportService.cs           # Service implementation
├── SalesReportResults.cs           # DTOs and result classes
└── SalesReportQuery.cs             # Query parameters

ErpWeb.UI/Sales/Reports/
├── SalesReportPageBase.cs          # Base page class
├── Report1.razor                   # Report page
├── Report1.razor.cs                # Code-behind
└── Report1.razor.css               # Styles
```

### 1.2 Service Layer Pattern

**Follow existing pattern from `ISaSalesAnalysisService`:**

```csharp
// File: ErpWeb.Core/Sales/ISalesReportService.cs
namespace ErpWeb.Core.Sales;

public interface ISalesReportService
{
    Task<IvMasterOperationResult<CustomerTransactionResult>> GetCustomerTransactionsAsync(
        CustomerTransactionQuery query,
        CancellationToken cancellationToken = default);
        
    Task<IvMasterOperationResult<SalesOrderOutstandingResult>> GetSalesOrderOutstandingAsync(
        SalesOrderOutstandingQuery query,
        CancellationToken cancellationToken = default);
        
    // Add more methods as needed
}
```

### 1.3 Query Pattern

**Follow existing pattern from `SaSalesAnalysisQuery`:**

```csharp
// File: ErpWeb.Core/Sales/SalesReportQuery.cs
namespace ErpWeb.Core.Sales;

public sealed class CustomerTransactionQuery
{
    public DateTime? DateFrom { get; set; }
    public DateTime? DateTo { get; set; }
    public string? CustCode { get; set; }
    public string? SalesmanCode { get; set; }
    public string? BranchCode { get; set; }
    public string? Status { get; set; }
    
    // Pagination
    public int Skip { get; set; }
    public int Take { get; set; } = 20;
    
    // Sorting
    public string? SortField { get; set; }
    public bool SortDescending { get; set; }
}

public sealed class SalesOrderOutstandingQuery
{
    public DateTime? DateFrom { get; set; }
    public DateTime? DateTo { get; set; }
    public string? CustCode { get; set; }
    public string? SalesmanCode { get; set; }
    public string? WarehouseCode { get; set; }
    public string? ItemCode { get; set; }
    public string? Status { get; set; }
    public string? BranchCode { get; set; }
    
    // Pagination
    public int Skip { get; set; }
    public int Take { get; set; } = 20;
}
```

### 1.4 Result Pattern

**Follow existing pattern from `SaSalesSummaryResult`:**

```csharp
// File: ErpWeb.Core/Sales/SalesReportResults.cs
namespace ErpWeb.Core.Sales;

public sealed class CustomerTransactionResult
{
    public IReadOnlyList<CustomerTransactionRow> Rows { get; init; } = [];
    public CustomerTransactionTotals Totals { get; init; } = new();
    public int TotalCount { get; init; }
}

public sealed class CustomerTransactionRow
{
    public string DocType { get; init; } = string.Empty;
    public string DocNo { get; init; } = string.Empty;
    public DateTime DocDate { get; init; }
    public string CustCode { get; init; } = string.Empty;
    public string? CustName { get; init; }
    public decimal Amount { get; init; }
    public string Status { get; init; } = string.Empty;
    public string? Reference { get; init; }
}

public sealed class CustomerTransactionTotals
{
    public int TotalDocuments { get; init; }
    public decimal TotalAmount { get; init; }
    public int QuotationCount { get; init; }
    public int SalesOrderCount { get; init; }
    public int DeliveryOrderCount { get; init; }
    public int InvoiceCount { get; init; }
    public int CreditNoteCount { get; init; }
    public int DebitNoteCount { get; init; }
}
```

## 2. Phase 1: Core Operational Inquiries Implementation

### 2.1 Customer Transaction Inquiry

**Service Implementation:**

```csharp
// File: ErpWeb.Core/Sales/SalesReportService.cs
public sealed class SalesReportService : ISalesReportService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IInventoryTenantContext _tenant;
    private readonly IAccessRightService _accessRights;

    public SalesReportService(
        IDbContextFactory<AppDbContext> dbFactory,
        IInventoryTenantContext tenant,
        IAccessRightService accessRights)
    {
        _dbFactory = dbFactory;
        _tenant = tenant;
        _accessRights = accessRights;
    }

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

        // Build queries for each document type
        var quotations = BuildQuotationQuery(db, company, branch, query);
        var salesOrders = BuildSalesOrderQuery(db, company, branch, query);
        var deliveryOrders = BuildDeliveryOrderQuery(db, company, branch, query);
        var invoices = BuildInvoiceQuery(db, company, branch, query);
        var creditNotes = BuildCreditNoteQuery(db, company, branch, query);
        var debitNotes = BuildDebitNoteQuery(db, company, branch, query);

        // Execute queries and combine results
        var rows = new List<CustomerTransactionRow>();
        
        rows.AddRange(await quotations.ToListAsync(cancellationToken));
        rows.AddRange(await salesOrders.ToListAsync(cancellationToken));
        rows.AddRange(await deliveryOrders.ToListAsync(cancellationToken));
        rows.AddRange(await invoices.ToListAsync(cancellationToken));
        rows.AddRange(await creditNotes.ToListAsync(cancellationToken));
        rows.AddRange(await debitNotes.ToListAsync(cancellationToken));

        // Apply sorting
        rows = ApplySorting(rows, query.SortField, query.SortDescending);

        // Calculate totals
        var totals = CalculateTotals(rows);

        // Apply pagination
        var totalCount = rows.Count;
        var pagedRows = rows
            .Skip(query.Skip)
            .Take(query.Take)
            .ToList();

        return IvMasterOperationResult<CustomerTransactionResult>.Ok(new CustomerTransactionResult
        {
            Rows = pagedRows,
            Totals = totals,
            TotalCount = totalCount
        });
    }

    private IQueryable<CustomerTransactionRow> BuildQuotationQuery(
        AppDbContext db, string company, string branch, CustomerTransactionQuery query)
    {
        var q = db.SaQts.AsNoTracking()
            .Where(x => x.CompanyCode == company && x.BranchCode == branch && x.IsCurrent);

        if (!string.IsNullOrWhiteSpace(query.CustCode))
        {
            var cust = query.CustCode.Trim();
            q = q.Where(x => x.CustCode == cust);
        }

        if (!string.IsNullOrWhiteSpace(query.SalesmanCode))
        {
            var rep = query.SalesmanCode.Trim();
            q = q.Where(x => x.SalesRep == rep);
        }

        if (!string.IsNullOrWhiteSpace(query.Status))
        {
            var status = query.Status.Trim();
            q = q.Where(x => x.Status == status);
        }

        if (query.DateFrom is DateTime from)
        {
            var fromDate = from.Date;
            q = q.Where(x => x.QtDate >= fromDate);
        }

        if (query.DateTo is DateTime to)
        {
            var toExclusive = to.Date.AddDays(1);
            q = q.Where(x => x.QtDate < toExclusive);
        }

        return q.Select(x => new CustomerTransactionRow
        {
            DocType = "QT",
            DocNo = x.QtNo,
            DocDate = x.QtDate,
            CustCode = x.CustCode,
            CustName = x.CustName,
            Amount = x.TotAmnt,
            Status = x.Status,
            Reference = x.CustPo
        });
    }

    // Similar methods for other document types...
}
```

**UI Implementation:**

```razor
@page "/sales/reports/customer-transactions"
@inherits SalesReportPageBase
@using ErpWeb.Core.Sales

<PageTitle>Customer Transaction Inquiry</PageTitle>

<MenuAuthorize MenuCode="@MenuCodes.SalesCustomerTransactions">
    <div class="iv-page">
        <header class="iv-hero">
            <div class="iv-hero__mark" aria-hidden="true"><i class="fa-solid fa-users"></i></div>
            <div class="iv-hero__copy">
                <p class="iv-eyebrow">Sales · Reports</p>
                <h1 class="iv-title">Customer Transaction Inquiry</h1>
                <div class="iv-chips">
                    <span class="iv-chip">All document types</span>
                    <span class="iv-chip">@DateFrom?.ToString("dd/MM/yyyy") - @DateTo?.ToString("dd/MM/yyyy")</span>
                </div>
            </div>
            @if (Result is not null)
            {
                <div class="iv-hero__kpi">
                    <span>Total documents</span>
                    <strong>@Result.TotalCount.ToString("N0")</strong>
                </div>
            }
        </header>

        <section class="iv-card">
            <div style="display:grid;grid-template-columns:repeat(auto-fit,minmax(180px,1fr));gap:.5rem .75rem;">
                <div class="iv-field">
                    <label class="iv-field__label">From</label>
                    <DxDateEdit @bind-Date="@DateFrom" />
                </div>
                <div class="iv-field">
                    <label class="iv-field__label">To</label>
                    <DxDateEdit @bind-Date="@DateTo" />
                </div>
                <div class="iv-field">
                    <label class="iv-field__label">Customer</label>
                    <IvCodeComboBox Data="@CustomerOptions" @bind-Value="@CustCode" NullText="All customers" />
                </div>
                <div class="iv-field">
                    <label class="iv-field__label">Salesman</label>
                    <IvCodeComboBox Data="@SalesmanOptions" @bind-Value="@SalesmanCode" NullText="All salesmen" />
                </div>
                <div class="iv-field">
                    <label class="iv-field__label">Status</label>
                    <DxComboBox Data="@StatusOptions" @bind-Value="@Status" NullText="All statuses" />
                </div>
            </div>

            <div class="iv-toolbar-row" style="margin-top:.75rem;">
                <DxButton RenderStyle="ButtonRenderStyle.Primary"
                          Text="@(IsLoading ? "Running…" : "Run")"
                          Click="@RunAsync"
                          Enabled="@(!IsLoading)" />
            </div>
        </section>

        @if (Result is not null)
        {
            <section class="iv-card">
                <div class="iv-chips" style="margin-bottom: 1rem;">
                    <span class="iv-chip">Quotations: @Result.Totals.QuotationCount.ToString("N0")</span>
                    <span class="iv-chip">SO: @Result.Totals.SalesOrderCount.ToString("N0")</span>
                    <span class="iv-chip">DO: @Result.Totals.DeliveryOrderCount.ToString("N0")</span>
                    <span class="iv-chip">Invoices: @Result.Totals.InvoiceCount.ToString("N0")</span>
                    <span class="iv-chip">CN: @Result.Totals.CreditNoteCount.ToString("N0")</span>
                    <span class="iv-chip">DN: @Result.Totals.DebitNoteCount.ToString("N0")</span>
                </div>

                <DxGrid Data="@Result.Rows" CssClass="w-100"
                        PageSizeSelectorVisible="true"
                        PageSizeSelectorItems="@(new[] { 20, 50, 100 })">
                    <Columns>
                        <DxGridDataColumn FieldName="@nameof(CustomerTransactionRow.DocType)" Caption="Type" Width="60px" />
                        <DxGridDataColumn FieldName="@nameof(CustomerTransactionRow.DocNo)" Caption="Document No" Width="120px">
                            <CellDisplayTemplate>
                                <a href="@GetDocUrl(context.DataItem)" class="code-style">
                                    @((context.DataItem as CustomerTransactionRow)?.DocNo)
                                </a>
                            </CellDisplayTemplate>
                        </DxGridDataColumn>
                        <DxGridDataColumn FieldName="@nameof(CustomerTransactionRow.DocDate)" Caption="Date" Width="100px" DisplayFormat="dd/MM/yyyy" />
                        <DxGridDataColumn FieldName="@nameof(CustomerTransactionRow.CustCode)" Caption="Customer" Width="100px" />
                        <DxGridDataColumn FieldName="@nameof(CustomerTransactionRow.CustName)" Caption="Name" Width="200px" />
                        <DxGridDataColumn FieldName="@nameof(CustomerTransactionRow.Amount)" Caption="Amount" Width="120px" DisplayFormat="n2" TextAlignment="GridTextAlignment.Right" />
                        <DxGridDataColumn FieldName="@nameof(CustomerTransactionRow.Status)" Caption="Status" Width="100px" />
                        <DxGridDataColumn FieldName="@nameof(CustomerTransactionRow.Reference)" Caption="Reference" Width="150px" />
                    </Columns>
                </DxGrid>
            </section>
        }
    </div>
</MenuAuthorize>
```

```csharp
// File: ErpWeb.UI/Sales/Reports/CustomerTransactions.razor.cs
namespace ErpWeb.UI.Sales.Reports;

public partial class CustomerTransactions : SalesReportPageBase
{
    protected CustomerTransactionResult? Result { get; set; }
    protected string? CustCode { get; set; }
    protected string? SalesmanCode { get; set; }
    protected string? Status { get; set; }

    protected IReadOnlyList<IvCodeLookupRow> CustomerOptions { get; set; } = [];
    protected IReadOnlyList<IvCodeLookupRow> SalesmanOptions { get; set; } = [];
    protected IReadOnlyList<string> StatusOptions { get; set; } = ["NEW", "SENT", "ACCEPTED", "CLOSED", "CANCELLED"];

    protected override async Task OnReportInitializedAsync()
    {
        CustomerOptions = await CustLookups.SearchCustomersAsync(string.Empty, 200);
        SalesmanOptions = await CustLookups.ListSalesRepsForAssignmentAsync();
    }

    protected async Task RunAsync()
    {
        IsLoading = true;
        LoadError = null;
        try
        {
            var query = new CustomerTransactionQuery
            {
                DateFrom = DateFrom,
                DateTo = DateTo,
                CustCode = CustCode,
                SalesmanCode = SalesmanCode,
                Status = Status,
                BranchCode = BranchCode,
                Skip = 0,
                Take = 100
            };

            var result = await ReportService.GetCustomerTransactionsAsync(query);
            if (!result.Succeeded)
            {
                LoadError = result.Message ?? "Unable to run the report.";
                Result = null;
                return;
            }

            Result = result.Data;
            HasRun = true;
        }
        finally
        {
            IsLoading = false;
        }
    }

    protected string GetDocUrl(CustomerTransactionRow row) => row.DocType switch
    {
        "QT" => $"/sales/qt/{row.DocNo}",
        "SO" => $"/sales/so/{row.DocNo}",
        "DO" => $"/sales/do/{row.DocNo}",
        "INV" => $"/sales/invoice/{row.DocNo}",
        "CN" or "DN" => $"/sales/cdn/{row.DocNo}",
        _ => "#"
    };
}
```

### 2.2 Sales Order Outstanding Report

**SQL View Required:**

```sql
-- File: scripts/views/vSalesOrderOutstanding.sql
CREATE VIEW vSalesOrderOutstanding AS
SELECT 
    so.CompanyCode,
    so.BranchCode,
    so.SoNo,
    so.SoDate,
    so.CustCode,
    so.CustName,
    so.SalesRep,
    so.Status,
    sod.ICode AS ItemCode,
    sod.IDesc AS ItemDesc,
    sod.Qty AS OrderedQty,
    sod.StdQty AS OrderedStdQty,
    sod.UnitPrice,
    sod.Amount,
    sod.FrWarehouse,
    ISNULL(delivered.DeliveredQty, 0) AS DeliveredQty,
    ISNULL(invoiced.InvoicedQty, 0) AS InvoicedQty,
    sod.Qty - ISNULL(delivered.DeliveredQty, 0) AS OutstandingQty,
    sod.StdQty - ISNULL(delivered.DeliveredStdQty, 0) AS OutstandingStdQty
FROM SaSo so
INNER JOIN SaSoDetail sod ON so.CompanyCode = sod.CompanyCode 
    AND so.BranchCode = sod.BranchCode 
    AND so.SoNo = sod.SoNo
    AND so.CustRel = sod.CustRel
LEFT JOIN (
    SELECT 
        CompanyCode, BranchCode, SoNo, SoLine,
        SUM(Qty) AS DeliveredQty,
        SUM(StdQty) AS DeliveredStdQty
    FROM SaDoDetail
    GROUP BY CompanyCode, BranchCode, SoNo, SoLine
) delivered ON sod.CompanyCode = delivered.CompanyCode 
    AND sod.BranchCode = delivered.BranchCode 
    AND sod.SoNo = delivered.SoNo 
    AND sod.Line = delivered.SoLine
LEFT JOIN (
    SELECT 
        CompanyCode, BranchCode, SoNo, SoLine,
        SUM(Qty) AS InvoicedQty
    FROM SaInvoiceDetail
    GROUP BY CompanyCode, BranchCode, SoNo, SoLine
) invoiced ON sod.CompanyCode = invoiced.CompanyCode 
    AND sod.BranchCode = invoiced.BranchCode 
    AND sod.SoNo = invoiced.SoNo 
    AND sod.Line = invoiced.SoLine
WHERE so.IsCurrent = 1
    AND so.Status NOT IN ('CANCELLED', 'CLOSED')
    AND sod.Qty > ISNULL(delivered.DeliveredQty, 0);
```

**Service Implementation:**

```csharp
// Add to ISalesReportService.cs
Task<IvMasterOperationResult<SalesOrderOutstandingResult>> GetSalesOrderOutstandingAsync(
    SalesOrderOutstandingQuery query,
    CancellationToken cancellationToken = default);

// Add to SalesReportService.cs
public async Task<IvMasterOperationResult<SalesOrderOutstandingResult>> GetSalesOrderOutstandingAsync(
    SalesOrderOutstandingQuery query,
    CancellationToken cancellationToken = default)
{
    var scope = _tenant.TryBranchScope();
    if (scope is null)
    {
        return IvMasterOperationResult<SalesOrderOutstandingResult>.Fail(
            IvMasterErrorCode.Unauthorized, "No tenant context");
    }

    var company = scope.CompanyCode;
    var branch = scope.BranchCode;

    await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

    // Use the SQL view
    var q = db.Set<SalesOrderOutstandingView>().AsNoTracking()
        .Where(x => x.CompanyCode == company && x.BranchCode == branch);

    // Apply filters
    if (!string.IsNullOrWhiteSpace(query.CustCode))
    {
        var cust = query.CustCode.Trim();
        q = q.Where(x => x.CustCode == cust);
    }

    if (!string.IsNullOrWhiteSpace(query.SalesmanCode))
    {
        var rep = query.SalesmanCode.Trim();
        q = q.Where(x => x.SalesRep == rep);
    }

    if (!string.IsNullOrWhiteSpace(query.WarehouseCode))
    {
        var warehouse = query.WarehouseCode.Trim();
        q = q.Where(x => x.FrWarehouse == warehouse);
    }

    if (!string.IsNullOrWhiteSpace(query.ItemCode))
    {
        var item = query.ItemCode.Trim();
        q = q.Where(x => x.ItemCode == item);
    }

    if (query.DateFrom is DateTime from)
    {
        var fromDate = from.Date;
        q = q.Where(x => x.SoDate >= fromDate);
    }

    if (query.DateTo is DateTime to)
    {
        var toExclusive = to.Date.AddDays(1);
        q = q.Where(x => x.SoDate < toExclusive);
    }

    // Get total count
    var totalCount = await q.CountAsync(cancellationToken);

    // Apply pagination
    var rows = await q
        .OrderByDescending(x => x.SoDate)
        .ThenBy(x => x.SoNo)
        .Skip(query.Skip)
        .Take(query.Take)
        .ToListAsync(cancellationToken);

    // Calculate totals
    var totals = new SalesOrderOutstandingTotals
    {
        TotalOrders = rows.Select(x => x.SoNo).Distinct().Count(),
        TotalOutstandingQty = rows.Sum(x => x.OutstandingQty),
        TotalOutstandingAmount = rows.Sum(x => x.OutstandingQty * x.UnitPrice)
    };

    return IvMasterOperationResult<SalesOrderOutstandingResult>.Ok(new SalesOrderOutstandingResult
    {
        Rows = rows,
        Totals = totals,
        TotalCount = totalCount
    });
}
```

### 2.3 Document Flow Inquiry Enhancement

**Enhance existing `SaDocFlowQuery` service:**

```csharp
// File: ErpWeb.Core/Sales/SaDocFlowQuery.cs
// Add new method to ISaDocFlowQuery interface
public interface ISaDocFlowQuery
{
    Task<SaDocFlowResult> QueryAsync(
        string docType,
        string docNo,
        CancellationToken cancellationToken = default);
        
    // New method for full document chain
    Task<SaDocFlowChainResult> QueryFullChainAsync(
        string docType,
        string docNo,
        CancellationToken cancellationToken = default);
}

// Add implementation
public async Task<SaDocFlowChainResult> QueryFullChainAsync(
    string docType,
    string docNo,
    CancellationToken cancellationToken = default)
{
    var type = SaDocFlowTypes.Normalize(docType);
    var no = (docNo ?? string.Empty).Trim();
    var scope = _tenant.TryBranchScope();
    if (scope is null || no.Length == 0 || type.Length == 0)
    {
        return new SaDocFlowChainResult { DocType = type, DocNo = no };
    }

    var company = scope.CompanyCode;
    var branch = scope.BranchCode!;
    await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

    var chain = new List<SaDocFlowChainNode>();
    
    // Start from the given document
    var currentNode = new SaDocFlowChainNode
    {
        DocType = type,
        DocNo = no,
        Level = 0
    };
    
    chain.Add(currentNode);
    
    // Traverse upstream
    await TraverseUpstreamAsync(db, company, branch, currentNode, chain, cancellationToken);
    
    // Traverse downstream
    await TraverseDownstreamAsync(db, company, branch, currentNode, chain, cancellationToken);
    
    // Enrich with document details
    await EnrichChainAsync(db, company, branch, chain, cancellationToken);

    return new SaDocFlowChainResult
    {
        DocType = type,
        DocNo = no,
        Chain = chain.OrderBy(x => x.Level).ThenBy(x => x.DocType).ToList()
    };
}

private async Task TraverseUpstreamAsync(
    AppDbContext db,
    string company,
    string branch,
    SaDocFlowChainNode node,
    List<SaDocFlowChainNode> chain,
    CancellationToken cancellationToken)
{
    // Get upstream documents
    var upstream = await db.SaDocApplications.AsNoTracking()
        .Where(x => x.CompanyCode == company && x.BranchCode == branch
            && x.TargetDocType == node.DocType && x.TargetDocId == node.DocNo)
        .ToListAsync(cancellationToken);

    foreach (var app in upstream)
    {
        var parentNode = new SaDocFlowChainNode
        {
            DocType = app.SourceDocType,
            DocNo = app.SourceDocId,
            Line = app.SourceLineId,
            Qty = app.AppliedQty,
            Amount = app.AppliedAmount ?? 0m,
            Relationship = $"{app.SourceDocType} → {node.DocType}",
            Level = node.Level - 1,
            ParentDocType = node.DocType,
            ParentDocNo = node.DocNo
        };

        if (!chain.Any(x => x.DocType == parentNode.DocType && x.DocNo == parentNode.DocNo))
        {
            chain.Add(parentNode);
            // Recursively traverse upstream
            await TraverseUpstreamAsync(db, company, branch, parentNode, chain, cancellationToken);
        }
    }
    
    // Handle quotation → SO conversion
    if (node.DocType == SaDocTypes.So)
    {
        var so = await db.SaSos.AsNoTracking()
            .Where(x => x.CompanyCode == company && x.BranchCode == branch && x.SoNo == node.DocNo && x.IsCurrent)
            .FirstOrDefaultAsync(cancellationToken);
            
        if (so?.QtNo is { Length: > 0 } qtNo)
        {
            var qtNode = new SaDocFlowChainNode
            {
                DocType = SaDocFlowTypes.Qt,
                DocNo = qtNo,
                Relationship = $"QT rev {so.QtCustRel} → SO (conversion)",
                Level = node.Level - 1,
                ParentDocType = node.DocType,
                ParentDocNo = node.DocNo
            };
            
            if (!chain.Any(x => x.DocType == qtNode.DocType && x.DocNo == qtNode.DocNo))
            {
                chain.Add(qtNode);
                await TraverseUpstreamAsync(db, company, branch, qtNode, chain, cancellationToken);
            }
        }
    }
}
```

## 3. Phase 2: Sales Analysis Implementation

### 3.1 Sales by Item Report

**SQL View:**

```sql
-- File: scripts/views/vSalesByItem.sql
CREATE VIEW vSalesByItem AS
SELECT 
    inv.CompanyCode,
    inv.BranchCode,
    inv.InvNo,
    inv.InvDate,
    inv.CustCode,
    inv.CustName,
    inv.SalesmanCode,
    invd.ICode AS ItemCode,
    invd.IDesc AS ItemDesc,
    invd.Qty,
    invd.StdQty,
    invd.UnitPrice,
    invd.Amount,
    invd.NetAmount,
    invd.FrWarehouse,
    invd.Classification,
    stk.ItemType,
    stk.ItemGroup,
    stk.ItemCategory
FROM SaInvoice inv
INNER JOIN SaInvoiceDetail invd ON inv.CompanyCode = invd.CompanyCode 
    AND inv.BranchCode = invd.BranchCode 
    AND inv.InvNo = invd.InvNo
LEFT JOIN IvStockMaster stk ON invd.CompanyCode = stk.CompanyCode 
    AND invd.ICode = stk.ICode
WHERE inv.Status = 'POSTED';
```

**Service Method:**

```csharp
// Add to ISalesReportService.cs
Task<IvMasterOperationResult<SalesByItemResult>> GetSalesByItemAsync(
    SalesByItemQuery query,
    CancellationToken cancellationToken = default);

// Add to SalesReportService.cs
public async Task<IvMasterOperationResult<SalesByItemResult>> GetSalesByItemAsync(
    SalesByItemQuery query,
    CancellationToken cancellationToken = default)
{
    var scope = _tenant.TryBranchScope();
    if (scope is null)
    {
        return IvMasterOperationResult<SalesByItemResult>.Fail(
            IvMasterErrorCode.Unauthorized, "No tenant context");
    }

    var company = scope.CompanyCode;
    var branch = scope.BranchCode;

    await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

    var q = db.Set<SalesByItemView>().AsNoTracking()
        .Where(x => x.CompanyCode == company && x.BranchCode == branch);

    // Apply filters
    if (query.DateFrom is DateTime from)
    {
        var fromDate = from.Date;
        q = q.Where(x => x.InvDate >= fromDate);
    }

    if (query.DateTo is DateTime to)
    {
        var toExclusive = to.Date.AddDays(1);
        q = q.Where(x => x.InvDate < toExclusive);
    }

    if (!string.IsNullOrWhiteSpace(query.ItemCode))
    {
        var item = query.ItemCode.Trim();
        q = q.Where(x => x.ItemCode == item);
    }

    if (!string.IsNullOrWhiteSpace(query.WarehouseCode))
    {
        var warehouse = query.WarehouseCode.Trim();
        q = q.Where(x => x.FrWarehouse == warehouse);
    }

    if (!string.IsNullOrWhiteSpace(query.Category))
    {
        var category = query.Category.Trim();
        q = q.Where(x => x.ItemCategory == category);
    }

    // Group by item
    var grouped = await q
        .GroupBy(x => new { x.ItemCode, x.ItemDesc, x.ItemCategory })
        .Select(g => new SalesByItemRow
        {
            ItemCode = g.Key.ItemCode,
            ItemDesc = g.Key.ItemDesc,
            ItemCategory = g.Key.ItemCategory,
            Quantity = g.Sum(x => x.Qty),
            Amount = g.Sum(x => x.NetAmount),
            InvoiceCount = g.Select(x => x.InvNo).Distinct().Count()
        })
        .OrderByDescending(x => x.Amount)
        .ToListAsync(cancellationToken);

    // Calculate totals
    var totals = new SalesByItemTotals
    {
        TotalItems = grouped.Count,
        TotalQuantity = grouped.Sum(x => x.Quantity),
        TotalAmount = grouped.Sum(x => x.Amount),
        TotalInvoices = grouped.Sum(x => x.InvoiceCount)
    };

    return IvMasterOperationResult<SalesByItemResult>.Ok(new SalesByItemResult
    {
        Rows = grouped,
        Totals = totals
    });
}
```

## 4. Phase 3: Management Dashboard Implementation

### 4.1 Dashboard Service

```csharp
// File: ErpWeb.Core/Sales/ISalesDashboardService.cs
namespace ErpWeb.Core.Sales;

public interface ISalesDashboardService
{
    Task<IvMasterOperationResult<SalesDashboardResult>> GetDashboardDataAsync(
        SalesDashboardQuery query,
        CancellationToken cancellationToken = default);
}

public sealed class SalesDashboardQuery
{
    public DateTime? DateFrom { get; set; }
    public DateTime? DateTo { get; set; }
    public string? BranchCode { get; set; }
}

public sealed class SalesDashboardResult
{
    public SalesKpis Kpis { get; init; } = new();
    public IReadOnlyList<SalesTrendRow> MonthlyTrend { get; init; } = [];
    public IReadOnlyList<TopCustomerRow> TopCustomers { get; init; } = [];
    public IReadOnlyList<TopItemRow> TopItems { get; init; } = [];
    public IReadOnlyList<SalesByCategoryRow> SalesByCategory { get; init; } = [];
}

public sealed class SalesKpis
{
    public decimal SalesToday { get; init; }
    public decimal SalesThisMonth { get; init; }
    public decimal SalesThisYear { get; init; }
    public int OpenQuotations { get; init; }
    public int OpenSalesOrders { get; init; }
    public int PendingDeliveries { get; init; }
    public decimal OutstandingInvoices { get; init; }
    public decimal OverdueInvoices { get; init; }
    public decimal CreditNotesThisMonth { get; init; }
    public decimal DebitNotesThisMonth { get; init; }
}
```

### 4.2 Dashboard UI

```razor
@page "/sales/dashboard"
@inherits SalesDashboardPageBase

<PageTitle>Sales Dashboard</PageTitle>

<MenuAuthorize MenuCode="@MenuCodes.SalesDashboard">
    <div class="iv-page">
        <header class="iv-hero">
            <div class="iv-hero__mark" aria-hidden="true"><i class="fa-solid fa-chart-line"></i></div>
            <div class="iv-hero__copy">
                <p class="iv-eyebrow">Sales · Dashboard</p>
                <h1 class="iv-title">Sales Dashboard</h1>
                <div class="iv-chips">
                    <span class="iv-chip">Real-time KPIs</span>
                    <span class="iv-chip">@DateTime.Now.ToString("dd/MM/yyyy HH:mm")</span>
                </div>
            </div>
        </header>

        @if (DashboardData is not null)
        {
            <!-- KPI Cards -->
            <section class="iv-card">
                <div class="dashboard-kpis">
                    <div class="kpi-card">
                        <div class="kpi-card__header">Sales Today</div>
                        <div class="kpi-card__value">@DashboardData.Kpis.SalesToday.ToString("N2")</div>
                    </div>
                    <div class="kpi-card">
                        <div class="kpi-card__header">Sales This Month</div>
                        <div class="kpi-card__value">@DashboardData.Kpis.SalesThisMonth.ToString("N2")</div>
                    </div>
                    <div class="kpi-card">
                        <div class="kpi-card__header">Sales This Year</div>
                        <div class="kpi-card__value">@DashboardData.Kpis.SalesThisYear.ToString("N2")</div>
                    </div>
                    <div class="kpi-card">
                        <div class="kpi-card__header">Open Quotations</div>
                        <div class="kpi-card__value">@DashboardData.Kpis.OpenQuotations.ToString("N0")</div>
                    </div>
                    <div class="kpi-card">
                        <div class="kpi-card__header">Open Sales Orders</div>
                        <div class="kpi-card__value">@DashboardData.Kpis.OpenSalesOrders.ToString("N0")</div>
                    </div>
                    <div class="kpi-card">
                        <div class="kpi-card__header">Pending Deliveries</div>
                        <div class="kpi-card__value">@DashboardData.Kpis.PendingDeliveries.ToString("N0")</div>
                    </div>
                    <div class="kpi-card">
                        <div class="kpi-card__header">Outstanding Invoices</div>
                        <div class="kpi-card__value">@DashboardData.Kpis.OutstandingInvoices.ToString("N2")</div>
                    </div>
                    <div class="kpi-card">
                        <div class="kpi-card__header">Overdue Invoices</div>
                        <div class="kpi-card__value kpi-card__value--alert">@DashboardData.Kpis.OverdueInvoices.ToString("N2")</div>
                    </div>
                </div>
            </section>

            <!-- Charts Row -->
            <div class="dashboard-charts">
                <section class="iv-card">
                    <h3>Monthly Sales Trend</h3>
                    <DxChart Data="@DashboardData.MonthlyTrend">
                        <DxChartLineSeries ArgumentField="@((SalesTrendRow x) => x.Month)"
                                          ValueField="@((SalesTrendRow x) => x.Amount)"
                                          Name="Sales Amount" />
                    </DxChart>
                </section>

                <section class="iv-card">
                    <h3>Top 10 Customers</h3>
                    <DxChart Data="@DashboardData.TopCustomers.Take(10)">
                        <DxChartBarSeries ArgumentField="@((TopCustomerRow x) => x.CustName)"
                                          ValueField="@((TopCustomerRow x) => x.Amount)"
                                          Name="Sales Amount" />
                    </DxChart>
                </section>
            </div>

            <!-- Tables Row -->
            <div class="dashboard-tables">
                <section class="iv-card">
                    <h3>Sales by Category</h3>
                    <DxGrid Data="@DashboardData.SalesByCategory" CssClass="w-100">
                        <Columns>
                            <DxGridDataColumn FieldName="@nameof(SalesByCategoryRow.Category)" Caption="Category" />
                            <DxGridDataColumn FieldName="@nameof(SalesByCategoryRow.Amount)" Caption="Amount" DisplayFormat="n2" />
                            <DxGridDataColumn FieldName="@nameof(SalesByCategoryRow.Percentage)" Caption="%" DisplayFormat="n1" />
                        </Columns>
                    </DxGrid>
                </section>

                <section class="iv-card">
                    <h3>Top 10 Items</h3>
                    <DxGrid Data="@DashboardData.TopItems.Take(10)" CssClass="w-100">
                        <Columns>
                            <DxGridDataColumn FieldName="@nameof(TopItemRow.ItemCode)" Caption="Item" />
                            <DxGridDataColumn FieldName="@nameof(TopItemRow.ItemDesc)" Caption="Description" />
                            <DxGridDataColumn FieldName="@nameof(TopItemRow.Quantity)" Caption="Qty" />
                            <DxGridDataColumn FieldName="@nameof(TopItemRow.Amount)" Caption="Amount" DisplayFormat="n2" />
                        </Columns>
                    </DxGrid>
                </section>
            </div>
        }
    </div>
</MenuAuthorize>
```

## 5. Phase 4: Advanced Analysis Implementation

### 5.1 Customer Statement Report

**Complex Query Required:**

```csharp
// File: ErpWeb.Core/Sales/CustomerStatementService.cs
public sealed class CustomerStatementService : ICustomerStatementService
{
    public async Task<IvMasterOperationResult<CustomerStatementResult>> GetCustomerStatementAsync(
        CustomerStatementQuery query,
        CancellationToken cancellationToken = default)
    {
        var scope = _tenant.TryBranchScope();
        if (scope is null)
        {
            return IvMasterOperationResult<CustomerStatementResult>.Fail(
                IvMasterErrorCode.Unauthorized, "No tenant context");
        }

        var company = scope.CompanyCode;
        var branch = scope.BranchCode;

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        // Get opening balance (transactions before DateFrom)
        var openingBalance = await CalculateOpeningBalanceAsync(db, company, branch, query, cancellationToken);

        // Get transactions within date range
        var transactions = await GetTransactionsAsync(db, company, branch, query, cancellationToken);

        // Calculate running balance
        var runningBalance = openingBalance;
        var rows = new List<CustomerStatementRow>();
        
        foreach (var txn in transactions.OrderBy(x => x.DocDate).ThenBy(x => x.DocNo))
        {
            runningBalance += txn.Debit - txn.Credit;
            rows.Add(new CustomerStatementRow
            {
                DocDate = txn.DocDate,
                DocType = txn.DocType,
                DocNo = txn.DocNo,
                Reference = txn.Reference,
                Debit = txn.Debit,
                Credit = txn.Credit,
                Balance = runningBalance
            });
        }

        return IvMasterOperationResult<CustomerStatementResult>.Ok(new CustomerStatementResult
        {
            OpeningBalance = openingBalance,
            ClosingBalance = runningBalance,
            Rows = rows,
            Totals = new CustomerStatementTotals
            {
                TotalDebit = rows.Sum(x => x.Debit),
                TotalCredit = rows.Sum(x => x.Credit)
            }
        });
    }

    private async Task<decimal> CalculateOpeningBalanceAsync(
        AppDbContext db,
        string company,
        string branch,
        CustomerStatementQuery query,
        CancellationToken cancellationToken)
    {
        var custCode = query.CustCode;
        var dateFrom = query.DateFrom?.Date ?? DateTime.MinValue;

        // Sum invoices before dateFrom
        var invoiceTotal = await db.SaInvoices.AsNoTracking()
            .Where(x => x.CompanyCode == company && x.BranchCode == branch
                && x.CustCode == custCode
                && x.Status == "POSTED"
                && x.InvDate < dateFrom)
            .SumAsync(x => (decimal?)x.TotAmnt, cancellationToken) ?? 0m;

        // Sum credit notes before dateFrom
        var creditNoteTotal = await db.SaCdns.AsNoTracking()
            .Where(x => x.CompanyCode == company && x.BranchCode == branch
                && x.CustCode == custCode
                && x.Status == "POSTED"
                && x.Type == "CN"
                && x.DocDate < dateFrom)
            .SumAsync(x => (decimal?)x.TotAmnt, cancellationToken) ?? 0m;

        // Sum debit notes before dateFrom
        var debitNoteTotal = await db.SaCdns.AsNoTracking()
            .Where(x => x.CompanyCode == company && x.BranchCode == branch
                && x.CustCode == custCode
                && x.Status == "POSTED"
                && x.Type == "DN"
                && x.DocDate < dateFrom)
            .SumAsync(x => (decimal?)x.TotAmnt, cancellationToken) ?? 0m;

        // TODO: Add payments when payment integration is available
        
        return invoiceTotal + debitNoteTotal - creditNoteTotal;
    }
}
```

## 6. Implementation Guidelines

### 6.1 File Naming Conventions

**Services:**
- Interface: `I{ReportName}Service.cs`
- Implementation: `{ReportName}Service.cs`
- Results: `{ReportName}Results.cs`
- Query: `{ReportName}Query.cs`

**UI Pages:**
- Page: `{ReportName}.razor`
- Code-behind: `{ReportName}.razor.cs`
- Styles: `{ReportName}.razor.css`

**SQL Views:**
- `v{ReportName}.sql`

### 6.2 Menu Registration

**Add to menu configuration:**

```csharp
// File: ErpWeb.Core/Menus/MenuCodes.cs
public static class MenuCodes
{
    // Existing codes...
    
    // Phase 1
    public const string SalesCustomerTransactions = "SA_RPT_CUST_TXN";
    public const string SalesOrderOutstanding = "SA_RPT_SO_OUT";
    public const string DeliveryStatus = "SA_RPT_DO_STATUS";
    public const string InvoiceOutstanding = "SA_RPT_INV_OUT";
    public const string InvoiceAging = "SA_RPT_INV_AGING";
    public const string EInvoiceStatus = "SA_RPT_EINV_STATUS";
    
    // Phase 2
    public const string SalesByItem = "SA_RPT_BY_ITEM";
    public const string SalesByCategory = "SA_RPT_BY_CAT";
    public const string SalesByWarehouse = "SA_RPT_BY_WH";
    
    // Phase 3
    public const string SalesDashboard = "SA_DASHBOARD";
    
    // Phase 4
    public const string CustomerStatement = "SA_RPT_CUST_STMT";
    public const string Profitability = "SA_RPT_PROFIT";
}
```

### 6.3 Dependency Injection Registration

**Register services in `CoreServiceCollectionExtensions.cs`:**

```csharp
// File: ErpWeb.Core/CoreServiceCollectionExtensions.cs
services.AddScoped<ISalesReportService, SalesReportService>();
services.AddScoped<ISalesDashboardService, SalesDashboardService>();
services.AddScoped<ICustomerStatementService, CustomerStatementService>();
// Add more as needed
```

### 6.4 Database Migration

**Create migration for SQL views:**

```bash
# Add migration for new views
dotnet ef migrations add AddSalesReportViews --project ErpWeb.Model --startup-project ErpWeb
```

**Migration file:**

```csharp
// File: ErpWeb.Model/Migrations/XXXXXXXXXXXXXX_AddSalesReportViews.cs
public partial class AddSalesReportViews : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(@"
            CREATE VIEW vSalesOrderOutstanding AS
            -- SQL from section 2.2
        ");
        
        migrationBuilder.Sql(@"
            CREATE VIEW vSalesByItem AS
            -- SQL from section 3.1
        ");
        
        // Add more views
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("DROP VIEW IF EXISTS vSalesOrderOutstanding");
        migrationBuilder.Sql("DROP VIEW IF EXISTS vSalesByItem");
        // Drop other views
    }
}
```

## 7. Testing Strategy

### 7.1 Unit Tests

```csharp
// File: ErpWeb.Tests/Sales/SalesReportServiceTests.cs
[TestClass]
public class SalesReportServiceTests
{
    [TestMethod]
    public async Task GetCustomerTransactions_WithFilters_ReturnsFilteredResults()
    {
        // Arrange
        var service = CreateService();
        var query = new CustomerTransactionQuery
        {
            DateFrom = DateTime.Today.AddDays(-30),
            DateTo = DateTime.Today,
            CustCode = "CUST001"
        };

        // Act
        var result = await service.GetCustomerTransactionsAsync(query);

        // Assert
        Assert.IsTrue(result.Succeeded);
        Assert.IsNotNull(result.Data);
        Assert.IsTrue(result.Data.Rows.All(x => x.CustCode == "CUST001"));
    }
}
```

### 7.2 Integration Tests

```csharp
// File: ErpWeb.Tests/Integration/SalesReportIntegrationTests.cs
[TestClass]
public class SalesReportIntegrationTests
{
    [TestMethod]
    public async Task SalesDashboard_ReturnsAllKpis()
    {
        // Arrange
        var client = CreateAuthenticatedClient();

        // Act
        var response = await client.GetAsync("/api/sales/dashboard");

        // Assert
        response.EnsureSuccessStatusCode();
        var content = await response.Content.ReadAsStringAsync();
        var dashboard = JsonSerializer.Deserialize<SalesDashboardResult>(content);
        
        Assert.IsNotNull(dashboard);
        Assert.IsNotNull(dashboard.Kpis);
        Assert.IsTrue(dashboard.MonthlyTrend.Count > 0);
    }
}
```

## 8. Performance Optimization

### 8.1 Indexing Strategy

```sql
-- Create indexes for common query patterns
CREATE INDEX IX_SaInvoice_CustDate_Status 
ON SaInvoice (CompanyCode, BranchCode, CustCode, InvDate, Status)
INCLUDE (TotAmnt, GrossAmnt, Taxes);

CREATE INDEX IX_SaSo_CustDate_Status 
ON SaSo (CompanyCode, BranchCode, CustCode, SoDate, Status, IsCurrent)
INCLUDE (TotAmnt, SalesRep);

CREATE INDEX IX_SaDoDetail_SoNo 
ON SaDoDetail (CompanyCode, BranchCode, SoNo, SoLine)
INCLUDE (Qty, StdQty);
```

### 8.2 Query Optimization

1. **Use IQueryable with filters before materialization**
2. **Implement pagination at database level**
3. **Use AsNoTracking() for read-only queries**
4. **Batch related queries where possible**
5. **Use compiled queries for frequently executed queries**

### 8.3 Caching Strategy

```csharp
// Cache reference data
services.AddMemoryCache();

// In service
public async Task<IReadOnlyList<IvCodeLookupRow>> GetCustomersAsync()
{
    var cacheKey = "customers_list";
    if (!_cache.TryGetValue(cacheKey, out IReadOnlyList<IvCodeLookupRow> customers))
    {
        customers = await LoadCustomersFromDbAsync();
        _cache.Set(cacheKey, customers, TimeSpan.FromMinutes(30));
    }
    return customers;
}
```

## 9. Deployment Checklist

### 9.1 Pre-deployment

- [ ] All SQL views created and tested
- [ ] Database migrations applied
- [ ] Services registered in DI container
- [ ] Menu codes added and permissions configured
- [ ] UI pages created and tested
- [ ] Unit tests passing
- [ ] Integration tests passing
- [ ] Performance tests completed

### 9.2 Deployment Steps

1. Backup database
2. Apply database migrations
3. Deploy application code
4. Verify menu items appear
5. Test each report with sample data
6. Monitor performance
7. Collect user feedback

### 9.3 Post-deployment

- [ ] User training completed
- [ ] Documentation updated
- [ ] Support procedures established
- [ ] Performance monitoring in place
- [ ] Feedback collection mechanism active

---

*Document Version: 1.0*
*Last Updated: 2026-09-26*
*Prepared by: AI Assistant*