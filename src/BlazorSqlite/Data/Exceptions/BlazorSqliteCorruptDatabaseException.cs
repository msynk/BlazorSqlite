namespace BlazorSqlite.Data;

/// <summary>The file image is not a usable SQLite database.</summary>
public sealed class BlazorSqliteCorruptDatabaseException : BlazorSqliteException
{
    public BlazorSqliteCorruptDatabaseException(string message, int? sqliteErrorCode = 11)
        : base(message, sqliteErrorCode)
    {
    }
}
