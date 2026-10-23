using Microsoft.AspNetCore.Components;

namespace ErpWeb.UI.Components.Pages;

public partial class ForgotPassword
{
    [SupplyParameterFromQuery(Name = "submitted")]
    public bool Submitted { get; set; }
}
