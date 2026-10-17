using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using BlazorSqlite.Data;
using BlazorSqlite.Interop;
using Xunit;

namespace BlazorSqlite.Interop.Tests;

/// <summary>
/// The wire format is the only thing standing between a 64-bit key and a silently rounded one, so
/// every storage class and the exact byte layout are pinned here rather than left to a browser test
/// that would also be testing the engine. <see cref="ReplyBuilder"/> writes replies the way
/// <c>blazor-sqlite-wire.js</c> does; a layout change must be made in both places.
/// </summary>
public sealed class SqliteWireFormatTests
{
    [Fact]
    public void EncodeBatch_WritesTheLayoutTheWorkerReads()
    {
        var bytes = SqliteWireFormat.EncodeBatch(
        [
            new SqliteCommandRequest
            {
                CommandText = "INSERT INTO t (n) VALUES (@n)",
                ResultKind = SqliteResultKind.Reader,
                RowLimit = 5,
                Parameters = [new SqliteParameterValue("@n", 7)],
            },
        ]);

        var reader = new SpanReader(bytes);
        Assert.Equal(SqliteWireFormat.Magic, reader.Byte());
        Assert.Equal(SqliteWireFormat.Version, reader.Byte());
        Assert.Equal(1u, reader.UInt32());
        Assert.Equal("INSERT INTO t (n) VALUES (@n)", reader.String());
        Assert.Equal(2, reader.Byte());                 // reader
        Assert.Equal(5, reader.Int32());                // row limit
        Assert.Equal(1, reader.UInt16());               // parameter count
        Assert.Equal("@n", reader.String());
        Assert.Equal(SqliteWireFormat.TypeCode.Integer, reader.Byte());
        Assert.Equal(7L, reader.Int64());
        Assert.Equal(0, reader.Remaining);
    }

    [Fact]
    public void EncodeBatch_WritesMinusOne_WhenThereIsNoRowLimit()
    {
        var bytes = SqliteWireFormat.EncodeBatch([new SqliteCommandRequest { CommandText = "SELECT 1" }]);

        var reader = new SpanReader(bytes);
        reader.Skip(6);
        reader.String();
        Assert.Equal(0, reader.Byte());
        Assert.Equal(-1, reader.Int32());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(-1)]
    [InlineData(9007199254740993)]
    [InlineData(long.MaxValue)]
    [InlineData(long.MinValue)]
    public void Integers_TravelAsEightBytes_WhateverTheirMagnitude(long value)
    {
        var (type, encoded) = SqliteWireFormat.EncodeValue(value, "p");

        Assert.Equal(SqliteWireFormat.TypeCode.Integer, type);
        Assert.Equal(value, Assert.IsType<long>(encoded));

        var bytes = SqliteWireFormat.EncodeBatch(
            [new SqliteCommandRequest { CommandText = "x", Parameters = [new SqliteParameterValue("p", value)] }]);
        Assert.Equal(value, BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(bytes.Length - 8)));
    }

    [Fact]
    public void Decode_RestoresALargeInteger_Exactly()
    {
        var reply = new ReplyBuilder()
            .Result(["v"], [SqliteWireFormat.TypeCode.Integer])
            .Row(long.MaxValue)
            .End()
            .Build();

        var results = SqliteWireFormat.DecodeReply(reply);

        Assert.Equal(long.MaxValue, Assert.Single(Assert.Single(Assert.Single(results).Rows)));
    }

    [Fact]
    public void Blob_RoundTripsAsRawBytes()
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

        // The last nine bytes are the blob tag, its length, and the bytes themselves.
        Assert.Equal(SqliteWireFormat.TypeCode.Blob, encoded[^9]);
        Assert.Equal(4u, BinaryPrimitives.ReadUInt32LittleEndian(encoded.AsSpan(encoded.Length - 8)));
        Assert.Equal(blob, encoded[^4..]);

        var results = SqliteWireFormat.DecodeReply(new ReplyBuilder()
            .Result(["b"], [SqliteWireFormat.TypeCode.Blob])
            .Row(blob)
            .End()
            .Build());

        Assert.Equal(blob, Assert.Single(Assert.Single(Assert.Single(results).Rows)));
    }

    [Fact]
    public void EveryStorageClass_DecodesToItsClrType()
    {
        var results = SqliteWireFormat.DecodeReply(new ReplyBuilder()
            .Result(
                ["i", "r", "t", "b", "n"],
                [SqliteWireFormat.TypeCode.Integer, SqliteWireFormat.TypeCode.Real, SqliteWireFormat.TypeCode.Text, SqliteWireFormat.TypeCode.Blob, 0])
            .Row(42L, 1.5d, "héllo wörld", new byte[] { 9 }, null)
            .End()
            .Build());

        var result = Assert.Single(results);
        var row = Assert.Single(result.Rows);
        Assert.Equal(["INTEGER", "REAL", "TEXT", "BLOB", null], result.ColumnTypes);
        Assert.Equal(42L, row[0]);
        Assert.Equal(1.5d, row[1]);
        Assert.Equal("héllo wörld", row[2]);
        Assert.Equal(new byte[] { 9 }, row[3]);
        Assert.Null(row[4]);
    }

    [Fact]
    public void Decode_CarriesCountersAndChangedTables()
    {
        var results = SqliteWireFormat.DecodeReply(new ReplyBuilder()
            .Result([], [], recordsAffected: 3, lastInsertRowId: 77)
            .End("Products", "Orders")
            .Build());

        var result = Assert.Single(results);
        Assert.Equal(3, result.RecordsAffected);
        Assert.Equal(77L, result.LastInsertRowId);
        Assert.NotNull(result.ChangedTables);
        Assert.True(result.ChangedTables.SetEquals(["products", "ORDERS"]));
    }

    [Fact]
    public void Decode_ReturnsOneResultPerRequest_InOrder()
    {
        var results = SqliteWireFormat.DecodeReply(new ReplyBuilder()
            .Result([], [], recordsAffected: 1).End()
            .Result(["n"], [SqliteWireFormat.TypeCode.Text]).Row("First").Row("Second").End()
            .Build());

        Assert.Equal(2, results.Count);
        Assert.Equal(1, results[0].RecordsAffected);
        Assert.Equal(2, results[1].Rows.Count);
        Assert.Equal("Second", results[1].Rows[1][0]);
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
    public void DecodeReply_ThrowsBlazorSqliteException_CarryingTheResultCode()
    {
        var error = Assert.Throws<BlazorSqliteConstraintException>(
            () => SqliteWireFormat.DecodeReply(ReplyBuilder.Error("UNIQUE constraint failed", 19, "SQLiteError")));

        Assert.Equal("UNIQUE constraint failed", error.Message);
        Assert.Equal(19, error.SqliteErrorCode);
    }

    [Fact]
    public void DecodeReply_ThrowsWithoutACode_WhenTheFailureIsTheTransports()
    {
        var error = Assert.Throws<BlazorSqliteException>(
            () => SqliteWireFormat.DecodeReply(ReplyBuilder.Error("No database is open in this worker.", -1, "Error")));

        Assert.Null(error.SqliteErrorCode);
    }

    [Theory]
    [InlineData(5, typeof(BlazorSqliteConcurrencyException))]
    [InlineData(6, typeof(BlazorSqliteConcurrencyException))]
    [InlineData(11, typeof(BlazorSqliteCorruptDatabaseException))]
    [InlineData(13, typeof(BlazorSqliteQuotaExceededException))]
    [InlineData(19, typeof(BlazorSqliteConstraintException))]
    [InlineData(1, typeof(BlazorSqliteException))]
    public void ResultCodes_MapToTypedExceptions(int code, Type expected)
    {
        var error = SqliteWireFormat.CreateException("x", code, null);

        Assert.IsType(expected, error, exactMatch: true);
        Assert.Equal(code, error.SqliteErrorCode);
    }

    [Fact]
    public void AQuotaExceededError_IsRecognisedByName_WhenTheBrowserThrewIt()
    {
        var error = SqliteWireFormat.CreateException("The quota has been exceeded.", null, "QuotaExceededError");

        Assert.IsType<BlazorSqliteQuotaExceededException>(error);
    }

    [Fact]
    public void DecodeReply_RejectsTheWrongMagic()
    {
        Assert.Throws<FormatException>(() => SqliteWireFormat.DecodeReply([0x00, 0x01, 0x00]));
    }

    [Fact]
    public void DecodeReply_RejectsAnotherVersion_NamingTheMismatch()
    {
        var error = Assert.Throws<FormatException>(
            () => SqliteWireFormat.DecodeReply([SqliteWireFormat.Magic, (byte)(SqliteWireFormat.Version + 1), 0]));

        Assert.Contains("BlazorSqlite.Js", error.Message);
    }

    [Fact]
    public void DecodeReply_RejectsATruncatedMessage()
    {
        var whole = new ReplyBuilder().Result(["v"], [SqliteWireFormat.TypeCode.Text]).Row("abc").End().Build();

        Assert.Throws<FormatException>(() => SqliteWireFormat.DecodeReply(whole.AsSpan(0, whole.Length - 2)));
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
        var error = Assert.Throws<BlazorSqliteConstraintException>(
            () => SqliteWireFormat.DecodeCall(Parse(
                """{ "ok": false, "error": { "message": "UNIQUE constraint failed", "sqliteCode": 19 } }""")));

        Assert.Equal("UNIQUE constraint failed", error.Message);
        Assert.Equal(19, error.SqliteErrorCode);
    }

    [Fact]
    public void DecodeCall_RejectsABareArray_SoAMissingEnvelopeCannotBeMistakenForSuccess()
    {
        Assert.Throws<FormatException>(() => SqliteWireFormat.DecodeCall(Parse("[1]")));
    }

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    /// <summary>Walks a batch the way the worker's `BinaryReader` does.</summary>
    private ref struct SpanReader(ReadOnlySpan<byte> data)
    {
        private readonly ReadOnlySpan<byte> _data = data;
        private int _offset = 0;

        public readonly int Remaining => _data.Length - _offset;

        public void Skip(int count) => _offset += count;

        public byte Byte() => _data[_offset++];

        public ushort UInt16()
        {
            var value = BinaryPrimitives.ReadUInt16LittleEndian(_data[_offset..]);
            _offset += 2;
            return value;
        }

        public int Int32()
        {
            var value = BinaryPrimitives.ReadInt32LittleEndian(_data[_offset..]);
            _offset += 4;
            return value;
        }

        public uint UInt32()
        {
            var value = BinaryPrimitives.ReadUInt32LittleEndian(_data[_offset..]);
            _offset += 4;
            return value;
        }

        public long Int64()
        {
            var value = BinaryPrimitives.ReadInt64LittleEndian(_data[_offset..]);
            _offset += 8;
            return value;
        }

        public string String()
        {
            var length = (int)UInt32();
            var value = Encoding.UTF8.GetString(_data.Slice(_offset, length));
            _offset += length;
            return value;
        }
    }
}

/// <summary>Writes replies exactly as <c>blazor-sqlite-wire.js</c> does, so the decoder is tested against the real layout.</summary>
internal sealed class ReplyBuilder
{
    private readonly MemoryStream _stream = new();
    private readonly BinaryWriter _writer;
    private int _resultCount;
    private long _rowCountPosition;
    private int _rowCount;

    public ReplyBuilder()
    {
        _writer = new BinaryWriter(_stream, Encoding.UTF8, leaveOpen: true);
        _writer.Write(SqliteWireFormat.Magic);
        _writer.Write(SqliteWireFormat.Version);
        _writer.Write((byte)0);
        _writer.Write(0u); // result count, patched by Build
    }

    public static byte[] Error(string message, int code, string name)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8);
        writer.Write(SqliteWireFormat.Magic);
        writer.Write(SqliteWireFormat.Version);
        writer.Write((byte)1);
        WriteString(writer, message);
        writer.Write(code);
        WriteString(writer, name);
        writer.Flush();
        return stream.ToArray();
    }

    public ReplyBuilder Result(string[] columns, int[] storageClasses, int recordsAffected = 0, long lastInsertRowId = 0)
    {
        _resultCount++;
        _writer.Write((ushort)columns.Length);
        for (var i = 0; i < columns.Length; i++)
        {
            WriteString(_writer, columns[i]);
            _writer.Write((byte)storageClasses[i]);
        }

        _writer.Write(recordsAffected);
        _writer.Write(lastInsertRowId);
        _rowCountPosition = _stream.Position;
        _writer.Write(0u);
        _rowCount = 0;
        return this;
    }

    public ReplyBuilder Row(params object?[] cells)
    {
        _rowCount++;
        foreach (var cell in cells)
        {
            switch (cell)
            {
                case null:
                    _writer.Write((byte)SqliteWireFormat.TypeCode.Null);
                    break;
                case long value:
                    _writer.Write((byte)SqliteWireFormat.TypeCode.Integer);
                    _writer.Write(value);
                    break;
                case double value:
                    _writer.Write((byte)SqliteWireFormat.TypeCode.Real);
                    _writer.Write(value);
                    break;
                case string value:
                    _writer.Write((byte)SqliteWireFormat.TypeCode.Text);
                    WriteString(_writer, value);
                    break;
                case byte[] value:
                    _writer.Write((byte)SqliteWireFormat.TypeCode.Blob);
                    _writer.Write((uint)value.Length);
                    _writer.Write(value);
                    break;
                default:
                    throw new ArgumentException($"Unsupported cell {cell.GetType()}.");
            }
        }

        return this;
    }

    public ReplyBuilder End(params string[] changedTables)
    {
        var end = _stream.Position;
        _stream.Position = _rowCountPosition;
        _writer.Write((uint)_rowCount);
        _stream.Position = end;

        _writer.Write((ushort)changedTables.Length);
        foreach (var table in changedTables)
        {
            WriteString(_writer, table);
        }

        return this;
    }

    public byte[] Build()
    {
        _writer.Flush();
        var bytes = _stream.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(3), (uint)_resultCount);
        return bytes;
    }

    private static void WriteString(BinaryWriter writer, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        writer.Write((uint)bytes.Length);
        writer.Write(bytes);
    }
}
