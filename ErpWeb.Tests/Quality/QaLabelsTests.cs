using ErpWeb.Core.Quality;

namespace ErpWeb.Tests.Quality;

[Trait(TestCategories.Name, TestCategories.Quality)]
public class QaLabelsTests
{
    [Theory]
    [InlineData(QaSourceTypes.IncomingPreGrn, "Incoming delivery (before GRN)")]
    [InlineData(QaSourceTypes.PostedGrn, "Posted goods receipt")]
    [InlineData(QaSourceTypes.InventoryStock, "Posted miscellaneous receipt")]
    [InlineData(QaSourceTypes.FgStagingLot, "Finished-goods staging")]
    public void Source_uses_operator_labels_not_raw_codes(string code, string expected)
    {
        Assert.Equal(expected, QaLabels.Source(code));
        Assert.DoesNotContain('_', QaLabels.Source(code));
    }

    [Theory]
    [InlineData(QaCaseStatuses.PendingReview, "Pending review")]
    [InlineData(QaCaseStatuses.InInspection, "In inspection")]
    [InlineData(QaCaseStatuses.Draft, "Draft")]
    public void Status_uses_operator_labels_not_raw_codes(string code, string expected)
    {
        Assert.Equal(expected, QaLabels.Status(code));
        Assert.DoesNotContain('_', QaLabels.Status(code));
    }

    [Fact]
    public void List_row_exposes_labels_and_pending_review_badge()
    {
        var row = new QaCaseListRow
        {
            CaseNo = "QA-1",
            Status = QaCaseStatuses.PendingReview,
            SourceType = QaSourceTypes.IncomingPreGrn
        };

        Assert.Equal("Pending review", row.StatusLabel);
        Assert.Equal("Incoming delivery (before GRN)", row.SourceTypeLabel);
        Assert.Equal("Approve or reject", row.NextAction);
        Assert.True(row.IsPendingReview);
    }

    [Fact]
    public void Next_action_and_workflow_step_follow_status()
    {
        Assert.Equal("source", QaLabels.WorkflowStep(QaCaseStatuses.Draft));
        Assert.Equal("inspect", QaLabels.WorkflowStep(QaCaseStatuses.InInspection));
        Assert.Equal("review", QaLabels.WorkflowStep(QaCaseStatuses.PendingReview));
        Assert.Equal("followup", QaLabels.WorkflowStep(QaCaseStatuses.Approved));
        Assert.Contains("submit", QaLabels.NextAction(QaCaseStatuses.Draft), StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Reject follow-up", QaLabels.ActionType(QaActionTypes.Reject));
        Assert.DoesNotContain('_', QaLabels.ActionType(QaActionTypes.FollowUp));
    }
}
