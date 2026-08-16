using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using BlazorSqlite.Data;

namespace BlazorSqlite.Interop;

/// <summary>
/// Translates between the ADO.NET layer's types and the JSON the browser transport carries.
/// </summary>
/// <remarks>
/// <para>
/// A wire format exists because JSON cannot represent two things SQLite returns routinely: integers
/// outside the range a JavaScript number holds exactly, and blobs. Left to a plain JSON projection,
/// a 64-bit key silently loses its low bits and a blob arrives as an array of numbers. Both failures
/// are quiet, which is why the format is explicit rather than incidental.
/// </para>
/// <para>
/// Every value therefore travels with its SQLite storage class, using SQLite's own type codes so the
/// JavaScript half can label values from <c>sqlite3_column_type</c> without a translation table. Rows
/// are sent as parallel type and value arrays rather than as objects per value: the type of a column is
/// usually but not necessarily uniform, so per-cell fidelity is required, and one small array per row
/// is much cheaper than an object per cell.
/// </para>
/// <para>
/// This is a JSON encoding because it must survive Blazor's JS interop, which serializes arguments.
/// It is also the layer to replace when interop cost shows up in the S2 measurements — the ADO.NET
/// layer never sees it, so a byte-oriented marshaller can be swapped in without touching anything above.
/// </para>
/// </remarks>
public static class SqliteWireFormat
{
    /// <summary>SQLite's storage class codes, used verbatim as the wire type tags.</summary>
    public static class TypeCode
    {
        /// <summary><c>SQLITE_INTEGER</c>.</summary>
        public const int Integer = 1;

        /// <summary><c>SQLITE_FLOAT</c>.</summary>
        public const int Real = 2;

        /// <summary><c>SQLITE_TEXT</c>.</summary>
        public const int Text = 3;

        /// <summary><c>SQLITE_BLOB</c>.</summary>
        public const int Blob = 4;

        /// <summary><c>SQLITE_NULL</c>.</summary>
        public const int Null = 5;
    }

    /// <summary>
    /// Integers up to this magnitude are sent as JSON numbers; beyond it they are sent as decimal
    /// strings, because a JavaScript number can no longer represent them exactly.
    /// </summary>
    private const long MaxExactInteger = 9007199254740991; // 2^53 - 1

    /// <summary>Encodes a batch for the worker.</summary>
    public static WireRequest[] EncodeBatch(IReadOnlyList<SqliteCommandRequest> batch)
    {
        ArgumentNullException.ThrowIfNull(batch);

        var encoded = new WireRequest[batch.Count];
        for (var i = 0; i < batch.Count; i++)
        {
            encoded[i] = EncodeRequest(batch[i]);
        }

        return encoded;
    }

    private static WireRequest EncodeRequest(SqliteCommandRequest request)
    {
        var parameters = new WireParameter[request.Parameters.Count];
        for (var i = 0; i < request.Parameters.Count; i++)
        {
            var parameter = request.Parameters[i];
            var (type, value) = EncodeValue(parameter.Value, parameter.Name);
            parameters[i] = new WireParameter(parameter.Name, type, value);
        }

        return new WireRequest(request.CommandText, EncodeResultKind(request.ResultKind), parameters);
    }

    private static string EncodeResultKind(SqliteResultKind kind) => kind switch
    {
        SqliteResultKind.NonQuery => "nonQuery",
        SqliteResultKind.Scalar => "scalar",
        SqliteResultKind.Reader => "reader",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown result kind."),
    };

    /// <summary>
    /// Maps a parameter value onto a storage class and a JSON-safe representation.
    /// </summary>
    /// <remarks>
    /// Only conversions with one obvious answer are performed here. Dates, times, and GUIDs have more
    /// than one reasonable SQLite representation, and choosing silently would put this layer in
    /// disagreement with the ADO.NET binder that owns the decision, so they are rejected with a message
    /// saying so. <see cref="SqliteParameterBinding"/> converts them before a parameter reaches the
    /// transport.
    /// </remarks>
    internal static (int Type, object? Value) EncodeValue(object? value, string parameterName)
    {
        switch (value)
        {
            case null or DBNull:
                return (TypeCode.Null, null);

            // SQLite has no boolean storage class; it stores 0 and 1, as every SQLite provider does.
            case bool flag:
                return (TypeCode.Integer, flag ? 1L : 0L);

            case byte or sbyte or short or ushort or int or uint or long:
                return EncodeInteger(Convert.ToInt64(value, CultureInfo.InvariantCulture));

            case ulong unsigned when unsigned <= long.MaxValue:
                return EncodeInteger((long)unsigned);

            case ulong tooLarge:
                throw new NotSupportedException(
                    $"Parameter '{parameterName}' is {tooLarge}, which exceeds the range of SQLite's "
                    + "64-bit signed INTEGER.");

            case float or double:
                return (TypeCode.Real, Convert.ToDouble(value, CultureInfo.InvariantCulture));

            // Text, so that no precision is lost. Comparisons and arithmetic over these columns are
            // what the ef_* function set exists to make correct.
            case decimal money:
                return (TypeCode.Text, money.ToString(CultureInfo.InvariantCulture));

            case string text:
                return (TypeCode.Text, text);

            case char character:
                return (TypeCode.Text, character.ToString());

            case byte[] blob:
                return (TypeCode.Blob, Convert.ToBase64String(blob));

            case ReadOnlyMemory<byte> memory:
                return (TypeCode.Blob, Convert.ToBase64String(memory.Span));

            case Memory<byte> memory:
                return (TypeCode.Blob, Convert.ToBase64String(memory.Span));

            default:
                throw new NotSupportedException(
                    $"Parameter '{parameterName}' is of type {value.GetType()}, which the transport does "
                    + "not convert. Values with more than one sensible SQLite representation — dates, "
                    + "times, and GUIDs among them — must be converted by the ADO.NET binder before "
                    + "reaching the transport, so that one component owns the choice.");
        }
    }

    private static (int Type, object? Value) EncodeInteger(long value)
        => value is >= -MaxExactInteger and <= MaxExactInteger
            ? (TypeCode.Integer, value)
            : (TypeCode.Integer, value.ToString(CultureInfo.InvariantCulture));

    /// <summary>
    /// Reads the envelope returned by the host's <c>call</c> method, which never throws across JS
    /// interop — Blazor would otherwise reduce a JavaScript error to its message and drop the SQLite
    /// result code.
    /// </summary>
    /// <exception cref="BlazorSqliteException">The worker reported a failure.</exception>
    /// <exception cref="FormatException">The reply did not match the envelope.</exception>
    public static JsonElement DecodeCall(JsonElement envelope)
    {
        if (envelope.ValueKind != JsonValueKind.Object
            || !envelope.TryGetProperty("ok", out var ok))
        {
            throw new FormatException(
                "Expected a { ok, result } or { ok, error } envelope from the worker.");
        }

        if (ok.GetBoolean())
        {
            return envelope.TryGetProperty("result", out var result)
                ? result
                : default;
        }

        throw ReadError(envelope);
    }

    private static BlazorSqliteException ReadError(JsonElement envelope)
    {
        if (!envelope.TryGetProperty("error", out var error)
            || error.ValueKind != JsonValueKind.Object)
        {
            throw new BlazorSqliteException("The SQLite worker reported a failure without a message.");
        }

        var message = error.TryGetProperty("message", out var text) && text.ValueKind == JsonValueKind.String
            ? text.GetString()
            : null;

        int? code = error.TryGetProperty("sqliteCode", out var sqliteCode)
            && sqliteCode.ValueKind == JsonValueKind.Number
                ? sqliteCode.GetInt32()
                : null;

        var textMessage = string.IsNullOrWhiteSpace(message)
            ? "The SQLite worker reported a failure."
            : message;

        var name = error.TryGetProperty("name", out var nameElement)
            && nameElement.ValueKind == JsonValueKind.String
                ? nameElement.GetString()
                : null;

        if (code is 13 || string.Equals(name, "QuotaExceededError", StringComparison.Ordinal))
        {
            return new BlazorSqliteQuotaExceededException(textMessage, code ?? 13);
        }

        if (code is 11)
        {
            return new BlazorSqliteCorruptDatabaseException(textMessage, code);
        }

        if (code is 5 or 6)
        {
            return new BlazorSqliteConcurrencyException(textMessage, code);
        }

        return new BlazorSqliteException(textMessage, code);
    }

    /// <summary>Decodes the worker's reply into results the ADO.NET layer can read.</summary>
    /// <exception cref="FormatException">The reply did not match the wire format.</exception>
    public static IReadOnlyList<SqliteCommandResult> DecodeResults(JsonElement json)
    {
        if (json.ValueKind != JsonValueKind.Array)
        {
            throw new FormatException(
                $"Expected an array of results from the worker but received {json.ValueKind}.");
        }

        var results = new List<SqliteCommandResult>(json.GetArrayLength());
        foreach (var element in json.EnumerateArray())
        {
            results.Add(DecodeResult(element));
        }

        return results;
    }

    private static SqliteCommandResult DecodeResult(JsonElement element)
    {
        var columnNames = ReadStringArray(element, "columnNames");
        var columnTypes = ReadNullableStringArray(element, "columnTypes");
        var rows = ReadRows(element);

        return new SqliteCommandResult
        {
            ColumnNames = columnNames,
            ColumnTypes = columnTypes,
            Rows = rows,
            RecordsAffected = element.TryGetProperty("recordsAffected", out var affected)
                ? affected.GetInt32()
                : 0,
        };
    }

    private static object?[][] ReadRows(JsonElement element)
    {
        if (!element.TryGetProperty("rows", out var rows) || rows.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var decoded = new object?[rows.GetArrayLength()][];
        var index = 0;

        foreach (var row in rows.EnumerateArray())
        {
            var types = row.GetProperty("t");
            var values = row.GetProperty("v");

            if (types.GetArrayLength() != values.GetArrayLength())
            {
                throw new FormatException(
                    $"Row {index} carries {types.GetArrayLength()} type tags for "
                    + $"{values.GetArrayLength()} values.");
            }

            var cells = new object?[values.GetArrayLength()];
            for (var i = 0; i < cells.Length; i++)
            {
                cells[i] = DecodeValue(types[i].GetInt32(), values[i]);
            }

            decoded[index++] = cells;
        }

        return decoded;
    }

    private static object? DecodeValue(int type, JsonElement value) => type switch
    {
        TypeCode.Null => null,

        // A large integer arrives as a decimal string, so both forms are accepted for INTEGER.
        TypeCode.Integer => value.ValueKind switch
        {
            JsonValueKind.String => long.Parse(value.GetString()!, CultureInfo.InvariantCulture),
            _ => value.GetInt64(),
        },

        TypeCode.Real => value.GetDouble(),
        TypeCode.Text => value.GetString(),
        TypeCode.Blob => value.GetBytesFromBase64(),

        _ => throw new FormatException($"Unknown wire type tag {type}."),
    };

    private static string[] ReadStringArray(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var values = new string[array.GetArrayLength()];
        var index = 0;
        foreach (var item in array.EnumerateArray())
        {
            values[index++] = item.GetString() ?? string.Empty;
        }

        return values;
    }

    private static string?[] ReadNullableStringArray(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var values = new string?[array.GetArrayLength()];
        var index = 0;
        foreach (var item in array.EnumerateArray())
        {
            values[index++] = item.ValueKind == JsonValueKind.Null ? null : item.GetString();
        }

        return values;
    }
}

/// <summary>One command on the wire.</summary>
public sealed record WireRequest(
    [property: JsonPropertyName("commandText")] string CommandText,
    [property: JsonPropertyName("resultKind")] string ResultKind,
    [property: JsonPropertyName("parameters")] WireParameter[] Parameters);

/// <summary>One parameter on the wire, tagged with its SQLite storage class.</summary>
public sealed record WireParameter(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("type")] int Type,
    [property: JsonPropertyName("value")] object? Value);
