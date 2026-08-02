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
}
