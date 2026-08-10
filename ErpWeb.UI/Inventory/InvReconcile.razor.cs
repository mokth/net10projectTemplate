using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.UI.Components.Pages;
using Microsoft.AspNetCore.Components;

namespace ErpWeb.UI.Inventory;

public partial class InvReconcile : PageBase
{
    [Inject] private IInventoryReconciliationService Recon { get; set; } = default!;
    protected string? StatusMessage;
    protected bool Busy;
    protected bool _ran;
    protected List<StockIntegrityIssue> Issues { get; set; } = [];

    protected async Task FindAsync()
    {
        Busy = true; ErrorMessage = null;
        try
        {
            Issues = (await Recon.FindIssuesAsync()).ToList();
            _ran = true;
            StatusMessage = $"{Issues.Count} issue(s).";
        }
        finally { Busy = false; }
    }

    protected async Task RebuildAsync()
    {
        Busy = true; ErrorMessage = null;
        try
        {
            var result = await Recon.RebuildOperationalBalancesAsync();
            if (!result.Succeeded) { ErrorMessage = $"{result.ErrorCode}: {result.ErrorMessage}"; return; }
            StatusMessage = $"Rebuilt balances={result.BalanceRows}, lots={result.LotBalanceRows}, costs={result.ItemCostRows}.";
            await FindAsync();
        }
        finally { Busy = false; }
    }
}
