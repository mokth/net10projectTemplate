using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Components;

namespace ErpWeb.UI.Components.Pages;

public partial class ForgotPassword
{
    private ForgotPasswordModel Model { get; set; } = new();

    private bool Submitted { get; set; }

    private bool IsSubmitting { get; set; }

    private async Task SubmitAsync()
    {
        if (IsSubmitting)
        {
            return;
        }

        IsSubmitting = true;
        try
        {
            // No self-service email reset in this product — admins reset passwords.
            // Always show the same outcome so account existence is not revealed.
            await Task.Delay(350);
            Submitted = true;
        }
        finally
        {
            IsSubmitting = false;
        }
    }

    private sealed class ForgotPasswordModel
    {
        [Required(ErrorMessage = "Company code is required.")]
        [MaxLength(5)]
        public string CompanyCode { get; set; } = "DEMO";

        [Required(ErrorMessage = "Username is required.")]
        [MaxLength(10)]
        public string Username { get; set; } = string.Empty;
    }
}
