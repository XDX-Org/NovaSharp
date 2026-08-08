namespace NovaSharp.Tests;

[TestClass]
[DoNotParallelize]
public sealed class Phase8AcceptanceTests
{
    [TestMethod]
    [Timeout(30000)]
    public async Task NavigationSymbolsAndRenameUseTheLoadedSolutionAndDirtyBuffer()
    {
        using var fixture = new Fixture();
        await using var projectSystem = new RoslynProjectSystem();
        await projectSystem.OpenAsync(fixture.Project);
        using var editor = new EditorDocumentState();
        await editor.OpenAsync(fixture.Program);
        projectSystem.Track(editor);
        editor.Content = "interface IService { void Run(); } class Service : IService { public void Run() { } } class C { void M() { new Service().Run(); } }";
        var provider = new CSharpLanguageProvider(projectSystem);
        var call = editor.Content.LastIndexOf("Run", StringComparison.Ordinal);
        var request = Request(projectSystem, editor, call);

        var definitions = await provider.GetDefinitionsAsync(request, false, default);
        var references = await provider.FindReferencesAsync(request, default);
        var symbols = await provider.GetDocumentSymbolsAsync(request, default);
        var rename = await provider.RenameAsync(request, "Execute", default);

        Assert.IsTrue(definitions.Any(item => item.DisplayText.Contains("Run", StringComparison.Ordinal)));
        Assert.IsTrue(references.Count >= 1);
        Assert.IsTrue(symbols.Any(item => item.Name == "Service" && item.Range.Length > "Service".Length));
        Assert.IsNotNull(rename);
        Assert.HasCount(1, rename.Documents);
        StringAssert.Contains(rename.Documents[0].NewText, "Execute()");
        Assert.IsTrue(rename.Documents[0].ExpectedVersion == editor.Version);
    }

    [TestMethod]
    public async Task WorkspaceTransactionUpdatesDirtyBuffersAndUnopenedFilesTogether()
    {
        using var fixture = new Fixture();
        using var editor = new EditorDocumentState();
        await editor.OpenAsync(fixture.Program);
        editor.Content = "dirty before";
        var other = Path.Combine(fixture.Root, "Other.cs");
        await File.WriteAllTextAsync(other, "disk before");
        var edit = new WorkspaceEdit("change", [
            new(fixture.Program, editor.Version, "dirty before", "dirty after"),
            new(other, null, "disk before", "disk after")]);

        await new WorkspaceEditTransaction().ApplyAsync(edit, [editor]);

        Assert.AreEqual("dirty after", editor.Content);
        Assert.AreEqual("disk after", await File.ReadAllTextAsync(other));
        Assert.IsTrue(editor.IsDirty);
    }

    [TestMethod]
    public async Task WorkspaceTransactionRejectsConflictsBeforeChangingAnything()
    {
        using var fixture = new Fixture();
        var first = fixture.Program;
        var second = Path.Combine(fixture.Root, "Other.cs");
        await File.WriteAllTextAsync(second, "changed externally");
        var original = await File.ReadAllTextAsync(first);
        var edit = new WorkspaceEdit("conflict", [
            new(first, null, original, "replacement"),
            new(second, null, "expected", "replacement")]);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            new WorkspaceEditTransaction().ApplyAsync(edit, []));

        Assert.AreEqual(original, await File.ReadAllTextAsync(first));
        Assert.AreEqual("changed externally", await File.ReadAllTextAsync(second));
    }

    private static LanguageRequest Request(RoslynProjectSystem projectSystem, EditorDocumentState editor, int position) =>
        new(editor.FilePath!, projectSystem.GetActiveDocument(editor.FilePath!)?.Project.Id.Id.ToString(),
            editor.Version, position);

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "NovaSharp.Phase8.Tests", Guid.NewGuid().ToString("N"));
        internal string Project => Path.Combine(Root, "App.csproj");
        internal string Program => Path.Combine(Root, "Program.cs");
        internal Fixture()
        {
            Directory.CreateDirectory(Root);
            File.WriteAllText(Project, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
            File.WriteAllText(Program, "class C { }");
        }
        public void Dispose() { try { Directory.Delete(Root, true); } catch { } }
    }
}
