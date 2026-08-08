namespace NovaSharp.Tests;

[TestClass]
public sealed class QuickAccessTests
{
    [TestMethod]
    public async Task FilesRankByNameKeepDuplicatePathsAndIgnoreGeneratedFolders()
    {
        using var fixture = new Fixture();
        fixture.Write("one/Program.cs");
        fixture.Write("two/Program.cs");
        fixture.Write("obj/Program.cs");
        fixture.Write("one/Project.cs");
        var service = new QuickAccessService(new CommandRegistry());

        var results = await service.FindFilesAsync(fixture.Root, "prog");

        Assert.HasCount(2, results);
        Assert.IsTrue(results.All(item => item.Label == "Program.cs"));
        CollectionAssert.AreEquivalent(new[] { "one/Program.cs", "two/Program.cs" },
            results.Select(item => item.Detail).ToArray());
    }

    [TestMethod]
    public void CommandsUseSharedRegistryEnablementAndFuzzyRanking()
    {
        var registry = new CommandRegistry();
        registry.Register(new("files.open", "Open File", "Ctrl+O", () => true, _ => Task.CompletedTask));
        registry.Register(new("files.save", "Save File", "Ctrl+S", () => false, _ => Task.CompletedTask));
        registry.Register(new("search.workspace", "Search Workspace", null, () => true, _ => Task.CompletedTask));

        var results = new QuickAccessService(registry).FindCommands("opf");

        Assert.HasCount(1, results);
        Assert.AreEqual("files.open", results[0].Id);
        Assert.AreEqual("Ctrl+O", results[0].Keybinding);
    }

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "NovaSharp.Quick.Tests", Guid.NewGuid().ToString("N"));
        internal Fixture() => Directory.CreateDirectory(Root);
        internal void Write(string relative)
        {
            var path = Path.Combine(Root, relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "class C;");
        }
        public void Dispose() { try { Directory.Delete(Root, true); } catch { } }
    }
}
