namespace BlazorSqlite.Data;

/// <summary>A single statement plus its parameters.</summary>
public sealed record BlazorSqliteCommandRequest
{
    /// <summary>The SQL to execute. May contain multiple statements.</summary>
    public required string CommandText { get; init; }

    public IReadOnlyList<BlazorSqliteParameterValue> Parameters { get; init; } = [];

    public BlazorSqliteResultKind ResultKind { get; init; } = BlazorSqliteResultKind.NonQuery;
}
