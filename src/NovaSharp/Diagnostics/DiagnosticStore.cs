namespace NovaSharp.Diagnostics;

public enum DiagnosticSeverity
{
    Information,
    Warning,
    Error,
}

public sealed record WorkbenchDiagnostic(
    string Producer,
    string Context,
    long SourceVersion,
    string Identity,
    DiagnosticSeverity Severity,
    string Message,
    string? DocumentUri = null,
    int? StartLine = null,
    int? StartColumn = null);

public sealed record DiagnosticStoreSnapshot(
    IReadOnlyList<WorkbenchDiagnostic> Diagnostics,
    long Version,
    int DroppedCount);

/// <summary>Bounded, identity-keyed diagnostics shared by project loading and later language providers.</summary>
public sealed class DiagnosticStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<(string Producer, string Context, string Identity), WorkbenchDiagnostic> _diagnostics = [];
    private long _version;
    private int _dropped;

    public DiagnosticStore(int capacity = 5_000)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        Capacity = capacity;
    }

    public int Capacity { get; }

    public event Action<DiagnosticStoreSnapshot>? Changed;

    public DiagnosticStoreSnapshot Snapshot
    {
        get
        {
            lock (_gate)
            {
                return BuildSnapshot();
            }
        }
    }

    public void Replace(string producer, string context, long sourceVersion, IEnumerable<WorkbenchDiagnostic> diagnostics)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(producer);
        ArgumentException.ThrowIfNullOrWhiteSpace(context);
        ArgumentNullException.ThrowIfNull(diagnostics);

        DiagnosticStoreSnapshot snapshot;
        lock (_gate)
        {
            foreach (var key in _diagnostics.Keys
                         .Where(key => key.Producer == producer && key.Context == context)
                         .ToArray())
            {
                _diagnostics.Remove(key);
            }

            foreach (var diagnostic in diagnostics)
            {
                if (diagnostic.Producer != producer || diagnostic.Context != context || diagnostic.SourceVersion != sourceVersion)
                {
                    throw new ArgumentException("Diagnostic producer, context, and version must match the replacement scope.", nameof(diagnostics));
                }

                _diagnostics[(producer, context, diagnostic.Identity)] = diagnostic;
            }

            while (_diagnostics.Count > Capacity)
            {
                var oldest = _diagnostics.MinBy(static pair => pair.Value.SourceVersion);
                _diagnostics.Remove(oldest.Key);
                _dropped++;
            }

            _version++;
            snapshot = BuildSnapshot();
        }

        Changed?.Invoke(snapshot);
    }

    public void Clear(string producer, string context)
    {
        Replace(producer, context, 0, []);
    }

    private DiagnosticStoreSnapshot BuildSnapshot()
    {
        return new(
        _diagnostics.Values
            .OrderByDescending(static diagnostic => diagnostic.Severity)
            .ThenBy(static diagnostic => diagnostic.Context, StringComparer.Ordinal)
            .ThenBy(static diagnostic => diagnostic.Identity, StringComparer.Ordinal)
            .ToArray(),
        _version,
        _dropped);
    }
}
