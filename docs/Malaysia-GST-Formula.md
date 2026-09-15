Malaysia Tax Formula (GST/SST-Style)

Use `decimal` values in C#; never use `double` or `float`.

```text
Rate = tax percentage ÷ 100
Example: 6% = 0.06
```

## Tax Exclusive

The entered price does **not** include tax.

```text
Net Amount = Quantity × Unit Price - Discount
Tax Amount = round(Net Amount × Rate, 2)
Grand Total = Net Amount + Tax Amount
```

Example: 2 units × RM50.00, 6% tax

```text
Net Amount = 2 × 50.00 = 100.00
Tax Amount = 100.00 × 0.06 = 6.00
Grand Total = 100.00 + 6.00 = 106.00
```

## Tax Inclusive

The entered price **already includes** tax.

```text
Gross Amount = Quantity × Unit Price - Discount
Net Amount = round(Gross Amount ÷ (1 + Rate), 2)
Tax Amount = Gross Amount - Net Amount
```

Example: 2 units × RM53.00, 6% tax

```text
Gross Amount = 2 × 53.00 = 106.00
Net Amount = 106.00 ÷ 1.06 = 100.00
Tax Amount = 106.00 - 100.00 = 6.00
```

> Do not calculate inclusive tax as `Gross Amount × Rate`; that overstates tax.

## Discount Order

Apply discount before tax.

```text
Line Amount = Quantity × Unit Price
Discounted Amount = Line Amount - Discount

Exclusive Tax = Discounted Amount × Rate
Inclusive Net = Discounted Amount ÷ (1 + Rate)
Inclusive Tax = Discounted Amount - Inclusive Net
```

Example: RM100.00 with RM10.00 discount at 6% exclusive tax

```text
Discounted Amount = 100.00 - 10.00 = 90.00
Tax = 90.00 × 0.06 = 5.40
Grand Total = 95.40
```

## Document Total

Calculate and round tax per line, then sum the lines.

```text
Document Net = sum(Line Net)
Document Tax = sum(Line Tax)
Document Total = sum(Line Grand Total)
```

For different tax codes/rates, calculate each line with its own rate, then sum all line taxes.

```csharp
decimal RoundMoney(decimal value) =>
    decimal.Round(value, 2, MidpointRounding.AwayFromZero);

// Exclusive
decimal net = quantity * unitPrice - discount;
decimal tax = RoundMoney(net * taxRate);
decimal total = net + tax;

// Inclusive
decimal gross = quantity * unitPrice - discount;
decimal inclusiveNet = RoundMoney(gross / (1m + taxRate));
decimal inclusiveTax = gross - inclusiveNet;