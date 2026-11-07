# Change audit - global inline search

Branch: `ui/global-inline-search-dev`  
Base: `8d09a224ae740695199e4bed30c475afb2cf268f`

## Path classes

| Class | Count | Verdict |
| --- | --- | --- |
| ErpWeb.UI markup (*.razor) | 62 | LAYOUT-ONLY |
| ErpWeb.UI scoped styles (*.razor.css) | 2 | LAYOUT-ONLY |
| ErpWeb.UI code-behind (*.razor.cs) | 41 | LAYOUT-ONLY (see member list) |
| Shared stylesheet (ErpWeb/wwwroot/css) | 1 | LAYOUT-ONLY |
| Source contract tests (ErpWeb.Tests/UI) | 1 | TEST-ONLY |

## Denylist check

No file under `ErpWeb.Core/`, `ErpWeb.Model/`, `ErpWeb.Data/`, SQL or migrations was touched. Result: **PASS**.

## Method-level review of code-behind changes

Member declarations added or removed in each `.razor.cs` file. Only search wiring, layout state, debounce/generation bookkeeping and disposal are permitted.

### `ErpWeb.UI/Inventory/Inquiry/IvBalanceLot.razor.cs`
- `private bool _disposed;`
- `private void CancelPendingSearch()`
- `protected async Task ApplySearchAsync()`

### `ErpWeb.UI/Inventory/Inquiry/IvTrxInquiry.razor.cs`
- `private bool _disposed;`
- `private void CancelPendingSearch()`
- `protected async Task ApplySearchAsync()`

### `ErpWeb.UI/Inventory/Masters/IvStockMasterList.razor.cs`
- `private bool _disposed;`
- `private void CancelPendingSearch()`
- `protected async Task ApplySearchAsync()`

### `ErpWeb.UI/Inventory/Transactions/IvGoodsReceipt.razor.cs`
- `protected async Task ApplyPoPickerSearchAsync()`

### `ErpWeb.UI/Inventory/Transactions/IvGoodsReceiptList.razor.cs`
- `private bool _disposed;`
- `private void CancelPendingSearch()`
- `protected async Task ApplySearchAsync()`

### `ErpWeb.UI/Inventory/Transactions/IvMiscIssueList.razor.cs`
- `private bool _disposed;`
- `private void CancelPendingSearch()`
- `protected async Task ApplySearchAsync()`

### `ErpWeb.UI/Inventory/Transactions/IvMiscReceiptList.razor.cs`
- `private bool _disposed;`
- `private void CancelPendingSearch()`
- `protected async Task ApplySearchAsync()`

### `ErpWeb.UI/Inventory/Transactions/IvScrapList.razor.cs`
- `private bool _disposed;`
- `private void CancelPendingSearch()`
- `protected async Task ApplySearchAsync()`

### `ErpWeb.UI/Inventory/Transactions/IvStockAdjustmentList.razor.cs`
- `private bool _disposed;`
- `private void CancelPendingSearch()`
- `protected async Task ApplySearchAsync()`

### `ErpWeb.UI/Inventory/Transactions/IvStockCountList.razor.cs`
- `private bool _disposed;`
- `private void CancelPendingSearch()`
- `protected async Task ApplySearchAsync()`

### `ErpWeb.UI/Inventory/Transactions/IvStockReturnList.razor.cs`
- `private bool _disposed;`
- `private void CancelPendingSearch()`
- `protected async Task ApplySearchAsync()`

### `ErpWeb.UI/Inventory/Transactions/IvStockTransferList.razor.cs`
- `private bool _disposed;`
- `private void CancelPendingSearch()`
- `protected async Task ApplySearchAsync()`

### `ErpWeb.UI/Inventory/Transactions/IvVendorReturnList.razor.cs`
- `private bool _disposed;`
- `private void CancelPendingSearch()`
- `protected async Task ApplySearchAsync()`

### `ErpWeb.UI/Planning/Inquiry/PrProductionBalance.razor.cs`
- `private bool _disposed;`
- `private void CancelPendingSearch()`
- `protected async Task ApplySearchAsync()`

### `ErpWeb.UI/Planning/Masters/PrProductDefList.razor.cs`
- `private bool _disposed;`
- `private void CancelPendingSearch()`
- `protected async Task ApplySearchAsync()`

### `ErpWeb.UI/Planning/WorkOrders/PrDailyProductionList.razor.cs`
- `private bool _disposed;`
- `private void CancelPendingSearch()`
- `protected async Task ApplySearchAsync()`

### `ErpWeb.UI/Planning/WorkOrders/PrFinishedGoodReceiptList.razor.cs`
- `private bool _disposed;`
- `private void CancelPendingSearch()`
- `protected async Task ApplySearchAsync()`

### `ErpWeb.UI/Planning/WorkOrders/PrMachineSchedulePlanner.razor.cs`
- `private async Task ApplySearchAsync()`

### `ErpWeb.UI/Planning/WorkOrders/PrMaterialIssueList.razor.cs`
- `private bool _disposed;`
- `private void CancelPendingSearch()`
- `protected async Task ApplySearchAsync()`

### `ErpWeb.UI/Planning/WorkOrders/PrProductionStockAdjustmentList.razor.cs`
- `private DxGrid? _grid; private Timer? _debounce; private int _version; private bool _disposed; private readonly List<ProductionStockAdjustmentSummary> _selected=[];`
- `private DxGrid? _grid; private Timer? _debounce; private int _version; private readonly List<ProductionStockAdjustmentSummary> _selected=[];`
- `private void CancelPendingSearch()`
- `protected async Task ApplySearchAsync()`
- `protected Task OnSearchChanged(string value)`
- `protected Task OnSearchChanged(string value){SearchText=value??"";_debounce?.Stop();_debounce?.Dispose();_debounce=new Timer(400){AutoReset=false};var n=++_version;_debounce.Elapsed+=async(_,_)=>{if(n==_version)await InvokeAsync(ReloadAsync);};_debounce.Start();return Task.CompletedTask;}`
- `public void Dispose(){ if (_disposed) return; _disposed = true; Interlocked.Increment(ref _version); var timer = Interlocked.Exchange(ref _debounce, null); timer?.Stop(); timer?.Dispose(); } protected sealed record StatusChoice(string Value,string Text);`
- `public void Dispose(){_debounce?.Stop();_debounce?.Dispose();} protected sealed record StatusChoice(string Value,string Text);`

### `ErpWeb.UI/Planning/WorkOrders/PrProductionStockReturnEntry.razor.cs`
- `protected Task ApplySourceSearchAsync()`

### `ErpWeb.UI/Planning/WorkOrders/PrProductionStockReturnList.razor.cs`
- `private DxGrid? _grid; private Timer? _debounce; private int _version;`
- `private DxGrid? _grid; private Timer? _debounce; private int _version; private bool _disposed;`
- `private void CancelPendingSearch()`
- `protected async Task ApplySearchAsync()`
- `protected async Task OnSearchChanged(string value) { SearchText = value ?? ""; _debounce?.Stop(); _debounce?.Dispose(); _debounce = new Timer(400) { AutoReset=false }; var n=++_version; _debounce.Elapsed += async (_,_) => { if(n==_version) await InvokeAsync(ReloadAsync); }; _debounce.Start(); }`
- `protected Task OnSearchChanged(string value)`
- `public void Dispose(){ if (_disposed) return; _disposed = true; Interlocked.Increment(ref _version); var timer = Interlocked.Exchange(ref _debounce, null); timer?.Stop(); timer?.Dispose(); }`
- `public void Dispose(){_debounce?.Stop();_debounce?.Dispose();}`

### `ErpWeb.UI/Planning/WorkOrders/PrWorkOrderList.razor.cs`
- `private bool _disposed;`
- `private void CancelPendingSearch()`
- `protected async Task ApplySearchAsync()`

### `ErpWeb.UI/Planning/WorkOrders/PrWorkOrderTransferEntry.razor.cs`
- `protected async Task ApplySourceSearchAsync()`

### `ErpWeb.UI/Planning/WorkOrders/PrWorkOrderTransferList.razor.cs`
- `private bool _disposed;`
- `private void CancelPendingSearch()`
- `protected async Task ApplySearchAsync()`

### `ErpWeb.UI/Purchase/Masters/PoSuppList.razor.cs`
- `private bool _disposed;`
- `private void CancelPendingSearch()`
- `protected async Task ApplySearchAsync()`

### `ErpWeb.UI/Purchase/Transactions/PoCdnList.razor.cs`
- `private bool _disposed;`
- `private void CancelPendingSearch()`
- `protected async Task ApplySearchAsync()`

### `ErpWeb.UI/Purchase/Transactions/PoInvoiceList.razor.cs`
- `private bool _disposed;`
- `private void CancelPendingSearch()`
- `protected async Task ApplySearchAsync()`

### `ErpWeb.UI/Purchase/Transactions/PoOrderList.razor.cs`
- `private bool _disposed;`
- `private void CancelPendingSearch()`
- `protected async Task ApplySearchAsync()`

### `ErpWeb.UI/Purchase/Transactions/PoPrList.razor.cs`
- `private bool _disposed;`
- `private void CancelPendingSearch()`
- `protected async Task ApplySearchAsync()`

### `ErpWeb.UI/Purchase/Transactions/PoSbCdnList.razor.cs`
- `private bool _disposed;`
- `private void CancelPendingSearch()`
- `protected async Task ApplySearchAsync()`

### `ErpWeb.UI/Purchase/Transactions/PoSbInvoiceList.razor.cs`
- `private bool _disposed;`
- `private void CancelPendingSearch()`
- `protected async Task ApplySearchAsync()`

### `ErpWeb.UI/Sales/Masters/SaCustList.razor.cs`
- `private bool _disposed;`
- `private void CancelPendingSearch()`
- `protected async Task ApplySearchAsync()`

### `ErpWeb.UI/Sales/Transactions/SaCdnList.razor.cs`
- `private bool _disposed;`
- `private void CancelPendingSearch()`
- `protected async Task ApplySearchAsync()`

### `ErpWeb.UI/Sales/Transactions/SaDeliveryRequestList.razor.cs`
- `private bool _disposed;`
- `private void CancelPendingSearch()`
- `protected async Task ApplySearchAsync()`

### `ErpWeb.UI/Sales/Transactions/SaDoList.razor.cs`
- `private bool _disposed;`
- `private void CancelPendingSearch()`
- `protected async Task ApplySearchAsync()`

### `ErpWeb.UI/Sales/Transactions/SaInvoiceList.razor.cs`
- `private bool _disposed;`
- `private void CancelPendingSearch()`
- `protected async Task ApplySearchAsync()`

### `ErpWeb.UI/Sales/Transactions/SaQtList.razor.cs`
- `private bool _disposed;`
- `private void CancelPendingSearch()`
- `protected async Task ApplySearchAsync()`

### `ErpWeb.UI/Sales/Transactions/SaSoList.razor.cs`
- `private bool _disposed;`
- `private void CancelPendingSearch()`
- `protected async Task ApplySearchAsync()`


