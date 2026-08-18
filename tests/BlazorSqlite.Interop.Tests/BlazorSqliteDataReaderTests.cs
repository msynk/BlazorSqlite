using System.Data.Common;
using BlazorSqlite.Data;
using Xunit;

namespace BlazorSqlite.Interop.Tests;

/// <summary>
/// The reader is the surface EF materializes through, so the ADO.NET contract matters here more
/// than anywhere else: schema questions must be answerable before the first row, and no answer may
/// be <see cref="DBNull"/>.
/// </summary>
public sealed class BlazorSqliteDataReaderTests
{
    [Fact]
    public void GetFieldType_AnswersBeforeTheFirstRead()
    {
        using var reader = Reader(
            ["id", "name"],
            ["INTEGER", "TEXT"],
            [[1L, "Widget"]]);

        Assert.Equal(typeof(long), reader.GetFieldType(0));
        Assert.Equal(typeof(string), reader.GetFieldType(1));
    }

    /// <summary>
    /// A NULL cell has no type of its own. Answering <c>DBNull</c> - which is what asking the
    /// current row would give - is not a CLR type any caller can materialize into.
    /// </summary>
    [Fact]
    public void GetFieldType_NeverAnswersDBNull_ForAColumnThatStartsNull()
    {
        using var reader = Reader(
            ["name"],
            ["TEXT"],
            [[null], ["Widget"]]);

        Assert.True(reader.Read());
        Assert.True(reader.IsDBNull(0));
        Assert.Equal(typeof(string), reader.GetFieldType(0));
    }

    /// <summary>
    /// With no data to go on the column's reported type decides, by SQLite's affinity rules - which
    /// have to work for the worker's storage-class spellings as well as declared types.
    /// </summary>
    [Theory]
    [InlineData("INTEGER", typeof(long))]
    [InlineData("int", typeof(long))]
    [InlineData("BIGINT", typeof(long))]
    [InlineData("TEXT", typeof(string))]
    [InlineData("nvarchar(50)", typeof(string))]
    [InlineData("CLOB", typeof(string))]
    [InlineData("BLOB", typeof(byte[]))]
    [InlineData("REAL", typeof(double))]
    [InlineData("decimal(18,2)", typeof(double))]
    [InlineData(null, typeof(object))]
    public void GetFieldType_FallsBackToAffinity_ForAnEmptyResult(string? declared, Type expected)
    {
        using var reader = Reader(["v"], [declared], []);

        Assert.Equal(expected, reader.GetFieldType(0));
    }

    /// <summary>
    /// Reading past the end of a blob is how a caller finds the end, so it yields nothing rather
    /// than an exception out of the copy.
    /// </summary>
    [Fact]
    public void GetBytes_ReturnsZero_WhenTheOffsetIsPastTheEnd()
    {
        using var reader = Reader(["b"], ["BLOB"], [[new byte[] { 1, 2, 3 }]]);
        Assert.True(reader.Read());

        var buffer = new byte[8];

        Assert.Equal(3, reader.GetBytes(0, 0, null, 0, 0));
        Assert.Equal(2, reader.GetBytes(0, 1, buffer, 0, 8));
        Assert.Equal(0, reader.GetBytes(0, 3, buffer, 0, 8));
    }

    [Fact]
    public void GetChars_ReturnsZero_WhenTheOffsetIsPastTheEnd()
    {
        using var reader = Reader(["s"], ["TEXT"], [["abc"]]);
        Assert.True(reader.Read());

        var buffer = new char[8];

        Assert.Equal(3, reader.GetChars(0, 0, null, 0, 0));
        Assert.Equal(0, reader.GetChars(0, 3, buffer, 0, 8));
    }

    private static DbDataReader Reader(
        string[] columnNames,
        string?[] columnTypes,
        object?[][] rows)
        => new BlazorSqliteDataReader(new SqliteCommandResult
        {
            ColumnNames = columnNames,
            ColumnTypes = columnTypes,
            Rows = rows,
        });
}
