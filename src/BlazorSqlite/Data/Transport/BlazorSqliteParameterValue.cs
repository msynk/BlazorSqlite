namespace BlazorSqlite.Data;

/// <summary>A parameter value in transport form.</summary>
public sealed record BlazorSqliteParameterValue(string Name, object? Value);
