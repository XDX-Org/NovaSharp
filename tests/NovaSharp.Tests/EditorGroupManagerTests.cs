using NovaSharp.Async;
using NovaSharp.Diagnostics;
using NovaSharp.Editing;
using NovaSharp.Platform;
using NovaSharp.Workspace;
using Xunit;

namespace NovaSharp.Tests;

public sealed class EditorGroupManagerTests : IAsyncDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("novasharp-groups").FullName;
    private readonly BoundedWorkQueue _queue = new(32, 2);
    private readonly DocumentFileStore _store = new();
    private readonly WorkspacePaths _paths = new();
    private readonly RegistryEditorHost _host = new();
    private readonly NotificationService _notifications = new(new BoundedWorkbenchLog());
    private readonly WorkspacePersistenceService _persistence;
    private readonly DocumentRegistry _documents;
    private EditorGroupManager? _groups;

    public EditorGroupManagerTests()
    {
        var codec = new DocumentTextCodec();
        var loader = new DocumentLoader(_paths, _store, codec, _queue);
        var saver = new DocumentSaver(_paths, _store, codec, _queue);
        _persistence = new WorkspacePersistenceService(new RegistryApplicationPaths(_root), _store, _queue);
        _documents = new DocumentRegistry(
            _host,
            _paths,
            _persistence,
            () => new DocumentSession(_host, loader, saver, _store, new FakeDocumentWatcher(), _queue, _notifications),
            _notifications);
        _host.InitializeAsync(default,
            new EditorBridge(_documents.Replicate, _documents.RequestResync, _ => Task.CompletedTask), default)
            .AsTask().GetAwaiter().GetResult();
    }

    [Fact]
    public async Task SplitCopiesAViewWithoutDuplicatingTheDocumentModel()
    {
        await OpenAsync("shared.cs");
        var groups = await CreateGroupsAsync();

        await groups.SplitAsync(EditorSplitDirection.Right, TestContext.Current.CancellationToken);

        Assert.Equal(2, groups.Snapshot.Groups.Count);
        Assert.Equal(2, groups.ViewCount(_documents.Snapshot.ActiveId!));
        Assert.Single(_documents.Snapshot.Tabs);
        Assert.Equal(1, _host.ModelCount);

        await groups.CloseViewAsync(groups.Snapshot.ActiveTab!.ViewId, discardDirty: false,
            TestContext.Current.CancellationToken);

        Assert.Single(groups.Snapshot.Groups);
        Assert.Single(_documents.Snapshot.Tabs);
        Assert.Equal(1, _host.ModelCount);
    }

    [Fact]
    public async Task MoveCopyResizeAndFocusUseOneNormalizedLayoutTree()
    {
        await OpenAsync("first.cs");
        await OpenAsync("second.cs");
        var groups = await CreateGroupsAsync();
        var originalGroup = groups.Snapshot.ActiveGroupId;
        var moved = groups.Snapshot.ActiveTab!;
        await groups.SplitAsync(EditorSplitDirection.Down, TestContext.Current.CancellationToken);
        var secondGroup = groups.Snapshot.ActiveGroupId;

        await groups.MoveViewAsync(moved.ViewId, secondGroup, 0, TestContext.Current.CancellationToken);
        await groups.CopyViewAsync(groups.Snapshot.ActiveTab!.ViewId, originalGroup, 0,
            TestContext.Current.CancellationToken);
        var split = Assert.IsType<EditorSplitNodeSnapshot>(groups.Snapshot.Layout);
        await groups.ResizeAsync(split.Id, 0.72, TestContext.Current.CancellationToken);
        await groups.FocusRelativeAsync(-1, TestContext.Current.CancellationToken);

        split = Assert.IsType<EditorSplitNodeSnapshot>(groups.Snapshot.Layout);
        Assert.Equal(0.72, split.Ratio, 3);
        Assert.Equal(secondGroup, groups.Snapshot.ActiveGroupId);
        Assert.All(groups.Snapshot.Groups.Values, group => Assert.NotEmpty(group.Tabs));
    }

    [Fact]
    public async Task OpeningANewDocumentInAMountedSplitAttachesItToThatView()
    {
        await OpenAsync("first.cs");
        var groups = await CreateGroupsAsync();
        await groups.SplitAsync(EditorSplitDirection.Right, TestContext.Current.CancellationToken);
        var splitGroup = groups.Snapshot.ActiveGroupId;
        await groups.RegisterGroupAsync(splitGroup, default, TestContext.Current.CancellationToken);
        var secondPath = Path.Combine(_root, "second.cs");
        await File.WriteAllTextAsync(secondPath, "public sealed class Second;\n",
            TestContext.Current.CancellationToken);

        await groups.OpenPinnedAsync(secondPath, TestContext.Current.CancellationToken);

        var expected = _paths.ToDocumentUri(_paths.Canonicalize(secondPath));
        Assert.Equal(expected, _host.UriForView(splitGroup));
        Assert.Equal(expected, groups.Snapshot.ActiveTab!.DocumentUri);
        Assert.Equal(2, _host.ModelCount);
    }

    [Fact]
    public async Task FocusingAnEditorGroupMakesItsDocumentTheCommandTarget()
    {
        await OpenAsync("left.cs");
        var groups = await CreateGroupsAsync();
        var leftGroup = groups.Snapshot.ActiveGroupId;
        var leftDocument = groups.Snapshot.ActiveTab!.DocumentId;
        await groups.SplitAsync(EditorSplitDirection.Right, TestContext.Current.CancellationToken);
        var rightGroup = groups.Snapshot.ActiveGroupId;
        await groups.RegisterGroupAsync(rightGroup, default, TestContext.Current.CancellationToken);
        var rightPath = Path.Combine(_root, "right.cs");
        await File.WriteAllTextAsync(rightPath, "public sealed class Right;\n", TestContext.Current.CancellationToken);
        await groups.OpenPinnedAsync(rightPath, TestContext.Current.CancellationToken);

        await groups.FocusAsync(leftGroup, TestContext.Current.CancellationToken);

        Assert.Equal(leftGroup, groups.Snapshot.ActiveGroupId);
        Assert.Equal(leftDocument, groups.Snapshot.ActiveTab!.DocumentId);
        Assert.Equal(leftDocument, _documents.Snapshot.ActiveId);

        _host.Type("// saved from left\n");
        var result = await _documents.ActiveDocument!.SaveAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(DocumentSaveStatus.Saved, result?.Status);
        Assert.Contains("saved from left", await File.ReadAllTextAsync(Path.Combine(_root, "left.cs"),
            TestContext.Current.CancellationToken), StringComparison.Ordinal);
        Assert.DoesNotContain("saved from left", await File.ReadAllTextAsync(rightPath,
            TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExplorerDropOpensTheFileAtTheRequestedGroupAndTabPosition()
    {
        await OpenAsync("first.cs");
        await OpenAsync("second.cs");
        var groups = await CreateGroupsAsync();
        var targetGroup = groups.Snapshot.ActiveGroupId;
        var droppedPath = Path.Combine(_root, "dropped.cs");
        await File.WriteAllTextAsync(droppedPath, "public sealed class Dropped;\n",
            TestContext.Current.CancellationToken);

        await groups.OpenPinnedInGroupAsync(droppedPath, targetGroup, 0,
            TestContext.Current.CancellationToken);

        Assert.Equal(targetGroup, groups.Snapshot.ActiveGroupId);
        Assert.Equal("dropped.cs", groups.Snapshot.ActiveGroup.Tabs[0].Label);
        Assert.Equal("dropped.cs", groups.Snapshot.ActiveTab!.Label);
    }

    [Fact]
    public async Task ExplorerEdgeDropOpensTheFileInANewSplit()
    {
        await OpenAsync("existing.cs");
        var groups = await CreateGroupsAsync();
        var targetGroup = groups.Snapshot.ActiveGroupId;
        var droppedPath = Path.Combine(_root, "edge.cs");
        await File.WriteAllTextAsync(droppedPath, "public sealed class Edge;\n",
            TestContext.Current.CancellationToken);

        await groups.OpenPinnedAtEdgeAsync(droppedPath, targetGroup, EditorSplitDirection.Left,
            TestContext.Current.CancellationToken);

        Assert.Equal(2, groups.Snapshot.Groups.Count);
        Assert.NotEqual(targetGroup, groups.Snapshot.ActiveGroupId);
        Assert.Equal("edge.cs", groups.Snapshot.ActiveTab!.Label);
        Assert.Single(groups.Snapshot.Groups[targetGroup].Tabs);
        var split = Assert.IsType<EditorSplitNodeSnapshot>(groups.Snapshot.Layout);
        Assert.Equal(groups.Snapshot.ActiveGroupId, Assert.IsType<EditorGroupNodeSnapshot>(split.First).GroupId);
    }

    [Fact]
    public async Task ExplorerEdgeDropOfAnOpenFileCreatesASharedModelView()
    {
        await OpenAsync("shared-edge.cs");
        var groups = await CreateGroupsAsync();
        var targetGroup = groups.Snapshot.ActiveGroupId;
        var path = Path.Combine(_root, "shared-edge.cs");

        await groups.OpenPinnedAtEdgeAsync(path, targetGroup, EditorSplitDirection.Right,
            TestContext.Current.CancellationToken);

        Assert.Equal(2, groups.ViewCount(_documents.Snapshot.ActiveId!));
        Assert.Single(_documents.Snapshot.Tabs);
        Assert.Single(groups.Snapshot.Groups[targetGroup].Tabs);
        Assert.Equal(1, _host.ModelCount);
    }

    [Fact]
    public async Task PersistedLayoutRestoresAndInvalidLayoutFallsBackToOneGroup()
    {
        await OpenAsync("restored.cs");
        var groups = await CreateGroupsAsync();
        await groups.SplitAsync(EditorSplitDirection.Right, TestContext.Current.CancellationToken);
        var splitId = Assert.IsType<EditorSplitNodeSnapshot>(groups.Snapshot.Layout).Id;
        for (var index = 0; index < 50; index++)
            await groups.ResizeAsync(splitId, 0.30 + index / 100.0, TestContext.Current.CancellationToken);
        await groups.DisposeAsync();
        _groups = null;

        groups = await CreateGroupsAsync();
        Assert.Equal(2, groups.Snapshot.Groups.Count);
        Assert.Equal(0.79, Assert.IsType<EditorSplitNodeSnapshot>(groups.Snapshot.Layout).Ratio, 3);
        await groups.DisposeAsync();
        _groups = null;

        await _persistence.UpdateAsync(state => state with
        {
            EditorLayout = new PersistedEditorLayout(
                new PersistedEditorLayoutNode("bad", "split"), [], "missing"),
        }, TestContext.Current.CancellationToken);
        groups = await CreateGroupsAsync();

        Assert.Single(groups.Snapshot.Groups);
        Assert.IsType<EditorGroupNodeSnapshot>(groups.Snapshot.Layout);
        Assert.Single(groups.Snapshot.ActiveGroup.Tabs);
    }

    [Fact]
    public async Task RestoringRemainingSplitGroupReusesThePrimaryEditor()
    {
        await OpenAsync("first.cs");
        await OpenAsync("second.cs");
        var groups = await CreateGroupsAsync();
        var firstView = groups.Snapshot.ActiveGroup.Tabs.Single(tab => tab.Label == "first.cs");
        await groups.SplitAsync(EditorSplitDirection.Right, TestContext.Current.CancellationToken);
        var remainingGroup = groups.Snapshot.ActiveGroupId;
        await groups.RegisterGroupAsync(remainingGroup, default, TestContext.Current.CancellationToken);
        await groups.CopyViewAsync(firstView.ViewId, remainingGroup, 0, TestContext.Current.CancellationToken);
        await groups.CloseGroupAsync(EditorGroupManager.MainGroupId, discardDirty: false,
            TestContext.Current.CancellationToken);
        Assert.Equal(remainingGroup, groups.Snapshot.ActiveGroupId);
        await groups.DisposeAsync();
        _groups = null;

        groups = await CreateGroupsAsync();

        Assert.Equal(EditorGroupManager.MainGroupId, groups.Snapshot.ActiveGroupId);
        Assert.Equal(EditorGroupManager.MainGroupId,
            Assert.IsType<EditorGroupNodeSnapshot>(groups.Snapshot.Layout).GroupId);
        Assert.Equal(["first.cs", "second.cs"], groups.Snapshot.ActiveGroup.Tabs.Select(tab => tab.Label));
        Assert.Equal("first.cs", groups.Snapshot.ActiveTab!.Label);
        Assert.Equal(groups.Snapshot.ActiveTab.DocumentUri, _host.UriForView(EditorGroupManager.MainGroupId));
    }

    [Fact]
    public async Task RestoredSplitWithoutPrimaryGroupPreservesGroupTabsAndFocus()
    {
        await OpenAsync("first.cs");
        await OpenAsync("second.cs");
        var first = _documents.Snapshot.Tabs.Single(tab => tab.Label == "first.cs");
        var second = _documents.Snapshot.Tabs.Single(tab => tab.Label == "second.cs");
        await _persistence.UpdateAsync(state => state with
        {
            EditorLayout = new PersistedEditorLayout(
                new PersistedEditorLayoutNode("split-restored", "split", Orientation: "Vertical", Ratio: 0.65,
                    First: new PersistedEditorLayoutNode("group-left", "group", GroupId: "group-left"),
                    Second: new PersistedEditorLayoutNode("group-right", "group", GroupId: "group-right")),
                [
                    new PersistedEditorGroup("group-left", [new PersistedEditorView("view-first", first.Id)],
                        "view-first"),
                    new PersistedEditorGroup("group-right", [new PersistedEditorView("view-second", second.Id)],
                        "view-second"),
                ],
                "group-right"),
        }, TestContext.Current.CancellationToken);

        var groups = await CreateGroupsAsync();

        var layout = Assert.IsType<EditorSplitNodeSnapshot>(groups.Snapshot.Layout);
        Assert.Equal(EditorSplitOrientation.Vertical, layout.Orientation);
        Assert.Equal(0.65, layout.Ratio);
        Assert.Equal("group-left", Assert.IsType<EditorGroupNodeSnapshot>(layout.First).GroupId);
        Assert.Equal(EditorGroupManager.MainGroupId, Assert.IsType<EditorGroupNodeSnapshot>(layout.Second).GroupId);
        Assert.Equal(EditorGroupManager.MainGroupId, groups.Snapshot.ActiveGroupId);
        Assert.Equal("first.cs", Assert.Single(groups.Snapshot.Groups["group-left"].Tabs).Label);
        Assert.Equal("second.cs", Assert.Single(groups.Snapshot.ActiveGroup.Tabs).Label);
        Assert.Equal(second.DocumentUri, _host.UriForView(EditorGroupManager.MainGroupId));
    }

    [Fact]
    public async Task RestoredSplitAttachesEachDocumentToItsPersistedGroup()
    {
        await OpenAsync("first.cs");
        await OpenAsync("second.cs");
        var first = _documents.Snapshot.Tabs.Single(tab => tab.Label == "first.cs");
        var second = _documents.Snapshot.Tabs.Single(tab => tab.Label == "second.cs");
        const string secondaryGroup = "group-secondary";
        await _persistence.UpdateAsync(state => state with
        {
            EditorLayout = new PersistedEditorLayout(
                new PersistedEditorLayoutNode("split-restored", "split", Orientation: "Vertical", Ratio: 0.65,
                    First: new PersistedEditorLayoutNode("main", "group", GroupId: "main"),
                    Second: new PersistedEditorLayoutNode(secondaryGroup, "group", GroupId: secondaryGroup)),
                [
                    new PersistedEditorGroup("main", [new PersistedEditorView("view-first", first.Id)], "view-first"),
                    new PersistedEditorGroup(secondaryGroup, [new PersistedEditorView("view-second", second.Id)],
                        "view-second"),
                ],
                secondaryGroup),
        }, TestContext.Current.CancellationToken);

        var groups = await CreateGroupsAsync();

        Assert.Equal(first.DocumentUri, _host.UriForView(EditorGroupManager.MainGroupId));
        Assert.Equal(second.Id, _documents.Snapshot.ActiveId);

        await groups.RegisterGroupAsync(secondaryGroup, default, TestContext.Current.CancellationToken);

        Assert.Equal(first.DocumentUri, _host.UriForView(EditorGroupManager.MainGroupId));
        Assert.Equal(second.DocumentUri, _host.UriForView(secondaryGroup));
        Assert.Equal(secondaryGroup, groups.Snapshot.ActiveGroupId);
    }

    [Fact]
    public async Task SplitDepthAndGroupCountsAreBounded()
    {
        await OpenAsync("bounded.cs");
        var groups = await CreateGroupsAsync();
        for (var index = 0; index < EditorGroupManager.MaximumGroups * 2; index++)
            await groups.SplitAsync(EditorSplitDirection.Right, TestContext.Current.CancellationToken);

        Assert.True(groups.Snapshot.Groups.Count <= EditorGroupManager.MaximumGroups);
        Assert.True(Depth(groups.Snapshot.Layout) <= EditorGroupManager.MaximumLayoutDepth);
    }

    private async Task<EditorGroupManager> CreateGroupsAsync()
    {
        var groups = new EditorGroupManager(_host, _documents, _persistence, _notifications);
        await groups.RestoreAsync(TestContext.Current.CancellationToken);
        _groups = groups;
        return groups;
    }

    private async Task OpenAsync(string name)
    {
        var path = Path.Combine(_root, name);
        await File.WriteAllTextAsync(path, $"class {Path.GetFileNameWithoutExtension(name)};\n",
            TestContext.Current.CancellationToken);
        await _documents.OpenPinnedAsync(path, TestContext.Current.CancellationToken);
    }

    private static int Depth(EditorLayoutNodeSnapshot node) => node is EditorSplitNodeSnapshot split
        ? 1 + Math.Max(Depth(split.First), Depth(split.Second)) : 1;

    public async ValueTask DisposeAsync()
    {
        if (_groups is not null) await _groups.DisposeAsync();
        await _documents.DisposeAsync();
        await _host.DisposeAsync();
        await _queue.DisposeAsync();
        Directory.Delete(_root, recursive: true);
    }
}
