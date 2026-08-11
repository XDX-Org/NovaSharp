using System.Collections.Concurrent;
using System.Diagnostics;

namespace NovaSharp.Tests;

[TestClass]
public sealed class WorkspaceServiceTests
{
    [TestMethod]
    public async Task EnumerationIsSortedTypedAndShowsBuildFolders()
    {
        using var fixture = new WorkspaceFixture();
        Directory.CreateDirectory(Path.Combine(fixture.Root, "z-folder"));
        Directory.CreateDirectory(Path.Combine(fixture.Root, ".git"));
        Directory.CreateDirectory(Path.Combine(fixture.Root, "bin"));
        Directory.CreateDirectory(Path.Combine(fixture.Root, "obj"));
        Directory.CreateDirectory(Path.Combine(fixture.Root, ".cache"));
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "a.cs"), "class A;");
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "b.data"), "data");
        using var workspace = new WorkspaceService([".cache"]);
        workspace.Open(fixture.Root);

        var children = await workspace.GetChildrenAsync(fixture.Root);

        CollectionAssert.AreEqual(new[] { ".git", "bin", "obj", "z-folder", "a.cs", "b.data" }, children.Select(entry => entry.Name).ToArray());
        Assert.AreEqual(WorkspaceEntryKind.Folder, children[0].Kind);
        Assert.AreEqual(WorkspaceEntryKind.SupportedFile, children[4].Kind);
        Assert.AreEqual(WorkspaceEntryKind.UnknownFile, children[5].Kind);
    }

    [TestMethod]
    public void OperationsRejectEscapesAndRenameOpenDirtyDocumentWithoutLosingBuffer()
    {
        using var fixture = new WorkspaceFixture();
        var original = Path.Combine(fixture.Root, "before.cs");
        File.WriteAllText(original, "class Before;");
        using var workspace = new WorkspaceService();
        workspace.Open(fixture.Root);
        using var document = new EditorDocumentState();
        var view = new EditorViewState();
        document.OpenAsync(original).GetAwaiter().GetResult();
        document.Content = "class Dirty;";
        view.SetSelection(6, 11, document.Content.Length);

        Assert.ThrowsExactly<UnauthorizedAccessException>(() => workspace.CreateFile(Path.GetDirectoryName(fixture.Root)!, "escape.cs"));
        var renamed = workspace.Move(original, fixture.Root, "after.cs");
        document.Relocate(original, renamed);

        Assert.AreEqual(renamed, document.FilePath);
        Assert.AreEqual("class Dirty;", document.Content);
        Assert.IsTrue(document.IsDirty);
        Assert.AreEqual(6, view.SelectionStart);
        Assert.AreEqual(11, view.SelectionEnd);
        Assert.IsTrue(File.Exists(renamed));
    }

    [TestMethod]
    public async Task MoveAcrossFoldersRefreshesBothWatcherBranches()
    {
        using var fixture = new WorkspaceFixture();
        var source = Path.Combine(fixture.Root, "source");
        var destination = Path.Combine(fixture.Root, "destination");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(destination);
        var oldPath = Path.Combine(source, "item.cs");
        await File.WriteAllTextAsync(oldPath, "class Item;");
        using var workspace = new WorkspaceService();
        var changed = new ConcurrentDictionary<string, byte>(PathComparer);
        workspace.Changed += path => { if (path is not null) changed.TryAdd(path, 0); };
        workspace.Open(fixture.Root);

        File.Move(oldPath, Path.Combine(destination, "item.cs"));

        await WaitUntilAsync(() => changed.ContainsKey(source) && changed.ContainsKey(destination));
    }

    [TestMethod]
    public async Task ExternalCreateAndDeleteRefreshOnlyTheAffectedBranch()
    {
        using var fixture = new WorkspaceFixture();
        var affected = Path.Combine(fixture.Root, "affected");
        var unaffected = Path.Combine(fixture.Root, "unaffected");
        Directory.CreateDirectory(affected);
        Directory.CreateDirectory(unaffected);
        using var workspace = new WorkspaceService();
        workspace.Open(fixture.Root);
        var changed = new ConcurrentQueue<string>();
        workspace.Changed += path => { if (path is not null) changed.Enqueue(path); };

        var external = Path.Combine(affected, "external.cs");
        await File.WriteAllTextAsync(external, "class External;");
        await WaitUntilAsync(() => changed.Contains(affected, PathComparer));
        while (changed.TryDequeue(out _)) { }
        File.Delete(external);
        await WaitUntilAsync(() => changed.Contains(affected, PathComparer));

        Assert.IsFalse(changed.Contains(unaffected, PathComparer));
    }

    [TestMethod]
    public async Task MovingFolderRelocatesDirtyDocumentAndRetainsViewSelection()
    {
        using var fixture = new WorkspaceFixture();
        var folder = Path.Combine(fixture.Root, "before");
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, "active.cs");
        await File.WriteAllTextAsync(file, "class Before;");
        using var document = new EditorDocumentState();
        await document.OpenAsync(file);
        document.Content = "class Dirty;";
        var view = new EditorViewState();
        view.SetSelection(6, 11, document.Content.Length);
        using var workspace = new WorkspaceService();
        workspace.Open(fixture.Root);

        var movedFolder = workspace.Move(folder, fixture.Root, "after");
        document.Relocate(folder, movedFolder);

        Assert.AreEqual(Path.Combine(movedFolder, "active.cs"), document.FilePath);
        Assert.AreEqual("class Dirty;", document.Content);
        Assert.IsTrue(document.IsDirty);
        Assert.AreEqual(6, view.SelectionStart);
        Assert.AreEqual(11, view.SelectionEnd);
    }

    [TestMethod]
    public async Task WatcherOverflowRestartsWatchingAndRequestsFullRescan()
    {
        using var fixture = new WorkspaceFixture();
        using var workspace = new WorkspaceService();
        string? message = null;
        var rescans = 0;
        var subsequentChange = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        workspace.Error += value => message = value;
        workspace.RescanRequired += () => rescans++;
        workspace.Open(fixture.Root);
        workspace.Changed += path => { if (PathComparer.Equals(path, fixture.Root)) subsequentChange.TrySetResult(); };

        workspace.HandleWatcherError(new InternalBufferOverflowException("buffer full"));
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "after-overflow.cs"), "class Recovered;");

        Assert.IsTrue(message?.Contains("rescan", StringComparison.OrdinalIgnoreCase));
        Assert.AreEqual(1, rescans);
        await subsequentChange.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [TestMethod]
    [Timeout(30000)]
    public async Task TwentyThousandEntryWorkspaceEnumeratesResponsively()
    {
        using var fixture = new WorkspaceFixture();
        for (var directory = 0; directory < 20; directory++)
        {
            var path = Path.Combine(fixture.Root, $"folder-{directory:D2}");
            Directory.CreateDirectory(path);
            for (var file = 0; file < 1000; file++)
                File.Create(Path.Combine(path, $"file-{file:D4}.cs")).Dispose();
        }
        using var workspace = new WorkspaceService();
        workspace.Open(fixture.Root);

        var stopwatch = Stopwatch.StartNew();
        var roots = await workspace.GetChildrenAsync(fixture.Root);
        var children = await Task.WhenAll(roots.Select(entry => workspace.GetChildrenAsync(entry.Path)));
        stopwatch.Stop();

        Assert.AreEqual(20_000, children.Sum(entries => entries.Count));
        Assert.IsTrue(stopwatch.Elapsed < TimeSpan.FromSeconds(5),
            $"20,000-entry enumeration took {stopwatch.Elapsed}.");
    }

    [TestMethod]
    public async Task PersistenceFallsBackFromCorruptionAndRoundTripsVersionedState()
    {
        using var fixture = new WorkspaceFixture();
        var path = Path.Combine(fixture.Root, "state.json");
        var persistence = new WorkspacePersistence(path);
        await File.WriteAllTextAsync(path, "not json");
        Assert.IsNull((await persistence.LoadAsync()).WorkspacePath);

        var expected = new WorkspaceRestoreState(1, fixture.Root, [fixture.Root], true, 320);
        await persistence.SaveAsync(expected);

        var actual = await persistence.LoadAsync();
        Assert.AreEqual(expected.SchemaVersion, actual.SchemaVersion);
        Assert.AreEqual(expected.WorkspacePath, actual.WorkspacePath);
        CollectionAssert.AreEqual(expected.ExpandedPaths, actual.ExpandedPaths);
        Assert.AreEqual(expected.SidebarCollapsed, actual.SidebarCollapsed);
        Assert.AreEqual(expected.SidebarWidth, actual.SidebarWidth);
    }

    [TestMethod]
    public async Task SymbolicLinksAreLeavesAndCannotEscapeThroughEnumeration()
    {
        if (OperatingSystem.IsWindows()) Assert.Inconclusive("Symlink creation requires developer mode on Windows.");
        using var fixture = new WorkspaceFixture();
        var outside = Path.Combine(Path.GetTempPath(), $"novasharp-outside-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outside);
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(fixture.Root, "outside-link"), outside);
            using var workspace = new WorkspaceService();
            workspace.Open(fixture.Root);

            var link = (await workspace.GetChildrenAsync(fixture.Root)).Single();

            Assert.AreEqual(WorkspaceEntryKind.SymbolicLink, link.Kind);
            Assert.IsFalse(link.IsDirectory);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => workspace.GetChildrenAsync(link.Path));
            Assert.ThrowsExactly<UnauthorizedAccessException>(() => workspace.CreateFile(link.Path, "escape.cs"));
            Assert.IsFalse(File.Exists(Path.Combine(outside, "escape.cs")));
        }
        finally { Directory.Delete(outside); }
    }

    private sealed class WorkspaceFixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "NovaSharp.Tests", Guid.NewGuid().ToString("N"));
        internal WorkspaceFixture() => Directory.CreateDirectory(Root);
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true); }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(25, timeout.Token);
    }

    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;
}
