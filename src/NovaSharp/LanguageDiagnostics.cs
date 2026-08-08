namespace NovaSharp;

internal sealed class LanguageDiagnosticStore
{
    private static readonly StringComparer Paths = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private readonly object _gate = new();
    private readonly Dictionary<(string Path, LanguageDiagnosticSource Source),
        (long Version, IReadOnlyList<LanguageDiagnostic> Entries)> _entries = new();

    internal event Action? Changed;

    internal IReadOnlyList<LanguageDiagnostic> Entries
    {
        get
        {
            lock (_gate) return _entries.Values.SelectMany(value => value.Entries)
                .OrderByDescending(item => item.Severity).ThenBy(item => item.DocumentPath, Paths)
                .ThenBy(item => item.Range.Start).ToArray();
        }
    }

    internal bool Replace(string documentPath, long version, LanguageDiagnosticSource source,
        IReadOnlyList<LanguageDiagnostic> diagnostics)
    {
        var key = (Path.GetFullPath(documentPath), source);
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var current) && current.Version > version) return false;
            _entries[key] = (version, diagnostics);
        }
        Changed?.Invoke();
        return true;
    }

    internal void Remove(string documentPath)
    {
        var path = Path.GetFullPath(documentPath);
        lock (_gate)
            foreach (var key in _entries.Keys.Where(key => Paths.Equals(key.Path, path)).ToArray())
                _entries.Remove(key);
        Changed?.Invoke();
    }

    internal void Clear()
    {
        lock (_gate) _entries.Clear();
        Changed?.Invoke();
    }
}
