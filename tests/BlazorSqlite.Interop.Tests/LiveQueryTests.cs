using BlazorSqlite.Data;
using BlazorSqlite.Testing;
using Xunit;

namespace BlazorSqlite.Interop.Tests;

public sealed class LiveQueryTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ReRuns_WhenAWatchedTableIsWritten()
    {
        var transport = new InProcessSqliteTransport();
        await using var connection = new BlazorSqliteConnection(transport, "live.db");
        await connection.OpenAsync(Ct);
        await using var create = connection.CreateCommand();
        create.CommandText = "CREATE TABLE product (id INTEGER PRIMARY KEY, name TEXT)";
        await create.ExecuteNonQueryAsync(Ct);

        var seen = new List<int>();
        await using var live = new LiveQuery<int>(
            connection,
            async ct =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT COUNT(*) FROM product";
                return Convert.ToInt32(await command.ExecuteScalarAsync(ct));
            },
            ["product"]);

        live.Changed += (_, count) => seen.Add(count);
        Assert.Equal(0, await live.RefreshAsync(Ct));

        await using var insert = connection.CreateCommand();
        insert.CommandText = "INSERT INTO product (name) VALUES ('Kept')";
        await insert.ExecuteNonQueryAsync(Ct);

        await WaitUntilAsync(() => seen.Contains(1));
        Assert.Equal(1, live.Current);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        Assert.True(condition());
    }
}
