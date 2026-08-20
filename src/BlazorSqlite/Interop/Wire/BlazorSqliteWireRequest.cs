using System.Text.Json.Serialization;

namespace BlazorSqlite.Interop;

/// <summary>One command on the wire.</summary>
public sealed record BlazorSqliteWireRequest(
    [property: JsonPropertyName("commandText")] string CommandText,
    [property: JsonPropertyName("resultKind")] string ResultKind,
    [property: JsonPropertyName("parameters")] BlazorSqliteWireParameter[] Parameters);
