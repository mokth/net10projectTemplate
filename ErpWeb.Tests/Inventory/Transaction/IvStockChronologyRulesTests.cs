using ErpWeb.Core.Inventory;

namespace ErpWeb.Tests.Inventory.Transaction;
[Trait(TestCategories.Name, TestCategories.Inventory)]
public sealed class IvStockDateRulesTests
{
    [Fact]
    public void IsAvailableOn_allows_same_day_and_earlier()
    {
        var doc = new DateTime(2026, 10, 5);
        Assert.True(IvStockDateRules.IsAvailableOn(new DateTime(2026, 10, 1), doc));
        Assert.True(IvStockDateRules.IsAvailableOn(new DateTime(2026, 10, 5), doc));
        Assert.False(IvStockDateRules.IsAvailableOn(new DateTime(2026, 10, 6), doc));
        Assert.False(IvStockDateRules.IsAvailableOn(null, doc));
    }

    [Fact]
    public void IsFutureMovementDate_rejects_after_business_date()
    {
        var business = new DateTime(2026, 10, 1);
        Assert.False(IvStockDateRules.IsFutureMovementDate(new DateTime(2026, 10, 1), business));
        Assert.True(IvStockDateRules.IsFutureMovementDate(new DateTime(2026, 10, 2), business));
    }
}

[Trait(TestCategories.Name, TestCategories.Inventory)]
public sealed class IvStockMovementRulesTests
{
    [Fact]
    public void New_post_later_day_only()
    {
        var doc = new DateTime(2026, 10, 5);
        Assert.False(IvStockMovementRules.IsLaterDayMovement(new DateTime(2026, 10, 5), doc));
        Assert.True(IvStockMovementRules.IsLaterDayMovement(new DateTime(2026, 10, 6), doc));
    }

    [Fact]
    public void Rollback_same_day_uses_history_id()
    {
        var day = new DateTime(2026, 10, 5);
        Assert.False(IvStockMovementRules.IsLaterRollbackMovement(day, 100, day, 100));
        Assert.True(IvStockMovementRules.IsLaterRollbackMovement(day, 101, day, 100));
        Assert.True(IvStockMovementRules.IsLaterRollbackMovement(day.AddDays(1), 50, day, 999));
    }

    [Fact]
    public void ResolveMovementDate_defaults_and_rejects_future()
    {
        var business = new DateTime(2026, 10, 5);
        var (defaulted, noError) = IvStockMovementRules.ResolveMovementDate(default, business);
        Assert.Equal(business.Date, defaulted);
        Assert.Null(noError);

        var (ok, okError) = IvStockMovementRules.ResolveMovementDate(new DateTime(2026, 10, 1), business);
        Assert.Equal(new DateTime(2026, 10, 1), ok);
        Assert.Null(okError);

        var (future, futureError) = IvStockMovementRules.ResolveMovementDate(new DateTime(2026, 10, 6), business);
        Assert.Equal(new DateTime(2026, 10, 6), future);
        Assert.False(string.IsNullOrEmpty(futureError));
    }

    [Fact]
    public void ResolveDraftMovementDate_defaults_and_preserves_future_dates()
    {
        var business = new DateTime(2026, 10, 13);

        Assert.Equal(business, IvStockMovementRules.ResolveDraftMovementDate(default, business));
        Assert.Equal(new DateTime(2026, 10, 12), IvStockMovementRules.ResolveDraftMovementDate(new DateTime(2026, 10, 12, 18, 30, 0), business));
        Assert.Equal(business, IvStockMovementRules.ResolveDraftMovementDate(business, business));
        Assert.Equal(new DateTime(2026, 10, 14), IvStockMovementRules.ResolveDraftMovementDate(new DateTime(2026, 10, 14, 23, 59, 0), business));
    }

    [Fact]
    public void Operator_text_never_quotes_an_internal_balance_id()
    {
        var message = IvStockMovementRules.RollbackLaterMovementMessage(12, new DateTime(2026, 10, 5));

        Assert.Contains("Batch 12", message, StringComparison.Ordinal);
        Assert.Contains("05/10/2026", message, StringComparison.Ordinal);
        Assert.DoesNotContain("balance Id", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("BalLoc", message, StringComparison.OrdinalIgnoreCase);
    }
}

[Trait(TestCategories.Name, TestCategories.Inventory)]
public sealed class IvStockDateRulesMessageTests
{
    [Fact]
    public void Null_stock_date_message_is_actionable_and_carries_no_database_id()
    {
        var message = IvStockDateRules.NullStockDateMessage();

        Assert.Contains("stock date", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("administrator", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Id ", message, StringComparison.Ordinal);
        Assert.DoesNotContain("BalLoc", message, StringComparison.OrdinalIgnoreCase);
    }
}
