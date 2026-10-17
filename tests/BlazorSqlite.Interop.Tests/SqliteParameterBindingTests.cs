using BlazorSqlite.Data;
using BlazorSqlite.Interop;
using Xunit;

namespace BlazorSqlite.Interop.Tests;

/// <summary>
/// The wire format refuses dates and GUIDs. The ADO.NET binder must convert them first, using the
/// same TEXT forms Microsoft.Data.Sqlite binds, or a browser SaveChanges of a DateTime column fails.
/// </summary>
public sealed class SqliteParameterBindingTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void DateTime_MatchesMicrosoftDataSqliteText()
    {
        var value = BlazorSqliteParameterBinding.ToTransportValue(
            new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc));

        Assert.Equal("2026-03-01 12:00:00", value);
    }

    [Fact]
    public void Guid_MatchesMicrosoftDataSqliteText()
    {
        var guid = Guid.Parse("d1e2f3a4-b5c6-4789-abcd-ef0123456789");

        Assert.Equal("D1E2F3A4-B5C6-4789-ABCD-EF0123456789", BlazorSqliteParameterBinding.ToTransportValue(guid));
    }

    [Fact]
    public void ConvertedValues_AreAcceptedByTheWireFormat()
    {
        var date = BlazorSqliteParameterBinding.ToTransportValue(new DateTime(2026, 1, 1));
        var guid = BlazorSqliteParameterBinding.ToTransportValue(Guid.Empty);

        var (dateType, dateValue) = BlazorSqliteWireFormat.EncodeValue(date, "when");
        var (guidType, guidValue) = BlazorSqliteWireFormat.EncodeValue(guid, "id");

        Assert.Equal(BlazorSqliteWireFormat.TypeCode.Text, dateType);
        Assert.Equal("2026-01-01 00:00:00", dateValue);
        Assert.Equal(BlazorSqliteWireFormat.TypeCode.Text, guidType);
        Assert.Equal("00000000-0000-0000-0000-000000000000", guidValue);
    }

    [Fact]
    public void FromTransportValue_ParsesTheTextToTransportValueWrote()
    {
        var lead = TimeSpan.FromDays(2);
        var born = new DateOnly(1978, 4, 12);
        var ordered = new DateTimeOffset(2026, 6, 2, 10, 15, 0, TimeSpan.Zero);

        Assert.Equal(lead, BlazorSqliteParameterBinding.FromTransportValue<TimeSpan>(
            BlazorSqliteParameterBinding.ToTransportValue(lead)));
        Assert.Equal(born, BlazorSqliteParameterBinding.FromTransportValue<DateOnly>(
            BlazorSqliteParameterBinding.ToTransportValue(born)));
        Assert.Equal(ordered, BlazorSqliteParameterBinding.FromTransportValue<DateTimeOffset>(
            BlazorSqliteParameterBinding.ToTransportValue(ordered)));
    }

    [Fact]
    public async Task Reader_GetFieldValue_ParsesTimeSpanText()
    {
        var transport = new ResultTransport(new BlazorSqliteCommandResult
        {
            ColumnNames = ["lead"],
            ColumnTypes = ["TEXT"],
            Rows = [["2.00:00:00"]],
        });
        await using var connection = new BlazorSqliteConnection(transport, "read.db");
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT lead FROM t";
        await using var reader = await command.ExecuteReaderAsync(Ct);

        Assert.True(await reader.ReadAsync(Ct));
        Assert.Equal(TimeSpan.FromDays(2), reader.GetFieldValue<TimeSpan>(0));
    }

    [Fact]
    public async Task Command_ConvertsDateTimeBeforeTheTransportSeesIt()
    {
        var transport = new CapturingTransport();
        await using var connection = new BlazorSqliteConnection(transport, "bind.db");
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO t (d) VALUES (@p1)";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@p1";
        parameter.Value = new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc);
        command.Parameters.Add(parameter);

        await command.ExecuteNonQueryAsync(Ct);

        var batch = Assert.Single(transport.LastBatch!);
        Assert.Equal("2026-03-01 12:00:00", Assert.Single(batch.Parameters).Value);
        _ = BlazorSqliteWireFormat.EncodeBatch(transport.LastBatch!);
    }

    private sealed class CapturingTransport : IBlazorSqliteTransport
    {
        public IReadOnlyList<BlazorSqliteCommandRequest>? LastBatch { get; private set; }

        public Task OpenAsync(string databaseName, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task CloseAsync(CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<IReadOnlyList<BlazorSqliteCommandResult>> ExecuteAsync(
            IReadOnlyList<BlazorSqliteCommandRequest> batch,
            CancellationToken cancellationToken = default)
        {
            LastBatch = batch;
            return Task.FromResult<IReadOnlyList<BlazorSqliteCommandResult>>([new BlazorSqliteCommandResult()]);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ResultTransport(BlazorSqliteCommandResult result) : IBlazorSqliteTransport
    {
        public Task OpenAsync(string databaseName, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task CloseAsync(CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<IReadOnlyList<BlazorSqliteCommandResult>> ExecuteAsync(
            IReadOnlyList<BlazorSqliteCommandRequest> batch,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<BlazorSqliteCommandResult>>([result]);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
