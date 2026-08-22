using NovaSharp.Async;
using NovaSharp.Commands;
using NovaSharp.Configuration;
using NovaSharp.Diagnostics;
using NovaSharp.Text;

namespace NovaSharp.Editing;

/// <summary>What the user chose when told the file changed underneath them.</summary>
public enum ExternalChangeChoice
{
    /// <summary>Discard the editor's text and re-read the file.</summary>
    Reload,

    /// <summary>Keep the editor's text and stop asking, leaving the file alone until an explicit save.</summary>
    Keep,
}

/// <summary>
/// One open document: its Monaco model, its .NET shadow, its file, and the commands that move text between them.
/// </summary>
/// <remarks>
/// The composition point of phase 2, and the only class that knows about all three. Everything expensive is delegated
/// — reading and writing to the bounded queue, replication to the pump — so what remains here is ordering: which
/// barrier a save waits on, which record a result updates, and which state the workbench is told about afterwards.
/// The document registry owns several of these by URI and routes only that document's host operations here.
/// </remarks>
public sealed class DocumentSession : IAsyncDisposable
{
    private readonly IEditorHost _host;
    private readonly DocumentLoader _loader;
    private readonly DocumentSaver _saver;
    private readonly IDocumentFileStore _store;
    private readonly IDocumentWatcher _watcher;
    private readonly BoundedWorkQueue _queue;
    private readonly SupersedingOperation _open = new();
    private readonly Lock _gate = new();

    private readonly INotificationService _notifications;
    private readonly Func<WorkbenchSettings> _settings;
    private readonly string _notificationScope = Guid.NewGuid().ToString("N");
    private DocumentRecord? _record;
    private DocumentReplica? _replica;
    private DocumentReplicationPump? _pump;
    private long _alternativeSequence;
    private bool _externalChangeAcknowledged;
    private DocumentStatus _status = new();
    private bool _disposed;

    /// <summary>Creates a session over the given seams.</summary>
    public DocumentSession(
        IEditorHost host,
        DocumentLoader loader,
        DocumentSaver saver,
        IDocumentFileStore store,
        IDocumentWatcher watcher,
        BoundedWorkQueue queue,
        INotificationService notifications,
        Func<WorkbenchSettings>? settings = null)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(loader);
        ArgumentNullException.ThrowIfNull(saver);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(watcher);
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(notifications);

        _host = host;
        _loader = loader;
        _saver = saver;
        _store = store;
        _watcher = watcher;
        _queue = queue;
        _notifications = notifications;

        // A function rather than a value, so a settings change takes effect on the next open without the session
        // having to be told about it or rebuilt.
        _settings = settings ?? (static () => WorkbenchSettings.Defaults);

        _watcher.Changed += OnFileChanged;
    }

    private WorkbenchSettings Settings => _settings();

    /// <summary>The current state of the document, as a snapshot.</summary>
    public DocumentStatus Status
    {
        get
        {
            lock (_gate)
            {
                return _status;
            }
        }
    }

    /// <summary>Raised whenever <see cref="Status"/> changes, possibly from a background thread.</summary>
    public event Func<DocumentStatus, Task>? StatusChanged;

    /// <summary>Whether closing now would lose the user's work.</summary>
    /// <remarks>
    /// Read from the window's closing callback, which is neither the UI thread nor able to await, so it is a plain
    /// snapshot read of state the session already maintains rather than anything that has to be computed on demand.
    /// </remarks>
    public bool HasUnsavedChanges => Status.IsDirty;

    /// <summary>
    /// The ordered .NET shadow of the open document, or <see langword="null"/> when nothing is open.
    /// </summary>
    /// <remarks>
    /// Exposed because it is the thing ADR 0001 keeps a shadow for: Roslyn, dirty-buffer search, and recovery all read
    /// document text from here rather than from Monaco. Readers take a snapshot; only the pump writes.
    /// </remarks>
    public DocumentReplica? Replica => _replica;

    /// <summary>The canonical identity of this session's document.</summary>
    public Uri? DocumentUri
    {
        get
        {
            lock (_gate)
            {
                return _record?.Uri;
            }
        }
    }

    /// <summary>Updates the file identity after an Explorer rename without touching Monaco text or view state.</summary>
    public async Task RelocateAsync(
        string oldPath,
        string newPath,
        Uri newUri,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(oldPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(newPath);
        ArgumentNullException.ThrowIfNull(newUri);

        DocumentRecord? record;
        DocumentReplica? replica;
        DocumentReplicationPump? pump;
        lock (_gate)
        {
            if (_record is null || !string.Equals(_record.Path, oldPath, StringComparison.Ordinal))
            {
                return;
            }

            record = _record;
            replica = _replica;
            pump = _pump;
        }

        DocumentSnapshot? before = null;
        if (replica is not null && pump is not null)
            before = await DrainPumpForRelocationAsync(pump, replica).ConfigureAwait(false);
        DocumentSnapshot relocated;
        try
        {
            relocated = await _host.RelocateDocumentAsync(
                record.Uri,
                newUri,
                LanguageIds.FromPath(newPath),
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            RestorePumpAfterFailedRelocation(replica);
            throw;
        }
        DocumentReplicationPump? replacementPump = null;
        if (replica is not null)
        {
            replica.Resync(relocated.Text, relocated.Sequence, relocated.AlternativeSequence);
            replacementPump = CreatePump(replica);
        }
        var wasDirty = before is not null
            && (record.IsDirty(before.AlternativeSequence)
                || !string.Equals(before.Text, relocated.Text, StringComparison.Ordinal));

        lock (_gate)
        {
            _record = record with
            {
                Path = newPath,
                Uri = newUri,
                DisplayName = Path.GetFileName(newPath),
                Disk = _store.GetState(newPath),
                SavedSequence = wasDirty ? long.MinValue : relocated.AlternativeSequence,
            };
            _alternativeSequence = relocated.AlternativeSequence;
            if (replacementPump is not null) _pump = replacementPump;
            _externalChangeAcknowledged = false;
            _status = _status with { IsComparing = false };
        }

        _watcher.Watch(newPath);
        replacementPump?.RequestResync();
        await PublishAsync(BuildStatus()).ConfigureAwait(false);
    }

    /// <summary>Opens <paramref name="path"/>, replacing whatever document was open.</summary>
    /// <param name="path">The file to open.</param>
    /// <param name="encoding">The encoding to try, or <see langword="null"/> for the configured default.</param>
    /// <param name="cancellationToken">Cancels the open.</param>
    public async Task OpenAsync(
        string path,
        TextEncodingProfile? encoding = null,
        bool foreground = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        await PublishAsync(Status with { IsBusy = true }).ConfigureAwait(false);

        try
        {
            // Superseded rather than queued: opening a second file while the first is still being read makes the first
            // result stale, and publishing it would show the wrong document.
            await _open.RunAsync(
                token => _loader.OpenAsync(
                    path,
                    encoding ?? Settings.DefaultEncoding,
                    Settings.DefaultLineEnding,
                    foreground,
                    token),
                AdoptAsync,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsFileFailure(exception))
        {
            await FailAsync(
                NotificationIds.OpenFailed,
                $"Could not open {System.IO.Path.GetFileName(path)}: {exception.Message}").ConfigureAwait(false);
        }
    }

    private async Task AdoptAsync(OpenedDocument opened, CancellationToken cancellationToken)
    {
        var sequence = await _host.OpenDocumentAsync(opened.Content, cancellationToken).ConfigureAwait(false);

        // Replaced wholesale rather than reset: a new document is a new model, a new shadow, and a new pump, and
        // reusing any of them would carry the previous document's sequence into this one.
        await DisposeDocumentAsync().ConfigureAwait(false);

        var replica = new DocumentReplica(opened.Content.Text, sequence.Sequence, sequence.AlternativeSequence);
        var pump = new DocumentReplicationPump(replica, RequestSnapshotAsync);
        pump.ResyncFailed += OnResyncFailed;

        lock (_gate)
        {
            _replica = replica;
            _pump = pump;
            _record = opened.Record with { SavedSequence = sequence.AlternativeSequence };
            _alternativeSequence = sequence.AlternativeSequence;
            _externalChangeAcknowledged = false;
        }

        _watcher.Watch(opened.Record.Path);
        _notifications.Dismiss(Scoped(NotificationIds.OpenFailed));
        _notifications.Dismiss(Scoped(NotificationIds.ExternalChange));
        _notifications.Dismiss(Scoped(NotificationIds.EncodingFallback));

        if (opened.Record.DecodedWithFallback)
        {
            // Said out loud rather than left in the status bar for the user to notice. A document NovaSharp guessed
            // the encoding of is one where "save" means something different from what they expect.
            _notifications.Raise(new Notification(
                Scoped(NotificationIds.EncodingFallback),
                NotificationSeverity.Warning,
                $"{opened.Record.DisplayName} is not valid {Settings.DefaultEncoding.DisplayName}. "
                + $"It was opened as {opened.Record.Encoding.DisplayName}, which preserves every byte.",
                [new NotificationAction(WorkbenchCommands.ChooseEncoding, "Choose an encoding", opened.Record.Uri.AbsoluteUri)],
                DateTimeOffset.UtcNow));
        }

        await PublishAsync(BuildStatus()).ConfigureAwait(false);
    }

    /// <summary>Reads the file as it is on disk right now, decoded the way the open document was.</summary>
    /// <remarks>
    /// The original side of a comparison. It goes through the loader so the text is decoded and its line endings
    /// normalized exactly as the editor's text was, and a difference in the view is therefore a difference in the
    /// document rather than an artefact of how the two sides were read.
    /// </remarks>
    public async Task<string?> ReadFileTextAsync(CancellationToken cancellationToken = default)
    {
        DocumentRecord? record;
        lock (_gate)
        {
            record = _record;
        }

        if (record is null)
        {
            return null;
        }

        try
        {
            var opened = await _loader
                .OpenAsync(record.Path, record.Encoding, Settings.DefaultLineEnding, cancellationToken)
                .ConfigureAwait(false);

            return opened.Content.Text;
        }
        catch (Exception exception) when (IsFileFailure(exception))
        {
            await FailAsync(
                NotificationIds.ReloadFailed,
                $"Could not read {record.DisplayName}: {exception.Message}").ConfigureAwait(false);
            return null;
        }
    }

    /// <summary>Records whether the comparison view is open, so the workbench and the commands agree.</summary>
    public Task SetComparingAsync(bool comparing)
    {
        lock (_gate)
        {
            _status = _status with { IsComparing = comparing };
        }

        return PublishAsync(BuildStatus());
    }

    /// <summary>Receives consecutive batches of edits from Monaco without waiting for anything.</summary>
    /// <returns><see langword="false"/> when a batch was dropped and a resynchronization was scheduled instead.</returns>
    public bool Replicate(IReadOnlyList<TextEditBatch> batches)
    {
        ArgumentNullException.ThrowIfNull(batches);

        if (batches.Count == 0)
        {
            return true;
        }

        DocumentReplicationPump? pump;
        bool dirtyChanged;
        lock (_gate)
        {
            pump = _pump;
            var wasDirty = _record is not null && _record.IsDirty(_alternativeSequence);
            _alternativeSequence = batches[^1].AlternativeSequence;
            dirtyChanged = _record is not null && _record.IsDirty(_alternativeSequence) != wasDirty;
        }

        if (pump is null)
        {
            return false;
        }

        var queued = true;
        foreach (var batch in batches)
        {
            queued &= pump.TryEnqueue(batch);
        }

        // The workbench is told only when the answer changed. Publishing on every keystroke would put Blazor rendering
        // back on the typing path, which is the one thing this whole pipeline exists to prevent.
        if (dirtyChanged)
        {
            _ = PublishAsync(BuildStatus());
        }

        return queued;
    }

    /// <summary>Writes the document to its own file.</summary>
    public Task<DocumentSaveResult?> SaveAsync(
        bool overwriteExternalChange = false,
        CancellationToken cancellationToken = default) =>
        SaveCoreAsync(targetPath: null, encoding: null, lineEnding: null, overwriteExternalChange, cancellationToken);

    /// <summary>Writes the document to <paramref name="targetPath"/> and continues editing it there.</summary>
    public Task<DocumentSaveResult?> SaveAsAsync(string targetPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        return SaveCoreAsync(targetPath, encoding: null, lineEnding: null, overwriteExternalChange: false, cancellationToken);
    }

    /// <summary>Re-encodes the document with <paramref name="encoding"/> and writes it.</summary>
    /// <remarks>Not the same command as reopening: this converts the text that is in the editor.</remarks>
    public Task<DocumentSaveResult?> SaveWithEncodingAsync(
        TextEncodingProfile encoding,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(encoding);
        return SaveCoreAsync(targetPath: null, encoding, lineEnding: null, overwriteExternalChange: false, cancellationToken);
    }

    /// <summary>Writes the document with <paramref name="lineEnding"/> and keeps using it.</summary>
    public Task<DocumentSaveResult?> SaveWithLineEndingAsync(
        LineEndingStyle lineEnding,
        CancellationToken cancellationToken = default) =>
        SaveCoreAsync(targetPath: null, encoding: null, lineEnding, overwriteExternalChange: false, cancellationToken);

    private async Task<DocumentSaveResult?> SaveCoreAsync(
        string? targetPath,
        TextEncodingProfile? encoding,
        LineEndingStyle? lineEnding,
        bool overwriteExternalChange,
        CancellationToken cancellationToken)
    {
        DocumentRecord? record;
        DocumentReplicationPump? pump;
        DocumentReplica? replica;
        lock (_gate)
        {
            record = _record;
            pump = _pump;
            replica = _replica;
        }

        if (record is null || pump is null || replica is null)
        {
            return null;
        }

        await PublishAsync(BuildStatus() with { IsBusy = true }).ConfigureAwait(false);

        try
        {
            // The barrier: the sequence Monaco is at right now, then the shadow catching up to it. What is written is
            // the document as it was at one instant, even if the user keeps typing while it is written.
            var target = await _host.GetSequenceAsync(record.Uri, cancellationToken).ConfigureAwait(false);
            await pump.WaitForSequenceAsync(target.Sequence, cancellationToken).ConfigureAwait(false);
            var snapshot = replica.Snapshot();

            var result = await _saver.SaveAsync(
                record,
                snapshot,
                targetPath,
                encoding,
                lineEnding,
                overwriteExternalChange,
                cancellationToken).ConfigureAwait(false);

            if (result.Status == DocumentSaveStatus.Saved)
            {
                var savedRecord = result.Record;
                long? relocatedAlternativeSequence = null;
                if (!HostUrisMatch(record.Uri, savedRecord.Uri))
                {
                    await DrainPumpForRelocationAsync(pump, replica).ConfigureAwait(false);
                    DocumentSnapshot relocated;
                    try
                    {
                        relocated = await _host.RelocateDocumentAsync(
                            record.Uri,
                            savedRecord.Uri,
                            LanguageIds.FromPath(savedRecord.Path),
                            cancellationToken).ConfigureAwait(false);
                    }
                    catch
                    {
                        RestorePumpAfterFailedRelocation(replica);
                        throw;
                    }
                    replica.Resync(relocated.Text, relocated.Sequence, relocated.AlternativeSequence);
                    var replacementPump = CreatePump(replica);
                    savedRecord = savedRecord with
                    {
                        SavedSequence = string.Equals(snapshot.Text, relocated.Text, StringComparison.Ordinal)
                            ? relocated.AlternativeSequence
                            : long.MinValue,
                    };
                    relocatedAlternativeSequence = relocated.AlternativeSequence;
                    lock (_gate)
                    {
                        _pump = replacementPump;
                    }
                    result = result with { Record = savedRecord };
                }

                lock (_gate)
                {
                    _record = savedRecord;
                    if (relocatedAlternativeSequence is { } currentAlternativeSequence)
                        _alternativeSequence = currentAlternativeSequence;
                    _externalChangeAcknowledged = false;

                    // The document and its file agree again, so whatever the notice was about is settled.
                    _status = _status with { ExternalChange = ExternalChangeState.None };
                }

                // Save-as continues editing the new file, so the watcher follows it there.
                _watcher.Watch(result.Record.Path);
                _notifications.Dismiss(Scoped(NotificationIds.SaveFailed));
                _notifications.Dismiss(Scoped(NotificationIds.ExternalChange));
                await PublishAsync(BuildStatus()).ConfigureAwait(false);
                if (relocatedAlternativeSequence is not null)
                {
                    lock (_gate)
                    {
                        _pump?.RequestResync();
                    }
                }
            }
            else
            {
                _notifications.Raise(
                    Scoped(NotificationIds.SaveFailed),
                    result.Status == DocumentSaveStatus.ExternallyChanged
                        ? NotificationSeverity.Warning
                        : NotificationSeverity.Error,
                    result.Message ?? $"{record.DisplayName} was not saved.");
                await PublishAsync(BuildStatus()).ConfigureAwait(false);
            }

            return result;
        }
        catch (OperationCanceledException)
        {
            await PublishAsync(BuildStatus()).ConfigureAwait(false);
            return null;
        }
        catch (Exception exception) when (IsFileFailure(exception))
        {
            await FailAsync(
                NotificationIds.SaveFailed,
                $"Could not save {record.DisplayName}: {exception.Message}").ConfigureAwait(false);
            return new DocumentSaveResult(DocumentSaveStatus.Failed, record, exception.Message);
        }
    }

    /// <summary>Discards unsaved changes and re-reads the file, keeping the same Monaco model.</summary>
    /// <param name="encoding">A different encoding to decode with, or <see langword="null"/> to keep the current one.</param>
    /// <param name="cancellationToken">Cancels the reload.</param>
    public async Task ReloadAsync(TextEncodingProfile? encoding = null, CancellationToken cancellationToken = default)
    {
        DocumentRecord? record;
        DocumentReplica? replica;
        lock (_gate)
        {
            record = _record;
            replica = _replica;
        }

        if (record is null)
        {
            return;
        }

        await PublishAsync(BuildStatus() with { IsBusy = true }).ConfigureAwait(false);

        try
        {
            var opened = await _loader
                .OpenAsync(record.Path, encoding ?? record.Encoding, Settings.DefaultLineEnding, cancellationToken)
                .ConfigureAwait(false);

            // Pushed as an edit with its own undo stop, so the model keeps its view state and the change stays undoable.
            var sequence = await _host
                .ReplaceDocumentAsync(record.Uri, opened.Content.Text, opened.Content.LineEnding, cancellationToken)
                .ConfigureAwait(false);

            // The shadow is set from the text that was just written into the model rather than read back out of it.
            // The editor suppresses replication for its own replacement precisely so this is exact: one snapshot both
            // sides already agree on, instead of a round trip to fetch what NovaSharp is holding anyway.
            replica?.Resync(opened.Content.Text, sequence.Sequence, sequence.AlternativeSequence);

            lock (_gate)
            {
                _record = opened.Record with { SavedSequence = sequence.AlternativeSequence };
                _alternativeSequence = sequence.AlternativeSequence;
                _externalChangeAcknowledged = false;
                _status = _status with { ExternalChange = ExternalChangeState.None };
            }

            _notifications.Dismiss(Scoped(NotificationIds.ExternalChange));
            _notifications.Dismiss(Scoped(NotificationIds.ReloadFailed));

            await _host.SetReadOnlyAsync(record.Uri, opened.Record.IsReadOnly, cancellationToken).ConfigureAwait(false);
            await PublishAsync(BuildStatus()).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (IsFileFailure(exception))
        {
            await FailAsync(
                NotificationIds.ReloadFailed,
                $"Could not reload {record.DisplayName}: {exception.Message}").ConfigureAwait(false);
        }
    }

    /// <summary>Asks the pump for a full resynchronization, for a model change no batch can describe.</summary>
    public void RequestResync()
    {
        lock (_gate)
        {
            _pump?.RequestResync();
        }
    }

    /// <summary>Applies the user's answer to an external change.</summary>
    public Task ResolveExternalChangeAsync(ExternalChangeChoice choice, CancellationToken cancellationToken = default)
    {
        if (choice == ExternalChangeChoice.Reload)
        {
            return ReloadAsync(cancellationToken: cancellationToken);
        }

        // Keeping means the editor's text is now the intended content and the notice has been answered. The file is
        // still not written: that stays an explicit save, which will ask again before overwriting.
        lock (_gate)
        {
            _externalChangeAcknowledged = true;
            _status = _status with { ExternalChange = ExternalChangeState.None };
        }

        _notifications.Dismiss(Scoped(NotificationIds.ExternalChange));

        return PublishAsync(BuildStatus());
    }

    private void OnFileChanged()
    {
        // The watcher fires on a background thread and knows only that something happened. Confirming it against the
        // metadata NovaSharp recorded is what turns a stream of file-system noise into at most one question.
        _ = _queue.EnqueueAsync(async _ =>
        {
            DocumentRecord? record;
            bool acknowledged;
            bool isDirty;
            lock (_gate)
            {
                record = _record;
                acknowledged = _externalChangeAcknowledged;
                isDirty = record is not null && record.IsDirty(_alternativeSequence);
            }

            if (record is null || acknowledged)
            {
                return false;
            }

            var current = _store.GetState(record.Path);
            if (current.Matches(record.Disk))
            {
                return false;
            }

            var state = current.Exists ? ExternalChangeState.Modified : ExternalChangeState.Deleted;

            // A clean document can simply follow the file, when the user has asked for that. A dirty one never loses
            // text to a background event: the question is asked and the editor keeps what the user typed until they
            // answer it.
            if (!isDirty && state == ExternalChangeState.Modified && Settings.ReloadUnmodifiedFiles)
            {
                await ReloadAsync().ConfigureAwait(false);
                return true;
            }

            var deleted = state == ExternalChangeState.Deleted;
            _notifications.Raise(new Notification(
                Scoped(NotificationIds.ExternalChange),
                NotificationSeverity.Warning,
                deleted
                    ? $"{record.DisplayName} was deleted on disk. The editor still has your text."
                    : $"{record.DisplayName} changed on disk.",
                // Named as commands, so the buttons a notification offers are the same commands the palette and the
                // toolbar invoke, with the same enablement.
                deleted
                    ? [new NotificationAction(WorkbenchCommands.KeepEditorText, "Keep my text", record.Uri.AbsoluteUri)]
                    : [
                        new NotificationAction(WorkbenchCommands.Compare, "Compare", record.Uri.AbsoluteUri),
                        new NotificationAction(WorkbenchCommands.Reload, "Reload from disk", record.Uri.AbsoluteUri),
                        new NotificationAction(WorkbenchCommands.KeepEditorText, "Keep my text", record.Uri.AbsoluteUri),
                    ],
                DateTimeOffset.UtcNow));

            lock (_gate)
            {
                _status = _status with { ExternalChange = state };
            }

            await PublishAsync(BuildStatus()).ConfigureAwait(false);
            return true;
        });
    }

    private void OnResyncFailed(Exception exception) =>
        _ = FailAsync(
            NotificationIds.ResyncFailed,
            $"The editor and NovaSharp are out of step and could not be resynchronized: {exception.Message}");

    private async Task<DocumentSnapshot> RequestSnapshotAsync(CancellationToken cancellationToken)
    {
        Uri? uri;
        lock (_gate)
        {
            uri = _record?.Uri;
        }

        return uri is null
            ? new DocumentSnapshot(string.Empty, 0, 0)
            : await _host.GetSnapshotAsync(uri, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Describes the document, and only the document.
    /// </summary>
    /// <remarks>
    /// Nothing here is a message. What the user is told goes to <see cref="INotificationService"/>, which carries
    /// severity and offers the commands that answer it; leaving a string on the status as well would mean two places
    /// telling the user two versions of the same thing.
    /// </remarks>
    private DocumentStatus BuildStatus()
    {
        lock (_gate)
        {
            if (_record is null)
            {
                return new DocumentStatus();
            }

            return new DocumentStatus(
                IsOpen: true,
                _record.DisplayName,
                _record.Path,
                _record.IsDirty(_alternativeSequence),
                _record.IsReadOnly,
                _record.Encoding,
                _record.LineEnding,
                _record.LineEndingsWereMixed,
                _record.DecodedWithFallback,
                _externalChangeAcknowledged ? ExternalChangeState.None : _status.ExternalChange,
                _status.IsBusy,
                _status.IsComparing);
        }
    }

    private Task FailAsync(string id, string message)
    {
        _notifications.Raise(Scoped(id), NotificationSeverity.Error, message);
        return PublishAsync(BuildStatus() with { IsBusy = false });
    }

    private string Scoped(string id) => $"{id}:{_notificationScope}";

    private async Task PublishAsync(DocumentStatus status)
    {
        lock (_gate)
        {
            _status = status;
        }

        var handler = StatusChanged;
        if (handler is not null)
        {
            await handler(status).ConfigureAwait(false);
        }
    }

    private static bool IsFileFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or NotSupportedException or System.Text.EncoderFallbackException;

    private static bool HostUrisMatch(Uri left, Uri right) =>
        string.Equals(left.AbsoluteUri, right.AbsoluteUri, StringComparison.Ordinal);

    private async Task<DocumentSnapshot> DrainPumpForRelocationAsync(
        DocumentReplicationPump pump,
        DocumentReplica replica)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_pump, pump)) _pump = null;
        }
        pump.ResyncFailed -= OnResyncFailed;
        await pump.DisposeAsync().ConfigureAwait(false);
        return replica.Snapshot();
    }

    private DocumentReplicationPump CreatePump(DocumentReplica replica)
    {
        var replacement = new DocumentReplicationPump(replica, RequestSnapshotAsync);
        replacement.ResyncFailed += OnResyncFailed;
        return replacement;
    }

    private void RestorePumpAfterFailedRelocation(DocumentReplica? replica)
    {
        if (replica is null) return;
        var replacement = CreatePump(replica);
        lock (_gate)
        {
            _pump = replacement;
        }
        replacement.RequestResync();
    }

    private async Task DisposeDocumentAsync()
    {
        DocumentReplicationPump? pump;
        lock (_gate)
        {
            pump = _pump;
            _pump = null;
            _replica = null;
            _record = null;
        }

        if (pump is not null)
        {
            pump.ResyncFailed -= OnResyncFailed;
            await pump.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        // Dependency order: stop the file events, then the operation that could start a new document, then the pump
        // that is still draining edits into a replica nothing will read again.
        _watcher.Changed -= OnFileChanged;
        await _watcher.DisposeAsync().ConfigureAwait(false);
        await _open.DisposeAsync().ConfigureAwait(false);
        await DisposeDocumentAsync().ConfigureAwait(false);
        foreach (var id in new[]
        {
            NotificationIds.OpenFailed,
            NotificationIds.SaveFailed,
            NotificationIds.ReloadFailed,
            NotificationIds.ExternalChange,
            NotificationIds.ResyncFailed,
            NotificationIds.EncodingFallback,
        })
        {
            _notifications.Dismiss(Scoped(id));
        }
    }
}
