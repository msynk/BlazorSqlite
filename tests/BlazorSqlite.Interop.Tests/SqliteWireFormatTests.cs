using System.Globalization;
using System.Text.Json;
using BlazorSqlite.Data;
using BlazorSqlite.Interop;
using Xunit;

namespace BlazorSqlite.Interop.Tests;

/// <summary>
/// The wire format is the only thing standing between a 64-bit key and a silently rounded one, so
/// every storage class and both integer widths are pinned here rather than left to a browser test
/// that would also be testing the engine.
/// </summary>
public sealed class SqliteWireFormatTests
{
    [Fact]
    public void EncodeBatch_WritesTheShapeTheWorkerBinds()
    {
        var json = Serialize(BlazorSqliteWireFormat.EncodeBatch(
        [
            new BlazorSqliteCommandRequest
            {
                CommandText = "INSERT INTO t (n) VALUES (@n)",
                ResultKind = BlazorSqliteResultKind.NonQuery,
                Parameters = [new BlazorSqliteParameterValue("@n", 7)],
            },
        ]));

        Assert.Equal("INSERT INTO t (n) VALUES (@n)", json[0].GetProperty("commandText").GetString());
        Assert.Equal("nonQuery", json[0].GetProperty("resultKind").GetString());
        Assert.Equal("@n", json[0].GetProperty("parameters")[0].GetProperty("name").GetString());
        Assert.Equal(BlazorSqliteWireFormat.TypeCode.Integer, json[0].GetProperty("parameters")[0].GetProperty("type").GetInt32());
        Assert.Equal(7, json[0].GetProperty("parameters")[0].GetProperty("value").GetInt64());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(-1)]
    [InlineData(9007199254740991)]
    [InlineData(-9007199254740991)]
    public void IntegersInsideTheSafeRange_TravelAsJsonNumbers(long value)
    {
        var (_, encoded) = BlazorSqliteWireFormat.EncodeValue(value, "p");

        Assert.Equal(value, encoded);
        Assert.IsType<long>(encoded);
    }

    [Theory]
    [InlineData(9007199254740992)]
    [InlineData(-9007199254740992)]
    [InlineData(long.MaxValue)]
    [InlineData(long.MinValue)]
    public void IntegersOutsideTheSafeRange_TravelAsDecimalStrings(long value)
    {
        var (type, encoded) = BlazorSqliteWireFormat.EncodeValue(value, "p");

        Assert.Equal(BlazorSqliteWireFormat.TypeCode.Integer, type);
        Assert.Equal(value.ToString(CultureInfo.InvariantCulture), encoded);
    }

    [Fact]
    public void Decode_RestoresALargeIntegerFromItsDecimalString()
    {
        var results = BlazorSqliteWireFormat.DecodeResults(Parse(
            """
            [{
              "columnNames": ["v"],
              "columnTypes": ["INTEGER"],
              "recordsAffected": 0,
              "rows": [{ "t": [1], "v": ["9223372036854775807"] }]
            }]
            """));

        Assert.Equal(long.MaxValue, Assert.Single(Assert.Single(Assert.Single(results).Rows)));
    }

    [Fact]
    public void Decode_AcceptsASafeIntegerAsAJsonNumber()
    {
        var results = BlazorSqliteWireFormat.DecodeResults(Parse(
            """[{ "columnNames": ["v"], "columnTypes": ["INTEGER"], "rows": [{ "t": [1], "v": [42] }] }]"""));

        Assert.Equal(42L, Assert.Single(Assert.Single(Assert.Single(results).Rows)));
    }

    /// <summary>
    /// JSON has no spelling for the two IEEE values SQLite can hold. Left alone, an infinite
    /// parameter would fail inside the serializer with a message about JSON, and an infinite cell
    /// would arrive as null and fail inside the decoder. NaN is stored by SQLite as NULL, so that is
    /// what it becomes on the way in.
    /// </summary>
    [Fact]
    public void InfinityAndNaN_SurviveTheWire()
    {
        Assert.Equal((BlazorSqliteWireFormat.TypeCode.Real, "Infinity"), BlazorSqliteWireFormat.EncodeValue(double.PositiveInfinity, "p"));
        Assert.Equal((BlazorSqliteWireFormat.TypeCode.Real, "-Infinity"), BlazorSqliteWireFormat.EncodeValue(double.NegativeInfinity, "p"));
        Assert.Equal((BlazorSqliteWireFormat.TypeCode.Real, "Infinity"), BlazorSqliteWireFormat.EncodeValue(float.PositiveInfinity, "p"));
        Assert.Equal((BlazorSqliteWireFormat.TypeCode.Null, (object?)null), BlazorSqliteWireFormat.EncodeValue(double.NaN, "p"));

        var results = BlazorSqliteWireFormat.DecodeResults(Parse(
            """[{ "columnNames": ["v"], "columnTypes": ["REAL"], "rows": [{ "t": [2, 2, 2], "v": ["Infinity", "-Infinity", 1.5] }] }]"""));

        Assert.Equal(
            [double.PositiveInfinity, double.NegativeInfinity, 1.5d],
            Assert.Single(Assert.Single(results).Rows));
    }

    [Fact]
    public void Blob_RoundTripsThroughBase64()
    {
        byte[] blob = [0, 1, 250, 255];
        var encoded = BlazorSqliteWireFormat.EncodeBatch(
        [
            new BlazorSqliteCommandRequest
            {
                CommandText = "INSERT INTO t (b) VALUES (@b)",
                Parameters = [new BlazorSqliteParameterValue("@b", blob)],
            },
        ]);

        Assert.Equal(BlazorSqliteWireFormat.TypeCode.Blob, encoded[0].Parameters[0].Type);
        Assert.Equal(Convert.ToBase64String(blob), encoded[0].Parameters[0].Value);

        var results = BlazorSqliteWireFormat.DecodeResults(Parse(
            $$"""
            [{
              "columnNames": ["b"],
              "columnTypes": ["BLOB"],
              "rows": [{ "t": [4], "v": ["{{Convert.ToBase64String(blob)}}"] }]
            }]
            """));

        Assert.Equal(blob, Assert.Single(Assert.Single(Assert.Single(results).Rows)));
    }

    [Fact]
    public void NullBoolDecimalAndText_HaveOneObviousEncodingEach()
    {
        Assert.Equal((BlazorSqliteWireFormat.TypeCode.Null, (object?)null), BlazorSqliteWireFormat.EncodeValue(null, "p"));
        Assert.Equal((BlazorSqliteWireFormat.TypeCode.Null, (object?)null), BlazorSqliteWireFormat.EncodeValue(DBNull.Value, "p"));
        Assert.Equal((BlazorSqliteWireFormat.TypeCode.Integer, 1L), BlazorSqliteWireFormat.EncodeValue(true, "p"));
        Assert.Equal((BlazorSqliteWireFormat.TypeCode.Integer, 0L), BlazorSqliteWireFormat.EncodeValue(false, "p"));
        Assert.Equal((BlazorSqliteWireFormat.TypeCode.Text, "1.25"), BlazorSqliteWireFormat.EncodeValue(1.250m, "p"));
        Assert.Equal((BlazorSqliteWireFormat.TypeCode.Text, "hi"), BlazorSqliteWireFormat.EncodeValue("hi", "p"));
        Assert.Equal((BlazorSqliteWireFormat.TypeCode.Real, 1.5d), BlazorSqliteWireFormat.EncodeValue(1.5d, "p"));
    }

    /// <summary>
    /// The oracle is Microsoft.Data.Sqlite: whatever it would have stored for a decimal parameter is
    /// what the worker has to store, or a database written in the browser stops matching one written
    /// on the server - and EF's decimal equality, which is a plain TEXT comparison against a literal
    /// in this same form, starts returning nothing.
    /// </summary>
    [Theory]
    [InlineData("10", "10.0")]
    [InlineData("1.50", "1.5")]
    [InlineData("100.000", "100.0")]
    [InlineData("0", "0.0")]
    [InlineData("-3", "-3.0")]
    [InlineData("12.34", "12.34")]
    [InlineData("0.3333333333333333333333333333", "0.3333333333333333333333333333")]
    public void ADecimal_TravelsInMicrosoftDataSqlitesCanonicalTextForm(string clr, string expected)
    {
        var value = decimal.Parse(clr, CultureInfo.InvariantCulture);

        Assert.Equal((BlazorSqliteWireFormat.TypeCode.Text, expected), BlazorSqliteWireFormat.EncodeValue(value, "p"));
        Assert.Equal(expected, StoredByMicrosoftDataSqlite(value));
    }

    /// <summary>What Microsoft.Data.Sqlite actually writes for a decimal parameter.</summary>
    private static string StoredByMicrosoftDataSqlite(decimal value)
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT @v";
        command.Parameters.AddWithValue("@v", value);
        return (string)command.ExecuteScalar()!;
    }

    [Fact]
    public void DatesAndGuids_AreRejected_SoTheTypeMappingOwnsTheChoice()
    {
        var date = Assert.Throws<NotSupportedException>(
            () => BlazorSqliteWireFormat.EncodeValue(new DateTime(2026, 1, 1), "when"));
        var guid = Assert.Throws<NotSupportedException>(
            () => BlazorSqliteWireFormat.EncodeValue(Guid.Empty, "id"));

        Assert.Contains("when", date.Message);
        Assert.Contains("id", guid.Message);
        Assert.Contains("ADO.NET binder", date.Message);
    }

    /// <summary>
    /// A reply that does not match the format is a wire problem and is reported as one, rather than
    /// as whatever <c>System.Text.Json</c> happens to raise when a property is missing or the wrong
    /// kind.
    /// </summary>
    [Theory]
    [InlineData("""{ "result": 1 }""")]
    [InlineData("""{ "ok": "yes", "result": 1 }""")]
    [InlineData("[]")]
    public void DecodeCall_ReportsAMalformedEnvelope_AsAFormatException(string json)
        => Assert.Throws<FormatException>(() => BlazorSqliteWireFormat.DecodeCall(Parse(json)));

    [Theory]
    [InlineData("""[{ "rows": [{ "v": [1] }] }]""")]
    [InlineData("""[{ "rows": [{ "t": 1, "v": [1] }] }]""")]
    [InlineData("""[{ "rows": [{ "t": [1, 3], "v": [1] }] }]""")]
    public void DecodeResults_ReportsAMalformedRow_AsAFormatException(string json)
        => Assert.Throws<FormatException>(() => BlazorSqliteWireFormat.DecodeResults(Parse(json)));

    [Fact]
    public void AnUnsignedIntegerPastInt64_IsRejectedRatherThanWrapped()
    {
        var error = Assert.Throws<NotSupportedException>(
            () => BlazorSqliteWireFormat.EncodeValue(ulong.MaxValue, "p"));

        Assert.Contains("64-bit signed INTEGER", error.Message);
    }

    [Fact]
    public void DecodeCall_ReturnsTheResult_WhenTheWorkerSucceeded()
    {
        var result = BlazorSqliteWireFormat.DecodeCall(Parse("""{ "ok": true, "result": [1, 2] }"""));

        Assert.Equal(2, result.GetArrayLength());
    }

    [Fact]
    public void DecodeCall_ThrowsBlazorSqliteException_CarryingTheResultCode()
    {
        var error = Assert.Throws<BlazorSqliteException>(
            () => BlazorSqliteWireFormat.DecodeCall(Parse(
                """{ "ok": false, "error": { "message": "UNIQUE constraint failed", "sqliteCode": 19 } }""")));

        Assert.Equal("UNIQUE constraint failed", error.Message);
        Assert.Equal(19, error.SqliteErrorCode);
    }

    [Fact]
    public void DecodeCall_ThrowsWithoutACode_WhenTheFailureIsTheTransports()
    {
        var error = Assert.Throws<BlazorSqliteException>(
            () => BlazorSqliteWireFormat.DecodeCall(Parse(
                """{ "ok": false, "error": { "message": "No database is open in this worker.", "sqliteCode": null } }""")));

        Assert.Null(error.SqliteErrorCode);
    }

    [Fact]
    public void DecodeCall_RejectsABareArray_SoAMissingEnvelopeCannotBeMistakenForSuccess()
    {
        Assert.Throws<FormatException>(() => BlazorSqliteWireFormat.DecodeCall(Parse("[1]")));
    }

    [Fact]
    public void DecodeResults_RejectsARowWhoseTypesAndValuesDisagree()
    {
        Assert.Throws<FormatException>(
            () => BlazorSqliteWireFormat.DecodeResults(Parse(
                """[{ "rows": [{ "t": [1, 3], "v": [1] }] }]""")));
    }

    private static JsonElement Serialize<T>(T value)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(value));
        return document.RootElement.Clone();
    }

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
