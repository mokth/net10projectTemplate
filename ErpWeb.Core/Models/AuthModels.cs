using System.ComponentModel.DataAnnotations;

namespace ErpWeb.Core.Models;

public class LoginInputModel
{
    [Required(ErrorMessage = "Company code is required.")]
    [MaxLength(5)]
    public string CompanyCode { get; set; } = string.Empty;

    [Required(ErrorMessage = "Username is required.")]
    [MaxLength(10)]
    public string Username { get; set; } = string.Empty;

    [Required(ErrorMessage = "Password is required.")]
    [DataType(DataType.Password)]
    public string Password { get; set; } = string.Empty;

    public bool RememberMe { get; set; }
}

public class ChangePasswordInputModel
{
    [Required]
    [DataType(DataType.Password)]
    public string CurrentPassword { get; set; } = string.Empty;

    [Required]
    [MinLength(8)]
    [DataType(DataType.Password)]
    public string NewPassword { get; set; } = string.Empty;

    [Required]
    [Compare(nameof(NewPassword), ErrorMessage = "Passwords do not match.")]
    [DataType(DataType.Password)]
    public string ConfirmPassword { get; set; } = string.Empty;
}
