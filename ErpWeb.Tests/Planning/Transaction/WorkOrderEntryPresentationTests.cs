using ErpWeb.Model.Entities.Production;
using ErpWeb.UI.Planning.WorkOrders;

namespace ErpWeb.Tests.Planning.Transaction;

[Trait(TestCategories.Name, TestCategories.Planning)]
public sealed class WorkOrderEntryPresentationTests
{
    [Fact]
    public void Old_plan_update_notice_takes_priority_over_a_preview_or_unsaved_changes()
    {
        var state = WorkOrderEntryText.PlanState(true, true, true, true, true);
        var next = WorkOrderEntryText.NextStep(ProductionWorkOrderStatuses.Draft, true, true, true, true);
        Assert.Contains("update", state, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("before", next);
        Assert.DoesNotContain("ready", state, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_current_preview_is_still_explicitly_unsaved_and_a_stale_one_requires_an_update()
    {
        Assert.Contains("not saved", WorkOrderEntryText.PlanState(false, false, true, true, true));
        Assert.Contains("updating", WorkOrderEntryText.PlanState(true, false, true, true, false));
        Assert.Contains("changed after the preview", WorkOrderEntryText.NextStep(ProductionWorkOrderStatuses.Draft, false, true, true, false));
    }

    [Theory]
    [InlineData(ProductionWorkOrderStatuses.Released)]
    [InlineData(ProductionWorkOrderStatuses.InProgress)]
    [InlineData(ProductionWorkOrderStatuses.Completed)]
    [InlineData(ProductionWorkOrderStatuses.Closed)]
    [InlineData(ProductionWorkOrderStatuses.Cancelled)]
    public void Read_only_orders_do_not_receive_draft_save_instructions_even_with_old_preview_flags(string status)
    {
        var next = WorkOrderEntryText.NextStep(status, false, true, true, false);
        Assert.DoesNotContain("save a draft", next, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("or save", next, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("when you are ready", next, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Viewing_a_draft_explains_how_to_edit_and_respects_available_actions()
    {
        var editable = WorkOrderEntryText.NextStep(ProductionWorkOrderStatuses.Draft, false, false, false, false,
            canEdit: false, canOpenEdit: true, canSend: true);
        Assert.Contains("Edit draft", editable);
        Assert.Contains("Send to production", editable);
        Assert.DoesNotContain("Save a draft", editable);

        var readOnly = WorkOrderEntryText.NextStep(ProductionWorkOrderStatuses.Draft, false, false, false, false,
            canEdit: false, canOpenEdit: false, canSend: false);
        Assert.DoesNotContain("Edit draft", readOnly);
        Assert.DoesNotContain("Send to production", readOnly);

        var older = WorkOrderEntryText.NextStep(ProductionWorkOrderStatuses.Draft, true, false, false, false,
            canEdit: false, canOpenEdit: true, canSend: false);
        Assert.Contains("Edit draft, then Update from production setup", older);

        var saveOnly = WorkOrderEntryText.NextStep(ProductionWorkOrderStatuses.Draft, false, true, false, false,
            canEdit: true, canOpenEdit: false, canSend: false);
        Assert.Contains("Save a draft", saveOnly);
        Assert.DoesNotContain("send", saveOnly, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Plain_language_errors_keep_support_codes_and_quantity_facts()
    {
        var message = WorkOrderEntryText.Message("WO_SNAPSHOT_STALE: Product Definition X, snapshot 3. Required 100.2500 KG.");
        Assert.Contains("WO_SNAPSHOT_STALE", message);
        Assert.Contains("100.2500 KG", message);
        Assert.Contains("production setup X", message);
        Assert.Contains("saved production plan 3", message);
    }
}
