namespace BlazorSqlite.Data;

/// <summary>The origin is out of storage quota, or SQLite reported <c>SQLITE_FULL</c>.</summary>
public sealed class BlazorSqliteQuotaExceededException : BlazorSqliteException
{
    public BlazorSqliteQuotaExceededException(string message, int? sqliteErrorCode = 13)
        : base(message, sqliteErrorCode)
    {
    }
}
