using ErpWeb.Core.Menus;
using ErpWeb.Core.Planning;
using ErpWeb.UI.Components.Pages;
using Microsoft.AspNetCore.Components;

namespace ErpWeb.UI.Planning.Masters;

public partial class PrBomExplorer : PageBase
{
    [Parameter] public string ProdCode { get; set; } = string.Empty;

    [Inject] private IBomExplosionService Explosion { get; set; } = default!;

    protected decimal Quantity { get; set; } = 1m;
    protected DateTime AsOfDate { get; set; } = DateTime.UtcNow.Date;
    protected string SelectedMode { get; set; } = nameof(BomExplosionMode.StructuralTree);
    protected bool IsLoading;
    protected BomExplosionResult? Result { get; set; }

    protected IReadOnlyList<ModeOption> ModeOptions { get; } =
    [
        new(nameof(BomExplosionMode.StructuralTree), "Structural Tree"),
        new(nameof(BomExplosionMode.MaterialRequirement), "Material Requirement"),
        new(nameof(BomExplosionMode.ProductionIssueRequirement), "Production Issue Requirement")
    ];

    protected string ModeTitle => ModeOptions.FirstOrDefault(x => x.Value == SelectedMode)?.Text
        ?? "Explosion";

    protected override async Task OnParametersSetAsync()
    {
        if (!string.IsNullOrWhiteSpace(ProdCode))
        {
            await ExplodeAsync();
        }
    }

    protected async Task ExplodeAsync()
    {
        IsLoading = true;
        ErrorMessage = null;
        try
        {
            if (!Enum.TryParse<BomExplosionMode>(SelectedMode, out var mode))
            {
                mode = BomExplosionMode.StructuralTree;
            }

            var result = await Explosion.ExplodeAsync(new BomExplosionRequest
            {
                ProdCode = ProdCode,
                Quantity = Quantity,
                AsOfDate = AsOfDate,
                Mode = mode
            });

            if (!result.Succeeded || result.Data is null)
            {
                Result = null;
                ErrorMessage = result.Message ?? "Explosion failed.";
                return;
            }

            Result = result.Data;
        }
        finally
        {
            IsLoading = false;
        }
    }

    protected Task GoBackAsync()
    {
        Navigation.NavigateTo($"/planning/product-definitions/view/{Uri.EscapeDataString(ProdCode)}");
        return Task.CompletedTask;
    }

    protected sealed record ModeOption(string Value, string Text);
}
