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
    [InlineData("SELECT * FROM \"Products\" AS p", "Products")]
    [InlineData("SELECT * FROM [Products]", "Products")]
    [InlineData("SELECT * FROM `Products`", "Products")]
    [InlineData("INSERT INTO main.Products (Name) VALUES ('x')", "Products")]
    [InlineData("INSERT INTO \"main\".\"Products\" (Name) VALUES ('x')", "Products")]
    [InlineData("CREATE TABLE IF NOT EXISTS \"Orders\" (Id INTEGER)", "Orders")]
    [InlineData("CREATE INDEX IX_Orders_Date ON Orders (Date)", "Orders")]
    [InlineData("DROP VIEW OpenOrders", "OpenOrders")]
    [InlineData("SELECT * FROM (SELECT 1)")]
    public void ExtractsTables(string sql, params string[] expected)
        => Assert.Equal(
            expected.Order(StringComparer.OrdinalIgnoreCase),
            SqliteTableNames.Extract(sql).Order(StringComparer.OrdinalIgnoreCase));

    [Fact]
    public void SkipsSqliteInternalTables()
        => Assert.Empty(SqliteTableNames.Extract("SELECT * FROM sqlite_master"));

    [Theory]
    [InlineData("INSERT INTO product (name) VALUES ('from')", true)]
    [InlineData("SELECT * FROM product", false)]
    [InlineData("BEGIN; INSERT INTO product (name) VALUES ('x'); COMMIT;", true)]
    [InlineData("PRAGMA foreign_keys = ON; DELETE FROM product", true)]
    [InlineData("-- comment\nUPDATE product SET name = 'x'", true)]
    [InlineData("/* comment */ DELETE FROM product", true)]
    [InlineData("WITH gone AS (SELECT id FROM product) DELETE FROM product WHERE id IN gone", true)]
    [InlineData("SELECT 'insert into' FROM product", false)]
    [InlineData("CREATE TABLE t (id INTEGER)", true)]
    public void DistinguishesWrites(string sql, bool write)
        => Assert.Equal(write, SqliteTableNames.LooksLikeWrite(sql));
}
