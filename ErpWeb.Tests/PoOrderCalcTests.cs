using ErpWeb.Core.Purchase;
using ErpWeb.Model.Entities.Purchase;

namespace ErpWeb.Tests;

public class PoOrderCalcTests
{
    [Fact]
    public void AllowedRecvQty_3m_at_5_percent_tolerance_is_3_15m()
    {
        Assert.Equal(3.15m, PoOrderCalc.AllowedRecvQty(3m, 5m));
    }

    [Fact]
    public void ComputeNetReceived_subtracts_return_from_recv()
    {
        Assert.Equal(100m, PoOrderCalc.ComputeNetReceived(110m, 10m));
        Assert.Equal(70m, PoOrderCalc.ComputeNetReceived(100m, 30m));
    }

    [Fact]
    public void ComputeBalance_clamps_at_zero_using_net_received()
    {
        Assert.Equal(9m, PoOrderCalc.ComputeBalance(10m, 2m, 1m)); // net 1
        Assert.Equal(0m, PoOrderCalc.ComputeBalance(5m, 5m, 0m));
        Assert.Equal(0m, PoOrderCalc.ComputeBalance(100m, 110m, 0m)); // over-receipt → balance 0
        Assert.Equal(30m, PoOrderCalc.ComputeBalance(100m, 100m, 30m)); // return restores balance
    }

    [Fact]
    public void ComputeOverRecv_uses_net_received_not_recv_plus_return()
    {
        Assert.Equal(0m, PoOrderCalc.ComputeOverRecv(100m, 110m, 10m)); // net 100
        Assert.Equal(10m, PoOrderCalc.ComputeOverRecv(100m, 110m, 0m));
        Assert.Equal(0m, PoOrderCalc.ComputeOverRecv(100m, 100m, 0m));
    }

    [Fact]
    public void AllowedInvoicedQty_equals_net_received()
    {
        Assert.Equal(100m, PoOrderCalc.AllowedInvoicedQty(110m, 10m));
        Assert.Equal(110m, PoOrderCalc.AllowedInvoicedQty(110m, 0m));
    }

    [Fact]
    public void ComputeInvoiceable_and_OverInvoiced_are_clamped()
    {
        Assert.Equal(60m, PoOrderCalc.ComputeInvoiceable(100m, 0m, 40m));
        Assert.Equal(0m, PoOrderCalc.ComputeInvoiceable(100m, 10m, 100m)); // net 90, inv 100
        Assert.Equal(10m, PoOrderCalc.ComputeOverInvoiced(100m, 10m, 100m));
        Assert.Equal(0m, PoOrderCalc.ComputeOverInvoiced(100m, 0m, 40m));
    }

    [Fact]
    public void ValidateQtyInvariants_accepts_tolerated_over_receipt()
    {
        Assert.True(PoOrderCalc.ValidateQtyInvariants(100m, 110m, 0m, out var error));
        Assert.Null(error);
    }

    [Fact]
    public void ValidateQtyInvariants_rejects_return_above_recv()
    {
        Assert.False(PoOrderCalc.ValidateQtyInvariants(100m, 50m, 60m, out var error));
        Assert.Contains("Return quantity", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateReceiptAgainstTolerance_allows_at_ceiling_rejects_beyond()
    {
        Assert.True(PoOrderCalc.ValidateReceiptAgainstTolerance(100m, 0m, 0m, 110m, 10m, out _));
        Assert.False(PoOrderCalc.ValidateReceiptAgainstTolerance(100m, 0m, 0m, 111m, 10m, out var error));
        Assert.Contains("exceeds allowed", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateReceiptAgainstTolerance_uses_net_received_after_prior_return()
    {
        // Recv 100, Return 10, Net 90; incoming 20 → new net 110 at 10% tol → allow
        Assert.True(PoOrderCalc.ValidateReceiptAgainstTolerance(100m, 100m, 10m, 20m, 10m, out _));
        // incoming 21 → new net 111 → reject
        Assert.False(PoOrderCalc.ValidateReceiptAgainstTolerance(100m, 100m, 10m, 21m, 10m, out _));
    }

    [Fact]
    public void ValidateOrderQtyChange_rejects_below_net_or_invoiced()
    {
        Assert.False(PoOrderCalc.ValidateOrderQtyChange(80m, 100m, 0m, 0m, out var belowNet));
        Assert.Contains("net received", belowNet, StringComparison.OrdinalIgnoreCase);

        Assert.False(PoOrderCalc.ValidateOrderQtyChange(80m, 50m, 0m, 100m, out var belowInv));
        Assert.Contains("invoiced", belowInv, StringComparison.OrdinalIgnoreCase);

        Assert.True(PoOrderCalc.ValidateOrderQtyChange(100m, 100m, 0m, 100m, out _));
    }

    [Fact]
    public void ApplyComputedQtyFields_sets_balance_and_over_recv()
    {
        var line = new PoOrderDetail { PoPurQty = 100m, RecvQty = 110m, ReturnQty = 0m };
        PoOrderCalc.ApplyComputedQtyFields(line);
        Assert.Equal(0m, line.BalanceQty);
        Assert.Equal(10m, line.OverRecvQty);
    }

    [Fact]
    public void PoStatusPolicy_force_close_then_reopen_restores_operational_status()
    {
        var header = new PoOrder
        {
            Status = PoOrderStatuses.New,
            Details =
            [
                new PoOrderDetail { ICode = "A100", PoPurQty = 10m, RecvQty = 0m, ReturnQty = 0m, BalanceQty = 10m },
                new PoOrderDetail { ICode = "A200", PoPurQty = 5m, RecvQty = 2m, ReturnQty = 0m, BalanceQty = 3m }
            ]
        };

        PoStatusPolicy.ForceClose(header, "Test close", "user", DateTime.UtcNow);
        Assert.Equal(PoOrderStatuses.Closed, header.Status);
        Assert.True(PoStatusPolicy.IsForceClosed(header, _ => false));
        Assert.Equal("Test close", header.CloseReason);
        Assert.Equal("user", header.ClosedBy);
        Assert.NotNull(header.ClosedOn);

        var reopenError = PoStatusPolicy.Reopen(header, _ => false);
        Assert.Null(reopenError);
        Assert.Equal(PoOrderStatuses.Received, header.Status);
        Assert.Null(header.CloseReason);
        Assert.Null(header.ClosedBy);
        Assert.Null(header.ClosedOn);
    }

    [Fact]
    public void PoStatusPolicy_OPEN_is_editable_but_not_gr_pickable()
    {
        Assert.True(PoStatusPolicy.IsEditableStatus(PoOrderStatuses.Open));
        Assert.False(PoStatusPolicy.IsGrPickable(PoOrderStatuses.Open));
        Assert.True(PoStatusPolicy.IsGrPickable(PoOrderStatuses.New));
        Assert.True(PoStatusPolicy.IsGrPickable(PoOrderStatuses.Received));
    }

    [Fact]
    public void PoPrCalc_ComputeDerivedStatus_partial_and_full()
    {
        Assert.Equal(
            PoPrStatuses.PartiallyOrdered,
            PoPrCalc.ComputeDerivedStatus(PoPrStatuses.New, [(10m, 4m), (5m, 0m)]));
        Assert.Equal(
            PoPrStatuses.FullyOrdered,
            PoPrCalc.ComputeDerivedStatus(PoPrStatuses.Approved, [(10m, 10m), (5m, 5m)]));
        Assert.Equal(
            PoPrStatuses.Cancelled,
            PoPrCalc.ComputeDerivedStatus(PoPrStatuses.Cancelled, [(10m, 10m)]));
        Assert.Equal(
            PoPrStatuses.Approved,
            PoPrCalc.ComputeDerivedStatus(PoPrStatuses.Approved, [(10m, 0m)]));
    }

    [Theory]
    [InlineData(PoPrStatuses.New, true)]
    [InlineData(PoPrStatuses.Approved, true)]
    [InlineData(PoPrStatuses.PartiallyOrdered, true)]
    [InlineData(PoPrStatuses.FullyOrdered, false)]
    [InlineData(PoPrStatuses.Cancelled, false)]
    [InlineData(PoPrStatuses.Open, false)]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("new", true)]
    [InlineData("Approved", true)]
    [InlineData("partially_ordered", true)]
    [InlineData("fully_ordered", false)]
    [InlineData("cancelled", false)]
    [InlineData("open", false)]
    public void PoPrCalc_IsAvailableForPo_status_matrix(string? status, bool expected) =>
        Assert.Equal(expected, PoPrCalc.IsAvailableForPo(status));

    [Fact]
    public void PoStatusPolicy_reopen_fails_when_no_balance_remains()
    {
        var header = new PoOrder
        {
            Status = PoOrderStatuses.Closed,
            Details =
            [
                new PoOrderDetail { ICode = "A100", PoPurQty = 10m, RecvQty = 10m, ReturnQty = 0m, BalanceQty = 0m }
            ]
        };

        var error = PoStatusPolicy.Reopen(header, _ => false);
        Assert.NotNull(error);
        Assert.Contains("No available quantity", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PoStatusPolicy_service_only_lines_do_not_block_close()
    {
        var header = new PoOrder
        {
            Status = PoOrderStatuses.New,
            Details =
            [
                new PoOrderDetail { ICode = "SVC1", PoPurQty = 5m, RecvQty = 0m, ReturnQty = 0m, BalanceQty = 5m },
                new PoOrderDetail { ICode = "A100", PoPurQty = 4m, RecvQty = 4m, ReturnQty = 0m, BalanceQty = 0m }
            ]
        };

        bool IsService(PoOrderDetail d) => string.Equals(d.ICode, "SVC1", StringComparison.OrdinalIgnoreCase);

        Assert.False(PoOrderCalc.HasRemainingBalance(header.Details, IsService));
        Assert.Equal(PoOrderStatuses.Closed, PoStatusPolicy.CalculateOperationalStatus(header, IsService));
    }

    [Fact]
    public void PoStatusPolicy_calculate_operational_status_received_when_partial_recv()
    {
        var header = new PoOrder
        {
            Status = PoOrderStatuses.New,
            Details =
            [
                new PoOrderDetail { ICode = "A100", PoPurQty = 10m, RecvQty = 3m, ReturnQty = 0m, BalanceQty = 7m }
            ]
        };

        Assert.Equal(PoOrderStatuses.Received, PoStatusPolicy.CalculateOperationalStatus(header, _ => false));
    }
}
