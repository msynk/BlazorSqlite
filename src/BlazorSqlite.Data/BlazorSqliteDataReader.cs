using System.Collections;
using System.Data.Common;
using System.Globalization;

namespace BlazorSqlite.Data;

/// <summary>Reads the materialized result sets returned by the transport.</summary>
/// <remarks>
/// The transport delivers each result set in one round trip, so this reader walks in-memory
/// buffers rather than streaming. A reader over a batch holds one result set per command and
/// moves between them with <see cref="NextResult"/>.
/// </remarks>
public sealed class BlazorSqliteDataReader : DbDataReader
{
    private readonly IReadOnlyList<SqliteCommandResult> _results;
    private readonly BlazorSqliteConnection? _closeConnection;
    private SqliteCommandResult _result;
    private Dictionary<string, int>? _ordinals;
    private int _resultIndex;
    private int _rowIndex = -1;
    private bool _closed;

    internal BlazorSqliteDataReader(IReadOnlyList<SqliteCommandResult> results, BlazorSqliteConnection? closeConnection = null)
    {
        _results = results.Count > 0 ? results : [new SqliteCommandResult()];
        _result = _results[0];
        _closeConnection = closeConnection;
    }

    public override int FieldCount => _result.ColumnNames.Count;

    public override bool HasRows => _result.Rows.Count > 0;

    public override bool IsClosed => _closed;

    /// <summary>Rows changed by the writes in the current result set's command.</summary>
    public override int RecordsAffected => _result.RecordsAffected;

    public override int Depth => 0;

    /// <summary>The rowid of the last <c>INSERT</c> in the current result set's command.</summary>
    public long LastInsertRowId => _result.LastInsertRowId;

    public override object this[int ordinal] => GetValue(ordinal);

    public override object this[string name] => GetValue(GetOrdinal(name));

    public override bool Read()
    {
        if (_rowIndex + 1 >= _result.Rows.Count)
        {
            _rowIndex = _result.Rows.Count;
            return false;
        }

        _rowIndex++;
        return true;
    }

    public override Task<bool> ReadAsync(CancellationToken cancellationToken)
        => Task.FromResult(Read());

    public override bool NextResult()
    {
        if (_resultIndex + 1 >= _results.Count)
        {
            return false;
        }

        _resultIndex++;
        _result = _results[_resultIndex];
        _ordinals = null;
        _rowIndex = -1;
        return true;
    }

    public override Task<bool> NextResultAsync(CancellationToken cancellationToken)
        => Task.FromResult(NextResult());

    public override void Close()
    {
        if (_closed)
        {
            return;
        }

        _closed = true;
        _closeConnection?.Close();
    }

    public override string GetName(int ordinal) => _result.ColumnNames[ordinal];

    public override int GetOrdinal(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (_ordinals is null)
        {
            var names = _result.ColumnNames;
            _ordinals = new Dictionary<string, int>(names.Count, StringComparer.OrdinalIgnoreCase);
            for (var i = names.Count - 1; i >= 0; i--)
            {
                // First occurrence wins for duplicate names, as every ADO.NET provider does.
                _ordinals[names[i]] = i;
            }
        }

        return _ordinals.TryGetValue(name, out var ordinal)
            ? ordinal
            : throw new IndexOutOfRangeException($"No column named '{name}'.");
    }

    public override string GetDataTypeName(int ordinal) => _result.ColumnTypes.Count > ordinal
        ? _result.ColumnTypes[ordinal] ?? "BLOB"
        : "BLOB";

    /// <summary>
    /// The CLR type of the column, from the current row's value when there is one and from the
    /// column's storage class otherwise, so it can be asked before <see cref="Read"/>.
    /// </summary>
    public override Type GetFieldType(int ordinal)
    {
        if (_rowIndex >= 0 && _rowIndex < _result.Rows.Count && _result.Rows[_rowIndex][ordinal] is { } value)
        {
            return value.GetType();
        }

        var typeName = _result.ColumnTypes.Count > ordinal ? _result.ColumnTypes[ordinal] : null;
        return TypeForStorageClass(typeName);
    }

    public override object GetValue(int ordinal) => CurrentRow[ordinal] ?? DBNull.Value;

    public override int GetValues(object[] values)
    {
        ArgumentNullException.ThrowIfNull(values);

        var row = CurrentRow;
        var count = Math.Min(values.Length, row.Length);
        for (var i = 0; i < count; i++)
        {
            values[i] = row[i] ?? DBNull.Value;
        }

        return count;
    }

    public override bool IsDBNull(int ordinal) => CurrentRow[ordinal] is null;

    public override Task<bool> IsDBNullAsync(int ordinal, CancellationToken cancellationToken)
        => Task.FromResult(IsDBNull(ordinal));

    public override bool GetBoolean(int ordinal) => RequireValue(ordinal) switch
    {
        bool value => value,
        long value => value != 0,
        string text => !(text is "0" || text.Equals("false", StringComparison.OrdinalIgnoreCase)),
        var other => Convert.ToBoolean(other, CultureInfo.InvariantCulture),
    };

    public override byte GetByte(int ordinal) => Convert.ToByte(RequireValue(ordinal), CultureInfo.InvariantCulture);

    public override char GetChar(int ordinal) => RequireValue(ordinal) switch
    {
        string { Length: > 0 } text => text[0],
        var other => Convert.ToChar(other, CultureInfo.InvariantCulture),
    };

    public override DateTime GetDateTime(int ordinal) => RequireValue(ordinal) switch
    {
        DateTime value => value,
        string text => DateTime.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        var other => Convert.ToDateTime(other, CultureInfo.InvariantCulture),
    };

    public override decimal GetDecimal(int ordinal) => RequireValue(ordinal) switch
    {
        decimal value => value,
        string text => decimal.Parse(text, NumberStyles.Number, CultureInfo.InvariantCulture),
        var other => Convert.ToDecimal(other, CultureInfo.InvariantCulture),
    };

    public override double GetDouble(int ordinal) => Convert.ToDouble(RequireValue(ordinal), CultureInfo.InvariantCulture);

    public override float GetFloat(int ordinal) => Convert.ToSingle(RequireValue(ordinal), CultureInfo.InvariantCulture);

    public override Guid GetGuid(int ordinal) => RequireValue(ordinal) switch
    {
        Guid value => value,
        string text => Guid.Parse(text),
        byte[] bytes => new Guid(bytes),
        var other => throw new InvalidCastException($"Cannot convert {other.GetType()} to Guid."),
    };

    public override short GetInt16(int ordinal) => Convert.ToInt16(RequireValue(ordinal), CultureInfo.InvariantCulture);

    public override int GetInt32(int ordinal) => Convert.ToInt32(RequireValue(ordinal), CultureInfo.InvariantCulture);

    public override long GetInt64(int ordinal) => Convert.ToInt64(RequireValue(ordinal), CultureInfo.InvariantCulture);

    public override string GetString(int ordinal) => Convert.ToString(RequireValue(ordinal), CultureInfo.InvariantCulture)!;

    public override T GetFieldValue<T>(int ordinal)
        => SqliteParameterBinding.FromTransportValue<T>(IsDBNull(ordinal) ? null : RequireValue(ordinal));

    public override Task<T> GetFieldValueAsync<T>(int ordinal, CancellationToken cancellationToken)
        => Task.FromResult(GetFieldValue<T>(ordinal));

    public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length)
    {
        var source = RequireValue(ordinal) switch
        {
            byte[] bytes => bytes,
            string text => System.Text.Encoding.UTF8.GetBytes(text),
            var other => throw new InvalidCastException($"Cannot read {other.GetType()} as bytes."),
        };

        if (buffer is null)
        {
            return source.Length;
        }

        if (dataOffset >= source.Length)
        {
            return 0;
        }

        var count = (int)Math.Min(length, source.Length - dataOffset);
        count = Math.Min(count, buffer.Length - bufferOffset);
        Array.Copy(source, dataOffset, buffer, bufferOffset, count);
        return count;
    }

    public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length)
    {
        var source = GetString(ordinal);
        if (buffer is null)
        {
            return source.Length;
        }

        if (dataOffset >= source.Length)
        {
            return 0;
        }

        var count = (int)Math.Min(length, source.Length - dataOffset);
        count = Math.Min(count, buffer.Length - bufferOffset);
        source.CopyTo((int)dataOffset, buffer, bufferOffset, count);
        return count;
    }

    public override Stream GetStream(int ordinal) => new MemoryStream(RequireValue(ordinal) switch
    {
        byte[] bytes => bytes,
        string text => System.Text.Encoding.UTF8.GetBytes(text),
        var other => throw new InvalidCastException($"Cannot read {other.GetType()} as a stream."),
    }, writable: false);

    public override TextReader GetTextReader(int ordinal) => new StringReader(GetString(ordinal));

    public override IEnumerator GetEnumerator() => new DbEnumerator(this, closeReader: false);

    private object?[] CurrentRow => _rowIndex >= 0 && _rowIndex < _result.Rows.Count
        ? _result.Rows[_rowIndex]
        : throw new InvalidOperationException("No row is available. Call Read first.");

    private object RequireValue(int ordinal)
        => CurrentRow[ordinal] ?? throw new InvalidCastException($"Column {ordinal} is NULL.");

    private static Type TypeForStorageClass(string? typeName)
    {
        if (typeName is null)
        {
            return typeof(object);
        }

        var upper = typeName.ToUpperInvariant();
        if (upper.Contains("INT", StringComparison.Ordinal))
        {
            return typeof(long);
        }

        if (upper.Contains("CHAR", StringComparison.Ordinal)
            || upper.Contains("CLOB", StringComparison.Ordinal)
            || upper.Contains("TEXT", StringComparison.Ordinal))
        {
            return typeof(string);
        }

        if (upper.Contains("BLOB", StringComparison.Ordinal))
        {
            return typeof(byte[]);
        }

        if (upper.Contains("REAL", StringComparison.Ordinal)
            || upper.Contains("FLOA", StringComparison.Ordinal)
            || upper.Contains("DOUB", StringComparison.Ordinal))
        {
            return typeof(double);
        }

        return typeof(object);
    }
}
