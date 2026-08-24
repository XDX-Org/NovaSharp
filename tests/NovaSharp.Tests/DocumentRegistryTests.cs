using System.Text;
using Microsoft.AspNetCore.Components;
using NovaSharp.Async;
using NovaSharp.Commands;
using NovaSharp.Configuration;
using NovaSharp.Diagnostics;
using NovaSharp.Editing;
using NovaSharp.Platform;
using NovaSharp.Workspace;
using Xunit;

namespace NovaSharp.Tests;

public sealed class DocumentRegistryTests : IAsyncDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("novasharp-tabs").FullName;
    private readonly BoundedWorkQueue _queue = new(32, 2);
    private readonly DocumentFileStore _store = new();
    private readonly WorkspacePaths _paths = new();
    private readonly RegistryEditorHost _host = new();
    private readonly NotificationService _notifications = new(new BoundedWorkbenchLog());
    private readonly WorkspacePersistenceService _persistence;
    private readonly DocumentRegistry _registry;

    public DocumentRegistryTests()
    {
        var codec = new DocumentTextCodec();
        var loader = new DocumentLoader(_paths, _store, codec, _queue);
        var saver = new DocumentSaver(_paths, _store, codec, _queue);
        _persistence = new WorkspacePersistenceService(new RegistryApplicationPaths(_root), _store, _queue);
        _registry = new DocumentRegistry(
            _host,
            _paths,
            _persistence,
            () => new DocumentSession(
                _host,
                loader,
                saver,
                _store,
                new FakeDocumentWatcher(),
                _queue,
                _notifications),
            _notifications);
        _host.InitializeAsync(default, new EditorBridge(_registry.Replicate, _registry.RequestResync, _ => Task.CompletedTask), default)
            .AsTask().GetAwaiter().GetResult();
    }

    [Fact]
    public async Task OpeningTheSamePathConcurrently_FocusesOneDocumentAndLoadsOneModel()
    {
        var path = await CreateFileAsync("Widget.cs");

        await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => _registry.OpenPinnedAsync(path, TestContext.Current.CancellationToken)));

        Assert.Single(_registry.Snapshot.Tabs);
        Assert.Equal(1, _host.ModelCount);
        Assert.Equal(_paths.ToDocumentUri(path).AbsoluteUri, _registry.Snapshot.ActiveId);
    }

    [Fact]
    public async Task ReorderAndClosePlans_AreDeterministicAndNameEveryDirtyDocument()
    {
        var first = await CreateFileAsync("first.cs");
        var second = await CreateFileAsync("second.cs");
        var third = await CreateFileAsync("third.cs");
        await _registry.OpenPinnedAsync(first, TestContext.Current.CancellationToken);
        await _registry.OpenPinnedAsync(second, TestContext.Current.CancellationToken);
        await _registry.OpenPinnedAsync(third, TestContext.Current.CancellationToken);

        await _registry.ActivateAsync(_paths.ToDocumentUri(second).AbsoluteUri, TestContext.Current.CancellationToken);
        _host.Type("// dirty");
        await WaitForAsync(() => _registry.Snapshot.ActiveTab?.IsDirty == true);

        var right = _registry.GetCloseCandidates(DocumentCloseKind.Right);
        Assert.Equal(["third.cs"], right.Select(tab => tab.Label));
        Assert.Equal(["second.cs"], _registry.GetCloseCandidates(DocumentCloseKind.All)
            .Where(tab => tab.IsDirty).Select(tab => tab.Label));

        await _registry.CloseAsync(
            _registry.GetCloseCandidates(DocumentCloseKind.All).Select(tab => tab.Id).ToArray(),
            discardDirty: false,
            TestContext.Current.CancellationToken);
        Assert.Equal(3, _registry.Snapshot.Tabs.Count);

        await _registry.MoveAsync(_paths.ToDocumentUri(third).AbsoluteUri, 0, TestContext.Current.CancellationToken);
        Assert.Equal(["third.cs", "first.cs", "second.cs"], _registry.Snapshot.Tabs.Select(tab => tab.Label));
    }

    [Fact]
    public async Task Preview_IsReusedUntilEditingPromotesIt()
    {
        var first = await CreateFileAsync("first.cs");
        var second = await CreateFileAsync("second.cs");

        await _registry.OpenPreviewAsync(first, TestContext.Current.CancellationToken);
        await _registry.OpenPreviewAsync(second, TestContext.Current.CancellationToken);
        Assert.Equal(["second.cs"], _registry.Snapshot.Tabs.Select(tab => tab.Label));

        _host.Type("// edited");
        await WaitForAsync(() => _registry.Snapshot.ActiveTab is { IsDirty: true, IsPreview: false, IsPinned: true });
        await _registry.OpenPreviewAsync(first, TestContext.Current.CancellationToken);

        Assert.Equal(2, _registry.Snapshot.Tabs.Count);
        Assert.Single(_registry.Snapshot.Tabs, tab => tab.IsPreview);
        Assert.Single(_registry.Snapshot.Tabs, tab => tab.IsDirty && tab.IsPinned);
    }

    [Fact]
    public async Task DuplicateNames_UseTheShortestUniqueParentSuffix()
    {
        var left = await CreateFileAsync(Path.Combine("left", "Widget.cs"));
        var right = await CreateFileAsync(Path.Combine("right", "Widget.cs"));

        await _registry.OpenPinnedAsync(left, TestContext.Current.CancellationToken);
        await _registry.OpenPinnedAsync(right, TestContext.Current.CancellationToken);

        Assert.Equal(["Widget.cs — left", "Widget.cs — right"], _registry.Snapshot.Tabs.Select(tab => tab.Label));
        Assert.All(_registry.Snapshot.Tabs, tab => Assert.Contains(tab.Label, tab.AccessibleLabel, StringComparison.Ordinal));
    }

    [Fact]
    public async Task SaveAs_RekeysTheRegistryAndKeepsOneLiveModel()
    {
        var original = await CreateFileAsync("original.cs");
        var target = Path.Combine(_root, "renamed.cs");
        await _registry.OpenPinnedAsync(original, TestContext.Current.CancellationToken);

        var result = await _registry.ActiveDocument!.SaveAsAsync(target, TestContext.Current.CancellationToken);
        await WaitForAsync(() => _registry.Snapshot.ActiveId == _paths.ToDocumentUri(target).AbsoluteUri);

        Assert.Equal(DocumentSaveStatus.Saved, result?.Status);
        Assert.Equal("renamed.cs", _registry.Snapshot.ActiveTab?.Label);
        Assert.Equal(1, _host.ModelCount);
        await _registry.OpenPinnedAsync(target, TestContext.Current.CancellationToken);
        Assert.Single(_registry.Snapshot.Tabs);
    }

    [Fact]
    public async Task Restore_PreservesOrderAndMissingFileTabs()
    {
        var present = await CreateFileAsync("present.cs");
        var missing = Path.Combine(_root, "missing.cs");
        var presentUri = _paths.ToDocumentUri(present).AbsoluteUri;
        var missingUri = _paths.ToDocumentUri(missing).AbsoluteUri;
        await _persistence.SaveAsync(new WorkspaceStateDocument
        {
            OpenDocuments =
            [
                new PersistedDocumentView(presentUri, present, false, false, true,
                    new EditorViewState(2, 3, 2, 1, 2, 3, 40, 5)),
                new PersistedDocumentView(missingUri, missing, false, true, false),
            ],
            ActiveDocumentId = missingUri,
        }, TestContext.Current.CancellationToken);

        await _registry.RestoreAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["present.cs", "missing.cs"], _registry.Snapshot.Tabs.Select(tab => tab.Label));
        Assert.True(_registry.Snapshot.Tabs[1].IsMissing);
        Assert.Equal(missingUri, _registry.Snapshot.ActiveId);
        Assert.Equal(1, _host.ModelCount);
        Assert.Null(_host.ActiveUri);
    }

    [Fact]
    public async Task RapidSwitchingAndClose_ReusesModelsAndReleasesEveryLease()
    {
        var paths = await Task.WhenAll(Enumerable.Range(0, 12).Select(index => CreateFileAsync($"file-{index}.cs")));
        foreach (var path in paths) await _registry.OpenPinnedAsync(path, TestContext.Current.CancellationToken);

        for (var index = 0; index < 200; index++)
            await _registry.ActivateAsync(_registry.Snapshot.Tabs[index % paths.Length].Id, TestContext.Current.CancellationToken);

        Assert.Equal(paths.Length, _host.ModelCount);
        await _registry.CloseAsync(
            _registry.Snapshot.Tabs.Select(tab => tab.Id).ToArray(),
            discardDirty: true,
            TestContext.Current.CancellationToken);
        Assert.Empty(_registry.Snapshot.Tabs);
        Assert.Equal(0, _host.ModelCount);
    }

    private async Task<string> CreateFileAsync(string relativePath)
    {
        var path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "class Widget;\n", TestContext.Current.CancellationToken);
        return path;
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(10, TestContext.Current.CancellationToken);
        Assert.True(condition());
    }

    public async ValueTask DisposeAsync()
    {
        await _registry.DisposeAsync();
        await _host.DisposeAsync();
        await _queue.DisposeAsync();
        Directory.Delete(_root, recursive: true);
    }
}

internal sealed class RegistryApplicationPaths(string root) : IApplicationPaths
{
    public string ConfigurationDirectory { get; } = Path.Combine(root, ".state");
}

internal sealed class RegistryEditorHost : IEditorHost
{
    private sealed class Model
    {
        public required Uri Uri { get; init; }
        public StringBuilder Text { get; } = new();
        public long Sequence { get; set; } = 1;
        public long AlternativeSequence { get; set; } = 1;
        public bool ReadOnly { get; set; }
        public EditorViewState? ViewState { get; set; }
    }

    private readonly Dictionary<string, Model> _models = new(StringComparer.Ordinal);
    private EditorBridge? _bridge;
    public string? ActiveUri { get; private set; }
    public int ModelCount => _models.Count;

    public ValueTask InitializeAsync(ElementReference container, EditorBridge bridge, CancellationToken cancellationToken)
    {
        _bridge = bridge;
        return ValueTask.CompletedTask;
    }

    public ValueTask<EditorSequence> OpenDocumentAsync(DocumentContent content, CancellationToken cancellationToken)
    {
        if (!_models.TryGetValue(content.Uri.AbsoluteUri, out var model))
        {
            model = new Model { Uri = content.Uri, ReadOnly = content.ReadOnly };
            model.Text.Append(content.Text);
            _models.Add(content.Uri.AbsoluteUri, model);
        }
        ActiveUri = content.Uri.AbsoluteUri;
        return ValueTask.FromResult(Sequence(model));
    }

    public ValueTask SwitchDocumentAsync(Uri uri, EditorViewState? viewState, CancellationToken cancellationToken)
    {
        var model = Get(uri);
        if (viewState is not null) model.ViewState = viewState;
        ActiveUri = uri.AbsoluteUri;
        return ValueTask.CompletedTask;
    }

    public ValueTask ClearDocumentAsync(CancellationToken cancellationToken)
    {
        ActiveUri = null;
        return ValueTask.CompletedTask;
    }

    public ValueTask<EditorViewState?> GetViewStateAsync(Uri uri, CancellationToken cancellationToken) =>
        ValueTask.FromResult(Get(uri).ViewState);

    public ValueTask CloseDocumentAsync(Uri uri, CancellationToken cancellationToken)
    {
        _models.Remove(uri.AbsoluteUri);
        if (ActiveUri == uri.AbsoluteUri) ActiveUri = null;
        return ValueTask.CompletedTask;
    }

    public ValueTask<DocumentSnapshot> RelocateDocumentAsync(Uri oldUri, Uri newUri, string languageId, CancellationToken cancellationToken)
    {
        var old = Get(oldUri);
        var model = new Model { Uri = newUri, ReadOnly = old.ReadOnly, ViewState = old.ViewState };
        model.Text.Append(old.Text);
        _models.Remove(oldUri.AbsoluteUri);
        _models[newUri.AbsoluteUri] = model;
        if (ActiveUri == oldUri.AbsoluteUri) ActiveUri = newUri.AbsoluteUri;
        return ValueTask.FromResult(new DocumentSnapshot(model.Text.ToString(), model.Sequence, model.AlternativeSequence));
    }

    public ValueTask<EditorSequence> ReplaceDocumentAsync(string text, string lineEnding, CancellationToken cancellationToken) =>
        ReplaceDocumentAsync(new Uri(ActiveUri!), text, lineEnding, cancellationToken);

    public ValueTask<EditorSequence> ReplaceDocumentAsync(Uri uri, string text, string lineEnding, CancellationToken cancellationToken)
    {
        var model = Get(uri);
        model.Text.Clear();
        model.Text.Append(text);
        model.Sequence++;
        model.AlternativeSequence++;
        return ValueTask.FromResult(Sequence(model));
    }

    public ValueTask<DocumentSnapshot> GetSnapshotAsync(CancellationToken cancellationToken) =>
        GetSnapshotAsync(new Uri(ActiveUri!), cancellationToken);

    public ValueTask<DocumentSnapshot> GetSnapshotAsync(Uri uri, CancellationToken cancellationToken)
    {
        var model = Get(uri);
        return ValueTask.FromResult(new DocumentSnapshot(model.Text.ToString(), model.Sequence, model.AlternativeSequence));
    }

    public ValueTask<EditorSequence> GetSequenceAsync(CancellationToken cancellationToken) =>
        GetSequenceAsync(new Uri(ActiveUri!), cancellationToken);

    public ValueTask<EditorSequence> GetSequenceAsync(Uri uri, CancellationToken cancellationToken) =>
        ValueTask.FromResult(Sequence(Get(uri)));

    public ValueTask SetReadOnlyAsync(bool readOnly, CancellationToken cancellationToken) =>
        SetReadOnlyAsync(new Uri(ActiveUri!), readOnly, cancellationToken);

    public ValueTask SetReadOnlyAsync(Uri uri, bool readOnly, CancellationToken cancellationToken)
    {
        Get(uri).ReadOnly = readOnly;
        return ValueTask.CompletedTask;
    }

    public ValueTask SetEditorFontAsync(EditorFontPreference font, CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;

    public void Type(string text)
    {
        var model = Get(new Uri(ActiveUri!));
        var start = model.Text.Length;
        model.Text.Append(text);
        model.Sequence++;
        model.AlternativeSequence++;
        _bridge!.ReplicateEdits([
            new TextEditBatch(model.Uri.AbsoluteUri, model.Sequence - 1, model.Sequence, model.AlternativeSequence,
                EditOrigins.User, [new TextEdit(start, start, text)]),
        ]);
    }

    public ValueTask<IReadOnlyList<string>> RegisterCommandsAsync(IReadOnlyList<CommandDescriptor> descriptors, CancellationToken cancellationToken) =>
        ValueTask.FromResult<IReadOnlyList<string>>([]);
    public ValueTask BeginCompareAsync(ElementReference diffContainer, string originalText, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    public ValueTask EndCompareAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
    public ValueTask<EditorRuntimeInfo> GetRuntimeInfoAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult(new EditorRuntimeInfo("test", true, ModelCount, 0));
    public ValueTask DisposeAsync()
    {
        _models.Clear();
        return ValueTask.CompletedTask;
    }

    private Model Get(Uri uri) => _models[uri.AbsoluteUri];
    private static EditorSequence Sequence(Model model) => new(model.Sequence, model.AlternativeSequence);
}
