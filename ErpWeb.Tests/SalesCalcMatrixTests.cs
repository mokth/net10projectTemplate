using ErpWeb.Core.Sales;

namespace ErpWeb.Tests;

/// <summary>
/// The locked R2 golden calculation matrix (sales_trans_enhancement_plan.md §9).
/// <para>
/// Exclusive rows are asserted today. Inclusive rows are skip-marked until Step 9 (R2) lands:
/// the inclusive branch of <see cref="SaInvoiceCalc.CalculateLine"/> is currently wrong
/// (it stores the tax-inclusive amount as <c>NetAmount</c> and derives a negative <c>TaxAmt</c>).
/// When Step 9 is implemented, remove the <c>Skip</c> from <see cref="Inclusive_rows_after_R2"/>.
/// </para>
/// <para>
/// Header invariant asserted for every row: <c>GrossAmt + Taxes == TotAmnt</c> and <c>GrossAmt</c> is ex-tax.
/// </para>
/// </summary>
public class SalesCalcMatrixTests
{
    public sealed class CalcCase
    {
        public required string Name { get; init; }
        public decimal Qty { get; init; } = 1m;
        public decimal UnitPrice { get; init; }
        public decimal TaxPercent { get; init; }
        /// <summary>Per-unit discount injected through <c>ItemDiscAmount</c> (raw, tax-inclusive for inclusive lines).</summary>
        public decimal DiscountPerUnit { get; init; }
        public bool Inclusive { get; init; }
        public bool DecPoint { get; init; }
        public decimal Amount { get; init; }
        public decimal NetAmount { get; init; }
        public decimal TaxAmt { get; init; }
        public override string ToString() => Name;
    }

    // ───────────────────────────── exclusive rows (asserted) ─────────────────────────────

    public static IEnumerable<object[]> ExclusiveCases() => new[]
    {
        // tax rate axis (0/5/6/8/10) + no discount
        new CalcCase { Name = "exc_100_0",  UnitPrice = 100m, TaxPercent = 0m,  Amount = 100m, NetAmount = 100m, TaxAmt = 0m },
        new CalcCase { Name = "exc_100_5",  UnitPrice = 100m, TaxPercent = 5m,  Amount = 100m, NetAmount = 100m, TaxAmt = 5m },
        new CalcCase { Name = "exc_100_6",  UnitPrice = 100m, TaxPercent = 6m,  Amount = 100m, NetAmount = 100m, TaxAmt = 6m },
        new CalcCase { Name = "exc_100_8",  UnitPrice = 100m, TaxPercent = 8m,  Amount = 100m, NetAmount = 100m, TaxAmt = 8m },
        new CalcCase { Name = "exc_100_10", UnitPrice = 100m, TaxPercent = 10m, Amount = 100m, NetAmount = 100m, TaxAmt = 10m },

        // discount axis (amount discount of 11 on 100)
        new CalcCase { Name = "exc_100_10_disc11", UnitPrice = 100m, TaxPercent = 10m, DiscountPerUnit = 11m, Amount = 100m, NetAmount = 89m, TaxAmt = 8.90m },

        // quantity axis (fractional)
        new CalcCase { Name = "exc_qty_2_5", Qty = 2.5m, UnitPrice = 100m, TaxPercent = 10m, Amount = 250m, NetAmount = 250m, TaxAmt = 25m },

        // decPoint axis (0 dp)
        new CalcCase { Name = "exc_decpoint", UnitPrice = 100m, TaxPercent = 6m, DecPoint = true, Amount = 100m, NetAmount = 100m, TaxAmt = 6m },

        // rounding boundary: unit price ending in .005 (Money rounds away from zero)
        new CalcCase { Name = "exc_rounding_boundary", UnitPrice = 100.005m, TaxPercent = 10m, Amount = 100.01m, NetAmount = 100.01m, TaxAmt = 10.00m }
    }.Select(x => new object[] { x });

    // ───────────────────────────── inclusive rows (pending Step 9) ─────────────────────────────

    public static IEnumerable<object[]> InclusiveCases() => new[]
    {
        // §9.1 headline cases
        new CalcCase { Name = "inc_110_10_nodisc", UnitPrice = 110m, TaxPercent = 10m, Inclusive = true, Amount = 100m, NetAmount = 100m, TaxAmt = 10m },
        new CalcCase { Name = "inc_110_10_disc11", UnitPrice = 110m, TaxPercent = 10m, DiscountPerUnit = 11m, Inclusive = true, Amount = 100m, NetAmount = 90m, TaxAmt = 9m },
        new CalcCase { Name = "inc_106_6",         UnitPrice = 106m, TaxPercent = 6m,  Inclusive = true, Amount = 100m, NetAmount = 100m, TaxAmt = 6m },
        new CalcCase { Name = "inc_100_0",         UnitPrice = 100m, TaxPercent = 0m,  Inclusive = true, Amount = 100m, NetAmount = 100m, TaxAmt = 0m },

        // tax rate axis
        new CalcCase { Name = "inc_105_5", UnitPrice = 105m, TaxPercent = 5m, Inclusive = true, Amount = 100m, NetAmount = 100m, TaxAmt = 5m },
        new CalcCase { Name = "inc_108_8", UnitPrice = 108m, TaxPercent = 8m, Inclusive = true, Amount = 100m, NetAmount = 100m, TaxAmt = 8m },

        // quantity axis (fractional)
        new CalcCase { Name = "inc_qty_2_5", Qty = 2.5m, UnitPrice = 110m, TaxPercent = 10m, Inclusive = true, Amount = 250m, NetAmount = 250m, TaxAmt = 25m },

        // decPoint axis (0 dp)
        new CalcCase { Name = "inc_decpoint", UnitPrice = 110m, TaxPercent = 10m, DecPoint = true, Inclusive = true, Amount = 100m, NetAmount = 100m, TaxAmt = 10m },

        // rounding boundary: unit price ending in .005
        new CalcCase { Name = "inc_rounding_boundary", UnitPrice = 110.005m, TaxPercent = 10m, Inclusive = true, Amount = 100m, NetAmount = 100m, TaxAmt = 10.01m }
    }.Select(x => new object[] { x });

    [Theory]
    [MemberData(nameof(ExclusiveCases))]
    public void Exclusive_rows_match_locked_matrix(CalcCase row) => AssertRow(row);

    [Theory]
    [MemberData(nameof(InclusiveCases))]
    public void Inclusive_rows_match_locked_matrix(CalcCase row) => AssertRow(row);

    [Fact]
    public void Exclusive_mixed_lines_header_reconciles()
    {
        var lines = new List<SaInvoiceLineCalcState>
        {
            Line(qty: 1m, price: 100m, taxPercent: 10m, inclusive: false),
            Line(qty: 2m, price: 50m, taxPercent: 6m, inclusive: false),
            Line(qty: 1m, price: 30m, taxPercent: 0m, inclusive: false, orderType: SaInvoiceCalc.ExcludedDiscountOrderType)
        };
        foreach (var line in lines)
        {
            SaInvoiceCalc.CalculateLine(line, line.TaxPercent, decPoint: false, discMethod: null);
        }

        var header = SaInvoiceCalc.CalculateHeader(lines, decPoint: false);
        Assert.Equal(header.GrossAmnt + header.Taxes, header.TotAmnt);
        // GrossAmnt is ex-tax and includes the EXCLD DIS line (CalculateHeader adds it back).
        Assert.Equal(230m, header.GrossAmnt); // 100 + 100 net + 30 excl
        Assert.Equal(16m, header.Taxes);      // 10 + 6 + 0
        Assert.Equal(246m, header.TotAmnt);
    }

    [Fact]
    public void Inclusive_mixed_lines_header_reconciles()
    {
        var lines = new List<SaInvoiceLineCalcState>
        {
            Line(qty: 1m, price: 110m, taxPercent: 10m, inclusive: true),
            Line(qty: 1m, price: 106m, taxPercent: 6m, inclusive: true),
            Line(qty: 1m, price: 100m, taxPercent: 0m, inclusive: false)
        };
        foreach (var line in lines)
        {
            SaInvoiceCalc.CalculateLine(line, line.TaxPercent, decPoint: false, discMethod: null);
        }
        SaInvoiceCalc.ApplyTaxAdaptiveRounding(lines);

        var header = SaInvoiceCalc.CalculateHeader(lines, decPoint: false);
        Assert.Equal(header.GrossAmnt + header.Taxes, header.TotAmnt);
        Assert.Equal(300m, header.GrossAmnt); // 100 + 100 + 100
        Assert.Equal(16m, header.Taxes);      // 10 + 6 + 0
        Assert.Equal(316m, header.TotAmnt);
    }

    [Fact]
    public void Inclusive_adaptive_rounding_does_not_corrupt_discounted_line()
    {
        // The pre-R2 branch produced Amount = 120 for this line. R2 must leave it at 100 / 90 / 9.
        var lines = new List<SaInvoiceLineCalcState> { Line(qty: 1m, price: 110m, taxPercent: 10m, inclusive: true) };
        lines[0].ItemDiscAmount = 11m;
        SaInvoiceCalc.CalculateLine(lines[0], 10m, decPoint: false, discMethod: null);
        SaInvoiceCalc.ApplyTaxAdaptiveRounding(lines);

        Assert.Equal(100m, lines[0].Amount);
        Assert.Equal(90m, lines[0].NetAmount);
        Assert.Equal(9m, lines[0].TaxAmt);
    }

    [Fact]
    public void Residual_rounding_is_order_independent()
    {
        var seed = new List<SaInvoiceLineCalcState>
        {
            Line(qty: 1m, price: 110m, taxPercent: 10m, inclusive: true),
            Line(qty: 3m, price: 106m, taxPercent: 6m, inclusive: true),
            Line(qty: 1m, price: 100m, taxPercent: 5m, inclusive: true),
            Line(qty: 2m, price: 108m, taxPercent: 8m, inclusive: true)
        };
        for (var i = 0; i < seed.Count; i++)
        {
            seed[i].Line = i + 1;
            seed[i].ItemDiscAmount = i == 1 ? 6m : 0m;
        }

        static Dictionary<int, decimal> TaxByLine(IEnumerable<SaInvoiceLineCalcState> source)
        {
            var lines = source.Select(x => new SaInvoiceLineCalcState
            {
                Line = x.Line,
                Qty = x.Qty,
                UnitPrice = x.UnitPrice,
                TaxPercent = x.TaxPercent,
                ItemDiscAmount = x.ItemDiscAmount,
                IsInclusive = x.IsInclusive
            }).OrderBy(x => x.Line).ToList();

            foreach (var line in lines)
            {
                SaInvoiceCalc.CalculateLine(line, line.TaxPercent, decPoint: false, discMethod: null);
            }
            SaInvoiceCalc.ApplyTaxAdaptiveRounding(lines);
            return lines.ToDictionary(x => x.Line, x => x.TaxAmt);
        }

        var forward = TaxByLine(seed);
        var reversed = TaxByLine(seed.AsEnumerable().Reverse());
        var shuffled = TaxByLine([seed[2], seed[0], seed[3], seed[1]]);

        Assert.Equal(forward, reversed);
        Assert.Equal(forward, shuffled);
    }

    [Fact]
    public void DecPoint_true_lines_are_whole_and_header_reconciles()
    {
        var lines = new List<SaInvoiceLineCalcState>
        {
            Line(qty: 1m, price: 110m, taxPercent: 10m, inclusive: true),
            Line(qty: 1m, price: 108m, taxPercent: 8m, inclusive: false)
        };
        foreach (var line in lines)
        {
            SaInvoiceCalc.CalculateLine(line, line.TaxPercent, decPoint: true, discMethod: null);
        }
        SaInvoiceCalc.ApplyTaxAdaptiveRounding(lines);

        var header = SaInvoiceCalc.CalculateHeader(lines, decPoint: true);
        Assert.Equal(0m, header.GrossAmnt % 1m);
        Assert.Equal(0m, header.Taxes % 1m);
        Assert.Equal(0m, header.TotAmnt % 1m);
        Assert.Equal(header.GrossAmnt + header.Taxes, header.TotAmnt);
    }

    private static void AssertRow(CalcCase row)
    {
        var line = Line(row);
        SaInvoiceCalc.CalculateLine(line, row.TaxPercent, row.DecPoint, discMethod: null);

        Assert.Equal(row.Amount, line.Amount);
        Assert.Equal(row.NetAmount, line.NetAmount);
        Assert.Equal(row.TaxAmt, line.TaxAmt);

        var header = SaInvoiceCalc.CalculateHeader([line], row.DecPoint);
        Assert.Equal(header.GrossAmnt + header.Taxes, header.TotAmnt);
        Assert.Equal(line.NetAmount, header.GrossAmnt);
        Assert.Equal(line.TaxAmt, header.Taxes);
        Assert.Equal(line.NetAmount + line.TaxAmt, header.TotAmnt);
    }

    private static SaInvoiceLineCalcState Line(CalcCase row) => new()
    {
        Qty = row.Qty,
        UnitPrice = row.UnitPrice,
        ItemDiscAmount = row.DiscountPerUnit,
        IsInclusive = row.Inclusive,
        Line = 1
    };

    private static SaInvoiceLineCalcState Line(
        decimal qty,
        decimal price,
        decimal taxPercent,
        bool inclusive,
        string? orderType = null) => new()
    {
        Qty = qty,
        UnitPrice = price,
        IsInclusive = inclusive,
        OrderType = orderType,
        Line = 1,
        TaxPercent = taxPercent
    };
}
