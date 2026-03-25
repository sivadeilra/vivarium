using System.Collections.Concurrent;

namespace Vivarium;

/// <summary>
/// Thread-safe bridge for injecting .NET objects into the Roslyn scripting session.
/// Tools place objects here, then eval code that retrieves them by key.
/// </summary>
public static class DataBridge
{
    private static readonly ConcurrentDictionary<string, object> _slots = new();
    private static int _counter;

    /// <summary>
    /// Store an object and return a unique key for retrieval.
    /// </summary>
    public static string Put(object value)
    {
        var key = $"__bridge_{Interlocked.Increment(ref _counter)}";
        _slots[key] = value;
        return key;
    }

    /// <summary>
    /// Retrieve and remove an object by key. Called from within the scripting session.
    /// </summary>
    public static T Take<T>(string key)
    {
        if (_slots.TryRemove(key, out var value))
            return (T)value;
        throw new KeyNotFoundException($"DataBridge key '{key}' not found or already consumed.");
    }
}
