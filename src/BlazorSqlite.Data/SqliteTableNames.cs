using System.Text.RegularExpressions;

namespace BlazorSqlite.Data;

/// <summary>
/// Pulls table names out of SQL so a live query can re-run when any of them change.
/// </summary>
/// <remarks>
/// <para>
/// Table-level and deliberately coarse: comments and quoted identifiers that contain SQL keywords
/// can produce extra names, which only causes extra re-queries, never a missed update. Schema
/// prefixes (<c>main.Products</c>) are skipped and every SQLite quoting style is accepted.
/// </para>
/// <para>
/// This is the fallback. When the transport reports what the engine actually wrote - the worker
/// does, through <c>sqlite3_update_hook</c> - the connection uses that instead, which also covers
/// triggers and cascading deletes that no regular expression can see.
/// </para>
/// </remarks>
public static partial class SqliteTableNames
{
    public static IReadOnlySet<string> Extract(string? sql)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(sql))
        {
            return names;
        }

        foreach (Match match in TableName().Matches(sql))
        {
            var name = match.Groups[1].Value;
            if (name.Length > 0 && !name.StartsWith("sqlite_", StringComparison.OrdinalIgnoreCase))
            {
                names.Add(name);
            }
        }

        return names;
    }

    /// <summary>
    /// Whether any statement in <paramref name="sql"/> could change data or schema. A CTE that
    /// begins with <c>WITH</c> counts, because it may wrap an <c>INSERT</c> or <c>DELETE</c>.
    /// </summary>
    public static bool LooksLikeWrite(string? sql)
        => !string.IsNullOrWhiteSpace(sql) && Write().IsMatch(sql);

    [GeneratedRegex(
        @"\b(?:from|join|into|update|table|view|trigger|index\s+(?:if\s+(?:not\s+)?exists\s+)?[\w""`\[\]]+\s+on)\s+(?:if\s+(?:not\s+)?exists\s+)?(?:(?:temp|temporary)\s+)?(?:[""`\[]?[A-Za-z_]\w*[""`\]]?\s*\.\s*)?[""`\[]?([A-Za-z_][\w]*)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TableName();

    [GeneratedRegex(
        @"(?:^|;)\s*(?:--[^\n]*\n\s*|/\*.*?\*/\s*)*(?:insert|update|delete|replace|create|drop|alter|with|vacuum|reindex)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex Write();
}
