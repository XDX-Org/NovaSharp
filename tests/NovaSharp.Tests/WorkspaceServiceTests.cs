namespace NovaSharp.Tests;

[TestClass]
public sealed class WorkspaceServiceTests
{
    [TestMethod]
    public async Task EnumerationIsSortedTypedAndIgnoresBuildFolders()
    {
        using var fixture = new WorkspaceFixture();
        Directory.CreateDirectory(Path.Combine(fixture.Root, "z-folder"));
        Directory.CreateDirectory(Path.Combine(fixture.Root, "bin"));
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "a.cs"), "class A;");
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "b.data"), "data");
        using var workspace = new WorkspaceService();
        workspace.Open(fixture.Root);

        var children = await workspace.GetChildrenAsync(fixture.Root);

        CollectionAssert.AreEqual(new[] { "z-folder", "a.cs", "b.data" }, children.Select(entry => entry.Name).ToArray());
        Assert.AreEqual(WorkspaceEntryKind.Folder, children[0].Kind);
        Assert.AreEqual(WorkspaceEntryKind.SupportedFile, children[1].Kind);
        Assert.AreEqual(WorkspaceEntryKind.UnknownFile, children[2].Kind);
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
        document.OpenAsync(original).GetAwaiter().GetResult();
        document.Content = "class Dirty;";

        Assert.ThrowsExactly<UnauthorizedAccessException>(() => workspace.CreateFile(Path.GetDirectoryName(fixture.Root)!, "escape.cs"));
        var renamed = workspace.Move(original, fixture.Root, "after.cs");
        document.Relocate(original, renamed);

        Assert.AreEqual(renamed, document.FilePath);
        Assert.AreEqual("class Dirty;", document.Content);
        Assert.IsTrue(document.IsDirty);
        Assert.IsTrue(File.Exists(renamed));
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
        }
        finally { Directory.Delete(outside); }
    }

    private sealed class WorkspaceFixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "NovaSharp.Tests", Guid.NewGuid().ToString("N"));
        internal WorkspaceFixture() => Directory.CreateDirectory(Root);
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true); }
    }
}
