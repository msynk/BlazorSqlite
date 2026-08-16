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
        var json = Serialize(SqliteWireFormat.EncodeBatch(
        [
            new SqliteCommandRequest
            {
                CommandText = "INSERT INTO t (n) VALUES (@n)",
                ResultKind = SqliteResultKind.NonQuery,
                Parameters = [new SqliteParameterValue("@n", 7)],
            },
        ]));

        Assert.Equal("INSERT INTO t (n) VALUES (@n)", json[0].GetProperty("commandText").GetString());
        Assert.Equal("nonQuery", json[0].GetProperty("resultKind").GetString());
        Assert.Equal("@n", json[0].GetProperty("parameters")[0].GetProperty("name").GetString());
        Assert.Equal(SqliteWireFormat.TypeCode.Integer, json[0].GetProperty("parameters")[0].GetProperty("type").GetInt32());
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
        var (_, encoded) = SqliteWireFormat.EncodeValue(value, "p");

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
        var (type, encoded) = SqliteWireFormat.EncodeValue(value, "p");

        Assert.Equal(SqliteWireFormat.TypeCode.Integer, type);
        Assert.Equal(value.ToString(CultureInfo.InvariantCulture), encoded);
    }

    [Fact]
    public void Decode_RestoresALargeIntegerFromItsDecimalString()
    {
        var results = SqliteWireFormat.DecodeResults(Parse(
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
        var results = SqliteWireFormat.DecodeResults(Parse(
            """[{ "columnNames": ["v"], "columnTypes": ["INTEGER"], "rows": [{ "t": [1], "v": [42] }] }]"""));

        Assert.Equal(42L, Assert.Single(Assert.Single(Assert.Single(results).Rows)));
    }

    [Fact]
    public void Blob_RoundTripsThroughBase64()
    {
        byte[] blob = [0, 1, 250, 255];
        var encoded = SqliteWireFormat.EncodeBatch(
        [
            new SqliteCommandRequest
            {
                CommandText = "INSERT INTO t (b) VALUES (@b)",
                Parameters = [new SqliteParameterValue("@b", blob)],
            },
        ]);

        Assert.Equal(SqliteWireFormat.TypeCode.Blob, encoded[0].Parameters[0].Type);
        Assert.Equal(Convert.ToBase64String(blob), encoded[0].Parameters[0].Value);

        var results = SqliteWireFormat.DecodeResults(Parse(
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
        Assert.Equal((SqliteWireFormat.TypeCode.Null, (object?)null), SqliteWireFormat.EncodeValue(null, "p"));
        Assert.Equal((SqliteWireFormat.TypeCode.Null, (object?)null), SqliteWireFormat.EncodeValue(DBNull.Value, "p"));
        Assert.Equal((SqliteWireFormat.TypeCode.Integer, 1L), SqliteWireFormat.EncodeValue(true, "p"));
        Assert.Equal((SqliteWireFormat.TypeCode.Integer, 0L), SqliteWireFormat.EncodeValue(false, "p"));
        Assert.Equal((SqliteWireFormat.TypeCode.Text, "1.250"), SqliteWireFormat.EncodeValue(1.250m, "p"));
        Assert.Equal((SqliteWireFormat.TypeCode.Text, "hi"), SqliteWireFormat.EncodeValue("hi", "p"));
        Assert.Equal((SqliteWireFormat.TypeCode.Real, 1.5d), SqliteWireFormat.EncodeValue(1.5d, "p"));
    }

    [Fact]
    public void DatesAndGuids_AreRejected_SoTheTypeMappingOwnsTheChoice()
    {
        var date = Assert.Throws<NotSupportedException>(
            () => SqliteWireFormat.EncodeValue(new DateTime(2026, 1, 1), "when"));
        var guid = Assert.Throws<NotSupportedException>(
            () => SqliteWireFormat.EncodeValue(Guid.Empty, "id"));

        Assert.Contains("when", date.Message);
        Assert.Contains("id", guid.Message);
        Assert.Contains("ADO.NET binder", date.Message);
    }

    [Fact]
    public void AnUnsignedIntegerPastInt64_IsRejectedRatherThanWrapped()
    {
        var error = Assert.Throws<NotSupportedException>(
            () => SqliteWireFormat.EncodeValue(ulong.MaxValue, "p"));

        Assert.Contains("64-bit signed INTEGER", error.Message);
    }

    [Fact]
    public void DecodeCall_ReturnsTheResult_WhenTheWorkerSucceeded()
    {
        var result = SqliteWireFormat.DecodeCall(Parse("""{ "ok": true, "result": [1, 2] }"""));

        Assert.Equal(2, result.GetArrayLength());
    }

    [Fact]
    public void DecodeCall_ThrowsBlazorSqliteException_CarryingTheResultCode()
    {
        var error = Assert.Throws<BlazorSqliteException>(
            () => SqliteWireFormat.DecodeCall(Parse(
                """{ "ok": false, "error": { "message": "UNIQUE constraint failed", "sqliteCode": 19 } }""")));

        Assert.Equal("UNIQUE constraint failed", error.Message);
        Assert.Equal(19, error.SqliteErrorCode);
    }

    [Fact]
    public void DecodeCall_ThrowsWithoutACode_WhenTheFailureIsTheTransports()
    {
        var error = Assert.Throws<BlazorSqliteException>(
            () => SqliteWireFormat.DecodeCall(Parse(
                """{ "ok": false, "error": { "message": "No database is open in this worker.", "sqliteCode": null } }""")));

        Assert.Null(error.SqliteErrorCode);
    }

    [Fact]
    public void DecodeCall_RejectsABareArray_SoAMissingEnvelopeCannotBeMistakenForSuccess()
    {
        Assert.Throws<FormatException>(() => SqliteWireFormat.DecodeCall(Parse("[1]")));
    }

    [Fact]
    public void DecodeResults_RejectsARowWhoseTypesAndValuesDisagree()
    {
        Assert.Throws<FormatException>(
            () => SqliteWireFormat.DecodeResults(Parse(
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
