using System.Globalization;
using ErpWeb.Core.Transactions;

namespace ErpWeb.Tests.Transactions;

public class TransactionPostingDateGuardTests
{
    [Fact]
    public void Past_document_date_is_allowed()
    {
        var error = TransactionPostingDateGuard.FutureDateError(
            new DateTime(2026, 10, 12),
            new DateTime(2026, 10, 13));

        Assert.Null(error);
    }

    [Fact]
    public void Same_document_date_is_allowed()
    {
        var error = TransactionPostingDateGuard.FutureDateError(
            new DateTime(2026, 10, 13),
            new DateTime(2026, 10, 13));

        Assert.Null(error);
    }

    [Fact]
    public void Same_calendar_date_with_different_times_is_allowed()
    {
        var error = TransactionPostingDateGuard.FutureDateError(
            new DateTime(2026, 10, 13, 23, 59, 59),
            new DateTime(2026, 10, 13, 0, 0, 1));

        Assert.Null(error);
    }

    [Fact]
    public void Future_document_date_returns_both_dates_in_error()
    {
        var error = TransactionPostingDateGuard.FutureDateError(
            new DateTime(2026, 10, 14, 19, 30, 0),
            new DateTime(2026, 10, 13, 8, 0, 0));

        Assert.Equal(
            $"Transaction date {new DateTime(2026, 10, 14).ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)} is after the business date {new DateTime(2026, 10, 13).ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)}.",
            error);
    }
}
