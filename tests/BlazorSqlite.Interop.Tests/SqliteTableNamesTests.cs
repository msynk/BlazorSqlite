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
    public void DistinguishesWrites(string sql, bool write)
        => Assert.Equal(write, SqliteTableNames.LooksLikeWrite(sql));
}
