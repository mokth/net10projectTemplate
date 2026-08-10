namespace ErpWeb.Core.Security;

/// <summary>
/// First-admin and default tenancy values collected when creating a new company.
/// </summary>
public sealed class CompanyBootstrapRequest
{
    public string AdminLoginId { get; set; } = "admin";

    public string AdminDisplayName { get; set; } = string.Empty;

    public string? AdminEmail { get; set; }

    public string AdminPassword { get; set; } = string.Empty;

    public string ConfirmPassword { get; set; } = string.Empty;

    public string BranchCode { get; set; } = "HQ";

    public string LocationCode { get; set; } = "MAIN";

    /// <summary>
    /// Optional template company whose roles/permissions are copied. When null, prefers DEMO then the creator's company.
    /// </summary>
    public string? TemplateCompanyCode { get; set; }
}
