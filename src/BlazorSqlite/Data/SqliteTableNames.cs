using System.Text.RegularExpressions;

namespace BlazorSqlite.Data;

/// <summary>
/// Pulls table names out of SQL so a live query can re-run when any of them change.
/// </summary>
/// <remarks>
/// <para>
/// Table-level and deliberately coarse: comments and quoted identifiers that contain SQL keywords
/// can produce extra names, which only causes extra re-queries, never a missed update.
/// </para>
/// <para>
/// The worker carries the same two patterns in JavaScript (<c>blazor-sqlite-worker.js</c>) for
/// the tables its update hook cannot see - DDL, and the truncate-optimised <c>DELETE FROM t</c> -
/// so a change here is a change there.
/// </para>
/// </remarks>
public static partial class SqliteTableNames
{
    /// <summary>
    /// The tables <paramref name="sql"/> reads or writes. Accepts the identifier quoting SQLite
    /// does (<c>"t"</c>, <c>`t`</c>, <c>[t]</c>), a schema prefix (<c>main."t"</c>), and the
    /// conflict clause an <c>UPDATE</c> may carry (<c>UPDATE OR REPLACE t</c>).
    /// </summary>
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
            if (!name.StartsWith("sqlite_", StringComparison.OrdinalIgnoreCase))
            {
                names.Add(name);
            }
        }

        return names;
    }

    /// <summary>
    /// Whether <paramref name="sql"/> contains a statement that writes.
    /// </summary>
    /// <remarks>
    /// Every statement is considered, not just the first. A batch is routinely
    /// <c>BEGIN; INSERT …; COMMIT;</c> or several statements EF sent together, and looking only at
    /// the leading keyword would call all of those reads - which is a missed live-query update, the
    /// one failure mode this heuristic is not allowed to have. A leading common table expression
    /// is skipped for the same reason: <c>WITH … INSERT INTO</c> is a write.
    /// </remarks>
    public static bool LooksLikeWrite(string? sql)
        => !string.IsNullOrWhiteSpace(sql) && Write().IsMatch(sql);

    [GeneratedRegex(
        @"\b(?:from|join|into|update(?:\s+or\s+(?:rollback|abort|replace|fail|ignore))?|table)\s+"
        + @"(?:if\s+(?:not\s+)?exists\s+)?"
        + @"(?:[""`\[]?[A-Za-z_]\w*[""`\]]?\s*\.\s*)?"
        + @"[""`\[]?([A-Za-z_]\w*)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TableName();

    [GeneratedRegex(
        @"(?:^|;)\s*(?:with\b[\s\S]*?)?\b(?:insert|update|delete|replace|create|drop|alter)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Write();
}
