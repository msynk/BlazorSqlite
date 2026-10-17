using BlazorSqlite.Data;
using BlazorSqlite.Interop;
using BlazorSqlite.Testing;

namespace BlazorSqlite.EntityFrameworkCore.Tests;

/// <summary>
/// An <see cref="IBlazorSqliteTransport"/> that pushes every parameter through the real wire format
/// before executing it in-process.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="BlazorSqliteInProcessTransport"/> hands CLR values straight to Microsoft.Data.Sqlite, which
/// applies its own conversions. That is the right thing for testing SQL generation, but it means
/// the desktop suite never sees the encoding the browser actually stores - so a value the worker
/// would write differently from the server looks fine in every test and is wrong only in
/// production.
/// </para>
/// <para>
/// This closes that gap by doing what the worker does: encode with
/// <see cref="BlazorSqliteWireFormat"/>, take the one representation the binary format carries for
/// each value, and bind that. What EF stores through this transport is byte-for-byte what it would
/// store through a worker.
/// </para>
/// </remarks>
internal sealed class WireLoopbackTransport : IBlazorSqliteTransport
{
    private readonly BlazorSqliteInProcessTransport _inner = new();

    public Task OpenAsync(string databaseName, CancellationToken cancellationToken = default)
        => _inner.OpenAsync(databaseName, cancellationToken);

    public Task CloseAsync(CancellationToken cancellationToken = default)
        => _inner.CloseAsync(cancellationToken);

    public Task<IReadOnlyList<BlazorSqliteCommandResult>> ExecuteAsync(
        IReadOnlyList<BlazorSqliteCommandRequest> batch,
        CancellationToken cancellationToken = default)
    {
        // The whole batch goes through the encoder, so anything it rejects is rejected here too.
        _ = BlazorSqliteWireFormat.EncodeBatch(batch);

        var rebound = new BlazorSqliteCommandRequest[batch.Count];
        for (var i = 0; i < batch.Count; i++)
        {
            rebound[i] = batch[i] with
            {
                Parameters = [.. batch[i].Parameters.Select(p =>
                {
                    var (type, value) = BlazorSqliteWireFormat.EncodeValue(p.Value, p.Name);
                    return new BlazorSqliteParameterValue(p.Name, Decode(type, value));
                })],
            };
        }

        return _inner.ExecuteAsync(rebound, cancellationToken);
    }

    public ValueTask DisposeAsync() => _inner.DisposeAsync();

    /// <summary>Mirrors <c>decodeBatchBinary</c> in <c>blazor-sqlite-wire.js</c>.</summary>
    private static object? Decode(int type, object? value) => type switch
    {
        BlazorSqliteWireFormat.TypeCode.Null => null,
        BlazorSqliteWireFormat.TypeCode.Integer => (long)value!,
        BlazorSqliteWireFormat.TypeCode.Real => (double)value!,
        BlazorSqliteWireFormat.TypeCode.Text => (string)value!,
        BlazorSqliteWireFormat.TypeCode.Blob => ((ReadOnlyMemory<byte>)value!).ToArray(),
        _ => throw new FormatException($"Unknown wire type tag {type}."),
    };
}
