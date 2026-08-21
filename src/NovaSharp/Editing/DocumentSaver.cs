using System.Text;
using NovaSharp.Async;
using NovaSharp.Platform;
using NovaSharp.Text;

namespace NovaSharp.Editing;

/// <summary>Why a save did or did not happen.</summary>
public enum DocumentSaveStatus
{
    /// <summary>The file was written and the record now describes it.</summary>
    Saved,

    /// <summary>Something else changed the file since NovaSharp last read it, and the user has not chosen what to do.</summary>
    ExternallyChanged,

    /// <summary>The file cannot be written.</summary>
    ReadOnly,

    /// <summary>The document's encoding cannot represent the text, so writing it would lose characters.</summary>
    Unrepresentable,

    /// <summary>The write itself failed.</summary>
    Failed,
}

/// <summary>The outcome of a save.</summary>
/// <param name="Status">What happened.</param>
/// <param name="Record">The updated record when the save succeeded; the unchanged one otherwise.</param>
/// <param name="Message">An explanation for the user when the save did not happen.</param>
public sealed record DocumentSaveResult(DocumentSaveStatus Status, DocumentRecord Record, string? Message = null);

/// <summary>
/// Writes an open document back to disk.
/// </summary>
/// <remarks>
/// The save path is deliberately narrow: it takes a snapshot that was already taken at a known sequence, refuses
/// rather than guesses when the file has moved underneath it, and hands the bytes to the store's replace-in-one-step
/// write. Nothing here reads the editor, so a save cannot race typing — the barrier that made the snapshot consistent
/// has already been passed by the time this runs.
/// </remarks>
public sealed class DocumentSaver
{
    private readonly IWorkspacePaths _paths;
    private readonly IDocumentFileStore _store;
    private readonly DocumentTextCodec _codec;
    private readonly BoundedWorkQueue _queue;

    /// <summary>Creates a saver over the given seams.</summary>
    public DocumentSaver(IWorkspacePaths paths, IDocumentFileStore store, DocumentTextCodec codec, BoundedWorkQueue queue)
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
    /// Writes <paramref name="snapshot"/> to <paramref name="record"/>'s path, or to <paramref name="targetPath"/> for
    /// a save-as.
    /// </summary>
    /// <param name="record">The document's metadata as it was when the snapshot was taken.</param>
    /// <param name="snapshot">A replica snapshot taken at or after the sequence the user asked to save.</param>
    /// <param name="targetPath">Where to write. <see langword="null"/> saves over the document's own file.</param>
    /// <param name="encoding">The encoding to write with. <see langword="null"/> keeps the document's own.</param>
    /// <param name="lineEnding">The ending to write. <see langword="null"/> keeps the document's own.</param>
    /// <param name="overwriteExternalChange">
    /// Whether to write over a file that changed since NovaSharp read it. Only ever set from an explicit user choice.
    /// </param>
    public Task<DocumentSaveResult> SaveAsync(
        DocumentRecord record,
        DocumentSnapshot snapshot,
        string? targetPath = null,
        TextEncodingProfile? encoding = null,
        LineEndingStyle? lineEnding = null,
        bool overwriteExternalChange = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(snapshot);

        return _queue.EnqueueAsync(async token =>
        {
            var savingElsewhere = targetPath is not null && !PathsMatch(targetPath, record.Path);
            var path = savingElsewhere ? Path.GetFullPath(targetPath!) : record.Path;
            var chosenEncoding = encoding ?? record.Encoding;
            var chosenLineEnding = lineEnding ?? record.LineEnding;

            var before = _store.GetState(path);

            // Save-as into an existing file is the user naming that file, not NovaSharp finding it changed. Only the
            // document's own file is checked against what NovaSharp last saw.
            if (!savingElsewhere && !overwriteExternalChange && !before.Matches(record.Disk))
            {
                return new DocumentSaveResult(
                    DocumentSaveStatus.ExternallyChanged,
                    record,
                    $"{record.DisplayName} changed on disk since it was opened.");
            }

            if (before.Exists && before.ReadOnly)
            {
                return new DocumentSaveResult(
                    DocumentSaveStatus.ReadOnly,
                    record,
                    $"{Path.GetFileName(path)} is read-only.");
            }

            byte[] bytes;
            try
            {
                bytes = _codec.Encode(snapshot.Text, chosenEncoding, chosenLineEnding);
            }
            catch (EncoderFallbackException)
            {
                var offending = chosenEncoding.FindUnrepresentableRune(snapshot.Text);
                var described = offending is { } rune
                    ? $" It cannot write U+{rune.Value:X4} '{rune}'."
                    : string.Empty;

                return new DocumentSaveResult(
                    DocumentSaveStatus.Unrepresentable,
                    record,
                    $"{chosenEncoding.DisplayName} cannot represent this document.{described}");
            }

            await _store.WriteAllBytesAsync(path, bytes, token).ConfigureAwait(false);

            var saved = record with
            {
                Uri = savingElsewhere ? _paths.ToDocumentUri(path) : record.Uri,
                Path = path,
                DisplayName = savingElsewhere ? _paths.ToDisplayName(path) : record.DisplayName,
                Encoding = chosenEncoding,
                LineEnding = chosenLineEnding,
                LineEndingsWereMixed = false,
                DecodedWithFallback = false,
                Disk = _store.GetState(path),
                SavedSequence = snapshot.AlternativeSequence,
            };

            return new DocumentSaveResult(DocumentSaveStatus.Saved, saved);
        }, cancellationToken);
    }

    /// <remarks>
    /// Ordinal comparison, matching <see cref="IWorkspacePaths.IsSameDocument"/>. Whether two differently cased paths
    /// are one file is a property of the file system holding them, not of the operating system NovaSharp is running
    /// on, and guessing here would be a guess in the one place where being wrong overwrites a different file.
    /// </remarks>
    private static bool PathsMatch(string left, string right) =>
        string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.Ordinal);
}
