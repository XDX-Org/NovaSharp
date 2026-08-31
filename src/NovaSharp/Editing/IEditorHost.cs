using Microsoft.AspNetCore.Components;
using NovaSharp.Commands;
using NovaSharp.Configuration;

namespace NovaSharp.Editing;

/// <summary>Where Monaco's text model is, expressed in its two version counters.</summary>
/// <param name="Sequence">
/// The version identifier. Strictly increasing with every change, including undo, so it orders edits and is what a
/// barrier waits for.
/// </param>
/// <param name="AlternativeSequence">
/// The alternative version identifier. It returns to an earlier value when the user undoes back to a previous state,
/// so it — and not <paramref name="Sequence"/> — answers whether the document still matches its file.
/// </param>
public sealed record EditorSequence(long Sequence, long AlternativeSequence);

/// <summary>A portable subset of Monaco view state persisted for one editor view.</summary>
public sealed record EditorViewState(
    int LineNumber = 1,
    int Column = 1,
    int SelectionStartLineNumber = 1,
    int SelectionStartColumn = 1,
    int PositionLineNumber = 1,
    int PositionColumn = 1,
    double ScrollTop = 0,
    double ScrollLeft = 0);

/// <summary>
/// The single interop surface between NovaSharp and the packaged Monaco editor.
/// </summary>
/// <remarks>
/// Every member is asynchronous and cancellable, and none of them is on the typing path: edits travel the other way,
/// pushed by Monaco into the replication pump without waiting for .NET. Whole document text crosses this boundary
/// only when a document is opened, reloaded, or resynchronized, including a canonical URI relocation.
/// </remarks>
public interface IEditorHost : IAsyncDisposable
{
    /// <summary>Creates the editor inside <paramref name="container"/>, which must already be mounted and empty.</summary>
    /// <param name="container">The mounted, empty element the editor is created in.</param>
    /// <param name="bridge">Receives replicated edits and editor-invoked commands.</param>
    /// <param name="cancellationToken">Cancels the creation.</param>
    ValueTask InitializeAsync(ElementReference container, EditorBridge bridge, CancellationToken cancellationToken);

    /// <summary>Creates another Monaco editor instance that shares this host's URI-keyed models.</summary>
    ValueTask CreateViewAsync(string viewId, ElementReference container, CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;

    /// <summary>Selects the view used by document/session operations that target the active editor.</summary>
    ValueTask SetActiveViewAsync(string viewId, CancellationToken cancellationToken) => ValueTask.CompletedTask;

    /// <summary>Attaches an open document to one editor view without cloning its model.</summary>
    async ValueTask SwitchViewDocumentAsync(
        string viewId,
        Uri uri,
        EditorViewState? viewState,
        bool focus,
        CancellationToken cancellationToken) =>
        await SwitchDocumentAsync(uri, viewState, cancellationToken).ConfigureAwait(false);

    /// <summary>Clears one editor view without releasing the document model.</summary>
    async ValueTask ClearViewAsync(string viewId, CancellationToken cancellationToken) =>
        await ClearDocumentAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>Captures portable state from one editor view.</summary>
    ValueTask<EditorViewState?> GetViewStateAsync(
        string viewId,
        Uri uri,
        CancellationToken cancellationToken) => GetViewStateAsync(uri, cancellationToken);

    /// <summary>Reads the active caret as a UTF-16 model offset without copying model text.</summary>
    ValueTask<int?> GetPositionOffsetAsync(
        string viewId,
        Uri uri,
        CancellationToken cancellationToken) => ValueTask.FromResult<int?>(null);

    /// <summary>Releases one editor instance while leaving its shared document models open.</summary>
    ValueTask RemoveViewAsync(string viewId, CancellationToken cancellationToken) => ValueTask.CompletedTask;

    /// <summary>Opens <paramref name="content"/> in the editor, replacing whatever model was shown before.</summary>
    /// <returns>The sequence the new model starts at.</returns>
    ValueTask<EditorSequence> OpenDocumentAsync(DocumentContent content, CancellationToken cancellationToken);

    /// <summary>Loads a document model without changing the visible editor, selection, or focus.</summary>
    ValueTask<EditorSequence> PrepareDocumentAsync(DocumentContent content, CancellationToken cancellationToken);

    /// <summary>Attaches an already-open model and restores its validated view state.</summary>
    ValueTask SwitchDocumentAsync(Uri uri, EditorViewState? viewState, CancellationToken cancellationToken);

    /// <summary>Detaches the current model when no tab with an editor view is active.</summary>
    ValueTask ClearDocumentAsync(CancellationToken cancellationToken);

    /// <summary>Captures cursor, selection, and scroll state for an open document.</summary>
    ValueTask<EditorViewState?> GetViewStateAsync(Uri uri, CancellationToken cancellationToken);

    /// <summary>Releases this view's lease on an open document model.</summary>
    ValueTask CloseDocumentAsync(Uri uri, CancellationToken cancellationToken);

    /// <summary>Moves a live model to a new canonical URI while retaining its text and view state.</summary>
    ValueTask<DocumentSnapshot> RelocateDocumentAsync(
        Uri oldUri,
        Uri newUri,
        string languageId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Replaces the whole text of the open model, as a reload does.
    /// </summary>
    /// <remarks>
    /// Applied as an edit operation with its own undo stop rather than by assigning the model's value, so the change
    /// is undoable and the editor keeps its selection, folding, and scroll position.
    /// </remarks>
    ValueTask<EditorSequence> ReplaceDocumentAsync(string text, string lineEnding, CancellationToken cancellationToken);

    /// <summary>Replaces a particular open document, whether or not its tab is active.</summary>
    ValueTask<EditorSequence> ReplaceDocumentAsync(
        Uri uri,
        string text,
        string lineEnding,
        CancellationToken cancellationToken);

    /// <summary>Reads the model's current text and sequence, for a resynchronization.</summary>
    ValueTask<DocumentSnapshot> GetSnapshotAsync(CancellationToken cancellationToken);

    /// <summary>Reads a particular open model for resynchronization.</summary>
    ValueTask<DocumentSnapshot> GetSnapshotAsync(Uri uri, CancellationToken cancellationToken);

    /// <summary>Reads the model's current sequence without its text, for a save barrier.</summary>
    ValueTask<EditorSequence> GetSequenceAsync(CancellationToken cancellationToken);

    /// <summary>Reads a particular open model's sequence for a save barrier.</summary>
    ValueTask<EditorSequence> GetSequenceAsync(Uri uri, CancellationToken cancellationToken);

    /// <summary>Makes the editor refuse edits, for a file that cannot be written.</summary>
    ValueTask SetReadOnlyAsync(bool readOnly, CancellationToken cancellationToken);

    /// <summary>Updates a particular open document's read-only state.</summary>
    ValueTask SetReadOnlyAsync(Uri uri, bool readOnly, CancellationToken cancellationToken);

    /// <summary>Updates every editor view to the selected locally packaged font.</summary>
    ValueTask SetEditorFontAsync(EditorFontPreference font, CancellationToken cancellationToken);

    /// <summary>Updates the active Roslyn project stamp used by Monaco's C# providers.</summary>
    ValueTask SetLanguageContextAsync(
        Uri uri,
        string? projectContextId,
        long sourceVersion,
        bool available,
        bool suggestionsEnabled,
        CancellationToken cancellationToken) => ValueTask.CompletedTask;

    /// <summary>
    /// Replaces the editor's actions with the ones <paramref name="descriptors"/> describe.
    /// </summary>
    /// <returns>
    /// The keybindings the editor could not resolve. Empty is the only acceptable result: a binding that does not
    /// resolve is a shortcut that silently does nothing.
    /// </returns>
    ValueTask<IReadOnlyList<string>> RegisterCommandsAsync(
        IReadOnlyList<CommandDescriptor> descriptors,
        CancellationToken cancellationToken);

    /// <summary>
    /// Shows <paramref name="originalText"/> beside the editor's text in <paramref name="diffContainer"/>.
    /// </summary>
    /// <remarks>
    /// The live model becomes the comparison's modified side, so what is shown is the user's unsaved text and stays
    /// editable. The container must already be mounted.
    /// </remarks>
    ValueTask BeginCompareAsync(ElementReference diffContainer, string originalText, CancellationToken cancellationToken);

    /// <summary>Stops comparing and gives the model back to the editor.</summary>
    ValueTask EndCompareAsync(CancellationToken cancellationToken);

    /// <summary>Reads back what the host observes about itself.</summary>
    ValueTask<EditorRuntimeInfo> GetRuntimeInfoAsync(CancellationToken cancellationToken);
}
