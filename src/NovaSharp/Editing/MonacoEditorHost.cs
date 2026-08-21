using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using NovaSharp.Commands;

namespace NovaSharp.Editing;

/// <inheritdoc cref="IEditorHost"/>
public sealed class MonacoEditorHost : IEditorHost
{
    private const string ModulePath = "./monaco-editor-host.js";

    private readonly IJSRuntime _jsRuntime;
    private IJSObjectReference? _module;
    private IJSObjectReference? _editor;
    private DotNetObjectReference<EditorBridge>? _bridge;
    private bool _disposed;

    /// <summary>Creates a host over the page's JavaScript runtime.</summary>
    public MonacoEditorHost(IJSRuntime jsRuntime)
    {
        ArgumentNullException.ThrowIfNull(jsRuntime);
        _jsRuntime = jsRuntime;
    }

    /// <inheritdoc />
    public async ValueTask InitializeAsync(
        ElementReference container,
        EditorBridge bridge,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(bridge);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_editor is not null)
        {
            return;
        }

        _module = await _jsRuntime.InvokeAsync<IJSObjectReference>("import", cancellationToken, ModulePath)
            .ConfigureAwait(false);

        // Created before the editor and disposed after it, so the reference is alive for every callback the editor can
        // still make.
        _bridge = DotNetObjectReference.Create(bridge);
        _editor = await _module.InvokeAsync<IJSObjectReference>("createEditor", cancellationToken, container, _bridge)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<EditorSequence> OpenDocumentAsync(DocumentContent content, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);

        return await InvokeAsync<EditorSequence>(
            "openDocument",
            cancellationToken,
            content.Uri.AbsoluteUri,
            content.LanguageId,
            content.Text,
            content.LineEnding,
            content.ReadOnly).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public ValueTask<EditorSequence> ReplaceDocumentAsync(
        string text,
        string lineEnding,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentException.ThrowIfNullOrEmpty(lineEnding);

        return InvokeAsync<EditorSequence>("replaceDocument", cancellationToken, text, lineEnding);
    }

    /// <inheritdoc />
    public ValueTask<DocumentSnapshot> GetSnapshotAsync(CancellationToken cancellationToken) =>
        InvokeAsync<DocumentSnapshot>("snapshot", cancellationToken);

    /// <inheritdoc />
    public ValueTask<EditorSequence> GetSequenceAsync(CancellationToken cancellationToken) =>
        InvokeAsync<EditorSequence>("sequence", cancellationToken);

    /// <inheritdoc />
    public async ValueTask SetReadOnlyAsync(bool readOnly, CancellationToken cancellationToken) =>
        await InvokeAsync<object?>("setReadOnly", cancellationToken, readOnly).ConfigureAwait(false);

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<string>> RegisterCommandsAsync(
        IReadOnlyList<CommandDescriptor> descriptors,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(descriptors);
        return InvokeAsync<IReadOnlyList<string>>("registerCommands", cancellationToken, descriptors);
    }

    /// <inheritdoc />
    public async ValueTask BeginCompareAsync(
        ElementReference diffContainer,
        string originalText,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(originalText);
        await InvokeAsync<object?>("beginCompare", cancellationToken, diffContainer, originalText).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask EndCompareAsync(CancellationToken cancellationToken) =>
        await InvokeAsync<object?>("endCompare", cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public ValueTask<EditorRuntimeInfo> GetRuntimeInfoAsync(CancellationToken cancellationToken) =>
        InvokeAsync<EditorRuntimeInfo>("runtimeInfo", cancellationToken);

    private async ValueTask<T> InvokeAsync<T>(string method, CancellationToken cancellationToken, params object?[] args)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var editor = _editor ?? throw new InvalidOperationException(
            $"{nameof(InitializeAsync)} must complete before '{method}' can be called.");

        return await editor.InvokeAsync<T>(method, cancellationToken, args).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // Dispose in dependency order: the editor releases its model, observer, and listeners before the module that
        // owns it goes away, and the callback reference outlives both so nothing in flight calls into a freed handle.
        // A disconnected page makes these throw, which is not a failure worth surfacing.
        try
        {
            if (_editor is not null)
            {
                await _editor.InvokeVoidAsync("dispose").ConfigureAwait(false);
                await _editor.DisposeAsync().ConfigureAwait(false);
            }

            if (_module is not null)
            {
                await _module.DisposeAsync().ConfigureAwait(false);
            }
        }
        catch (JSDisconnectedException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            _editor = null;
            _module = null;
            _bridge?.Dispose();
            _bridge = null;
        }
    }
}
