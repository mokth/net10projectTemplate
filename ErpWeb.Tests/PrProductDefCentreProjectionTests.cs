using ErpWeb.Core.Planning;

namespace ErpWeb.Tests;

[Trait(TestCategories.Name, TestCategories.Planning)]
public class PrProductDefCentreProjectionTests
{
    [Fact]
    public void BuildCentreRows_multiple_processes_produce_one_centre()
    {
        var ops = new List<PrProductDefOperationVm>
        {
            Op("WC01", "FG001", 10, 10, "P1"),
            Op("WC01", "FG001", 10, 20, "P2"),
            Op("WC02", "FG001", 20, 10, "P3")
        };

        var rows = PrProductDefCentreProjection.BuildCentreRows(ops);
        Assert.Equal(2, rows.Count);
        var wc01 = Assert.Single(rows, x => x.WorkCentreCode == "WC01");
        Assert.Equal(2, wc01.ProcessCount);
        Assert.False(wc01.HasSequenceConflict);
        Assert.Equal(10, wc01.CentralSequence);
    }

    [Fact]
    public void BuildCentreRows_flags_sequence_conflict()
    {
        var ops = new List<PrProductDefOperationVm>
        {
            Op("WC01", "FG001", 10, 10, "P1"),
            Op("WC01", "FG001", 20, 20, "P2")
        };

        var row = Assert.Single(PrProductDefCentreProjection.BuildCentreRows(ops));
        Assert.True(row.HasSequenceConflict);
        Assert.Equal(10, row.CentralSequence);
    }

    [Fact]
    public void TryUpdateCentre_propagates_sequence_to_all_ops()
    {
        var ops = new List<PrProductDefOperationVm>
        {
            Op("WC01", "FG001", 10, 10, "P1"),
            Op("WC01", "FG001", 10, 20, "P2")
        };
        var original = PrProductDefCentreProjection.CentreKey.From("WC01", "FG001");
        var edited = new PrProductDefCentreProjection.CentreRowVm
        {
            WorkCentreCode = "WC01",
            OutputItemCode = "FG001",
            CentralSequence = 30,
            OutputBaseQty = 2m,
            OutputUom = "PCS"
        };

        var result = PrProductDefCentreProjection.TryUpdateCentre(ops, original, edited);
        Assert.True(result.Succeeded);
        Assert.All(ops, op => Assert.Equal(30, op.CentralSequence));
        Assert.All(ops, op => Assert.Equal(2m, op.OutputBaseQty));
    }

    [Fact]
    public void TryUpdateCentre_renames_key_using_original_key()
    {
        var ops = new List<PrProductDefOperationVm>
        {
            Op("WC01", "FG001", 10, 10, "P1"),
            Op("WC01", "FG001", 10, 20, "P2"),
            Op("WC03", "FG001", 30, 10, "P3")
        };
        var original = PrProductDefCentreProjection.CentreKey.From("WC01", "FG001");
        var edited = new PrProductDefCentreProjection.CentreRowVm
        {
            WorkCentreCode = "WC02",
            OutputItemCode = "FG001",
            CentralSequence = 10,
            OutputBaseQty = 1m
        };

        var result = PrProductDefCentreProjection.TryUpdateCentre(ops, original, edited);
        Assert.True(result.Succeeded);
        Assert.Equal("WC02", result.NewKey.WorkCentreCode);
        Assert.Equal(2, ops.Count(x => x.WorkCentreCode == "WC02" && x.OutputItemCode == "FG001"));
        Assert.DoesNotContain(ops, x => x.WorkCentreCode == "WC01");
    }

    [Fact]
    public void TryUpdateCentre_rejects_key_collision()
    {
        var ops = new List<PrProductDefOperationVm>
        {
            Op("WC01", "FG001", 10, 10, "P1"),
            Op("WC02", "FG001", 20, 10, "P2")
        };
        var original = PrProductDefCentreProjection.CentreKey.From("WC02", "FG001");
        var edited = new PrProductDefCentreProjection.CentreRowVm
        {
            WorkCentreCode = "WC01",
            OutputItemCode = "FG001",
            CentralSequence = 20,
            OutputBaseQty = 1m
        };

        var result = PrProductDefCentreProjection.TryUpdateCentre(ops, original, edited);
        Assert.False(result.Succeeded);
        Assert.Contains("already exists", result.ErrorMessage);
        Assert.Equal("WC02", ops[1].WorkCentreCode);
    }

    [Fact]
    public void DeleteCentre_removes_processes_and_nested_resources()
    {
        var keep = Op("WC02", "FG001", 20, 10, "P3");
        keep.Machines.Add(new PrProductDefMachineVm { MachineCode = "M2" });
        var remove = Op("WC01", "FG001", 10, 10, "P1");
        remove.Machines.Add(new PrProductDefMachineVm
        {
            MachineCode = "M1",
            Labours = [new PrProductDefLabourVm { LabourCode = "L1" }]
        });
        var ops = new List<PrProductDefOperationVm> { remove, keep };

        var removed = PrProductDefCentreProjection.DeleteCentre(
            ops, PrProductDefCentreProjection.CentreKey.From("WC01", "FG001"));
        Assert.Equal(1, removed);
        Assert.Single(ops);
        Assert.Equal("WC02", ops[0].WorkCentreCode);
    }

    [Fact]
    public void OperationBelongsToCentre_clears_invalid_selection()
    {
        var op = Op("WC01", "FG001", 10, 10, "P1");
        var other = PrProductDefCentreProjection.CentreKey.From("WC02", "FG001");
        Assert.False(PrProductDefCentreProjection.OperationBelongsToCentre(op, other));
        Assert.True(PrProductDefCentreProjection.OperationBelongsToCentre(
            op, PrProductDefCentreProjection.CentreKey.From("WC01", "FG001")));
    }

    [Fact]
    public void FilterProcesses_sorts_by_sequence_then_identity()
    {
        var ops = new List<PrProductDefOperationVm>
        {
            Op("WC01", "FG001", 10, 20, "P2", uid: 2),
            Op("WC01", "FG001", 10, 10, "P1", uid: 1),
            Op("WC02", "FG001", 20, 10, "P3", uid: 3)
        };

        var filtered = PrProductDefCentreProjection.FilterProcesses(
            ops, PrProductDefCentreProjection.CentreKey.From("WC01", "FG001"));
        Assert.Equal(new[] { "P1", "P2" }, filtered.Select(x => x.OperationCode).ToArray());
    }

    [Fact]
    public void BuildCentreRows_includes_pending_when_key_unused()
    {
        var ops = new List<PrProductDefOperationVm> { Op("WC01", "FG001", 10, 10, "P1") };
        var pending = new PrProductDefCentreProjection.CentreRowVm
        {
            WorkCentreCode = "WC09",
            OutputItemCode = "FG001",
            CentralSequence = 90,
            OutputBaseQty = 1m
        };

        var rows = PrProductDefCentreProjection.BuildCentreRows(ops, pending);
        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, x => x.IsPending && x.WorkCentreCode == "WC09");
    }

    private static PrProductDefOperationVm Op(
        string wc, string output, int centreSeq, int processSeq, string process, long uid = 0) =>
        new()
        {
            Uid = uid,
            WorkCentreCode = wc,
            OutputItemCode = output,
            CentralSequence = centreSeq,
            ProcessSequence = processSeq,
            OperationCode = process,
            OutputBaseQty = 1m,
            OutputUom = "PCS"
        };
}
