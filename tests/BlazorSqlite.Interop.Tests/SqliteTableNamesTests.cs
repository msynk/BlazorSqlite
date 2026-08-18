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
    public void DistinguishesWrites(string sql, bool write)
        => Assert.Equal(write, SqliteTableNames.LooksLikeWrite(sql));
}
