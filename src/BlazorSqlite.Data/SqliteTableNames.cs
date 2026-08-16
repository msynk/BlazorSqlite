using System.Text.RegularExpressions;

namespace BlazorSqlite.Data;

/// <summary>
/// Pulls table names out of SQL so a live query can re-run when any of them change.
/// </summary>
/// <remarks>
/// Table-level and deliberately coarse: comments and quoted identifiers that contain SQL keywords
/// can produce extra names, which only causes extra re-queries, never a missed update.
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
            var name = match.Groups[1].Value.Trim('"');
            if (name.Length > 0 && !name.StartsWith("sqlite_", StringComparison.OrdinalIgnoreCase))
            {
                names.Add(name);
            }
        }

        return names;
    }

    public static bool LooksLikeWrite(string? sql)
        => !string.IsNullOrWhiteSpace(sql) && Write().IsMatch(sql);

    [GeneratedRegex(
        @"\b(?:from|join|into|update|table)\s+(?:if\s+(?:not\s+)?exists\s+)?[""`]?([A-Za-z_][\w]*)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TableName();

    [GeneratedRegex(
        @"^\s*(?:insert|update|delete|replace|create|drop|alter)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Write();
}
