namespace NovaSharp.Tests;

[TestClass]
public sealed class EditorGroupTests
{
    [TestMethod]
    public void DirectionControlsOrientationOrderAndFocus()
    {
        var initial = new EditorGroup();
        var layout = new EditorLayout(initial);

        var left = layout.Split(initial.Id, SplitDirection.Left)!;

        var root = (EditorSplit)layout.Root;
        Assert.AreEqual(SplitOrientation.Horizontal, root.Orientation);
        Assert.AreSame(left, root.First);
        Assert.AreSame(initial, root.Second);
        Assert.AreEqual(left.Id, layout.FocusedGroupId);
    }

    [TestMethod]
    public void EmptyGroupRemovalNormalizesTreeAndRetainsOneGroup()
    {
        var initial = new EditorGroup();
        var layout = new EditorLayout(initial);
        var second = layout.Split(initial.Id, SplitDirection.Right)!;

        Assert.IsTrue(layout.RemoveEmptyGroup(second.Id));

        Assert.AreSame(initial, layout.Root);
        Assert.AreEqual(initial.Id, layout.FocusedGroupId);
        Assert.IsFalse(layout.RemoveEmptyGroup(initial.Id));
    }

    [TestMethod]
    public void RatiosAreFiniteBoundedAndCanBeEqualized()
    {
        var layout = new EditorLayout();
        var second = layout.Split(layout.FocusedGroupId, SplitDirection.Down)!;
        var root = (EditorSplit)layout.Root;

        Assert.IsTrue(layout.Resize(root.Id, double.NaN));
        Assert.AreEqual(0.5, root.Ratio);
        layout.Resize(root.Id, 4);
        Assert.AreEqual(0.9, root.Ratio);
        layout.DistributeEvenly();
        Assert.AreEqual(0.5, root.Ratio);
        Assert.IsTrue(layout.Focus(second.Id));
    }

    [TestMethod]
    public void SplittingStopsAtMaximumDepth()
    {
        var layout = new EditorLayout();
        var group = layout.Groups[0];
        for (var depth = 0; depth < EditorLayout.MaximumDepth; depth++)
            group = layout.Split(group.Id, SplitDirection.Right)!;

        Assert.IsNull(layout.Split(group.Id, SplitDirection.Right));
        Assert.AreEqual(EditorLayout.MaximumDepth + 1, layout.Groups.Count);
    }

    [TestMethod]
    public async Task RegistrySharesDocumentUntilLastReferenceReleases()
    {
        var root = Path.Combine(Path.GetTempPath(), "NovaSharp.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "shared.cs");
        await File.WriteAllTextAsync(path, "class Shared;");
        try
        {
            using var registry = new DocumentRegistry();
            var first = await registry.AcquireAsync(path);
            var second = await registry.AcquireAsync(Path.Combine(root, ".", "shared.cs"));

            Assert.AreSame(first, second);
            Assert.AreEqual(1, registry.DocumentCount);
            registry.Release(first!);
            Assert.AreEqual(1, registry.DocumentCount);
            registry.Release(second!);
            Assert.AreEqual(0, registry.DocumentCount);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public async Task MoveTransfersViewAndCopySharesDocumentWithIndependentState()
    {
        var root = Path.Combine(Path.GetTempPath(), "NovaSharp.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "shared.cs");
        await File.WriteAllTextAsync(path, "class Shared;");
        try
        {
            using var workspace = new EditorGroupWorkspace();
            var firstGroup = workspace.FocusedGroup;
            var tab = await workspace.OpenAsync(path);
            tab!.ViewState.Restore(2, 5, 30, 4, tab.Document.Content!.Length);
            var secondGroup = workspace.Split(firstGroup.Id, SplitDirection.Right)!;

            Assert.IsTrue(workspace.Move(tab, firstGroup.Id, secondGroup.Id));
            Assert.AreSame(tab, secondGroup.ActiveTab);
            Assert.AreEqual(1, workspace.Layout.Groups.Count);
            var thirdGroup = workspace.Split(secondGroup.Id, SplitDirection.Down)!;
            var copy = await workspace.CopyAsync(tab, thirdGroup.Id);

            Assert.AreSame(tab.Document, copy!.Document);
            Assert.AreNotSame(tab.ViewState, copy.ViewState);
            Assert.AreEqual(2, copy.ViewState.SelectionStart);
            copy.Document.Content += " changed";
            Assert.AreEqual(tab.Document.Content, copy.Document.Content);
            Assert.IsTrue(workspace.Close(thirdGroup.Id, copy));
            Assert.IsFalse(workspace.Close(secondGroup.Id, tab));
            Assert.IsTrue(workspace.Close(secondGroup.Id, tab, discardDirty: true));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public async Task VersionTwoLayoutRoundTripsAndVersionOneMigratesToOneGroup()
    {
        var root = Path.Combine(Path.GetTempPath(), "NovaSharp.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var file = Path.Combine(root, "shared.cs");
        var session = Path.Combine(root, "session.json");
        await File.WriteAllTextAsync(file, "class Shared;");
        try
        {
            var persistence = new EditorLayoutPersistence(session);
            WorkbenchLayoutState captured;
            using (var workspace = new EditorGroupWorkspace())
            {
                var first = workspace.FocusedGroup;
                var tab = await workspace.OpenAsync(file);
                var second = workspace.Split(first.Id, SplitDirection.Right)!;
                await workspace.CopyAsync(tab!, second.Id);
                captured = workspace.CaptureState();
                await persistence.SaveAsync(captured);
            }
            using (var restored = new EditorGroupWorkspace())
            {
                await restored.RestoreAsync(await persistence.LoadAsync());
                Assert.AreEqual(2, restored.Layout.Groups.Count);
                Assert.AreEqual(2, restored.Layout.Groups.SelectMany(group => group.Tabs).Count());
                Assert.AreSame(restored.Layout.Groups[0].Tabs[0].Document, restored.Layout.Groups[1].Tabs[0].Document);
                Assert.AreEqual(captured.FocusedGroupId, restored.Layout.FocusedGroupId);
            }

            var legacy = new WorkbenchSessionState(1, file, [new(file, false, 1, 3, 12, 4)]);
            await File.WriteAllBytesAsync(session, System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(legacy));
            var migrated = await persistence.LoadAsync();
            Assert.AreEqual(2, migrated.SchemaVersion);
            Assert.AreEqual("group", migrated.Root!.Kind);
            Assert.AreEqual(1, migrated.Root.Tabs!.Length);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public async Task InvalidLayoutFallsBackWithoutLosingValidSibling()
    {
        using var workspace = new EditorGroupWorkspace();
        var valid = new LayoutNodeState("group", Guid.NewGuid());
        var invalid = new LayoutNodeState("split", Guid.NewGuid(), SplitOrientation.Horizontal, double.NaN);
        var root = new LayoutNodeState("split", Guid.NewGuid(), SplitOrientation.Horizontal, 0.5, valid, invalid);

        await workspace.RestoreAsync(new(2, root, Guid.NewGuid()));

        Assert.AreEqual(1, workspace.Layout.Groups.Count);
        Assert.AreEqual(valid.Id, workspace.Layout.Groups[0].Id);
        Assert.AreEqual(valid.Id, workspace.Layout.FocusedGroupId);
    }

    [TestMethod]
    public async Task DragDropUsesMoveOperationAndCancellationChangesNothing()
    {
        var root = Path.Combine(Path.GetTempPath(), "NovaSharp.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "move.cs");
        await File.WriteAllTextAsync(path, "class Move;");
        try
        {
            using var workspace = new EditorGroupWorkspace();
            var source = workspace.FocusedGroup;
            var tab = (await workspace.OpenAsync(path, preview: true))!;
            var target = workspace.Split(source.Id, SplitDirection.Right)!;
            workspace.BeginDrag(tab);
            workspace.CancelDrag();
            Assert.AreSame(tab, source.ActiveTab);

            workspace.BeginDrag(tab);
            Assert.IsTrue(await workspace.DropAsync(target.Id));
            Assert.AreSame(tab, workspace.FocusedGroup.ActiveTab);
            Assert.IsTrue(tab.IsPreview);
            var copyTarget = workspace.Split(target.Id, SplitDirection.Down)!;
            workspace.BeginDrag(tab);
            Assert.IsTrue(await workspace.DropAsync(copyTarget.Id, copy: true));
            Assert.IsTrue(copyTarget.ActiveTab!.IsPinned);
            Assert.AreSame(tab.Document, copyTarget.ActiveTab.Document);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
