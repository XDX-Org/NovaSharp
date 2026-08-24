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
    private readonly Dictionary<string, Uri?> _viewDocuments = new(StringComparer.Ordinal);
    private string _activeViewId = EditorGroupManager.MainGroupId;
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
        _viewDocuments.Add(EditorGroupManager.MainGroupId, null);
    }

    /// <inheritdoc />
    public async ValueTask CreateViewAsync(string viewId, ElementReference container, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(viewId);
        if (_viewDocuments.ContainsKey(viewId))
        {
            await InvokeAsync<object?>("remountView", cancellationToken, viewId, container).ConfigureAwait(false);
            return;
        }
        await InvokeAsync<object?>("createView", cancellationToken, viewId, container).ConfigureAwait(false);
        _viewDocuments.Add(viewId, null);
    }

    /// <inheritdoc />
    public ValueTask SetActiveViewAsync(string viewId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(viewId);
        if (!_viewDocuments.ContainsKey(viewId))
            throw new InvalidOperationException($"Editor view is not mounted: {viewId}");
        _activeViewId = viewId;
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public async ValueTask SwitchViewDocumentAsync(
        string viewId,
        Uri uri,
        EditorViewState? viewState,
        bool focus,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(viewId);
        ArgumentNullException.ThrowIfNull(uri);
        await InvokeAsync<object?>("switchViewDocument", cancellationToken,
            viewId, uri.AbsoluteUri, viewState, focus).ConfigureAwait(false);
        _viewDocuments[viewId] = uri;
    }

    /// <inheritdoc />
    public async ValueTask ClearViewAsync(string viewId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(viewId);
        await InvokeAsync<object?>("clearView", cancellationToken, viewId).ConfigureAwait(false);
        _viewDocuments[viewId] = null;
    }

    /// <inheritdoc />
    public ValueTask<EditorViewState?> GetViewStateAsync(
        string viewId,
        Uri uri,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(viewId);
        ArgumentNullException.ThrowIfNull(uri);
        return InvokeAsync<EditorViewState?>("viewStateForView", cancellationToken, viewId, uri.AbsoluteUri);
    }

    /// <inheritdoc />
    public async ValueTask RemoveViewAsync(string viewId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(viewId);
        if (viewId == EditorGroupManager.MainGroupId || !_viewDocuments.ContainsKey(viewId)) return;
        await InvokeAsync<object?>("removeView", cancellationToken, viewId).ConfigureAwait(false);
        _viewDocuments.Remove(viewId);
        if (_activeViewId == viewId) _activeViewId = EditorGroupManager.MainGroupId;
    }

    /// <inheritdoc />
    public async ValueTask<EditorSequence> OpenDocumentAsync(DocumentContent content, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);

        using var text = new MemoryStream(Encoding.UTF8.GetBytes(content.Text), writable: false);
        using var streamReference = new DotNetStreamReference(text);

        var sequence = await InvokeAsync<EditorSequence>(
            "openDocumentStreamInView",
            cancellationToken,
            _activeViewId,
            content.Uri.AbsoluteUri,
            content.LanguageId,
            streamReference,
            content.LineEnding,
            content.ReadOnly).ConfigureAwait(false);
        _viewDocuments[_activeViewId] = content.Uri;
        return sequence;
    }

    /// <inheritdoc />
    public async ValueTask SwitchDocumentAsync(Uri uri, EditorViewState? viewState, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(uri);
        await SwitchViewDocumentAsync(_activeViewId, uri, viewState, focus: true, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask ClearDocumentAsync(CancellationToken cancellationToken) =>
        await ClearViewAsync(_activeViewId, cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public ValueTask<EditorViewState?> GetViewStateAsync(Uri uri, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(uri);
        return GetViewStateAsync(_activeViewId, uri, cancellationToken);
    }

    /// <inheritdoc />
    public async ValueTask CloseDocumentAsync(Uri uri, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(uri);
        await InvokeAsync<object?>("closeDocument", cancellationToken, uri.AbsoluteUri).ConfigureAwait(false);
        foreach (var viewId in _viewDocuments.Where(item => item.Value == uri).Select(item => item.Key).ToArray())
            _viewDocuments[viewId] = null;
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
        var relocation = InvokeAsync<DocumentSnapshot>(
            "relocateDocument",
            cancellationToken,
            oldUri.AbsoluteUri,
            newUri.AbsoluteUri,
            languageId);
        foreach (var viewId in _viewDocuments.Where(item => item.Value == oldUri).Select(item => item.Key).ToArray())
            _viewDocuments[viewId] = newUri;
        return relocation;
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
        var uri = _viewDocuments.GetValueOrDefault(_activeViewId);
        return uri is null
            ? await InvokeAsync<EditorSequence>("replaceDocumentStream", cancellationToken, streamReference, lineEnding).ConfigureAwait(false)
            : await InvokeAsync<EditorSequence>("replaceDocumentStream", cancellationToken, uri.AbsoluteUri, streamReference, lineEnding).ConfigureAwait(false);
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
        _viewDocuments.GetValueOrDefault(_activeViewId) is { } uri
            ? InvokeAsync<DocumentSnapshot>("snapshot", cancellationToken, uri.AbsoluteUri)
            : InvokeAsync<DocumentSnapshot>("snapshot", cancellationToken);

    /// <inheritdoc />
    public ValueTask<DocumentSnapshot> GetSnapshotAsync(Uri uri, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(uri);
        return InvokeAsync<DocumentSnapshot>("snapshot", cancellationToken, uri.AbsoluteUri);
    }

    /// <inheritdoc />
    public ValueTask<EditorSequence> GetSequenceAsync(CancellationToken cancellationToken) =>
        _viewDocuments.GetValueOrDefault(_activeViewId) is { } uri
            ? InvokeAsync<EditorSequence>("sequence", cancellationToken, uri.AbsoluteUri)
            : InvokeAsync<EditorSequence>("sequence", cancellationToken);

    /// <inheritdoc />
    public ValueTask<EditorSequence> GetSequenceAsync(Uri uri, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(uri);
        return InvokeAsync<EditorSequence>("sequence", cancellationToken, uri.AbsoluteUri);
    }

    /// <inheritdoc />
    public async ValueTask SetReadOnlyAsync(bool readOnly, CancellationToken cancellationToken)
    {
        if (_viewDocuments.GetValueOrDefault(_activeViewId) is { } uri)
            await InvokeAsync<object?>("setReadOnly", cancellationToken, uri.AbsoluteUri, readOnly).ConfigureAwait(false);
        else
            await InvokeAsync<object?>("setReadOnly", cancellationToken, readOnly).ConfigureAwait(false);
    }

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
        await InvokeAsync<object?>("beginCompareInView", cancellationToken,
            _activeViewId, diffContainer, originalText).ConfigureAwait(false);
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
            _viewDocuments.Clear();
            _activeViewId = EditorGroupManager.MainGroupId;
        }
    }
}
