namespace BlazorSqlite.Data;

/// <summary>How much of the result the caller intends to consume.</summary>
public enum BlazorSqliteResultKind
{
    /// <summary>Row count only.</summary>
    NonQuery,

    /// <summary>First column of the first row.</summary>
    Scalar,

    /// <summary>Full result set.</summary>
    Reader,
}
