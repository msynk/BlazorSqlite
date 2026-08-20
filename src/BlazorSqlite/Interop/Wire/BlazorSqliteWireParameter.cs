using System.Text.Json.Serialization;

namespace BlazorSqlite.Interop;

/// <summary>One parameter on the wire, tagged with its SQLite storage class.</summary>
public sealed record BlazorSqliteWireParameter(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("type")] int Type,
    [property: JsonPropertyName("value")] object? Value);
