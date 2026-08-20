using System.Collections;
using System.Data.Common;

namespace BlazorSqlite.Data;

/// <summary>Parameter collection for <see cref="BlazorSqliteCommand"/>.</summary>
public sealed class BlazorSqliteParameterCollection : DbParameterCollection
{
    private readonly List<BlazorSqliteParameter> _parameters = [];

    public override int Count => _parameters.Count;

    public override object SyncRoot { get; } = new();

    public override int Add(object value)
    {
        _parameters.Add(Cast(value));
        return _parameters.Count - 1;
    }

    public override void AddRange(Array values)
    {
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

    public override int IndexOf(object value) => _parameters.IndexOf(Cast(value));

    public override int IndexOf(string parameterName)
        => _parameters.FindIndex(p => string.Equals(p.ParameterName, parameterName, StringComparison.Ordinal));

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

    private static BlazorSqliteParameter Cast(object value)
        => value as BlazorSqliteParameter
            ?? throw new InvalidCastException(
                $"Expected {nameof(BlazorSqliteParameter)} but received {value?.GetType().Name ?? "null"}.");
}
