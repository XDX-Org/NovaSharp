namespace NovaSharp.Tests;

[TestClass]
public sealed class DocumentTabServiceTests
{
    [TestMethod]
    public async Task OpeningSameCanonicalPathFocusesExistingDocument()
    {
        using var fixture = new TabFixture();
        var path = fixture.File("one.cs");
        using var tabs = new DocumentTabService();

        var first = await tabs.OpenAsync(path);
        var second = await tabs.OpenAsync(Path.Combine(fixture.Root, ".", "one.cs"));

        Assert.AreSame(first, second);
        Assert.AreEqual(1, tabs.Tabs.Count);
        Assert.AreSame(first!.Document, tabs.ActiveTab!.Document);
    }

    [TestMethod]
    public async Task CleanPreviewIsReusedAndEditingCanPromoteIt()
    {
        using var fixture = new TabFixture();
        using var tabs = new DocumentTabService();
        var first = await tabs.OpenAsync(fixture.File("one.cs"), preview: true);

        var second = await tabs.OpenAsync(fixture.File("two.cs"), preview: true);

        Assert.AreEqual(1, tabs.Tabs.Count);
        Assert.IsFalse(tabs.Tabs.Contains(first));
        tabs.Promote(second!);
        await tabs.OpenAsync(fixture.File("three.cs"), preview: true);
        Assert.AreEqual(2, tabs.Tabs.Count);
        Assert.IsTrue(second!.IsPinned);
        Assert.IsFalse(second.IsPreview);
    }

    [TestMethod]
    public async Task MoveCommitsRequestedOrderAndCloseSelectsNeighbour()
    {
        using var fixture = new TabFixture();
        using var tabs = new DocumentTabService();
        var one = await tabs.OpenAsync(fixture.File("one.cs"));
        var two = await tabs.OpenAsync(fixture.File("two.cs"));
        var three = await tabs.OpenAsync(fixture.File("three.cs"));

        tabs.Move(three!, 0);
        tabs.Activate(two!);
        Assert.IsTrue(tabs.Close(two!));

        CollectionAssert.AreEqual(new[] { three, one }, tabs.Tabs.ToArray());
        Assert.AreSame(one, tabs.ActiveTab);
    }

    [TestMethod]
    public async Task DirtyTabRequiresExplicitDiscard()
    {
        using var fixture = new TabFixture();
        using var tabs = new DocumentTabService();
        var tab = await tabs.OpenAsync(fixture.File("one.cs"));
        tab!.Document.Content += "changed";

        Assert.IsFalse(tabs.Close(tab));
        Assert.IsTrue(tabs.Close(tab, discardDirty: true));
    }

    [TestMethod]
    public async Task DuplicateNamesIncludeParentFolder()
    {
        using var fixture = new TabFixture();
        using var tabs = new DocumentTabService();
        var first = await tabs.OpenAsync(fixture.File(Path.Combine("left", "same.cs")));
        var second = await tabs.OpenAsync(fixture.File(Path.Combine("right", "same.cs")));

        Assert.AreEqual("same.cs — left", tabs.GetDisplayName(first!));
        Assert.AreEqual("same.cs — right", tabs.GetDisplayName(second!));
    }

    private sealed class TabFixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "NovaSharp.Tests", Guid.NewGuid().ToString("N"));
        internal TabFixture() => Directory.CreateDirectory(Root);
        internal string File(string relativePath)
        {
            var path = Path.Combine(Root, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            System.IO.File.WriteAllText(path, "class Example;");
            return path;
        }
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
