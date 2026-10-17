using System.Collections;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;

namespace BlazorSqlite.Data;

/// <summary>A parameter carried to the engine by the transport.</summary>
public sealed class BlazorSqliteParameter : DbParameter
{
    public BlazorSqliteParameter()
    {
    }

    public BlazorSqliteParameter(string name, object? value)
    {
        ParameterName = name;
        Value = value;
    }

    public override DbType DbType { get; set; } = DbType.Object;

    public override ParameterDirection Direction
    {
        get => ParameterDirection.Input;
        set
        {
            if (value != ParameterDirection.Input)
            {
                throw new ArgumentException("SQLite parameters are input-only.", nameof(value));
            }
        }
    }

    public override bool IsNullable { get; set; } = true;

    [AllowNull]
    public override string ParameterName { get; set; } = string.Empty;

    public override int Size { get; set; }

    [AllowNull]
    public override string SourceColumn { get; set; } = string.Empty;

    public override bool SourceColumnNullMapping { get; set; }

    public override object? Value { get; set; }

    public override void ResetDbType() => DbType = DbType.Object;
}

/// <summary>Parameter collection for <see cref="BlazorSqliteCommand"/>.</summary>
/// <remarks>
/// Names match with or without their sigil, because callers write <c>p0</c> as often as
/// <c>@p0</c> and SQLite accepts <c>@</c>, <c>$</c>, and <c>:</c> alike.
/// </remarks>
public sealed class BlazorSqliteParameterCollection : DbParameterCollection, IEnumerable<BlazorSqliteParameter>
{
    private readonly List<BlazorSqliteParameter> _parameters = [];

    public override int Count => _parameters.Count;

    public override object SyncRoot { get; } = new();

    public new BlazorSqliteParameter this[int index]
    {
        get => _parameters[index];
        set => _parameters[index] = value;
    }

    public new BlazorSqliteParameter this[string parameterName]
    {
        get => _parameters[RequireIndex(parameterName)];
        set => _parameters[RequireIndex(parameterName)] = value;
    }

    /// <summary>Adds a parameter by name and value and returns it.</summary>
    public BlazorSqliteParameter Add(string parameterName, object? value)
    {
        var parameter = new BlazorSqliteParameter(parameterName, value);
        _parameters.Add(parameter);
        return parameter;
    }

    /// <inheritdoc cref="Add(string, object?)"/>
    public BlazorSqliteParameter AddWithValue(string parameterName, object? value) => Add(parameterName, value);

    public override int Add(object value)
    {
        _parameters.Add(Cast(value));
        return _parameters.Count - 1;
    }

    public override void AddRange(Array values)
    {
        ArgumentNullException.ThrowIfNull(values);
        foreach (var value in values)
        {
            Add(value!);
        }
    }

    public override void Clear() => _parameters.Clear();

    public override bool Contains(object value) => _parameters.Contains(Cast(value));

    public override bool Contains(string value) => IndexOf(value) >= 0;

    public override void CopyTo(Array array, int index) => ((ICollection)_parameters).CopyTo(array, index);

    public override IEnumerator GetEnumerator() => _parameters.GetEnumerator();

    IEnumerator<BlazorSqliteParameter> IEnumerable<BlazorSqliteParameter>.GetEnumerator() => _parameters.GetEnumerator();

    public override int IndexOf(object value) => _parameters.IndexOf(Cast(value));

    public override int IndexOf(string parameterName)
    {
        if (parameterName is null)
        {
            return -1;
        }

        var index = _parameters.FindIndex(p => string.Equals(p.ParameterName, parameterName, StringComparison.Ordinal));
        if (index >= 0)
        {
            return index;
        }

        var bare = StripSigil(parameterName);
        return _parameters.FindIndex(p => string.Equals(StripSigil(p.ParameterName), bare, StringComparison.Ordinal));
    }

    public override void Insert(int index, object value) => _parameters.Insert(index, Cast(value));

    public override void Remove(object value) => _parameters.Remove(Cast(value));

    public override void RemoveAt(int index) => _parameters.RemoveAt(index);

    public override void RemoveAt(string parameterName) => RemoveAt(RequireIndex(parameterName));

    protected override DbParameter GetParameter(int index) => _parameters[index];

    protected override DbParameter GetParameter(string parameterName) => _parameters[RequireIndex(parameterName)];

    protected override void SetParameter(int index, DbParameter value) => _parameters[index] = Cast(value);

    protected override void SetParameter(string parameterName, DbParameter value)
        => _parameters[RequireIndex(parameterName)] = Cast(value);

    private int RequireIndex(string parameterName)
    {
        var index = IndexOf(parameterName);
        return index >= 0
            ? index
            : throw new IndexOutOfRangeException($"No parameter named '{parameterName}'.");
    }

    private static string StripSigil(string name)
        => name.Length > 0 && name[0] is '@' or '$' or ':' ? name[1..] : name;

    private static BlazorSqliteParameter Cast(object value)
        => value as BlazorSqliteParameter
            ?? throw new InvalidCastException(
                $"Expected {nameof(BlazorSqliteParameter)} but received {value?.GetType().Name ?? "null"}.");
}
