using System.Text;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using NovaSharp.Commands;
using NovaSharp.Configuration;

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

        using var text = new MemoryStream(Encoding.UTF8.GetBytes(content.Text), writable: false);
        using var streamReference = new DotNetStreamReference(text);

        return await InvokeAsync<EditorSequence>(
            "openDocumentStream",
            cancellationToken,
            content.Uri.AbsoluteUri,
            content.LanguageId,
            streamReference,
            content.LineEnding,
            content.ReadOnly).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask SwitchDocumentAsync(Uri uri, EditorViewState? viewState, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(uri);
        await InvokeAsync<object?>("switchDocument", cancellationToken, uri.AbsoluteUri, viewState).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask ClearDocumentAsync(CancellationToken cancellationToken) =>
        await InvokeAsync<object?>("clearDocument", cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public ValueTask<EditorViewState?> GetViewStateAsync(Uri uri, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(uri);
        return InvokeAsync<EditorViewState?>("viewState", cancellationToken, uri.AbsoluteUri);
    }

    /// <inheritdoc />
    public async ValueTask CloseDocumentAsync(Uri uri, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(uri);
        await InvokeAsync<object?>("closeDocument", cancellationToken, uri.AbsoluteUri).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public ValueTask<DocumentSnapshot> RelocateDocumentAsync(
        Uri oldUri,
        Uri newUri,
        string languageId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(oldUri);
        ArgumentNullException.ThrowIfNull(newUri);
        ArgumentException.ThrowIfNullOrWhiteSpace(languageId);
        return InvokeAsync<DocumentSnapshot>(
            "relocateDocument",
            cancellationToken,
            oldUri.AbsoluteUri,
            newUri.AbsoluteUri,
            languageId);
    }

    /// <inheritdoc />
    public async ValueTask<EditorSequence> ReplaceDocumentAsync(
        string text,
        string lineEnding,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentException.ThrowIfNullOrEmpty(lineEnding);

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(text), writable: false);
        using var streamReference = new DotNetStreamReference(stream);
        return await InvokeAsync<EditorSequence>(
            "replaceDocumentStream",
            cancellationToken,
            streamReference,
            lineEnding).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<EditorSequence> ReplaceDocumentAsync(
        Uri uri,
        string text,
        string lineEnding,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(uri);
        ArgumentNullException.ThrowIfNull(text);
        ArgumentException.ThrowIfNullOrEmpty(lineEnding);

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(text), writable: false);
        using var streamReference = new DotNetStreamReference(stream);
        return await InvokeAsync<EditorSequence>(
            "replaceDocumentStream",
            cancellationToken,
            uri.AbsoluteUri,
            streamReference,
            lineEnding).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public ValueTask<DocumentSnapshot> GetSnapshotAsync(CancellationToken cancellationToken) =>
        InvokeAsync<DocumentSnapshot>("snapshot", cancellationToken);

    /// <inheritdoc />
    public ValueTask<DocumentSnapshot> GetSnapshotAsync(Uri uri, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(uri);
        return InvokeAsync<DocumentSnapshot>("snapshot", cancellationToken, uri.AbsoluteUri);
    }

    /// <inheritdoc />
    public ValueTask<EditorSequence> GetSequenceAsync(CancellationToken cancellationToken) =>
        InvokeAsync<EditorSequence>("sequence", cancellationToken);

    /// <inheritdoc />
    public ValueTask<EditorSequence> GetSequenceAsync(Uri uri, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(uri);
        return InvokeAsync<EditorSequence>("sequence", cancellationToken, uri.AbsoluteUri);
    }

    /// <inheritdoc />
    public async ValueTask SetReadOnlyAsync(bool readOnly, CancellationToken cancellationToken) =>
        await InvokeAsync<object?>("setReadOnly", cancellationToken, readOnly).ConfigureAwait(false);

    /// <inheritdoc />
    public async ValueTask SetReadOnlyAsync(Uri uri, bool readOnly, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(uri);
        await InvokeAsync<object?>("setReadOnly", cancellationToken, uri.AbsoluteUri, readOnly).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask SetEditorFontAsync(EditorFontPreference font, CancellationToken cancellationToken) =>
        await InvokeAsync<object?>("setEditorFont", cancellationToken, EditorFonts.Id(font)).ConfigureAwait(false);

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
