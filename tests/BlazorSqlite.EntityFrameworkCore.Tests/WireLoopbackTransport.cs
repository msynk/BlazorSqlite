using System.Globalization;
using BlazorSqlite.Data;
using BlazorSqlite.Interop;
using BlazorSqlite.Testing;

namespace BlazorSqlite.EntityFrameworkCore.Tests;

/// <summary>
/// An <see cref="ISqliteTransport"/> that pushes every parameter through the real wire format
/// before executing it in-process.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="InProcessSqliteTransport"/> hands CLR values straight to Microsoft.Data.Sqlite, which
/// applies its own conversions. That is the right thing for testing SQL generation, but it means
/// the desktop suite never sees the encoding the browser actually stores - so a value the worker
/// would write differently from the server looks fine in every test and is wrong only in
/// production.
/// </para>
/// <para>
/// This closes that gap by doing what the worker does: encode with
/// <see cref="SqliteWireFormat"/>, decode the tagged value back, and bind the result. What EF
/// stores through this transport is byte-for-byte what it would store through a worker.
/// </para>
/// </remarks>
internal sealed class WireLoopbackTransport : ISqliteTransport
{
    private readonly InProcessSqliteTransport _inner = new();

    public Task OpenAsync(string databaseName, CancellationToken cancellationToken = default)
        => _inner.OpenAsync(databaseName, cancellationToken);

    public Task CloseAsync(CancellationToken cancellationToken = default)
        => _inner.CloseAsync(cancellationToken);

    public Task<IReadOnlyList<SqliteCommandResult>> ExecuteAsync(
        IReadOnlyList<SqliteCommandRequest> batch,
        CancellationToken cancellationToken = default)
    {
        var encoded = SqliteWireFormat.EncodeBatch(batch);
        var rebound = new SqliteCommandRequest[batch.Count];

        for (var i = 0; i < batch.Count; i++)
        {
            rebound[i] = batch[i] with
            {
                Parameters = [.. encoded[i].Parameters.Select(
                    p => new SqliteParameterValue(p.Name, Decode(p.Type, p.Value)))],
            };
        }

        return _inner.ExecuteAsync(rebound, cancellationToken);
    }

    public ValueTask DisposeAsync() => _inner.DisposeAsync();

    /// <summary>Mirrors <c>decodeParameter</c> in <c>blazor-sqlite-wire.js</c>.</summary>
    private static object? Decode(int type, object? value) => type switch
    {
        SqliteWireFormat.TypeCode.Null => null,
        SqliteWireFormat.TypeCode.Integer => value is string text
            ? long.Parse(text, CultureInfo.InvariantCulture)
            : value,
        SqliteWireFormat.TypeCode.Real or SqliteWireFormat.TypeCode.Text => value,
        SqliteWireFormat.TypeCode.Blob => Convert.FromBase64String((string)value!),
        _ => throw new FormatException($"Unknown wire type tag {type}."),
    };
}
