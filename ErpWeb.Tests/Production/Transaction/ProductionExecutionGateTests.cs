using ErpWeb.Core.Inventory;
using ErpWeb.Core.Production;

namespace ErpWeb.Tests;

[Trait(TestCategories.Name, TestCategories.Planning)]
public sealed class ProductionExecutionGateTests
{
    [Fact]
    public void Execution_posting_stays_closed()
    {
        Assert.False(ProductionExecutionGate.IsOpen);

        var result = ProductionExecutionGate.Block<string>("Material issue");

        Assert.False(result.Succeeded);
        Assert.Equal(IvMasterErrorCode.Validation, result.ErrorCode);
        Assert.Contains(ProductionExecutionGate.BlockedCode, result.Message, StringComparison.Ordinal);
    }
}
