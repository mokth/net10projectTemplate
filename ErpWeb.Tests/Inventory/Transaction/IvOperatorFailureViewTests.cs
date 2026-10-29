using ErpWeb.Core.Inventory;
using ErpWeb.Core.Services;
using ErpWeb.UI.Components.Common.OperatorError;

namespace ErpWeb.Tests.Inventory.Transaction;

/// <summary>
/// Presentation-only mapping from the structured posting result to the shared failure banner.
/// It must never invent a reason, never show cost rows to a page that did not check cost rights,
/// and must return null so pages keep their original message when nothing is structured.
/// </summary>
[Trait(TestCategories.Name, TestCategories.Inventory)]
[Trait(TestCategories.Name, TestCategories.InventoryPosting)]
public class ErpOperatorFailureViewTests
{
    private static ErpOperatorError Error(
        string title = "Cannot roll back Goods Receipt 1.",
        string explanation = "Removing this record would leave insufficient historical stock.",
        params ErpOperatorErrorDetail[] details) => new()
        {
            PresentationCode = "INVENTORY.HISTORICAL_NEGATIVE_STOCK_REPLAYING",
            Title = title,
            Explanation = explanation,
            NextStep = "Roll back the later transaction first.",
            ReferenceId = "REF0000001",
            Details = details
        };

    [Fact]
    public void Single_coded_failure_becomes_the_headline_and_reason()
    {
        var posting = IvInventoryPostingResult.FromBatches(
        [
            IvInventoryPostingBatchResult.Fail(1, "technical", Error())
        ]);

        var view = ErpOperatorFailureView.From(posting);

        Assert.NotNull(view);
        Assert.Equal("Cannot roll back Goods Receipt 1.", view!.Headline);
        Assert.Equal("Removing this record would leave insufficient historical stock.", view.Reason);
        var note = Assert.Single(view.Notes);
        Assert.Equal("REF0000001", note.Reference);
        Assert.Equal("Roll back the later transaction first.", note.NextStep);
    }

    [Fact]
    public void Monetary_details_are_never_rendered_without_a_cost_check()
    {
        var posting = IvInventoryPostingResult.FromBatches(
        [
            IvInventoryPostingBatchResult.Fail(1, "technical", Error(
                details:
                [
                    new ErpOperatorErrorDetail("Item", "A100"),
                    new ErpOperatorErrorDetail("Stock value", "3,000.00", IsMonetary: true)
                ]))
        ]);

        var view = ErpOperatorFailureView.From(posting);

        var note = Assert.Single(view!.Notes);
        Assert.Equal(new[] { "Item" }, note.Facts.Select(f => f.Label));
    }

    [Fact]
    public void Mixed_result_lists_plain_failures_instead_of_hiding_them()
    {
        var posting = IvInventoryPostingResult.FromBatches(
        [
            IvInventoryPostingBatchResult.Ok(1, Guid.NewGuid()),
            IvInventoryPostingBatchResult.Fail(2, "Only NEW records can be posted.", Error()),
            IvInventoryPostingBatchResult.Fail(3, "Plain guard text.")
        ]);

        var view = ErpOperatorFailureView.From(posting);

        Assert.NotNull(view);
        Assert.Equal("2 of the 3 selected records could not be completed.", view!.Headline);
        Assert.Equal("Removing this record would leave insufficient historical stock.", view.Reason);
        Assert.Equal(2, view.Notes.Count);
        Assert.Equal("Record 3", view.Notes[1].Title);
        Assert.Equal("Plain guard text.", view.Notes[1].Explanation);
    }

    [Fact]
    public void No_structured_error_keeps_the_pages_existing_message()
    {
        var posting = IvInventoryPostingResult.FromBatches(
        [
            IvInventoryPostingBatchResult.Fail(1, "Plain guard text.")
        ]);

        Assert.Null(ErpOperatorFailureView.From(posting));
        Assert.Null(ErpOperatorFailureView.From(null));
    }

    [Fact]
    public void Single_error_overload_matches_the_batch_shape()
    {
        var view = ErpOperatorFailureView.FromError(Error());

        Assert.NotNull(view);
        Assert.Equal("Cannot roll back Goods Receipt 1.", view!.Headline);
        Assert.Single(view.Notes);
        Assert.Null(ErpOperatorFailureView.FromError(null));
    }

    [Fact]
    public void Facts_are_capped_so_a_banner_stays_readable()
    {
        var details = Enumerable.Range(1, 9)
            .Select(i => new ErpOperatorErrorDetail($"Label {i}", $"Value {i}"))
            .ToArray();
        var posting = IvInventoryPostingResult.FromBatches(
        [
            IvInventoryPostingBatchResult.Fail(1, "technical", Error(details: details))
        ]);

        var note = Assert.Single(ErpOperatorFailureView.From(posting)!.Notes);

        Assert.Equal(6, note.Facts.Count);
    }
}
