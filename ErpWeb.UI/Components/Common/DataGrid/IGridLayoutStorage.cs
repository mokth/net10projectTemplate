using System.Text.Json;
using DevExpress.Blazor;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;

namespace ErpWeb.UI.Components.Common.DataGrid;

public interface IGridLayoutStorage
{
    Task<GridPersistentLayout?> LoadAsync(string gridKey);
    Task SaveAsync(string gridKey, GridPersistentLayout layout);
}

public class LocalStorageGridLayoutStorage : IGridLayoutStorage
{
    private readonly IJSRuntime _jsRuntime;
    private readonly ILogger<LocalStorageGridLayoutStorage> _logger;

    public LocalStorageGridLayoutStorage(IJSRuntime jsRuntime, ILogger<LocalStorageGridLayoutStorage> logger)
    {
        _jsRuntime = jsRuntime;
        _logger = logger;
    }

    public async Task<GridPersistentLayout?> LoadAsync(string gridKey)
    {
        if (string.IsNullOrWhiteSpace(gridKey))
        {
            return null;
        }

        try
        {
            var json = await _jsRuntime.InvokeAsync<string?>("localStorage.getItem", BuildKey(gridKey));
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            return JsonSerializer.Deserialize<GridPersistentLayout>(json);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load grid layout for {GridKey}", gridKey);
            return null;
        }
    }

    public async Task SaveAsync(string gridKey, GridPersistentLayout layout)
    {
        if (string.IsNullOrWhiteSpace(gridKey))
        {
            return;
        }

        try
        {
            var json = JsonSerializer.Serialize(layout);
            await _jsRuntime.InvokeVoidAsync("localStorage.setItem", BuildKey(gridKey), json);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save grid layout for {GridKey}", gridKey);
        }
    }

    private static string BuildKey(string gridKey) => $"erp-grid-layout:{gridKey}";
}
