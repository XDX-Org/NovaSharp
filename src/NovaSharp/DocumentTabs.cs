using System.Text.Json;

namespace NovaSharp;

internal sealed class DocumentRegistry : IDisposable
{
    private readonly Dictionary<string, RegistryEntry> _documents = new(PathComparer);
    private readonly object _gate = new();
    internal string? LastError { get; private set; }
    internal int DocumentCount { get { lock (_gate) return _documents.Count; } }

    internal async Task<EditorDocumentState?> AcquireAsync(string path, bool restoreMissing = false)
    {
        var canonicalPath = Path.GetFullPath(path);
        LastError = null;
        lock (_gate)
        {
            if (_documents.TryGetValue(canonicalPath, out var existing))
            {
                existing.ReferenceCount++;
                return existing.Document;
            }
        }

        var document = new EditorDocumentState();
        if (restoreMissing && !File.Exists(canonicalPath)) document.OpenMissing(canonicalPath);
        else await document.OpenAsync(canonicalPath);
        if (document.FilePath is null)
        {
            LastError = document.Error;
            document.Dispose();
            return null;
        }
        lock (_gate)
        {
            if (_documents.TryGetValue(canonicalPath, out var existing))
            {
                existing.ReferenceCount++;
                document.Dispose();
                return existing.Document;
            }
            _documents.Add(canonicalPath, new(document));
            return document;
        }
    }

    internal void Release(EditorDocumentState document)
    {
        lock (_gate)
        {
            var pair = _documents.FirstOrDefault(candidate => ReferenceEquals(candidate.Value.Document, document));
            if (pair.Value is null) throw new ArgumentException("The document is not owned by this registry.", nameof(document));
            if (--pair.Value.ReferenceCount > 0) return;
            _documents.Remove(pair.Key);
        }
        document.Dispose();
    }

    public void Dispose()
    {
        RegistryEntry[] entries;
        lock (_gate)
        {
            entries = _documents.Values.ToArray();
            _documents.Clear();
        }
        foreach (var entry in entries) entry.Document.Dispose();
    }

    private sealed class RegistryEntry(EditorDocumentState document)
    {
        internal EditorDocumentState Document { get; } = document;
        internal int ReferenceCount { get; set; } = 1;
    }

    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;
}

public sealed class DocumentTab(EditorDocumentState document, bool preview, Guid? id = null)
{
    internal Guid Id { get; } = id ?? Guid.NewGuid();
    internal EditorDocumentState Document { get; } = document;
    internal EditorViewState ViewState { get; } = new();
    internal bool IsPreview { get; private set; } = preview;
    internal bool IsPinned { get; private set; } = !preview;

    internal void Promote()
    {
        IsPreview = false;
        IsPinned = true;
    }

    internal void SetPreview(bool preview)
    {
        IsPreview = preview;
        IsPinned = !preview;
    }
}

internal sealed class DocumentTabService : IDisposable
{
    private readonly List<DocumentTab> _tabs = [];
    private readonly DocumentRegistry _registry;
    private readonly bool _ownsRegistry;

    internal DocumentTabService(DocumentRegistry? registry = null)
    {
        _registry = registry ?? new();
        _ownsRegistry = registry is null;
    }

    internal IReadOnlyList<DocumentTab> Tabs => _tabs;
    internal DocumentTab? ActiveTab { get; private set; }
    internal string? LastError { get; private set; }

    internal async Task<DocumentTab?> OpenAsync(string path, bool preview = false)
    {
        var canonicalPath = Path.GetFullPath(path);
        LastError = null;
        var existing = _tabs.FirstOrDefault(tab =>
            tab.Document.FilePath is not null && PathComparer.Equals(tab.Document.FilePath, canonicalPath));
        if (existing is not null)
        {
            ActiveTab = existing;
            if (!preview) existing.Promote();
            return existing;
        }

        if (preview)
        {
            var reusable = _tabs.FirstOrDefault(tab => tab.IsPreview && !tab.Document.IsDirty);
            if (reusable is not null) Close(reusable, discardDirty: true);
        }

        var document = await _registry.AcquireAsync(canonicalPath);
        if (document is null)
        {
            LastError = _registry.LastError;
            return null;
        }

        var tab = new DocumentTab(document, preview);
        _tabs.Add(tab);
        ActiveTab = tab;
        return tab;
    }

    internal async Task<DocumentTab> RestoreAsync(SessionTabState state)
    {
        var canonicalPath = Path.GetFullPath(state.Path);
        var existing = _tabs.FirstOrDefault(tab => PathComparer.Equals(tab.Document.FilePath, canonicalPath));
        if (existing is not null) return existing;
        var document = (await _registry.AcquireAsync(canonicalPath, restoreMissing: true))!;
        var tab = new DocumentTab(document, state.IsPreview);
        if (!state.IsPreview) tab.Promote();
        tab.ViewState.Restore(state.SelectionStart, state.SelectionEnd, state.ScrollTop, state.ScrollLeft,
            document.Content?.Length ?? 0);
        _tabs.Add(tab);
        return tab;
    }

    internal void SetActive(DocumentTab? tab)
    {
        if (tab is not null) EnsureOwned(tab);
        ActiveTab = tab;
    }

    internal void Activate(DocumentTab tab)
    {
        EnsureOwned(tab);
        ActiveTab = tab;
    }

    internal void Promote(DocumentTab tab)
    {
        EnsureOwned(tab);
        tab.Promote();
    }

    internal bool Close(DocumentTab tab, bool discardDirty = false)
    {
        var index = _tabs.IndexOf(tab);
        if (index < 0) return false;
        if (tab.Document.IsDirty && !discardDirty) return false;

        _tabs.RemoveAt(index);
        if (ReferenceEquals(ActiveTab, tab))
            ActiveTab = _tabs.Count == 0 ? null : _tabs[Math.Min(index, _tabs.Count - 1)];
        ReleaseDocument(tab.Document);
        return true;
    }

    internal void Move(DocumentTab tab, int destinationIndex)
    {
        var sourceIndex = _tabs.IndexOf(tab);
        if (sourceIndex < 0) throw new ArgumentException("The tab is not owned by this service.", nameof(tab));
        destinationIndex = Math.Clamp(destinationIndex, 0, _tabs.Count - 1);
        if (sourceIndex == destinationIndex) return;
        _tabs.RemoveAt(sourceIndex);
        _tabs.Insert(destinationIndex, tab);
    }

    internal string GetDisplayName(DocumentTab tab)
    {
        EnsureOwned(tab);
        var name = tab.Document.DisplayName;
        var duplicates = _tabs.Where(candidate =>
            string.Equals(candidate.Document.DisplayName, name, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (duplicates.Length == 1) return name;
        var path = tab.Document.FilePath!;
        var parent = Path.GetFileName(Path.GetDirectoryName(path));
        return string.IsNullOrEmpty(parent) ? path : $"{name} — {parent}";
    }

    private void ReleaseDocument(EditorDocumentState document)
    {
        if (_tabs.Any(tab => ReferenceEquals(tab.Document, document))) return;
        _registry.Release(document);
    }

    private void EnsureOwned(DocumentTab tab)
    {
        if (!_tabs.Contains(tab)) throw new ArgumentException("The tab is not owned by this service.", nameof(tab));
    }

    public void Dispose()
    {
        foreach (var document in _tabs.Select(tab => tab.Document).Distinct()) _registry.Release(document);
        _tabs.Clear();
        ActiveTab = null;
        if (_ownsRegistry) _registry.Dispose();
    }

    private static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}

internal sealed record SessionTabState(string Path, bool IsPreview = false, int SelectionStart = 0,
    int SelectionEnd = 0, double ScrollTop = 0, double ScrollLeft = 0);

internal sealed record WorkbenchSessionState(int SchemaVersion = 1, string? ActivePath = null,
    SessionTabState[]? Tabs = null);

internal sealed class WorkbenchSessionPersistence(string path)
{
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    internal async Task<WorkbenchSessionState> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path)) return new();
        try
        {
            await using var stream = File.OpenRead(path);
            var state = await JsonSerializer.DeserializeAsync<WorkbenchSessionState>(stream,
                cancellationToken: cancellationToken);
            if (state is null || state.SchemaVersion != 1) return new();
            var valid = (state.Tabs ?? []).Where(tab => !string.IsNullOrWhiteSpace(tab.Path)
                && Path.IsPathFullyQualified(tab.Path)).ToArray();
            return state with { Tabs = valid };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }

    internal async Task SaveAsync(WorkbenchSessionState state, CancellationToken cancellationToken = default)
    {
        await _saveGate.WaitAsync(cancellationToken);
        try { await AtomicFile.WriteAsync(path, JsonSerializer.SerializeToUtf8Bytes(state), cancellationToken); }
        finally { _saveGate.Release(); }
    }
}

internal sealed class EditorLayoutPersistence(string path)
{
    private static readonly Guid FallbackGroupId = Guid.Parse("4d711a6b-ff91-4931-a4ec-40f72c00eb52");
    private readonly SemaphoreSlim _saveGate = new(1, 1);

    internal async Task<WorkbenchLayoutState> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path)) return Empty();
        try
        {
            var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
            using var json = JsonDocument.Parse(bytes);
            var version = json.RootElement.TryGetProperty("SchemaVersion", out var property) ? property.GetInt32() : 0;
            if (version == 2)
                return JsonSerializer.Deserialize<WorkbenchLayoutState>(bytes) ?? Empty();
            if (version == 1)
            {
                var legacy = JsonSerializer.Deserialize<WorkbenchSessionState>(bytes) ?? new();
                var groupId = Guid.NewGuid();
                var views = (legacy.Tabs ?? []).Where(tab => !string.IsNullOrWhiteSpace(tab.Path)
                    && Path.IsPathFullyQualified(tab.Path)).Select(tab => new SessionViewState(Guid.NewGuid(),
                    tab.Path, tab.IsPreview, tab.SelectionStart, tab.SelectionEnd, tab.ScrollTop, tab.ScrollLeft)).ToArray();
                var active = views.FirstOrDefault(view => PathEquals(view.Path, legacy.ActivePath))?.Id;
                return new(2, new("group", groupId, Tabs: views, ActiveViewId: active), groupId);
            }
            return Empty();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
            or InvalidOperationException)
        { return Empty(); }
    }

    internal async Task SaveAsync(WorkbenchLayoutState state, CancellationToken cancellationToken = default)
    {
        await _saveGate.WaitAsync(cancellationToken);
        try { await AtomicFile.WriteAsync(path, JsonSerializer.SerializeToUtf8Bytes(state), cancellationToken); }
        finally { _saveGate.Release(); }
    }

    private static WorkbenchLayoutState Empty()
    {
        return new(2, new("group", FallbackGroupId), FallbackGroupId);
    }
    private static bool PathEquals(string left, string? right) => right is not null && string.Equals(
        Path.GetFullPath(left), Path.GetFullPath(right), OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
