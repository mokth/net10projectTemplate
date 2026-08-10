using Microsoft.Extensions.Options;

namespace ErpWeb.Core.Menus;

public sealed class MenusOptionsValidator : IValidateOptions<MenusOptions>
{
    public ValidateOptionsResult Validate(string? name, MenusOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.XmlPath))
        {
            return ValidateOptionsResult.Fail("Menus:XmlPath is required.");
        }

        return ValidateOptionsResult.Success;
    }
}
