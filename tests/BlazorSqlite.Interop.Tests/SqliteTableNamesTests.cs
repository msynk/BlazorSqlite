using BlazorSqlite.Data;
using Xunit;

namespace BlazorSqlite.Interop.Tests;

public sealed class SqliteTableNamesTests
{
    [Theory]
    [InlineData("SELECT * FROM product", "product")]
    [InlineData("INSERT INTO product (name) VALUES ('x')", "product")]
    [InlineData("UPDATE product SET name = 'x'", "product")]
    [InlineData("DELETE FROM product", "product")]
    [InlineData("SELECT * FROM product JOIN category ON 1=1", "product", "category")]
    // The quoting SQLite accepts, a schema prefix, and UPDATE's conflict clause: each of these hid a
    // table from the pattern once, and a hidden table is a live query that never re-runs.
    [InlineData("SELECT * FROM \"Products\" AS \"p\"", "Products")]
    [InlineData("INSERT INTO [Products] (Name) VALUES ('x')", "Products")]
    [InlineData("DELETE FROM `Products` WHERE Id = 1", "Products")]
    [InlineData("SELECT * FROM main.\"Products\"", "Products")]
    [InlineData("UPDATE OR REPLACE Products SET Name = 'x'", "Products")]
    [InlineData("UPDATE OR IGNORE \"Products\" SET Name = 'x'", "Products")]
    [InlineData("CREATE TABLE IF NOT EXISTS \"Products\" (Id INTEGER)", "Products")]
    [InlineData("SELECT * FROM sqlite_master", new string[0])]
    public void ExtractsTables(string sql, params string[] expected)
        => Assert.Equal(
            expected.Order(StringComparer.OrdinalIgnoreCase),
            SqliteTableNames.Extract(sql).Order(StringComparer.OrdinalIgnoreCase));

    [Theory]
    [InlineData("INSERT INTO product (name) VALUES ('from')", true)]
    [InlineData("SELECT * FROM product", false)]
    // A write is a write wherever it sits in the batch. Missing one of these is a live query that
    // never re-runs, which is the failure this heuristic exists to prevent.
    [InlineData("BEGIN; INSERT INTO product (name) VALUES ('x'); COMMIT;", true)]
    [InlineData("SELECT 1; UPDATE product SET name = 'x'", true)]
    [InlineData("BEGIN; SELECT * FROM product; COMMIT;", false)]
    [InlineData("  \n  DELETE FROM product", true)]
    [InlineData("SELECT * FROM product; SELECT * FROM category", false)]
    // A common table expression in front of a write is still a write.
    [InlineData("WITH ids AS (SELECT 1 AS id) INSERT INTO product (id) SELECT id FROM ids", true)]
    [InlineData("WITH ids AS (SELECT 1 AS id) UPDATE product SET name = 'x' WHERE id IN (SELECT id FROM ids)", true)]
    [InlineData("WITH ids AS (SELECT 1 AS id) SELECT * FROM product WHERE id IN (SELECT id FROM ids)", false)]
    [InlineData("SELECT * FROM withdrawals", false)]
    public void DistinguishesWrites(string sql, bool write)
        => Assert.Equal(write, SqliteTableNames.LooksLikeWrite(sql));
}
