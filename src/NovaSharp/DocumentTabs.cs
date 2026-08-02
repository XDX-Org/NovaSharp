namespace NovaSharp;

internal sealed class DocumentTab(EditorDocumentState document, bool preview)
{
    internal Guid Id { get; } = Guid.NewGuid();
    internal EditorDocumentState Document { get; } = document;
    internal EditorViewState ViewState { get; } = new();
    internal bool IsPreview { get; private set; } = preview;
    internal bool IsPinned { get; private set; } = !preview;

    internal void Promote()
    {
        IsPreview = false;
        IsPinned = true;
    }
}

internal sealed class DocumentTabService : IDisposable
{
    private readonly List<DocumentTab> _tabs = [];
    private readonly Dictionary<string, EditorDocumentState> _documents = new(PathComparer);

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

        var document = new EditorDocumentState();
        await document.OpenAsync(canonicalPath);
        if (document.FilePath is null)
        {
            LastError = document.Error;
            document.Dispose();
            return null;
        }

        _documents.Add(canonicalPath, document);
        var tab = new DocumentTab(document, preview);
        _tabs.Add(tab);
        ActiveTab = tab;
        return tab;
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
        if (document.FilePath is not null) _documents.Remove(document.FilePath);
        document.Dispose();
    }

    private void EnsureOwned(DocumentTab tab)
    {
        if (!_tabs.Contains(tab)) throw new ArgumentException("The tab is not owned by this service.", nameof(tab));
    }

    public void Dispose()
    {
        foreach (var document in _documents.Values) document.Dispose();
        _documents.Clear();
        _tabs.Clear();
        ActiveTab = null;
    }

    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;
}
