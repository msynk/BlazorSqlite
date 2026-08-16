using BlazorSqlite.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace BlazorSqlite.EntityFrameworkCore.Tests;

internal static class ContextFactory
{
    public static ProductContext Create(ISqliteTransport transport, string databaseName = "ef.db")
        => new(new DbContextOptionsBuilder<ProductContext>()
            .UseBlazorSqlite(new BlazorSqliteConnection(transport, databaseName))
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options);

    public static ProductContext CreateStock()
        => new(new DbContextOptionsBuilder<ProductContext>()
            .UseSqlite("Data Source=:memory:")
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options);
}
