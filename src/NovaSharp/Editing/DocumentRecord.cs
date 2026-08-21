using NovaSharp.Text;

namespace NovaSharp.Editing;

/// <summary>
/// The persistence metadata for one open document: everything about it that is not its text.
/// </summary>
/// <remarks>
/// Monaco owns the text; this owns identity, how that text became bytes and will become bytes again, and what was last
/// seen on disk. It is immutable, so a background save can hold the record it started from while the workbench moves
/// on, and publishing a new one is a single assignment rather than a series of field updates another thread can catch
/// half-finished.
/// </remarks>
/// <param name="Uri">The canonical document URI, which is also the Monaco model URI.</param>
/// <param name="Path">The file-system path the document is read from and written to.</param>
/// <param name="DisplayName">The short name shown in the workbench.</param>
/// <param name="Encoding">The encoding the document was decoded with and will be re-encoded with.</param>
/// <param name="LineEnding">The line ending a save writes.</param>
/// <param name="LineEndingsWereMixed">Whether the file held more than one kind of ending when it was opened.</param>
/// <param name="DecodedWithFallback">Whether the encoding above was a fallback rather than a confident answer.</param>
/// <param name="Disk">The file metadata last observed, used to notice an external change.</param>
/// <param name="SavedSequence">
/// Monaco's alternative version identifier at the moment the document last matched its file. Dirty state is the
/// comparison between this and the editor's current alternative version, so undoing back to the saved text clears it.
/// </param>
public sealed record DocumentRecord(
    Uri Uri,
    string Path,
    string DisplayName,
    TextEncodingProfile Encoding,
    LineEndingStyle LineEnding,
    bool LineEndingsWereMixed,
    bool DecodedWithFallback,
    DiskState Disk,
    long SavedSequence)
{
    /// <summary>Whether the file was marked read-only when it was last observed.</summary>
    public bool IsReadOnly => Disk.ReadOnly;

    /// <summary>Returns whether the editor's current state differs from what is on disk.</summary>
    public bool IsDirty(long alternativeSequence) => alternativeSequence != SavedSequence;
}
