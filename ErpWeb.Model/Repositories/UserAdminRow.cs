namespace ErpWeb.Model.Repositories;

/// <summary>
/// Company-scoped user projection for the User Accounts grid and password administration.
/// Intentionally excludes password/hash columns and the company/branch/location/image fields
/// that User Accounts must never expose or edit.
/// </summary>
public sealed class UserAdminRow
{
    public int Uid { get; init; }

    public string LoginId { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public string? Email { get; init; }

    public string? MobileNo { get; init; }

    public bool? Active { get; init; }

    public string? RoleCode { get; init; }

    public bool ChangePass { get; init; }

    public bool CanViewPrice { get; init; }

    public DateTime? Created { get; init; }

    public string? CreatedBy { get; init; }

    public DateTime? Updated { get; init; }

    public string? UpdatedBy { get; init; }
}
