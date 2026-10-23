using System.ComponentModel.DataAnnotations;

namespace ErpWeb.Model.Repositories;

/// <summary>
/// Narrow contract for creating a user in User Accounts. The server derives company, branch,
/// location and audit fields from the authenticated context; the password is a temporary value
/// that is validated and hashed server-side only.
/// </summary>
public sealed class CreateUserRequest
{
    [Required]
    [MaxLength(10)]
    public string LoginId { get; set; } = string.Empty;

    [Required]
    [MaxLength(50)]
    public string Name { get; set; } = string.Empty;

    [Required]
    [MaxLength(100)]
    public string TemporaryPassword { get; set; } = string.Empty;

    [Required]
    [MaxLength(100)]
    public string ConfirmPassword { get; set; } = string.Empty;

    [MaxLength(50)]
    public string? Email { get; set; }

    [MaxLength(20)]
    public string? MobileNo { get; set; }

    public bool Active { get; set; }

    [Required]
    [MaxLength(20)]
    public string RoleCode { get; set; } = string.Empty;

    public bool CanViewPrice { get; set; }
}

/// <summary>
/// Narrow contract for editing a user in User Accounts. No password, company, branch, location,
/// image or audit mutation fields are present; <see cref="LoginId"/> is display-only.
/// </summary>
public sealed class EditUserRequest
{
    public int Uid { get; set; }

    [MaxLength(10)]
    public string LoginId { get; set; } = string.Empty;

    [Required]
    [MaxLength(50)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(50)]
    public string? Email { get; set; }

    [MaxLength(20)]
    public string? MobileNo { get; set; }

    public bool Active { get; set; }

    [Required]
    [MaxLength(20)]
    public string RoleCode { get; set; } = string.Empty;

    public bool CanViewPrice { get; set; }
}

/// <summary>
/// Form model for an administrator password reset. <see cref="LoginId"/> and <see cref="Name"/>
/// are display-only; the service reads only <see cref="Uid"/>, <see cref="NewPassword"/> and
/// <see cref="ConfirmPassword"/>.
/// </summary>
public sealed class ResetUserPasswordRequest
{
    public int Uid { get; set; }

    [MaxLength(10)]
    public string LoginId { get; set; } = string.Empty;

    [MaxLength(50)]
    public string Name { get; set; } = string.Empty;

    [Required]
    [MaxLength(100)]
    public string NewPassword { get; set; } = string.Empty;

    [Required]
    [MaxLength(100)]
    public string ConfirmPassword { get; set; } = string.Empty;
}
