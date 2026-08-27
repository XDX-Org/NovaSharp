using Microsoft.JSInterop;
using NovaSharp.LanguageServices;

namespace NovaSharp.Editing;

/// <summary>
/// The object Monaco calls back into.
/// </summary>
/// <remarks>
/// Deliberately tiny and deliberately synchronous where it can be. <see cref="ReplicateEdits"/> is on the typing path:
/// it hands the batch to a queue and returns, so a keystroke never waits for .NET, for disk, or for Blazor to render.
/// Anything slower than that belongs behind the queue, not in this method.
/// </remarks>
public sealed class EditorBridge
{
    private readonly Func<IReadOnlyList<TextEditBatch>, bool> _replicate;
    private readonly Action<string?> _requestResync;
    private readonly Func<string, Task> _invokeCommand;
    private readonly ICSharpLanguageService? _language;

    /// <param name="replicate">Accepts batches without waiting. Returns whether they were all queued.</param>
    /// <param name="requestResync">Asks for a full resynchronization.</param>
    /// <param name="invokeCommand">Runs an editor-invoked command such as save.</param>
    public EditorBridge(
        Func<IReadOnlyList<TextEditBatch>, bool> replicate,
        Action requestResync,
        Func<string, Task> invokeCommand,
        ICSharpLanguageService? language = null)
        : this(replicate, _ => requestResync(), invokeCommand, language)
    {
        ArgumentNullException.ThrowIfNull(requestResync);
    }

    /// <summary>Creates a bridge that routes resynchronization by canonical document URI.</summary>
    public EditorBridge(
        Func<IReadOnlyList<TextEditBatch>, bool> replicate,
        Action<string?> requestResync,
        Func<string, Task> invokeCommand,
        ICSharpLanguageService? language = null)
    {
        ArgumentNullException.ThrowIfNull(replicate);
        ArgumentNullException.ThrowIfNull(requestResync);
        ArgumentNullException.ThrowIfNull(invokeCommand);

        _replicate = replicate;
        _requestResync = requestResync;
        _invokeCommand = invokeCommand;
        _language = language;
    }

    /// <summary>
    /// Receives consecutive ordered batches of edits from Monaco.
    /// </summary>
    /// <remarks>
    /// Several batches per call rather than one, because the JavaScript pump keeps at most one send in flight and
    /// everything Monaco raised while that send was outstanding travels together in the next one. They are still
    /// separate batches: coalescing the transport is safe, and merging the edits themselves would mean recomputing
    /// offsets in the one place where being wrong corrupts the document.
    /// </remarks>
    /// <returns>
    /// <see langword="false"/> when a batch was dropped, which tells the JavaScript side that a resynchronization is
    /// coming.
    /// </returns>
    [JSInvokable]
    public bool ReplicateEdits(IReadOnlyList<TextEditBatch> batches)
    {
        ArgumentNullException.ThrowIfNull(batches);
        return _replicate(batches);
    }

    /// <summary>Reports a change to the model that no edit batch can describe, such as its line ending.</summary>
    [JSInvokable]
    public void RequestResync(string? documentUri = null) => _requestResync(documentUri);

    /// <summary>Runs a command a Monaco action or keybinding invoked.</summary>
    [JSInvokable]
    public Task InvokeCommandAsync(string commandId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandId);
        return _invokeCommand(commandId);
    }

    [JSInvokable]
    public Task<LanguageCompletionList?> GetCompletionsAsync(LanguageRequest request) =>
        _language?.GetCompletionsAsync(request) ?? Task.FromResult<LanguageCompletionList?>(null);

    [JSInvokable]
    public Task<LanguageCompletionDetails?> ResolveCompletionAsync(LanguageCompletionResolveRequest request) =>
        _language?.ResolveCompletionAsync(request) ?? Task.FromResult<LanguageCompletionDetails?>(null);

    [JSInvokable]
    public Task<LanguageSignatureHelp?> GetSignatureHelpAsync(LanguageRequest request) =>
        _language?.GetSignatureHelpAsync(request) ?? Task.FromResult<LanguageSignatureHelp?>(null);

    [JSInvokable]
    public Task<LanguageHover?> GetHoverAsync(LanguageRequest request) =>
        _language?.GetHoverAsync(request) ?? Task.FromResult<LanguageHover?>(null);

    [JSInvokable]
    public Task<LanguageFormatResult?> FormatAsync(LanguageRequest request) =>
        _language?.FormatAsync(request) ?? Task.FromResult<LanguageFormatResult?>(null);

    [JSInvokable]
    public Task<LanguageSemanticTokens?> GetSemanticTokensAsync(LanguageRequest request) =>
        _language?.GetSemanticTokensAsync(request) ?? Task.FromResult<LanguageSemanticTokens?>(null);

    [JSInvokable]
    public void CancelLanguageRequest(string requestId) => _language?.Cancel(requestId);
}
