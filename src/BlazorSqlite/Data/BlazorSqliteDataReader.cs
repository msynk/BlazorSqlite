using System.Collections;
using System.Data.Common;
using System.Globalization;

namespace BlazorSqlite.Data;

/// <summary>Reads a materialized result set returned by the transport.</summary>
/// <remarks>
/// The transport delivers the whole result set in one round trip, so this reader walks an
/// in-memory buffer rather than streaming.
/// </remarks>
public sealed class BlazorSqliteDataReader : DbDataReader
{
    private readonly SqliteCommandResult _result;
    private int _rowIndex = -1;
    private bool _closed;

    internal BlazorSqliteDataReader(SqliteCommandResult result) => _result = result;

    public override int FieldCount => _result.ColumnNames.Count;

    public override bool HasRows => _result.Rows.Count > 0;

    public override bool IsClosed => _closed;

    public override int RecordsAffected => _result.RecordsAffected;

    public override int Depth => 0;

    public override object this[int ordinal] => GetValue(ordinal);

    public override object this[string name] => GetValue(GetOrdinal(name));

    public override bool Read()
    {
        if (_rowIndex + 1 >= _result.Rows.Count)
        {
            return false;
        }

        _rowIndex++;
        return true;
    }

    public override Task<bool> ReadAsync(CancellationToken cancellationToken)
        => Task.FromResult(Read());

    public override bool NextResult() => false;

    public override void Close() => _closed = true;

    public override string GetName(int ordinal) => _result.ColumnNames[ordinal];

    public override int GetOrdinal(string name)
    {
        for (var i = 0; i < _result.ColumnNames.Count; i++)
        {
            if (string.Equals(_result.ColumnNames[i], name, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        throw new IndexOutOfRangeException($"No column named '{name}'.");
    }

    public override string GetDataTypeName(int ordinal) => _result.ColumnTypes[ordinal] ?? "BLOB";

    /// <summary>The CLR type of the column, as a schema question rather than a per-row one.</summary>
    /// <remarks>
    /// Answered from the result set rather than from the current row, for two reasons. It must not
    /// require a positioned reader - callers read schema before the first <c>Read</c> - and it must
    /// never answer <see cref="DBNull"/>, which is what asking a NULL cell for its type would give.
    /// The first non-null value in the column is the most honest answer available; failing that the
    /// column's reported type decides, by SQLite's own affinity rules.
    /// </remarks>
    public override Type GetFieldType(int ordinal)
    {
        foreach (var row in _result.Rows)
        {
            if (ordinal < row.Length && row[ordinal] is { } value)
            {
                return value.GetType();
            }
        }

        return AffinityOf(ordinal < _result.ColumnTypes.Count ? _result.ColumnTypes[ordinal] : null);
    }

    /// <summary>
    /// SQLite's column-affinity rules, which are the only thing a declared type is good for.
    /// </summary>
    /// <remarks>
    /// The worker reports storage classes rather than declared types - its engine is built with
    /// <c>SQLITE_OMIT_DECLTYPE</c> - so both spellings have to land in the same place: "INTEGER"
    /// matches the INT rule either way, and so on down the list.
    /// </remarks>
    private static Type AffinityOf(string? declaredType)
    {
        if (string.IsNullOrEmpty(declaredType))
        {
            return typeof(object);
        }

        if (declaredType.Contains("INT", StringComparison.OrdinalIgnoreCase))
        {
            return typeof(long);
        }

        if (declaredType.Contains("CHAR", StringComparison.OrdinalIgnoreCase)
            || declaredType.Contains("CLOB", StringComparison.OrdinalIgnoreCase)
            || declaredType.Contains("TEXT", StringComparison.OrdinalIgnoreCase))
        {
            return typeof(string);
        }

        if (declaredType.Contains("BLOB", StringComparison.OrdinalIgnoreCase))
        {
            return typeof(byte[]);
        }

        return typeof(double);
    }

    public override object GetValue(int ordinal) => CurrentRow[ordinal] ?? DBNull.Value;

    public override int GetValues(object[] values)
    {
        var count = Math.Min(values.Length, FieldCount);
        for (var i = 0; i < count; i++)
        {
            values[i] = GetValue(i);
        }

        return count;
    }

    public override bool IsDBNull(int ordinal) => CurrentRow[ordinal] is null;

    public override bool GetBoolean(int ordinal) => Convert.ToBoolean(RequireValue(ordinal), CultureInfo.InvariantCulture);

    public override byte GetByte(int ordinal) => Convert.ToByte(RequireValue(ordinal), CultureInfo.InvariantCulture);

    public override char GetChar(int ordinal) => Convert.ToChar(RequireValue(ordinal), CultureInfo.InvariantCulture);

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
        var source = (byte[])RequireValue(ordinal);
        if (buffer is null)
        {
            return source.Length;
        }

        // Clamped: reading past the end of a blob is how a caller discovers where the end is, so it
        // returns nothing rather than throwing out of Array.Copy on a negative count.
        var count = (int)Math.Clamp(Math.Min(length, source.Length - dataOffset), 0, buffer.Length - bufferOffset);
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

        var count = (int)Math.Clamp(Math.Min(length, source.Length - dataOffset), 0, buffer.Length - bufferOffset);
        source.CopyTo((int)dataOffset, buffer, bufferOffset, count);
        return count;
    }

    public override IEnumerator GetEnumerator() => new DbEnumerator(this, closeReader: false);

    private object?[] CurrentRow => _rowIndex >= 0 && _rowIndex < _result.Rows.Count
        ? _result.Rows[_rowIndex]
        : throw new InvalidOperationException("No row is available. Call Read first.");

    private object RequireValue(int ordinal)
        => CurrentRow[ordinal] ?? throw new InvalidCastException($"Column {ordinal} is NULL.");
}
