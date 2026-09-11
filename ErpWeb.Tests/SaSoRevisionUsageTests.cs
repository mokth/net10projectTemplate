using ErpWeb.Core.Sales;

namespace ErpWeb.Tests;

/// <summary>
/// D15 — a Sales Order carrying a DO force-close write-off must never be revisable or deletable:
/// revision rebuilds fresh <c>SaSoDetail</c> rows via <c>AddDetails</c> and would silently drop
/// <c>WrittenOffQty</c>.
/// </summary>
public class SaSoRevisionUsageTests
{
    private static SaSoRevisionUsage Usage(bool writtenOff) => new() { HasWrittenOffQty = writtenOff };

    [Fact]
    public void WrittenOff_marks_the_revision_as_used()
    {
        Assert.False(Usage(writtenOff: true).IsUnused);
        Assert.True(Usage(writtenOff: false).IsUnused);
    }

    [Fact]
    public void BlockMessage_explains_the_write_off_when_revising()
    {
        var message = Usage(writtenOff: true).BlockMessage(SaSoUsageMutation.Revise);
        Assert.Contains("written off", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("force-closed", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("revised", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BlockMessage_explains_the_write_off_when_deleting()
    {
        var message = Usage(writtenOff: true).BlockMessage(SaSoUsageMutation.Delete);
        Assert.Contains("written off", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("force-closed", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("deleted", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Write_off_message_takes_precedence_over_the_generic_message()
    {
        // A write-off plus other usage must still surface the force-close reason, not "already in use".
        var usage = new SaSoRevisionUsage { HasWrittenOffQty = true, HasDeliveredQty = true, HasAllocation = true };
        Assert.Contains("written off", usage.BlockMessage(SaSoUsageMutation.Revise), StringComparison.OrdinalIgnoreCase);
    }
}
