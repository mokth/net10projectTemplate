using ErpWeb.UI.Planning.Masters;

namespace ErpWeb.Tests.Planning.Master;

[Trait(TestCategories.Name, TestCategories.Planning)]
public sealed class PrProductDefEntryDirtyTests
{
    [Fact]
    public void Pending_line_editor_is_dirty_when_any_field_changes()
    {
        var clean = PrProductDefEntry.SnapshotLineEditor(
            null, null, null, "BUY", 1m, 0m, null, true, null, 0m,
            "MANUAL", "PURCHASED", null, null, null, null);
        var typed = PrProductDefEntry.SnapshotLineEditor(
            "RM001", "Flour", "KG", "BUY", 1m, 0m, "WH01", true, null, 0m,
            "MANUAL", "PURCHASED", null, null, null, null);

        Assert.False(PrProductDefEntry.LineEditorIsDirty(clean, clean));
        Assert.True(PrProductDefEntry.LineEditorIsDirty(clean, typed));
    }
}
