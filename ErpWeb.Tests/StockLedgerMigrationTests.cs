using Microsoft.Data.Sqlite;

namespace ErpWeb.Tests;

[Trait(TestCategories.Name, TestCategories.Planning)]
public sealed class StockLedgerMigrationTests
{
    [Fact]
    public void Two_hop_consume_reversal_remaining_equals_issue_minus_consume_plus_reversal()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        using (var create = connection.CreateCommand())
        {
            create.CommandText =
                """
                CREATE TABLE PrMaterialMovement (
                    UID INTEGER PRIMARY KEY,
                    MovementType TEXT NOT NULL,
                    OriginalMovementID INTEGER NULL,
                    BaseQty NUMERIC NOT NULL
                );
                INSERT INTO PrMaterialMovement (UID, MovementType, OriginalMovementID, BaseQty)
                VALUES
                    (1, 'ISSUE', NULL, 100),
                    (2, 'CONSUME', 1, 40),
                    (3, 'CONSUME_REVERSAL', 2, 10);
                """;
            create.ExecuteNonQuery();
        }

        using var twoHop = connection.CreateCommand();
        twoHop.CommandText =
            """
            SELECT ROUND(
                i.BaseQty
                - IFNULL((
                    SELECT SUM(c.BaseQty)
                    FROM PrMaterialMovement c
                    WHERE c.MovementType = 'CONSUME'
                      AND c.OriginalMovementID = i.UID
                ), 0)
                + IFNULL((
                    SELECT SUM(cr.BaseQty)
                    FROM PrMaterialMovement cr
                    INNER JOIN PrMaterialMovement c
                        ON c.UID = cr.OriginalMovementID
                       AND c.MovementType = 'CONSUME'
                    WHERE cr.MovementType = 'CONSUME_REVERSAL'
                      AND c.OriginalMovementID = i.UID
                ), 0)
            , 4)
            FROM PrMaterialMovement i
            WHERE i.MovementType = 'ISSUE';
            """;
        var remaining = Convert.ToDecimal(twoHop.ExecuteScalar());

        using var oneHop = connection.CreateCommand();
        oneHop.CommandText =
            """
            SELECT ROUND(
                i.BaseQty
                - IFNULL((
                    SELECT SUM(c.BaseQty)
                    FROM PrMaterialMovement c
                    WHERE c.MovementType = 'CONSUME'
                      AND c.OriginalMovementID = i.UID
                ), 0)
                + IFNULL((
                    SELECT SUM(cr.BaseQty)
                    FROM PrMaterialMovement cr
                    WHERE cr.MovementType = 'CONSUME_REVERSAL'
                      AND cr.OriginalMovementID = i.UID
                ), 0)
            , 4)
            FROM PrMaterialMovement i
            WHERE i.MovementType = 'ISSUE';
            """;
        var wrongOneHop = Convert.ToDecimal(oneHop.ExecuteScalar());

        Assert.Equal(70m, remaining);
        Assert.Equal(60m, wrongOneHop);
    }
}
