using Microsoft.AspNetCore.Components;
using NovaSharp.Commands;

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

/// <summary>
/// The single interop surface between NovaSharp and the packaged Monaco editor.
/// </summary>
/// <remarks>
/// Every member is asynchronous and cancellable, and none of them is on the typing path: edits travel the other way,
/// pushed by Monaco into the replication pump without waiting for .NET. Whole document text crosses this boundary
/// only when a document is opened, reloaded, or resynchronized.
/// </remarks>
public interface IEditorHost : IAsyncDisposable
{
    /// <summary>Creates the editor inside <paramref name="container"/>, which must already be mounted and empty.</summary>
    /// <param name="container">The mounted, empty element the editor is created in.</param>
    /// <param name="bridge">Receives replicated edits and editor-invoked commands.</param>
    /// <param name="cancellationToken">Cancels the creation.</param>
    ValueTask InitializeAsync(ElementReference container, EditorBridge bridge, CancellationToken cancellationToken);

    /// <summary>Opens <paramref name="content"/> in the editor, replacing whatever model was shown before.</summary>
    /// <returns>The sequence the new model starts at.</returns>
    ValueTask<EditorSequence> OpenDocumentAsync(DocumentContent content, CancellationToken cancellationToken);

    /// <summary>
    /// Replaces the whole text of the open model, as a reload does.
    /// </summary>
    /// <remarks>
    /// Applied as an edit operation with its own undo stop rather than by assigning the model's value, so the change
    /// is undoable and the editor keeps its selection, folding, and scroll position.
    /// </remarks>
    ValueTask<EditorSequence> ReplaceDocumentAsync(string text, string lineEnding, CancellationToken cancellationToken);

    /// <summary>Reads the model's current text and sequence, for a resynchronization.</summary>
    ValueTask<DocumentSnapshot> GetSnapshotAsync(CancellationToken cancellationToken);

    /// <summary>Reads the model's current sequence without its text, for a save barrier.</summary>
    ValueTask<EditorSequence> GetSequenceAsync(CancellationToken cancellationToken);

    /// <summary>Makes the editor refuse edits, for a file that cannot be written.</summary>
    ValueTask SetReadOnlyAsync(bool readOnly, CancellationToken cancellationToken);

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
