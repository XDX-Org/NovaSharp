namespace NovaSharp.LanguageServers;

internal interface ILspDocumentSink
{
    bool IsReady { get; }
    Task NotifyAsync(string method, object parameters, CancellationToken cancellationToken = default);
}

internal sealed class LanguageDocumentCoordinator : IAsyncDisposable
{
    private sealed class Entry(EditorDocumentState document, Uri uri, string languageId)
    {
        internal EditorDocumentState Document { get; } = document;
        internal Uri Uri { get; set; } = uri;
        internal string LanguageId { get; } = languageId;
        internal long Version { get; set; } = 1;
        internal int Views { get; set; } = 1;
        internal bool Open { get; set; }
        internal string Text { get; set; } = document.Content ?? string.Empty;
        internal Action<EditorSnapshot>? Handler { get; set; }
        internal SemaphoreSlim SendLock { get; } = new(1, 1);
        internal Task Pending { get; set; } = Task.CompletedTask;
    }

    private readonly ILspDocumentSink _sink;
    private readonly Dictionary<EditorDocumentState, Entry> _entries = [];
    private readonly object _gate = new();

    internal LanguageDocumentCoordinator(ILspDocumentSink sink) => _sink = sink;

    internal async Task OpenAsync(EditorDocumentState document, CancellationToken cancellationToken = default)
    {
        if (document.FilePath is null) return;
        Entry entry;
        lock (_gate)
        {
            if (_entries.TryGetValue(document, out entry!)) { entry.Views++; return; }
            entry = new(document, LspConverters.FileUri(document.FilePath), LanguageId(document.FilePath));
            entry.Handler = snapshot => QueueChange(entry, snapshot);
            document.ContentChanged += entry.Handler;
            _entries.Add(document, entry);
        }
        if (_sink.IsReady) await SendOpenAsync(entry, cancellationToken);
    }

    internal async Task SavedAsync(EditorDocumentState document, CancellationToken cancellationToken = default)
    {
        Entry? entry;
        lock (_gate) _entries.TryGetValue(document, out entry);
        if (entry is null || !_sink.IsReady) return;
        await FlushAsync(entry, cancellationToken);
        await _sink.NotifyAsync("textDocument/didSave",
            new LspDidSaveTextDocumentParams(new(entry.Uri.AbsoluteUri)), cancellationToken);
    }

    internal async Task SynchronizeAsync(string path, CancellationToken cancellationToken = default)
    {
        Entry? entry;
        var uri = LspConverters.FileUri(path);
        lock (_gate) entry = _entries.Values.FirstOrDefault(item => item.Uri == uri);
        if (entry is null) return;
        await FlushAsync(entry, cancellationToken);
        if (!entry.Open && _sink.IsReady) await SendOpenAsync(entry, cancellationToken);
    }

    internal async Task CloseAsync(EditorDocumentState document, CancellationToken cancellationToken = default)
    {
        Entry? entry;
        lock (_gate)
        {
            if (!_entries.TryGetValue(document, out entry) || --entry.Views > 0) return;
            _entries.Remove(document);
            document.ContentChanged -= entry.Handler;
        }
        await FlushAsync(entry, cancellationToken);
        if (entry.Open && _sink.IsReady)
            await _sink.NotifyAsync("textDocument/didClose",
                new LspDidCloseTextDocumentParams(new(entry.Uri.AbsoluteUri)), cancellationToken);
        entry.SendLock.Dispose();
    }

    internal async Task ReplayAsync(CancellationToken cancellationToken = default)
    {
        Entry[] entries;
        lock (_gate) entries = _entries.Values.ToArray();
        foreach (var entry in entries)
        {
            entry.Open = false;
            await SendOpenAsync(entry, cancellationToken);
        }
    }

    private async Task ChangeAsync(Entry entry, EditorSnapshot snapshot)
    {
        if (!_sink.IsReady) { entry.Text = snapshot.Text; entry.Version++; return; }
        await entry.SendLock.WaitAsync();
        try
        {
            var previous = entry.Text;
            var change = IncrementalChange(previous, snapshot.Text);
            entry.Text = snapshot.Text;
            entry.Version++;
            if (!entry.Open) await SendOpenCoreAsync(entry, default);
            else await _sink.NotifyAsync("textDocument/didChange", new LspDidChangeTextDocumentParams(
                new(entry.Uri.AbsoluteUri, entry.Version), [change]));
        }
        finally { entry.SendLock.Release(); }
    }

    private void QueueChange(Entry entry, EditorSnapshot snapshot)
    {
        lock (entry)
            entry.Pending = entry.Pending.ContinueWith(_ => ChangeAsync(entry, snapshot), CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default).Unwrap();
    }

    private async Task SendOpenAsync(Entry entry, CancellationToken cancellationToken)
    {
        await entry.SendLock.WaitAsync(cancellationToken);
        try { if (!entry.Open) await SendOpenCoreAsync(entry, cancellationToken); }
        finally { entry.SendLock.Release(); }
    }

    private async Task SendOpenCoreAsync(Entry entry, CancellationToken cancellationToken)
    {
        entry.Text = entry.Document.Content ?? string.Empty;
        await _sink.NotifyAsync("textDocument/didOpen", new LspDidOpenTextDocumentParams(
            new(entry.Uri.AbsoluteUri, entry.LanguageId, entry.Version, entry.Text)), cancellationToken);
        entry.Open = true;
    }

    private static async Task FlushAsync(Entry entry, CancellationToken cancellationToken)
    {
        Task pending;
        lock (entry) pending = entry.Pending;
        await pending.WaitAsync(cancellationToken);
        await entry.SendLock.WaitAsync(cancellationToken);
        entry.SendLock.Release();
    }

    private static LspTextDocumentContentChangeEvent IncrementalChange(string before, string after)
    {
        var prefix = 0;
        var shared = Math.Min(before.Length, after.Length);
        while (prefix < shared && before[prefix] == after[prefix]) prefix++;
        if (prefix > 0 && prefix < before.Length && (char.IsSurrogatePair(before[prefix - 1], before[prefix])
            || before[prefix - 1] == '\r' && before[prefix] == '\n')) prefix--;
        var suffix = 0;
        while (suffix < shared - prefix && before[before.Length - suffix - 1] == after[after.Length - suffix - 1]) suffix++;
        var suffixStart = before.Length - suffix;
        if (suffix > 0 && suffixStart > prefix && (char.IsSurrogatePair(before[suffixStart - 1], before[suffixStart])
            || before[suffixStart - 1] == '\r' && before[suffixStart] == '\n')) suffix--;
        var removed = before.Length - prefix - suffix;
        var inserted = after.Substring(prefix, after.Length - prefix - suffix);
        return new(new(LspConverters.ToPosition(before, prefix), LspConverters.ToPosition(before, prefix + removed)),
            removed, inserted);
    }

    private static string LanguageId(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".cs" => "csharp", ".razor" => "razor", ".cshtml" => "aspnetcorerazor",
        ".html" or ".htm" => "html", ".css" => "css", _ => "plaintext"
    };

    public async ValueTask DisposeAsync()
    {
        Entry[] entries;
        lock (_gate) entries = _entries.Values.ToArray();
        foreach (var entry in entries) await CloseAsync(entry.Document);
    }
}
