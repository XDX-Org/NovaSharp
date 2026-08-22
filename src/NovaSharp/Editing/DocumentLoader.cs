using NovaSharp.Async;
using NovaSharp.Platform;
using NovaSharp.Text;

namespace NovaSharp.Editing;

/// <summary>A document as it was just read from disk.</summary>
/// <param name="Record">Its persistence metadata.</param>
/// <param name="Content">What Monaco needs in order to show it.</param>
public sealed record OpenedDocument(DocumentRecord Record, DocumentContent Content);

/// <summary>
/// Reads a file and turns it into an open document, on a bounded background worker.
/// </summary>
/// <remarks>
/// Every step that touches the file system or decodes bytes happens inside the queued work item, so nothing here runs
/// on the UI thread — including the metadata read, which the framework offers no asynchronous form of.
/// </remarks>
public sealed class DocumentLoader
{
    private readonly IWorkspacePaths _paths;
    private readonly IDocumentFileStore _store;
    private readonly DocumentTextCodec _codec;
    private readonly BoundedWorkQueue _queue;

    /// <summary>Creates a loader over the given seams.</summary>
    public DocumentLoader(IWorkspacePaths paths, IDocumentFileStore store, DocumentTextCodec codec, BoundedWorkQueue queue)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(codec);
        ArgumentNullException.ThrowIfNull(queue);

        _paths = paths;
        _store = store;
        _codec = codec;
        _queue = queue;
    }

    /// <summary>
    /// Reads <paramref name="path"/> and returns the document Monaco should open.
    /// </summary>
    /// <param name="path">The file to read.</param>
    /// <param name="encoding">
    /// The encoding to try when the file carries no byte-order mark. A reopen-with-encoding command passes the user's
    /// choice here; an ordinary open passes the configured default.
    /// </param>
    /// <param name="defaultLineEnding">The ending used for a file that contains no line break.</param>
    public Task<OpenedDocument> OpenAsync(
        string path,
        TextEncodingProfile encoding,
        LineEndingStyle defaultLineEnding,
        CancellationToken cancellationToken) =>
        OpenAsync(path, encoding, defaultLineEnding, foreground: true, cancellationToken);

    public Task<OpenedDocument> OpenAsync(
        string path,
        TextEncodingProfile encoding,
        LineEndingStyle defaultLineEnding,
        bool foreground,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(encoding);

        Task<OpenedDocument> ReadAsync(CancellationToken token) => ReadCoreAsync(token);
        return foreground
            ? _queue.EnqueueForegroundAsync(ReadAsync, cancellationToken)
            : _queue.EnqueueAsync(ReadAsync, cancellationToken);

        async Task<OpenedDocument> ReadCoreAsync(CancellationToken token)
        {
            var bytes = await _store.ReadAllBytesAsync(path, token).ConfigureAwait(false);

            // Read after the bytes, so the state recorded is the state of what was actually read rather than of a file
            // that may have changed between the two calls.
            var disk = _store.GetState(path);
            var decoded = _codec.Decode(bytes, encoding, defaultLineEnding);
            var uri = _paths.ToDocumentUri(path);

            var record = new DocumentRecord(
                uri,
                Path.GetFullPath(path),
                _paths.ToDisplayName(path),
                decoded.Encoding,
                decoded.LineEndings.Style,
                decoded.LineEndings.IsMixed,
                decoded.DecodedWithFallback,
                disk,
                // Monaco stamps a newly created model with alternative version 1, which is the state the file is in.
                SavedSequence: 1);

            var content = new DocumentContent(
                uri,
                record.DisplayName,
                LanguageIds.FromPath(path),
                decoded.Text,
                record.LineEnding.ToEditorSequence(),
                disk.ReadOnly);

            return new OpenedDocument(record, content);
        }
    }
}
