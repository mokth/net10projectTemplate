namespace ErpWeb.Model.Repositories;

/// <summary>
/// Company-scoped user list projection for password administration.
/// Intentionally excludes password/hash columns.
/// </summary>
public sealed class PasswordAdminUserRow
{
    public int Uid { get; init; }

    public string LoginId { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public string? UserId { get; init; }

    public bool? Active { get; init; }

    public bool ChangePass { get; init; }
}
