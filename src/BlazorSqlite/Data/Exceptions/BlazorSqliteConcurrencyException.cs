namespace BlazorSqlite.Data;

/// <summary>The database is locked or busy (<c>SQLITE_BUSY</c> / <c>SQLITE_LOCKED</c>).</summary>
public sealed class BlazorSqliteConcurrencyException : BlazorSqliteException
{
    public BlazorSqliteConcurrencyException(string message, int? sqliteErrorCode = 5)
        : base(message, sqliteErrorCode)
    {
    }
}
