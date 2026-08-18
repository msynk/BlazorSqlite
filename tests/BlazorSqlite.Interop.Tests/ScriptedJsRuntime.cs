using System.Text.Json;
using Microsoft.JSInterop;

namespace BlazorSqlite.Interop.Tests;

/// <summary>
/// A scripted <see cref="IJSRuntime"/> that plays back envelopes the way the real host's
/// <c>call</c> method would, so the transport can be tested without a browser.
/// </summary>
internal sealed class ScriptedJsRuntime : IJSRuntime
{
    private readonly Queue<JsonElement> _envelopes = new();

    public List<JsCall> Calls { get; } = [];

    public ScriptedJsObject Module { get; }

    public ScriptedJsObject Host { get; }

    public bool HostDisposed { get; private set; }

    public ScriptedJsRuntime()
    {
        Module = new ScriptedJsObject(this, "module");
        Host = new ScriptedJsObject(this, "host");
    }

    public void EnqueueEnvelope(string json)
    {
        using var document = JsonDocument.Parse(json);
        _envelopes.Enqueue(document.RootElement.Clone());
    }

    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
        => InvokeAsync<TValue>(identifier, CancellationToken.None, args);

    public ValueTask<TValue> InvokeAsync<TValue>(
        string identifier,
        CancellationToken cancellationToken,
        object?[]? args)
    {
        Calls.Add(new JsCall(identifier, args ?? []));

        object result = identifier switch
        {
            "import" => Module,
            _ => throw new InvalidOperationException($"Unexpected runtime call '{identifier}'."),
        };

        return ValueTask.FromResult((TValue)result);
    }

    internal ValueTask<TValue> InvokeObjectAsync<TValue>(
        string objectName,
        string identifier,
        object?[]? args)
    {
        Calls.Add(new JsCall($"{objectName}.{identifier}", args ?? []));

        if (objectName == "module" && identifier == "createHost")
        {
            return ValueTask.FromResult((TValue)(object)Host);
        }

        // Subscribes the transport to another tab's writes. Nothing to play back - the recorded
        // call in Calls is what the tests assert on.
        if (objectName == "module" && identifier == "listen")
        {
            return ValueTask.FromResult(default(TValue)!);
        }

        if (objectName == "host" && identifier == "call")
        {
            if (_envelopes.Count == 0)
            {
                throw new InvalidOperationException("The test did not enqueue an envelope for this call.");
            }

            return ValueTask.FromResult((TValue)(object)_envelopes.Dequeue());
        }

        if (objectName == "host" && identifier == "dispose")
        {
            HostDisposed = true;
            return ValueTask.FromResult(default(TValue)!);
        }

        throw new InvalidOperationException($"Unexpected call '{objectName}.{identifier}'.");
    }
}

internal sealed class ScriptedJsObject(ScriptedJsRuntime runtime, string name) : IJSObjectReference
{
    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
        => runtime.InvokeObjectAsync<TValue>(name, identifier, args);

    public ValueTask<TValue> InvokeAsync<TValue>(
        string identifier,
        CancellationToken cancellationToken,
        object?[]? args)
        => runtime.InvokeObjectAsync<TValue>(name, identifier, args);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed record JsCall(string Identifier, object?[] Args);
