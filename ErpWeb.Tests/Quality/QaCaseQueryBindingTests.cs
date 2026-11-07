using System.Reflection;
using ErpWeb.UI.Quality;
using Microsoft.AspNetCore.Components;

namespace ErpWeb.Tests.Quality;

[Trait(TestCategories.Name, TestCategories.Quality)]
public class QaCaseQueryBindingTests
{
    [Fact]
    public void Case_entry_does_not_bind_query_values_as_int16()
    {
        var unsupported = typeof(QaCaseEntry)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetCustomAttribute<SupplyParameterFromQueryAttribute>() is not null)
            .Where(p => p.PropertyType == typeof(short) || p.PropertyType == typeof(short?))
            .Select(p => p.Name)
            .ToList();

        Assert.True(
            unsupported.Count == 0,
            "Blazor cannot parse query strings as Int16. Bind poRelNo as int? then cast. Found: "
            + string.Join(", ", unsupported));
    }
}
